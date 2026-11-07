using System.IO.Pipes;
using System.Security.Principal;
using DoorMan.Core;
using DoorMan.Shell;

namespace DoorMan.Agent;

/// <summary>
/// The DoorMan process that stays in the background from sign-in, without windows. It keeps locked folders
/// connected (without it, Windows tells anyone opening one of their files that “the cloud file provider is not
/// running”) and runs the context menu commands, which reach it through a named pipe only this user can access.
/// </summary>
internal sealed class AgentHost : ApplicationContext
{
    static readonly TimeSpan DehydrateRetry = TimeSpan.FromSeconds(10);
    static readonly TimeSpan DehydrateGiveUp = TimeSpan.FromHours(2);

    readonly Control _ui;
    readonly System.Windows.Forms.Timer _dehydrateTimer;
    readonly Dictionary<string, (string Folder, DateTime Since)> _inUse = new(StringComparer.OrdinalIgnoreCase);
    public static AgentHost? Current { get; private set; }
    public CloudProvider Provider { get; }

    public static string PipeName =>
        $"DoorMan-{WindowsIdentity.GetCurrent().User?.Value}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>Runs the agent until the session ends. If one is already running, it returns right away.</summary>
    public static void Run()
    {
        using var single = new Mutex(true, @"Local\DoorMan.Agent", out bool first);
        if (!first)
            return;
        Application.Run(new AgentHost());
    }

    AgentHost()
    {
        Current = this;
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;
        Provider = new CloudProvider((folder, session) => Post(() => Commands.UnlockForProgram(folder, session)));
        _dehydrateTimer = new System.Windows.Forms.Timer { Interval = (int)DehydrateRetry.TotalMilliseconds };
        _dehydrateTimer.Tick += (_, _) => RetryDehydrate();

        try
        {
            ShellIntegration.Register();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var folder in AppState.Sync().Where(f => f.Locked))
            Watch(folder.Path, inUse: []);

        new Thread(ListenForCommands) { IsBackground = true, Name = "DoorMan commands" }.Start();
    }

    /// <summary>
    /// Connects a folder that has just been locked. Files that were open when it was locked are emptied as soon as
    /// they are closed.
    /// </summary>
    public void Watch(string folder, IEnumerable<string> inUse)
    {
        if (!CloudFiles.IsSyncRoot(folder))
            return;
        Provider.Connect(folder);
        foreach (var rel in inUse)
            _inUse[Path.Combine(folder, rel)] = (folder, DateTime.UtcNow);
        if (_inUse.Count > 0)
            _dehydrateTimer.Start();
    }

    void RetryDehydrate()
    {
        foreach (var (path, (folder, since)) in _inUse.ToList())
        {
            bool done = !FolderLocker.IsLocked(folder) || !File.Exists(path)
                || CloudFiles.GetState(path) != PlaceholderState.Hydrated
                || CloudFiles.TryDehydrate(path)
                || DateTime.UtcNow - since > DehydrateGiveUp;
            if (done)
                _inUse.Remove(path);
        }
        if (_inUse.Count == 0)
            _dehydrateTimer.Stop();
    }

    void Post(Action action) => _ui.BeginInvoke(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Program.ShowError(ex);
        }
    });

    void ListenForCommands()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var args = new List<string>();
                while (reader.ReadLine() is { Length: > 0 } line)
                    args.Add(line);
                var action = Commands.FromArguments(args.ToArray());
                writer.WriteLine(action is null ? "?" : "ok");
                if (action is not null)
                    Post(action);
            }
            catch (IOException)
            {
                // The client left too early; wait for the next one.
            }
        }
    }
}

/// <summary>Sends a context menu command to the agent, starting it if needed.</summary>
internal static class AgentClient
{
    public static bool TrySend(string[] args)
    {
        // The agent is not in the foreground: allow it to bring its windows to the front.
        Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1));
        if (TrySendOnce(args, 300))
            return true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--agent")
            {
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (TrySendOnce(args, 500))
                return true;
            Thread.Sleep(100);
        }
        return false;
    }

    static bool TrySendOnce(string[] args, int timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentHost.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeout);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            foreach (var arg in args)
                writer.WriteLine(arg);
            writer.WriteLine();
            return reader.ReadLine() == "ok";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
