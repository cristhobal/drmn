using System.Runtime.InteropServices;
using System.Text;
using DoorMan.Crypto;
using DoorMan.Shell;

namespace DoorMan.Core;

/// <param name="NotDeleted">Items still unencrypted because they could not be deleted.</param>
/// <param name="InUse">Files open while locking: they stay readable until they are closed (see <see cref="CloudFiles.TryDehydrate"/>).</param>
internal sealed record LockResult(IReadOnlyList<string> NotDeleted, IReadOnlyList<string> InUse);

/// <summary>
/// Locking and unlocking folders. All the content is stored in the encrypted file <see cref="VaultFileName"/> and a
/// desktop.ini gives the folder its padlock icon. Files are left as placeholders without content
/// (<see cref="CloudFiles"/>), so any app that tries to open one makes DoorMan ask for the password. If the volume
/// does not support placeholders, the originals are deleted and the folder only holds the encrypted file.
/// </summary>
internal static class FolderLocker
{
    public const string VaultExtension = ".doorman";
    public const string VaultFileName = "Unlock folder" + VaultExtension;
    /// <summary>Name of the encrypted file in folders locked by versions before 3.0. Never change it.</summary>
    public const string LegacyVaultFileName = "Desbloquear carpeta" + VaultExtension;
    const string TempFileName = ".doorman.tmp";
    const string StagingDirName = ".doorman-unlocking";
    const string LegacyStagingDirName = ".doorman-desbloqueo";
    const string DesktopIni = "desktop.ini";
    const string DesktopIniMarker = "; DoorMan";
    // Markers that versions before 3.0 wrote in the desktop.ini of unlocked folders. Never change them.
    const string LegacyUnlockedMarker = DesktopIniMarker + " desbloqueada";
    const string LegacyReadOnlyAddedMarker = "; DoorMan-solo-lectura";
    // Root record (1 + 1 + 4 + 8 + 8 bytes) + end record (1): the size of an empty folder.
    const int EmptyArchiveSize = 23;

    /// <summary>Tests only: lock without placeholders, as on volumes that do not support them.</summary>
    internal static bool UsePlaceholders { get; set; } = true;

    /// <summary>The encrypted file of the folder: its current name or, if the folder was locked by an earlier version, the old one.</summary>
    static string VaultPath(string folder)
    {
        var current = Path.Combine(folder, VaultFileName);
        if (File.Exists(current))
            return current;
        var legacy = Path.Combine(folder, LegacyVaultFileName);
        return File.Exists(legacy) ? legacy : current;
    }

    public static bool IsLocked(string folder) => File.Exists(VaultPath(folder));

    /// <summary>
    /// Encrypts the content of the folder. The encrypted file is written and fully verified before anything is
    /// deleted; if something fails before that point, the folder is left untouched.
    /// </summary>
    public static LockResult Lock(string folder, KeyMaterial key, IProgress<ProgressInfo>? progress = null)
    {
        if (!Directory.Exists(folder))
            throw new UserFacingException(Strings.FolderDoesNotExist);
        if (IsLocked(folder))
            throw new UserFacingException(Strings.FolderAlreadyProtected);

        RemoveUnlockedAppearance(folder);
        var tmp = Path.Combine(folder, TempFileName);
        DeleteFile(tmp);
        var scan = FolderScan.Run(folder, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TempFileName, StagingDirName, LegacyStagingDirName,
        });
        var tracker = new ProgressTracker(progress, scan.TotalBytes * 2);

        try
        {
            List<byte[]> hashes;
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetAttributes(tmp, FileAttributes.Hidden);
                using var enc = VaultFile.OpenWrite(fs, key);
                hashes = FolderArchive.Pack(scan, enc, tracker);
                enc.Complete();
                fs.Flush(flushToDisk: true);
            }

            tracker.SetItem(Strings.CheckingEncryptedFile);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.SequentialScan))
            {
                var header = VaultFile.ReadHeader(fs);
                using var dec = VaultFile.OpenRead(fs, header, key);
                FolderArchive.Verify(dec, scan, hashes, tracker);
            }
        }
        catch
        {
            TryDeleteFile(tmp);
            throw;
        }

        File.Move(tmp, Path.Combine(folder, VaultFileName));
        HideVault(folder);

        tracker.SetItem(Strings.RemovingUnencryptedFiles);
        // A sync root left registered (for example, after a power cut) is unregistered first.
        CloudFiles.Unregister(folder);
        LockResult? result = null;
        if (UsePlaceholders && CloudFiles.TryRegister(folder))
        {
            using var connection = CloudFiles.TryConnectForConversion(folder);
            if (connection is not null)
                result = ConvertToPlaceholders(scan);
            else
                CloudFiles.Unregister(folder);
        }
        result ??= new LockResult(DeleteOriginals(scan), []);

        WriteDesktopIni(folder, hasContent: scan.Entries.Count > 0);
        File.SetAttributes(folder, FolderArchive.Restorable(scan.RootEntry.Attributes) | FileAttributes.ReadOnly);
        ShellNotify.ItemChanged(folder);
        return result;
    }

    /// <summary>
    /// Leaves each file as a placeholder without content. If one cannot be converted, it is deleted as in the mode
    /// without placeholders.
    /// </summary>
    static LockResult ConvertToPlaceholders(FolderScan scan)
    {
        var notDeleted = new List<string>();
        var inUse = new List<string>();
        foreach (var e in scan.Entries.Where(e => e.Kind == EntryKind.Directory))
        {
            try
            {
                CloudFiles.ConvertToPlaceholder(Path.Combine(scan.Root, e.RelativePath), e.RelativePath, isDirectory: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                // A normal folder inside the sync root works too.
            }
        }
        foreach (var e in scan.Entries.Where(e => e.Kind == EntryKind.File))
        {
            var path = Path.Combine(scan.Root, e.RelativePath);
            try
            {
                if (!CloudFiles.ConvertToPlaceholder(path, e.RelativePath, isDirectory: false))
                    inUse.Add(e.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                try
                {
                    DeleteFile(path);
                }
                catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
                {
                    notDeleted.Add(e.RelativePath);
                }
            }
        }
        return new LockResult(notDeleted, inUse);
    }

    /// <summary>Hides the encrypted file as a system file.</summary>
    public static void HideVault(string folder)
    {
        var vault = VaultPath(folder);
        const FileAttributes hidden = FileAttributes.Hidden | FileAttributes.System;
        try
        {
            if (File.Exists(vault) && (File.GetAttributes(vault) & hidden) != hidden)
                File.SetAttributes(vault, hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads the header and checks the password without touching the folder.</summary>
    /// <exception cref="WrongPasswordException" />
    public static KeyMaterial OpenKey(string folder, string password)
    {
        using var fs = new FileStream(VaultPath(folder), FileMode.Open, FileAccess.Read, FileShare.Read);
        return KeyMaterial.Open(VaultFile.GetEnvelope(VaultFile.ReadHeader(fs)), password);
    }

    /// <summary>
    /// Decrypts the content. Everything is first decrypted into a hidden folder inside the folder itself; if that
    /// fails, the folder stays locked and unchanged. Then each file takes the place of its placeholder.
    /// </summary>
    /// <param name="extracted">
    /// Called with the folder holding the decrypted content before it is moved into place. The agent uses it to
    /// hand over the files some app is waiting for and to disconnect from the folder.
    /// </param>
    public static void Unlock(string folder, KeyMaterial key, IProgress<ProgressInfo>? progress = null, Action<string>? extracted = null)
    {
        var vault = VaultPath(folder);
        var staging = Path.Combine(folder, StagingDirName);
        ArchiveEntry root;

        DeleteTree(staging);
        DeleteTree(Path.Combine(folder, LegacyStagingDirName));
        try
        {
            var dir = Directory.CreateDirectory(staging);
            dir.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            using var fs = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var header = VaultFile.ReadHeader(fs);
            using var dec = VaultFile.OpenRead(fs, header, key);
            root = FolderArchive.Extract(dec, staging, new ProgressTracker(progress, fs.Length), []);
        }
        catch
        {
            TryDeleteTree(staging);
            throw;
        }

        extracted?.Invoke(staging);
        RemoveDesktopIni(folder);
        bool syncRoot = CloudFiles.IsSyncRoot(folder);
        MoveIntoPlace(staging, folder);
        if (syncRoot)
        {
            RemoveLeftoverPlaceholders(folder);
            CloudFiles.Unregister(folder);
        }
        File.Delete(vault);
        DeleteTree(staging);
        File.SetAttributes(folder, FolderArchive.Restorable(root.Attributes));
        ShellNotify.ItemChanged(folder);
    }

    /// <summary>Puts the decrypted content in the folder, replacing the placeholders.</summary>
    static void MoveIntoPlace(string staging, string folder)
    {
        var dirs = new List<(string Path, FileAttributes Attributes, DateTime Created, DateTime Written)>();
        void Walk(DirectoryInfo source, string target)
        {
            foreach (var item in source.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                var destination = Path.Combine(target, item.Name);
                if (item is DirectoryInfo sub)
                {
                    if (File.Exists(destination))
                        destination = UniquePath(destination);
                    // The dates are taken before emptying it, which changes them.
                    dirs.Add((destination, sub.Attributes, sub.CreationTimeUtc, sub.LastWriteTimeUtc));
                    Directory.CreateDirectory(destination);
                    Walk(sub, destination);
                }
                else
                {
                    MoveFileIntoPlace(item.FullName, destination);
                }
            }
        }
        Walk(new DirectoryInfo(staging), folder);
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var (path, attributes, created, written) = dirs[i];
            Directory.SetCreationTimeUtc(path, created);
            Directory.SetLastWriteTimeUtc(path, written);
            File.SetAttributes(path, FolderArchive.Restorable(attributes));
        }
    }

    static void MoveFileIntoPlace(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            destination = UniquePath(destination);
        }
        else if (File.Exists(destination))
        {
            switch (CloudFiles.GetState(destination))
            {
                case PlaceholderState.Dehydrated:
                    DeleteFile(destination);
                    break;
                case PlaceholderState.Hydrated when CloudFiles.TryRevert(destination, isDirectory: false):
                    // An app already got this content (or it was open while locking): it is the same file.
                    DeleteFile(source);
                    return;
                default:
                    // A file created while the folder was locked: both are kept.
                    destination = UniquePath(destination);
                    break;
            }
        }
        File.Move(source, destination);
    }

    /// <summary>
    /// Placeholders that match nothing in the encrypted file (for example, they were renamed while the folder was
    /// locked): their content is already in place under the original name.
    /// </summary>
    static void RemoveLeftoverPlaceholders(string folder)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            try
            {
                switch (CloudFiles.GetState(path))
                {
                    case PlaceholderState.Dehydrated:
                        DeleteFile(path);
                        break;
                    case PlaceholderState.Hydrated:
                        CloudFiles.TryRevert(path, isDirectory: false);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static List<string> DeleteOriginals(FolderScan scan)
    {
        var failed = new List<string>();
        foreach (var e in scan.Entries.Where(e => e.Kind == EntryKind.File))
        {
            try
            {
                DeleteFile(Path.Combine(scan.Root, e.RelativePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(e.RelativePath);
            }
        }
        // In reverse order: subfolders come after their parent folder.
        for (int i = scan.Entries.Count - 1; i >= 0; i--)
        {
            var e = scan.Entries[i];
            if (e.Kind != EntryKind.Directory)
                continue;
            var path = Path.Combine(scan.Root, e.RelativePath);
            try
            {
                if (Directory.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    Directory.Delete(path, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(e.RelativePath + "\\");
            }
        }
        return failed;
    }

    /// <summary>
    /// Brings the appearance of a locked folder up to date (for example, one locked by an earlier version):
    /// desktop.ini with the extension that asks for the password, the current icon and a hidden encrypted file.
    /// </summary>
    public static void RefreshLockedAppearance(string folder)
    {
        if (!IsLocked(folder))
            return;
        HideVault(folder);
        var path = Path.Combine(folder, DesktopIni);
        try
        {
            bool hasContent = VaultHasContent(folder);
            if (File.Exists(path) && File.ReadAllText(path) is var text && text.Contains(DesktopIniMarker)
                && (!text.Contains($"CLSID={ShellIntegration.LockedClsid(hasContent)}")
                    || !text.Contains($"IconResource={AppIcons.LockedFolderIconResource(hasContent)}")))
                WriteDesktopIni(folder, hasContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Leaves the folder with the normal Windows icon. Earlier versions gave unlocked folders an icon of their own:
    /// it is removed before locking, when DoorMan stops managing the folder and when syncing. Only a desktop.ini
    /// written by DoorMan is touched.
    /// </summary>
    public static void RemoveUnlockedAppearance(string folder)
    {
        var path = Path.Combine(folder, DesktopIni);
        try
        {
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            if (!text.Contains(LegacyUnlockedMarker))
                return;
            ShellNotify.ClearFolderIcon(folder);
            DeleteFile(path);
            if (text.Contains(LegacyReadOnlyAddedMarker))
            {
                var dir = new DirectoryInfo(folder);
                dir.Attributes &= ~FileAttributes.ReadOnly;
            }
            ShellNotify.ItemChanged(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Did the folder have anything inside when it was locked? It follows from the size of the encrypted file.</summary>
    static bool VaultHasContent(string folder)
    {
        var vault = new FileInfo(VaultPath(folder));
        return !vault.Exists || vault.Length > VaultFile.HeaderSize + ChunkFormat.HeaderSize + ChunkFormat.TagSize + EmptyArchiveSize;
    }

    /// <summary>
    /// desktop.ini of a locked folder. The CLSID makes Explorer open it through the extension, which asks for the
    /// password before showing it. The icon is written with the shell function so that Explorer updates it at once.
    /// </summary>
    static void WriteDesktopIni(string folder, bool hasContent)
    {
        var path = Path.Combine(folder, DesktopIni);
        DeleteFile(path);
        File.WriteAllText(path,
            "[.ShellClassInfo]\r\n" +
            $"CLSID={ShellIntegration.LockedClsid(hasContent)}\r\n" +
            DesktopIniMarker + "\r\n",
            Encoding.Unicode);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
        ShellNotify.SetFolderIcon(folder, AppIcons.LockedFolderIcon(hasContent), Strings.ProtectedFolderType);
    }

    static bool RemoveDesktopIni(string folder)
    {
        var path = Path.Combine(folder, DesktopIni);
        if (!File.Exists(path) || !File.ReadAllText(path).Contains(DesktopIniMarker))
            return false;
        // Unlocked, it goes back to the normal Windows icon; without this Explorer would show the cached one.
        ShellNotify.ClearFolderIcon(folder);
        DeleteFile(path);
        return true;
    }

    static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true };
        foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            e.Attributes = e is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
        new DirectoryInfo(path).Attributes = FileAttributes.Directory;
        Directory.Delete(path, recursive: true);
    }

    static void TryDeleteTree(string path)
    {
        try
        {
            DeleteTree(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void TryDeleteFile(string path)
    {
        try
        {
            DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
