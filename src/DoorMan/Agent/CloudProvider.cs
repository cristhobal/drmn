using System.Text;
using DoorMan.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace DoorMan.Agent;

/// <summary>
/// Serves the requests Windows makes when an app tries to read a file of a locked folder. The first request for a
/// folder opens an <see cref="UnlockSession"/> (which asks for the password); the others, including the ones
/// Windows repeats when the wait goes over 60 seconds, join the same session.
/// </summary>
internal sealed unsafe class CloudProvider
{
    // STATUS_CLOUD_FILE_ACCESS_DENIED: the app gets “Access to the cloud file is denied”.
    static readonly NTSTATUS AccessDenied = new(unchecked((int)0xC000CF18));
    const int TransferChunk = 1 << 20;

    // Services that read files on their own (indexing, antivirus): they must never trigger the password prompt.
    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchProtocolHost.exe", "SearchFilterHost.exe", "SearchIndexer.exe",
        "MsMpEng.exe", "MpDefenderCoreService.exe", "NisSrv.exe",
    };

    // After the password is cancelled, requests for that folder are refused without asking for a while:
    // many apps retry or read several files in a row.
    static readonly TimeSpan CancelCooldown = TimeSpan.FromSeconds(5);

    readonly object _sync = new();
    readonly Dictionary<string, CF_CONNECTION_KEY> _connections = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<long, string> _folderByConnection = [];
    readonly Dictionary<string, UnlockSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<string, UnlockSession> _unlockRequested;
    readonly CF_CALLBACK _fetchData;
    readonly CF_CALLBACK_REGISTRATION[] _callbacks;

    /// <param name="unlockRequested">Asks for the password of the folder (on the UI thread) and closes the session.</param>
    public CloudProvider(Action<string, UnlockSession> unlockRequested)
    {
        _unlockRequested = unlockRequested;
        _fetchData = OnFetchData; // kept alive as long as the provider exists
        _callbacks =
        [
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetchData },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
        ];
    }

    public void Connect(string folder)
    {
        lock (_sync)
        {
            if (_connections.ContainsKey(folder))
                return;
            var hr = PInvoke.CfConnectSyncRoot(folder, _callbacks, null,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
                out var key);
            if (hr.Failed)
                return;
            _connections[folder] = key;
            _folderByConnection[key.Value] = folder;
        }
    }

    public void Disconnect(string folder)
    {
        lock (_sync)
        {
            if (!_connections.Remove(folder, out var key))
                return;
            _folderByConnection.Remove(key.Value);
            PInvoke.CfDisconnectSyncRoot(key);
        }
    }

    /// <summary>
    /// A new unlock session for the folder, so that unlocking with a double-click also serves the apps waiting for
    /// one of its files.
    /// </summary>
    /// <returns>The session, or <c>null</c> if another one is already in progress for the folder (it will serve everyone).</returns>
    public UnlockSession? BeginSession(string folder)
    {
        lock (_sync)
        {
            if (_sessions.ContainsKey(folder))
                return null;
            return _sessions[folder] = new UnlockSession(this, folder);
        }
    }

    internal void EndSession(UnlockSession session, bool unlocked)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(session.Folder, out var current) && current == session)
                _sessions.Remove(session.Folder);
            if (!unlocked)
                _cooldownUntil[session.Folder] = Environment.TickCount64 + (long)CancelCooldown.TotalMilliseconds;
        }
    }

    void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var request = new FetchRequest(
            info->ConnectionKey, info->TransferKey, info->RequestKey,
            Encoding.Unicode.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength),
            parameters->FetchData.RequiredFileOffset, parameters->FetchData.RequiredLength);

        string? process = info->ProcessInfo is not null && info->ProcessInfo->ImagePath.Value is not null
            ? Path.GetFileName(new string(info->ProcessInfo->ImagePath.Value))
            : null;
        UnlockSession? session;
        bool isNew = false;
        lock (_sync)
        {
            if (!_folderByConnection.TryGetValue(info->ConnectionKey.Value, out var folder)
                || (process is not null && IgnoredProcesses.Contains(process)))
            {
                Fail(request);
                return;
            }
            if (!_sessions.TryGetValue(folder, out session))
            {
                if (_cooldownUntil.TryGetValue(folder, out var until) && Environment.TickCount64 < until)
                {
                    Fail(request);
                    return;
                }
                _sessions[folder] = session = new UnlockSession(this, folder);
                isNew = true;
            }
        }
        session.Add(request);
        if (isNew)
            _unlockRequested(session.Folder, session);
        // Windows waits for the content to be handed over, and so does the app that opened the file.
        if (!request.Wait(TimeSpan.FromMinutes(15)))
            Fail(request);
    }

    /// <summary>Hands over the whole file from the decrypted content.</summary>
    internal static void Transfer(FetchRequest request, string staging)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(staging, request.RelativePath));
            if (!PathUtil.IsSameOrInside(path, staging) || !File.Exists(path))
            {
                Fail(request);
                return;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var buffer = new byte[TransferChunk];
            long offset = 0;
            while (true)
            {
                int n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (n == 0)
                    break;
                fixed (byte* data = buffer)
                {
                    var operation = Operation(request);
                    var parameters = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
                    parameters.TransferData.Buffer = data;
                    parameters.TransferData.Offset = offset;
                    parameters.TransferData.Length = n;
                    if (PInvoke.CfExecute(operation, ref parameters).Failed)
                        break; // The app gave up waiting and Windows cancelled the request.
                }
                offset += n;
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(request);
        }
        finally
        {
            request.Complete();
        }
    }

    internal static void Fail(FetchRequest request)
    {
        var operation = Operation(request);
        var parameters = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        parameters.TransferData.CompletionStatus = AccessDenied;
        parameters.TransferData.Offset = request.Offset;
        parameters.TransferData.Length = request.Length;
        PInvoke.CfExecute(operation, ref parameters);
        request.Complete();
    }

    static CF_OPERATION_INFO Operation(FetchRequest request) => new()
    {
        StructSize = (uint)sizeof(CF_OPERATION_INFO),
        Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
        ConnectionKey = request.Connection,
        TransferKey = request.TransferKey,
        RequestKey = request.RequestKey,
    };
}

/// <summary>An app waiting for the content of a file.</summary>
internal sealed class FetchRequest(CF_CONNECTION_KEY connection, long transferKey, long requestKey,
    string relativePath, long offset, long length)
{
    readonly ManualResetEventSlim _done = new();
    int _completed;

    public CF_CONNECTION_KEY Connection { get; } = connection;
    public long TransferKey { get; } = transferKey;
    public long RequestKey { get; } = requestKey;
    public string RelativePath { get; } = relativePath;
    public long Offset { get; } = offset;
    public long Length { get; } = length;
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _done.Set();
    }
}

/// <summary>
/// An unlock in progress for a folder and the apps waiting for some of its files. As soon as the content is
/// decrypted it is handed over to them; if the folder is not unlocked, they are denied.
/// </summary>
internal sealed class UnlockSession(CloudProvider provider, string folder)
{
    readonly object _sync = new();
    readonly List<FetchRequest> _pending = [];
    string? _staging;
    bool _finished;

    public string Folder { get; } = folder;

    public void Add(FetchRequest request)
    {
        string? staging;
        lock (_sync)
        {
            if (_finished)
            {
                CloudProvider.Fail(request);
                return;
            }
            staging = _staging;
            if (staging is null)
                _pending.Add(request);
        }
        if (staging is not null)
            CloudProvider.Transfer(request, staging);
    }

    /// <summary>
    /// The content is now decrypted: it is handed over to whoever is waiting and the folder is disconnected so
    /// that the placeholders can be replaced.
    /// </summary>
    public void Extracted(string staging)
    {
        List<FetchRequest> pending;
        lock (_sync)
        {
            _staging = staging;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var request in pending)
            CloudProvider.Transfer(request, staging);
        provider.Disconnect(Folder);
    }

    public void Finish(bool unlocked)
    {
        List<FetchRequest> pending;
        lock (_sync)
        {
            _finished = true;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var request in pending.Where(r => !r.IsCompleted))
            CloudProvider.Fail(request);
        provider.EndSession(this, unlocked);
        // If it was not unlocked, the folder stays connected (or is connected again if it was disconnected).
        if (!unlocked && FolderLocker.IsLocked(Folder))
            provider.Connect(Folder);
    }
}
