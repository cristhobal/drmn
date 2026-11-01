using System.Text;
using DoorMan.Crypto;
using DoorMan.Shell;

namespace DoorMan.Core;

internal sealed record LockResult(IReadOnlyList<string> NotDeleted);

/// <summary>
/// Bloqueo y desbloqueo de carpetas. Una carpeta bloqueada solo contiene el archivo cifrado
/// <see cref="VaultFileName"/> y un desktop.ini que le pone el icono de candado.
/// </summary>
internal static class FolderLocker
{
    public const string VaultExtension = ".doorman";
    public const string VaultFileName = "Desbloquear carpeta" + VaultExtension;
    const string TempFileName = ".carpeta-protegida.tmp";
    const string DesktopIni = "desktop.ini";
    const string DesktopIniMarker = "; DoorMan";
    const string UnlockedMarker = DesktopIniMarker + " desbloqueada";
    const string ReadOnlyAddedMarker = "; DoorMan-solo-lectura";
    // Registro raíz (1 + 1 + 4 + 8 + 8 bytes) + registro final (1): lo que ocupa una carpeta vacía.
    const int EmptyArchiveSize = 23;

    public static bool IsLocked(string folder) => File.Exists(Path.Combine(folder, VaultFileName));

    /// <summary>
    /// Cifra el contenido de la carpeta. El archivo cifrado se escribe y se verifica por completo
    /// antes de borrar nada; si algo falla antes de ese punto, la carpeta queda intacta.
    /// </summary>
    public static LockResult Lock(string folder, KeyMaterial key, IProgress<ProgressInfo>? progress = null)
    {
        if (!Directory.Exists(folder))
            throw new UserFacingException("La carpeta no existe.");
        if (IsLocked(folder))
            throw new UserFacingException("La carpeta ya está protegida.");

        RemoveUnlockedAppearance(folder);
        var tmp = Path.Combine(folder, TempFileName);
        DeleteFile(tmp);
        var scan = FolderScan.Run(folder, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TempFileName });
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

            tracker.SetItem("Comprobando el archivo cifrado…");
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

        var vault = Path.Combine(folder, VaultFileName);
        File.Move(tmp, vault);
        HideVault(folder);

        tracker.SetItem("Eliminando los archivos sin cifrar…");
        var notDeleted = DeleteOriginals(scan);

        WriteDesktopIni(folder, hasContent: scan.Entries.Count > 0);
        File.SetAttributes(folder, FolderArchive.Restorable(scan.RootEntry.Attributes) | FileAttributes.ReadOnly);
        ShellNotify.ItemChanged(folder);
        return new LockResult(notDeleted);
    }

    /// <summary>Oculta el archivo cifrado como archivo de sistema.</summary>
    public static void HideVault(string folder)
    {
        var vault = Path.Combine(folder, VaultFileName);
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

    /// <summary>Lee la cabecera y comprueba la contraseña sin tocar la carpeta.</summary>
    /// <exception cref="WrongPasswordException" />
    public static KeyMaterial OpenKey(string folder, string password)
    {
        using var fs = new FileStream(Path.Combine(folder, VaultFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        return KeyMaterial.Open(VaultFile.GetEnvelope(VaultFile.ReadHeader(fs)), password);
    }

    /// <summary>Descifra el contenido en la carpeta. Si falla, deshace lo restaurado y la deja bloqueada.</summary>
    public static void Unlock(string folder, KeyMaterial key, IProgress<ProgressInfo>? progress = null)
    {
        var vault = Path.Combine(folder, VaultFileName);
        var created = new List<string>();
        ArchiveEntry root;

        using (var fs = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        {
            var header = VaultFile.ReadHeader(fs);
            using var dec = VaultFile.OpenRead(fs, header, key);
            bool hadIni = RemoveDesktopIni(folder);
            try
            {
                root = FolderArchive.Extract(dec, folder, new ProgressTracker(progress, fs.Length), created);
            }
            catch
            {
                Rollback(created);
                if (hadIni)
                    WriteDesktopIni(folder, VaultHasContent(folder));
                throw;
            }
        }

        File.Delete(vault);
        File.SetAttributes(folder, FolderArchive.Restorable(root.Attributes));
        ShellNotify.ItemChanged(folder);
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
        // En orden inverso: las subcarpetas aparecen después de su carpeta padre.
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

    static void Rollback(List<string> created)
    {
        for (int i = created.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(created[i]))
                    DeleteFile(created[i]);
                else if (Directory.Exists(created[i]))
                {
                    File.SetAttributes(created[i], FileAttributes.Normal);
                    Directory.Delete(created[i], recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo que no se pueda borrar se queda; la bóveda sigue intacta.
            }
        }
    }

    /// <summary>
    /// Pone al día la apariencia de una carpeta bloqueada (p. ej. con una versión anterior):
    /// desktop.ini con la extensión que pide la contraseña, icono actual y archivo cifrado oculto.
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
                && (!text.Contains($"CLSID={ShellIntegration.LockedFolderClsid}")
                    || !text.Contains($"IconResource={AppIcons.FolderIconResource(locked: true, hasContent)}")))
                WriteDesktopIni(folder, hasContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Icono de carpeta desbloqueada (vacía o con contenido) para las carpetas gestionadas que están
    /// abiertas. No se toca la carpeta si ya tenía su propio desktop.ini.
    /// </summary>
    public static void ApplyUnlockedAppearance(string folder)
    {
        if (!Directory.Exists(folder) || IsLocked(folder))
            return;
        var path = Path.Combine(folder, DesktopIni);
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (existing is not null && !existing.Contains(UnlockedMarker))
                return;
            bool hasContent = Directory.EnumerateFileSystemEntries(folder)
                .Any(p => !string.Equals(Path.GetFileName(p), DesktopIni, StringComparison.OrdinalIgnoreCase));
            // Windows solo usa desktop.ini si la carpeta es de solo lectura; se recuerda si lo pusimos nosotros.
            bool readOnlyAdded = existing?.Contains(ReadOnlyAddedMarker)
                ?? !new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReadOnly);
            var icon = AppIcons.FolderIcon(locked: false, hasContent);
            if (existing is not null && existing.Contains($"IconResource={icon.File},{icon.Index}"))
                return;
            var content =
                "[.ShellClassInfo]\r\n" +
                UnlockedMarker + "\r\n" +
                (readOnlyAdded ? ReadOnlyAddedMarker + "\r\n" : "");
            DeleteFile(path);
            File.WriteAllText(path, content, Encoding.Unicode);
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
            var dir = new DirectoryInfo(folder);
            dir.Attributes |= FileAttributes.ReadOnly;
            ShellNotify.SetFolderIcon(folder, icon, UnlockedInfoTip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Quita el icono de carpeta desbloqueada (antes de bloquear o al dejar de gestionarla).</summary>
    public static void RemoveUnlockedAppearance(string folder)
    {
        var path = Path.Combine(folder, DesktopIni);
        if (!File.Exists(path))
            return;
        var text = File.ReadAllText(path);
        if (!text.Contains(UnlockedMarker))
            return;
        DeleteFile(path);
        if (text.Contains(ReadOnlyAddedMarker))
        {
            var dir = new DirectoryInfo(folder);
            dir.Attributes &= ~FileAttributes.ReadOnly;
        }
        ShellNotify.ItemChanged(folder);
    }

    /// <summary>¿Tenía la carpeta algo dentro cuando se bloqueó? Se deduce del tamaño del archivo cifrado.</summary>
    static bool VaultHasContent(string folder)
    {
        var vault = new FileInfo(Path.Combine(folder, VaultFileName));
        return !vault.Exists || vault.Length > VaultFile.HeaderSize + ChunkFormat.HeaderSize + ChunkFormat.TagSize + EmptyArchiveSize;
    }

    const string LockedInfoTip = "Carpeta protegida con contraseña.";
    const string UnlockedInfoTip = "Carpeta protegida con contraseña (desbloqueada).";

    /// <summary>
    /// desktop.ini de una carpeta bloqueada. El CLSID hace que el Explorador la abra a través de la
    /// extensión, que pide la contraseña antes de mostrarla. El icono se escribe con la función del
    /// shell para que el Explorador lo actualice al momento.
    /// </summary>
    static void WriteDesktopIni(string folder, bool hasContent)
    {
        var path = Path.Combine(folder, DesktopIni);
        DeleteFile(path);
        File.WriteAllText(path,
            "[.ShellClassInfo]\r\n" +
            $"CLSID={ShellIntegration.LockedFolderClsid}\r\n" +
            DesktopIniMarker + "\r\n",
            Encoding.Unicode);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
        ShellNotify.SetFolderIcon(folder, AppIcons.FolderIcon(locked: true, hasContent), LockedInfoTip);
    }

    static bool RemoveDesktopIni(string folder)
    {
        var path = Path.Combine(folder, DesktopIni);
        if (!File.Exists(path) || !File.ReadAllText(path).Contains(DesktopIniMarker))
            return false;
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
