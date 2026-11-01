using System.Text;
using DoorMan.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace DoorMan.Agent;

/// <summary>
/// Atiende las peticiones de Windows cuando un programa intenta leer un archivo de una carpeta
/// bloqueada. La primera petición de una carpeta abre una <see cref="UnlockSession"/> (que pide la
/// contraseña); las demás, incluidas las que Windows repite si la espera pasa de 60 segundos, se
/// suman a la misma sesión.
/// </summary>
internal sealed unsafe class CloudProvider
{
    // STATUS_CLOUD_FILE_ACCESS_DENIED: el programa recibe «Se denegó el acceso al archivo de nube».
    static readonly NTSTATUS AccessDenied = new(unchecked((int)0xC000CF18));
    const int TransferChunk = 1 << 20;

    // Servicios que leen archivos por su cuenta (indexado, antivirus): nunca deben provocar la petición de contraseña.
    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchProtocolHost.exe", "SearchFilterHost.exe", "SearchIndexer.exe",
        "MsMpEng.exe", "MpDefenderCoreService.exe", "NisSrv.exe",
    };

    // Tras cancelar la contraseña, se rechazan sin preguntar las peticiones de esa carpeta durante un rato:
    // muchos programas reintentan o leen varios archivos seguidos.
    static readonly TimeSpan CancelCooldown = TimeSpan.FromSeconds(5);

    readonly object _sync = new();
    readonly Dictionary<string, CF_CONNECTION_KEY> _connections = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<long, string> _folderByConnection = [];
    readonly Dictionary<string, UnlockSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<string, UnlockSession> _unlockRequested;
    readonly CF_CALLBACK _fetchData;
    readonly CF_CALLBACK_REGISTRATION[] _callbacks;

    /// <param name="unlockRequested">Pide la contraseña de la carpeta (en el hilo de la interfaz) y cierra la sesión.</param>
    public CloudProvider(Action<string, UnlockSession> unlockRequested)
    {
        _unlockRequested = unlockRequested;
        _fetchData = OnFetchData; // referencia viva mientras exista el proveedor
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
    /// Sesión de desbloqueo de la carpeta: la existente (si un programa ya está esperando un archivo)
    /// o una nueva. Así un desbloqueo con doble clic también atiende a esos programas.
    /// </summary>
    /// <returns>La sesión, o <c>null</c> si ya hay otra en curso para la carpeta (que atenderá a todos).</returns>
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
        // Windows espera a que se entregue el contenido; el programa que abrió el archivo, también.
        if (!request.Wait(TimeSpan.FromMinutes(15)))
            Fail(request);
    }

    /// <summary>Entrega el archivo completo desde el contenido descifrado.</summary>
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
                        break; // El programa se cansó de esperar y Windows canceló la petición.
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

/// <summary>Un programa esperando el contenido de un archivo.</summary>
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
/// Un desbloqueo en curso de una carpeta y los programas que esperan alguno de sus archivos.
/// En cuanto el contenido está descifrado se les entrega; si no se desbloquea, se les deniega.
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
    /// El contenido ya está descifrado: se entrega a quienes esperan y la carpeta se desconecta para
    /// poder sustituir los marcadores.
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
        // Si no se desbloqueó, la carpeta sigue conectada (o se vuelve a conectar si se desconectó).
        if (!unlocked && FolderLocker.IsLocked(Folder))
            provider.Connect(Folder);
    }
}
