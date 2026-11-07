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
/// Placeholder files with the Windows Cloud Files API (the one behind OneDrive Files On-Demand). A locked folder is
/// registered as a sync root and each file is left as a placeholder without content: it keeps its name, size and
/// dates, but its data only exists in the encrypted file. When any app tries to read it, Windows asks DoorMan for
/// the data (see <see cref="Agent.CloudProvider"/>), and DoorMan asks for the password.
/// </summary>
internal static class CloudFiles
{
    static readonly Guid ProviderId = new("5b0c6d0e-9a3f-4c1e-8d2b-7e4f1a6c3d90");
    const string ProviderName = "DoorMan";

    const uint FileReadData = 0x0001;
    const uint FileWriteData = 0x0002;
    const uint FileReadAttributes = 0x0080;
    const uint FileWriteAttributes = 0x0100;

    /// <summary>Registers the folder as a DoorMan sync root.</summary>
    /// <returns><c>false</c> if the volume does not support it (not NTFS, network drive…) or the folder is already inside another sync root (OneDrive…).</returns>
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
                // Content is handed over whole and DoorMan creates every placeholder up front.
                Hydration = new() { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL },
                Population = new() { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_NONE,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            };
            return PInvoke.CfRegisterSyncRoot(folder, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE).Succeeded;
        }
    }

    /// <summary>
    /// Minimal connection that serves no requests. Windows only fully converts files into placeholders (and removes
    /// their content) in a sync root with a connected provider.
    /// </summary>
    /// <returns><c>null</c> if it cannot connect (for example, the agent is already connected to that folder).</returns>
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

    /// <summary>Unregisters the sync root. Folders and placeholders that already have their content become normal ones.</summary>
    public static void Unregister(string folder) => PInvoke.CfUnregisterSyncRoot(folder);

    /// <summary>Is the folder a DoorMan sync root?</summary>
    public static unsafe bool IsSyncRoot(string folder)
    {
        var buffer = new byte[sizeof(CF_SYNC_ROOT_BASIC_INFO) + 4096];
        fixed (byte* p = buffer)
        {
            if (PInvoke.CfGetSyncRootInfoByPath(folder, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_BASIC, p, (uint)buffer.Length).Failed)
                return false;
            // The query also answers for any folder inside a sync root: check that it is the root itself.
            var path = PathUtil.Normalize(folder);
            var info = (CF_SYNC_ROOT_BASIC_INFO*)p;
            return info->SyncRootFileId == FileId(path);
        }
    }

    /// <summary>
    /// Converts a file or a folder into a placeholder. Files lose their content.
    /// </summary>
    /// <returns><c>false</c> if the file is open in another app and its content could not be removed (it stays as
    /// a placeholder with content; <see cref="TryDehydrate"/> tries again).</returns>
    public static bool ConvertToPlaceholder(string path, string relativePath, bool isDirectory)
    {
        // A read-only file cannot be opened to remove its content: the attribute is cleared meanwhile.
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
        // In use: it is converted without removing its content, with an access that does not clash with whoever has it open.
        using (var handle = Open(path, FileReadAttributes | FileWriteAttributes, isDirectory, exclusive: false))
        {
            if (handle.IsInvalid)
                throw new IOException(Strings.CannotAccess(relativePath), Marshal.GetHRForLastWin32Error());
            PInvoke.CfConvertToPlaceholder(handle, identity, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC).ThrowOnFailure();
        }
        return isDirectory;
    }

    /// <summary>Removes the content of a placeholder that still has it (for example, it was open while locking).</summary>
    public static bool TryDehydrate(string path)
    {
        using var handle = Open(path, FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes, isDirectory: false, exclusive: true);
        return !handle.IsInvalid && PInvoke.CfDehydratePlaceholder(handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE).Succeeded;
    }

    /// <summary>
    /// Turns a placeholder with content into a normal file. It works even if another app has it open, because it
    /// only asks for access to the attributes.
    /// </summary>
    public static bool TryRevert(string path, bool isDirectory)
    {
        using var handle = Open(path, FileReadAttributes | FileWriteAttributes, isDirectory, exclusive: false);
        return !handle.IsInvalid && PInvoke.CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE).Succeeded;
    }

    /// <summary>
    /// The state of the file according to Windows. Looking at the attributes is not enough: without a connected
    /// provider, Windows shows them as those of a normal file.
    /// </summary>
    public static unsafe PlaceholderState GetState(string path)
    {
        using var handle = Open(path, FileReadAttributes, isDirectory: false, exclusive: false);
        if (handle.IsInvalid)
            throw new IOException(Strings.CannotAccess(path), Marshal.GetHRForLastWin32Error());
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
            // Opening a placeholder does not fetch its content; only reading it does.
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
