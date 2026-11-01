using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace DoorMan.Core;

internal enum PlaceholderState { None, Hydrated, Dehydrated }

/// <summary>
/// Archivos marcadores con la API Cloud Files de Windows (la de OneDrive «archivos a petición").
/// Una carpeta bloqueada se registra como raíz de sincronización y cada archivo queda como un
/// marcador sin contenido: conserva nombre, tamaño y fechas, pero sus datos solo están en el
/// archivo cifrado. Cuando cualquier programa intenta leerlo, Windows se los pide a DoorMan
/// (ver <see cref="Agent.CloudProvider"/>), que pide la contraseña.
/// </summary>
internal static class CloudFiles
{
    static readonly Guid ProviderId = new("5b0c6d0e-9a3f-4c1e-8d2b-7e4f1a6c3d90");
    const string ProviderName = "DoorMan";

    const uint FileReadData = 0x0001;
    const uint FileWriteData = 0x0002;
    const uint FileReadAttributes = 0x0080;
    const uint FileWriteAttributes = 0x0100;

    /// <summary>Registra la carpeta como raíz de sincronización de DoorMan.</summary>
    /// <returns><c>false</c> si el volumen no lo admite (no es NTFS, es de red…) o la carpeta ya está dentro de otra raíz (OneDrive…).</returns>
    public static unsafe bool TryRegister(string folder)
    {
        var identity = Encoding.Unicode.GetBytes(folder);
        var version = typeof(CloudFiles).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        fixed (byte* id = identity)
        fixed (char* name = ProviderName)
        fixed (char* ver = version)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = name,
                ProviderVersion = ver,
                SyncRootIdentity = id,
                SyncRootIdentityLength = (uint)identity.Length,
                ProviderId = ProviderId,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                // El contenido se entrega entero y DoorMan crea todos los marcadores por adelantado.
                Hydration = new() { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL },
                Population = new() { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_NONE,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            };
            return PInvoke.CfRegisterSyncRoot(folder, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE).Succeeded;
        }
    }

    /// <summary>
    /// Conexión mínima, sin atender peticiones. Windows solo convierte del todo en marcadores (y les
    /// quita el contenido) los archivos de una raíz con un proveedor conectado.
    /// </summary>
    /// <returns><c>null</c> si no se puede conectar (p. ej. el agente ya está conectado a esa carpeta).</returns>
    public static unsafe IDisposable? TryConnectForConversion(string folder)
    {
        CF_CALLBACK_REGISTRATION[] none = [new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE }];
        return PInvoke.CfConnectSyncRoot(folder, none, null, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out var key).Succeeded
            ? new Disconnector(key)
            : null;
    }

    sealed class Disconnector(CF_CONNECTION_KEY key) : IDisposable
    {
        public void Dispose() => PInvoke.CfDisconnectSyncRoot(key);
    }

    /// <summary>Da de baja la raíz. Convierte en normales las carpetas y los marcadores que ya tengan su contenido.</summary>
    public static void Unregister(string folder) => PInvoke.CfUnregisterSyncRoot(folder);

    /// <summary>¿Es la carpeta una raíz de sincronización de DoorMan?</summary>
    public static unsafe bool IsSyncRoot(string folder)
    {
        var buffer = new byte[sizeof(CF_SYNC_ROOT_BASIC_INFO) + 4096];
        fixed (byte* p = buffer)
        {
            if (PInvoke.CfGetSyncRootInfoByPath(folder, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_BASIC, p, (uint)buffer.Length).Failed)
                return false;
            // La consulta también responde para cualquier carpeta dentro de una raíz: se comprueba que sea la propia.
            var path = PathUtil.Normalize(folder);
            var info = (CF_SYNC_ROOT_BASIC_INFO*)p;
            return info->SyncRootFileId == FileId(path);
        }
    }

    /// <summary>
    /// Convierte un archivo o una carpeta en marcador. A los archivos se les quita el contenido.
    /// </summary>
    /// <returns><c>false</c> si el archivo está abierto por otro programa y no se le ha podido quitar el contenido
    /// (queda como marcador con contenido; <see cref="TryDehydrate"/> lo reintenta).</returns>
    public static bool ConvertToPlaceholder(string path, string relativePath, bool isDirectory)
    {
        // Un archivo de solo lectura no se puede abrir para quitarle el contenido: se le quita el atributo mientras tanto.
        var attributes = File.GetAttributes(path);
        bool readOnly = !isDirectory && attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            return Convert(path, relativePath, isDirectory);
        }
        finally
        {
            if (readOnly)
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    static bool Convert(string path, string relativePath, bool isDirectory)
    {
        var identity = Encoding.Unicode.GetBytes(relativePath);
        using (var handle = Open(path, FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes, isDirectory, exclusive: true))
        {
            if (!handle.IsInvalid)
            {
                var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
                if (!isDirectory)
                    flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_DEHYDRATE;
                PInvoke.CfConvertToPlaceholder(handle, identity, flags).ThrowOnFailure();
                return true;
            }
        }
        // En uso: se convierte sin quitarle el contenido, con un acceso que no choca con quien lo tiene abierto.
        using (var handle = Open(path, FileReadAttributes | FileWriteAttributes, isDirectory, exclusive: false))
        {
            if (handle.IsInvalid)
                throw new IOException($"No se puede acceder a «{relativePath}».", Marshal.GetHRForLastWin32Error());
            PInvoke.CfConvertToPlaceholder(handle, identity, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC).ThrowOnFailure();
        }
        return isDirectory;
    }

    /// <summary>Quita el contenido de un marcador que aún lo tiene (p. ej. estaba abierto al bloquear).</summary>
    public static bool TryDehydrate(string path)
    {
        using var handle = Open(path, FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes, isDirectory: false, exclusive: true);
        return !handle.IsInvalid && PInvoke.CfDehydratePlaceholder(handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE).Succeeded;
    }

    /// <summary>
    /// Convierte un marcador con contenido en un archivo normal. Funciona aunque otro programa lo
    /// tenga abierto, porque solo pide acceso a los atributos.
    /// </summary>
    public static bool TryRevert(string path, bool isDirectory)
    {
        using var handle = Open(path, FileReadAttributes | FileWriteAttributes, isDirectory, exclusive: false);
        return !handle.IsInvalid && PInvoke.CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE).Succeeded;
    }

    /// <summary>
    /// Estado del archivo según Windows. No vale mirar los atributos: sin un proveedor conectado,
    /// Windows los muestra como los de un archivo normal.
    /// </summary>
    public static unsafe PlaceholderState GetState(string path)
    {
        using var handle = Open(path, FileReadAttributes, isDirectory: false, exclusive: false);
        if (handle.IsInvalid)
            throw new IOException($"No se puede acceder a «{path}».", Marshal.GetHRForLastWin32Error());
        var buffer = new byte[sizeof(CF_PLACEHOLDER_STANDARD_INFO) + 4096];
        fixed (byte* p = buffer)
        {
            bool added = false;
            handle.DangerousAddRef(ref added);
            try
            {
                if (PInvoke.CfGetPlaceholderInfo((HANDLE)handle.DangerousGetHandle(), CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD,
                        p, (uint)buffer.Length, null).Failed)
                    return PlaceholderState.None;
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
            var info = (CF_PLACEHOLDER_STANDARD_INFO*)p;
            return info->OnDiskDataSize < new FileInfo(path).Length ? PlaceholderState.Dehydrated : PlaceholderState.Hydrated;
        }
    }

    static SafeFileHandle Open(string path, uint access, bool isDirectory, bool exclusive) =>
        PInvoke.CreateFile(path, access,
            exclusive ? FILE_SHARE_MODE.FILE_SHARE_NONE
                : FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            // Abrir un marcador no pide su contenido; solo leerlo.
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            isDirectory ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS : 0,
            null);

    static long FileId(string path)
    {
        using var handle = Open(path, FileReadAttributes, isDirectory: true, exclusive: false);
        if (handle.IsInvalid)
            return -1;
        return GetFileInformationByHandle(handle, out var info) ? ((long)info.FileIndexHigh << 32) | info.FileIndexLow : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
