using System.Security.Cryptography;
using System.Text;
using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Crypto;
using DoorMan.Shell;
using DoorMan.UI;

namespace DoorMan;

/// <summary>
/// The File Explorer context menu actions. They are normally carried out by the background agent
/// (<see cref="AgentHost"/>), which is also the one serving apps that open files of locked folders.
/// </summary>
internal static class Commands
{
    /// <summary>
    /// Arguments of versions before 3.0, which were in Spanish. They are still accepted because the registry of an
    /// earlier installation and an earlier Explorer extension (loaded until Explorer restarts) keep using them.
    /// </summary>
    static readonly Dictionary<string, string> LegacyArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--proteger"] = "--protect",
        ["--abrir"] = "--open",
        ["--ventana"] = "--window",
        ["--rebloquear"] = "--lock",
        ["--instalar"] = "--install",
        ["--desinstalar"] = "--uninstall",
        ["--registrar"] = "--register",
        ["--desregistrar"] = "--unregister",
        ["--inicio"] = "--startup",
        ["--agente"] = "--agent",
        ["--confiar-certificado"] = "--trust-certificate",
    };

    /// <summary>Options in lowercase and in their current names; other arguments (paths) unchanged.</summary>
    public static string[] NormalizeArguments(string[] args) =>
        args.Select(a => !a.StartsWith("--", StringComparison.Ordinal) ? a
            : LegacyArguments.TryGetValue(a, out var current) ? current
            : a.ToLowerInvariant()).ToArray();

    /// <summary>The action for the command-line arguments, or <c>null</c> if they are not valid.</summary>
    public static Action? FromArguments(string[] args) => NormalizeArguments(args) switch
    {
        ["--protect", var path] => () => Protect(path),
        ["--open", var path] => () => Open(path),
        ["--open", var path, "--window", var w] when long.TryParse(w, out var window) => () => Open(path, new IntPtr(window)),
        ["--lock", var path] => () => Relock(path),
        _ => null,
    };

    /// <summary>“Protect with password”.</summary>
    public static void Protect(string path, string? note = null)
    {
        var folder = PathUtil.Normalize(path);
        using var guard = FolderGuard.TryAcquire(folder);
        if (guard is null)
            return;

        if (FolderLocker.IsLocked(folder))
        {
            Notifications.Info(Strings.AlreadyProtectedTitle, Strings.DoubleClickToOpen);
            return;
        }
        FolderRules.EnsureCanProtect(folder);

        string password;
        using (var dialog = new SetPasswordForm(DisplayName(folder), note))
        {
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            password = dialog.Password;
        }

        ExplorerWindows.LeaveFolder(folder);
        var result = ProgressForm.Run(Strings.ProtectWithPassword, Strings.Encrypting(DisplayName(folder)), progress =>
        {
            using var key = KeyMaterial.Create(password);
            return FolderLocker.Lock(folder, key, progress);
        });
        AfterLock(folder, result);
    }

    /// <summary>
    /// Double-clicking a locked folder (the Explorer extension cancels the navigation and calls this), “Unlock”
    /// or opening its .doorman file: asks for the password and shows the folder.
    /// </summary>
    public static void Open(string path, IntPtr window = default)
    {
        var folder = PathUtil.Normalize(path);
        if (File.Exists(folder) && folder.EndsWith(FolderLocker.VaultExtension, StringComparison.OrdinalIgnoreCase))
            folder = Path.GetDirectoryName(folder)!;

        if (!FolderLocker.IsLocked(folder))
        {
            // No longer locked (for example, it was unlocked from another window): just open it.
            AppState.Sync();
            if (Directory.Exists(folder))
                ExplorerWindows.Show(folder, window);
            return;
        }
        UnlockSession? session = null;
        if (AgentHost.Current is { } agent && (session = agent.Provider.BeginSession(folder)) is null)
            return; // The password of this folder is already being asked for.
        if (TryUnlock(folder, session))
            ExplorerWindows.Show(folder, window);
    }

    /// <summary>
    /// An app tries to read a file of a locked folder: the password is asked for and, if it is correct, the folder
    /// is unlocked and the app gets the file. No Explorer window is opened.
    /// </summary>
    public static void UnlockForProgram(string folder, UnlockSession session) => TryUnlock(folder, session);

    /// <summary>Asks for the password and decrypts the folder.</summary>
    /// <param name="session">
    /// The apps waiting for some file of the folder (only in the agent). It is closed at the end, whether the
    /// folder was unlocked or not.
    /// </param>
    /// <returns><c>true</c> if the folder is now unlocked.</returns>
    static bool TryUnlock(string folder, UnlockSession? session)
    {
        bool unlocked = false;
        try
        {
            using var guard = FolderGuard.TryAcquire(folder);
            if (guard is null || !FolderLocker.IsLocked(folder))
                return false;

            KeyMaterial key;
            using (var dialog = new UnlockForm(DisplayName(folder), password => FolderLocker.OpenKey(folder, password)))
            {
                if (dialog.ShowDialog() != DialogResult.OK || dialog.Key is null)
                    return false;
                key = dialog.Key;
            }

            using (key)
            {
                ProgressForm.Run("DoorMan", Strings.Decrypting(DisplayName(folder)), progress =>
                {
                    FolderLocker.Unlock(folder, key, progress, session is null ? null : session.Extracted);
                    return true;
                });
                AppState.MarkUnlocked(folder, key);
            }
            unlocked = true;
            return true;
        }
        finally
        {
            session?.Finish(unlocked);
        }
    }

    /// <summary>“Lock”: uses the same password, kept while the folder is unlocked.</summary>
    public static void Relock(string path)
    {
        var folder = PathUtil.Normalize(path);
        KeyMaterial? key;
        using (var guard = FolderGuard.TryAcquire(folder))
        {
            if (guard is null)
                return;
            if (FolderLocker.IsLocked(folder))
            {
                Notifications.Info(Strings.AlreadyLockedTitle, Strings.DoubleClickToOpen);
                return;
            }
            FolderRules.EnsureCanProtect(folder);

            key = AppState.TryLoadKey(folder);
            if (key is not null)
            {
                using (key)
                {
                    ExplorerWindows.LeaveFolder(folder);
                    var result = ProgressForm.Run(Strings.LockVerb, Strings.Encrypting(DisplayName(folder)),
                        progress => FolderLocker.Lock(folder, key, progress));
                    AfterLock(folder, result);
                }
                return;
            }
        }

        // Without the stored key (another Windows user, the folder was moved…) a password has to be chosen.
        Protect(folder, Strings.PreviousPasswordNotFound);
    }

    static void AfterLock(string folder, LockResult result)
    {
        AppState.MarkLocked(folder);
        AgentHost.Current?.Watch(folder, result.InUse);
        ExplorerWindows.RefreshParentViews(folder);
        QuickAccess.Refresh(folder);
        ReportLocked(folder, result);
    }

    /// <summary>
    /// If everything went well nothing is shown: the folder icon already says it is locked. There is only a
    /// warning when files were left open or could not be removed.
    /// </summary>
    static void ReportLocked(string folder, LockResult result)
    {
        if (result.NotDeleted.Count > 0)
            Notifications.Warning(Strings.NotDeletedTitle, Strings.NotDeletedText(DisplayName(folder), Describe(result.NotDeleted)));
        else if (result.InUse.Count > 0)
            Notifications.Warning(Strings.OpenFilesTitle, Strings.OpenFilesText(DisplayName(folder), Describe(result.InUse)));
    }

    static string Describe(IReadOnlyList<string> items)
    {
        var first = Strings.Quoted(Path.GetFileName(items[0].TrimEnd('\\')));
        return items.Count > 1 ? Strings.AndMore(first, items.Count - 1) : first;
    }

    static string DisplayName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    /// <summary>
    /// Keeps two windows from working on the same folder at once (for example, after a repeated double-click),
    /// both inside the agent and across processes.
    /// </summary>
    sealed class FolderGuard : IDisposable
    {
        static readonly HashSet<string> Busy = new(StringComparer.OrdinalIgnoreCase);
        readonly Mutex _mutex;
        readonly string _folder;

        FolderGuard(Mutex mutex, string folder) => (_mutex, _folder) = (mutex, folder);

        public static FolderGuard? TryAcquire(string folder)
        {
            // The mutex is not enough inside the agent: the same thread can re-enter while it shows a window.
            lock (Busy)
                if (!Busy.Add(folder))
                    return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..32];
            var mutex = new Mutex(false, @"Local\DoorMan." + hash);
            try
            {
                if (mutex.WaitOne(0))
                    return new FolderGuard(mutex, folder);
            }
            catch (AbandonedMutexException)
            {
                return new FolderGuard(mutex, folder);
            }
            mutex.Dispose();
            lock (Busy)
                Busy.Remove(folder);
            return null;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            lock (Busy)
                Busy.Remove(_folder);
        }
    }
}
