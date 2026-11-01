using System.Security.Cryptography;
using System.Text;

namespace CarpetaProtegida.Core;

internal enum EntryKind : byte { End = 0, Root = 1, Directory = 2, File = 3 }

internal sealed record ArchiveEntry(
    EntryKind Kind, string RelativePath, FileAttributes Attributes,
    DateTime CreationUtc, DateTime LastWriteUtc, long Length = 0);

/// <summary>Lista de lo que hay en una carpeta, recogida antes de cifrarla.</summary>
internal sealed class FolderScan
{
    public required string Root { get; init; }
    public required ArchiveEntry RootEntry { get; init; }
    public List<ArchiveEntry> Entries { get; } = [];
    public long TotalBytes { get; private set; }

    static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static FolderScan Run(string root, IReadOnlySet<string> excludedRootNames)
    {
        var info = new DirectoryInfo(root);
        var scan = new FolderScan
        {
            Root = root,
            RootEntry = new(EntryKind.Root, "", info.Attributes, info.CreationTimeUtc, info.LastWriteTimeUtc),
        };
        scan.Walk(info, "", excludedRootNames);
        return scan;
    }

    void Walk(DirectoryInfo dir, string rel, IReadOnlySet<string> excluded)
    {
        foreach (var item in dir.EnumerateFileSystemInfos("*", Options).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (rel.Length == 0 && excluded.Contains(item.Name))
                continue;
            var childRel = rel.Length == 0 ? item.Name : rel + '\\' + item.Name;
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UserFacingException(
                    $"La carpeta contiene «{childRel}», que es un enlace simbólico, un punto de unión " +
                    "o un archivo en la nube sin descargar. Este tipo de elementos no se puede proteger.");

            if (item is DirectoryInfo sub)
            {
                Entries.Add(new(EntryKind.Directory, childRel, sub.Attributes, sub.CreationTimeUtc, sub.LastWriteTimeUtc));
                Walk(sub, childRel, excluded);
            }
            else if (item is FileInfo file)
            {
                Entries.Add(new(EntryKind.File, childRel, file.Attributes, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Length));
                TotalBytes += file.Length;
            }
        }
    }
}

/// <summary>
/// Empaqueta una carpeta en un flujo secuencial (que después se cifra) y lo restaura.
/// Cada registro: tipo, ruta relativa, atributos, fechas y, en los archivos, longitud + datos.
/// </summary>
internal static class FolderArchive
{
    const int BufferSize = 1 << 20;
    static readonly UTF8Encoding Utf8 = new(false, true);

    const FileAttributes RestorableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
        FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <returns>El hash SHA-256 de cada archivo, en el orden de <see cref="FolderScan.Entries"/>.</returns>
    public static List<byte[]> Pack(FolderScan scan, Stream output, ProgressTracker? progress)
    {
        var hashes = new List<byte[]>();
        var buffer = new byte[BufferSize];
        using var w = new BinaryWriter(output, Utf8, leaveOpen: true);
        WriteEntry(w, scan.RootEntry);
        foreach (var e in scan.Entries)
        {
            WriteEntry(w, e);
            if (e.Kind != EntryKind.File)
                continue;

            progress?.SetItem(e.RelativePath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // FileShare.Read: nadie puede modificar el archivo mientras se cifra.
            using var fs = new FileStream(Path.Combine(scan.Root, e.RelativePath),
                FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (fs.Length != e.Length)
                throw Changed(e);
            long remaining = e.Length;
            while (remaining > 0)
            {
                int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n == 0)
                    throw Changed(e);
                w.Write(buffer, 0, n);
                hash.AppendData(buffer, 0, n);
                remaining -= n;
                progress?.Advance(n);
            }
            hashes.Add(hash.GetHashAndReset());
        }
        w.Write((byte)EntryKind.End);
        CryptographicOperations.ZeroMemory(buffer);
        return hashes;
    }

    /// <summary>Comprueba que el flujo descifrado contiene exactamente lo que se empaquetó.</summary>
    public static void Verify(Stream input, FolderScan scan, List<byte[]> hashes, ProgressTracker? progress)
    {
        var buffer = new byte[BufferSize];
        using var r = new BinaryReader(input, Utf8, leaveOpen: true);
        if (ReadEntry(r)?.Kind != EntryKind.Root)
            throw VerifyFailed();

        int fileIndex = 0;
        foreach (var expected in scan.Entries)
        {
            var e = ReadEntry(r);
            if (e is null || e.Kind != expected.Kind || e.RelativePath != expected.RelativePath || e.Length != expected.Length)
                throw VerifyFailed();
            if (e.Kind != EntryKind.File)
                continue;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = e.Length;
            while (remaining > 0)
            {
                int n = r.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n == 0)
                    throw VerifyFailed();
                hash.AppendData(buffer, 0, n);
                remaining -= n;
                progress?.Advance(n);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), hashes[fileIndex++]))
                throw VerifyFailed();
        }
        if (ReadEntry(r) is not null || r.Read(buffer, 0, 1) != 0)
            throw VerifyFailed();
        CryptographicOperations.ZeroMemory(buffer);
    }

    /// <summary>
    /// Restaura el contenido en <paramref name="root"/>. Si un nombre ya existe, el restaurado
    /// se guarda como «nombre (2).ext». Las rutas creadas se añaden a <paramref name="created"/>
    /// para poder deshacer la operación si falla a medias.
    /// </summary>
    /// <returns>El registro de la carpeta raíz (sus atributos originales).</returns>
    public static ArchiveEntry Extract(Stream input, string root, ProgressTracker? progress, List<string> created)
    {
        var buffer = new byte[BufferSize];
        var dirs = new List<(string Path, ArchiveEntry Entry)>();
        using var r = new BinaryReader(input, Utf8, leaveOpen: true);
        var rootEntry = ReadEntry(r);
        if (rootEntry?.Kind != EntryKind.Root)
            throw Corrupt();

        while (ReadEntry(r) is { } e)
        {
            var target = ResolveInside(root, e.RelativePath);
            switch (e.Kind)
            {
                case EntryKind.Directory:
                    if (!Directory.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                        created.Add(target);
                    }
                    dirs.Add((target, e));
                    break;

                case EntryKind.File:
                    if (File.Exists(target) || Directory.Exists(target))
                        target = UniquePath(target);
                    progress?.SetItem(e.RelativePath);
                    using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                    {
                        created.Add(target);
                        long remaining = e.Length;
                        while (remaining > 0)
                        {
                            int n = r.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (n == 0)
                                throw Corrupt();
                            fs.Write(buffer, 0, n);
                            remaining -= n;
                            progress?.Advance(n);
                        }
                    }
                    File.SetCreationTimeUtc(target, e.CreationUtc);
                    File.SetLastWriteTimeUtc(target, e.LastWriteUtc);
                    File.SetAttributes(target, Restorable(e.Attributes));
                    break;

                default:
                    throw Corrupt();
            }
        }
        CryptographicOperations.ZeroMemory(buffer);

        // De dentro hacia fuera, para que crear el contenido no altere las fechas ya restauradas.
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var (path, e) = dirs[i];
            Directory.SetCreationTimeUtc(path, e.CreationUtc);
            Directory.SetLastWriteTimeUtc(path, e.LastWriteUtc);
            File.SetAttributes(path, Restorable(e.Attributes));
        }
        return rootEntry;
    }

    public static FileAttributes Restorable(FileAttributes attributes)
    {
        var a = attributes & RestorableAttributes;
        return a == 0 ? FileAttributes.Normal : a;
    }

    static void WriteEntry(BinaryWriter w, ArchiveEntry e)
    {
        w.Write((byte)e.Kind);
        w.Write(e.RelativePath);
        w.Write((int)e.Attributes);
        w.Write(e.CreationUtc.Ticks);
        w.Write(e.LastWriteUtc.Ticks);
        if (e.Kind == EntryKind.File)
            w.Write(e.Length);
    }

    static ArchiveEntry? ReadEntry(BinaryReader r)
    {
        try
        {
            var kind = (EntryKind)r.ReadByte();
            if (kind == EntryKind.End)
                return null;
            if (kind is not (EntryKind.Root or EntryKind.Directory or EntryKind.File))
                throw Corrupt();
            var path = r.ReadString();
            var attributes = (FileAttributes)r.ReadInt32();
            var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var written = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            long length = kind == EntryKind.File ? r.ReadInt64() : 0;
            if (length < 0)
                throw Corrupt();
            return new(kind, path, attributes, created, written, length);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or DecoderFallbackException)
        {
            throw Corrupt();
        }
    }

    static string ResolveInside(string root, string rel)
    {
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Contains(':'))
            throw Corrupt();
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw Corrupt();
        return full;
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

    static IOException Changed(ArchiveEntry e) =>
        new($"El archivo «{e.RelativePath}» cambió mientras se cifraba. Ciérralo e inténtalo de nuevo.");

    static InvalidDataException Corrupt() =>
        new("El archivo cifrado de la carpeta está dañado.");

    static IOException VerifyFailed() =>
        new("La comprobación del archivo cifrado ha fallado. No se ha modificado ningún archivo de la carpeta.");
}
