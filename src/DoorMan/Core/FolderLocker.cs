using System.Runtime.InteropServices;
using System.Text;
using DoorMan.Crypto;
using DoorMan.Shell;

namespace DoorMan.Core;

/// <param name="NotDeleted">Elementos que siguen sin cifrar porque no se pudieron borrar.</param>
/// <param name="InUse">Archivos abiertos al bloquear: siguen legibles hasta que se cierren (ver <see cref="CloudFiles.TryDehydrate"/>).</param>
internal sealed record LockResult(IReadOnlyList<string> NotDeleted, IReadOnlyList<string> InUse);

/// <summary>
/// Bloqueo y desbloqueo de carpetas. Todo el contenido se guarda en el archivo cifrado
/// <see cref="VaultFileName"/> y un desktop.ini le pone el icono de candado. Los archivos se
/// quedan como marcadores sin contenido (<see cref="CloudFiles"/>), de modo que cualquier programa
/// que intente abrir uno hace que DoorMan pida la contraseña. Si el volumen no admite marcadores,
/// los originales se borran y la carpeta solo contiene el archivo cifrado.
/// </summary>
internal static class FolderLocker
{
    public const string VaultExtension = ".doorman";
    public const string VaultFileName = "Desbloquear carpeta" + VaultExtension;
    const string TempFileName = ".doorman.tmp";
    const string StagingDirName = ".doorman-desbloqueo";
    const string DesktopIni = "desktop.ini";
    const string DesktopIniMarker = "; DoorMan";
    const string UnlockedMarker = DesktopIniMarker + " desbloqueada";
    const string ReadOnlyAddedMarker = "; DoorMan-solo-lectura";
    // Registro raíz (1 + 1 + 4 + 8 + 8 bytes) + registro final (1): lo que ocupa una carpeta vacía.
    const int EmptyArchiveSize = 23;

    /// <summary>Solo para las pruebas: bloquear sin marcadores, como en los volúmenes que no los admiten.</summary>
    internal static bool UsePlaceholders { get; set; } = true;

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
        var scan = FolderScan.Run(folder, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TempFileName, StagingDirName });
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
        // Una raíz que haya quedado registrada (p. ej. tras un corte de luz) se da de baja antes.
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
    /// Deja cada archivo como marcador sin contenido. Si uno no se puede convertir, se borra como en
    /// el modo sin marcadores.
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
                // Una carpeta normal dentro de la raíz también funciona.
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

    /// <summary>
    /// Descifra el contenido. Primero se descifra todo en una carpeta oculta dentro de la propia
    /// carpeta; si eso falla, la carpeta queda bloqueada y sin cambios. Después cada archivo pasa a su
    /// sitio en lugar de su marcador.
    /// </summary>
    /// <param name="extracted">
    /// Se llama con la carpeta del contenido descifrado antes de colocarlo. El agente la usa para
    /// entregar los archivos que algún programa está esperando y desconectarse de la carpeta.
    /// </param>
    public static void Unlock(string folder, KeyMaterial key, IProgress<ProgressInfo>? progress = null, Action<string>? extracted = null)
    {
        var vault = Path.Combine(folder, VaultFileName);
        var staging = Path.Combine(folder, StagingDirName);
        ArchiveEntry root;

        DeleteTree(staging);
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

    /// <summary>Coloca el contenido descifrado en la carpeta, sustituyendo los marcadores.</summary>
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
                    // Las fechas se toman antes de vaciarla, que las cambia.
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
                    // Un programa ya recibió este contenido (o estaba abierto al bloquear): es el mismo archivo.
                    DeleteFile(source);
                    return;
                default:
                    // Un archivo creado mientras la carpeta estaba bloqueada: se conservan los dos.
                    destination = UniquePath(destination);
                    break;
            }
        }
        File.Move(source, destination);
    }

    /// <summary>
    /// Marcadores que no corresponden a nada del archivo cifrado (p. ej. se renombraron mientras la
    /// carpeta estaba bloqueada): su contenido ya está en su sitio con el nombre original.
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
