using System.Reflection;

namespace DoorMan.Shell;

/// <summary>
/// The files that the installation puts next to the executable (the Explorer extension, the icons and the Windows
/// 11 context menu package) travel inside DoorMan.exe as “Payload/…” resources (see DoorMan.csproj), so that a
/// single downloaded file is enough. When installing they are extracted to a temporary folder with the same layout
/// they would have as loose files, and the installer copies them from there.
/// </summary>
internal sealed class Payload : IDisposable
{
    const string Prefix = "Payload/";

    public string Directory { get; }

    Payload(string directory) => Directory = directory;

    /// <summary>Extracts the embedded files; null if the executable has none (debug build).</summary>
    public static Payload? Extract()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        if (names.Count == 0)
            return null;

        var root = Path.Combine(Path.GetTempPath(), "DoorMan-" + Guid.NewGuid().ToString("N"));
        var payload = new Payload(root);
        try
        {
            foreach (var name in names)
            {
                var relative = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(target);
                source.CopyTo(file);
            }
            return payload;
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is a temporary folder: if something keeps it open, Windows will clean it up.
        }
    }
}
