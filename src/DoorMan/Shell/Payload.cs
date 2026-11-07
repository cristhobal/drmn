using System.Reflection;

namespace DoorMan.Shell;

/// <summary>
/// Los archivos que la instalación pone junto al ejecutable (la extensión del Explorador, los iconos y el
/// paquete del menú de Windows 11) viajan dentro de DoorMan.exe como recursos «Payload/…» (ver DoorMan.csproj),
/// para que baste con descargar un único archivo. Al instalar se extraen a una carpeta temporal con la misma
/// estructura que tendrían sueltos, y el instalador los copia desde ahí.
/// </summary>
internal sealed class Payload : IDisposable
{
    const string Prefix = "Payload/";

    public string Directory { get; }

    Payload(string directory) => Directory = directory;

    /// <summary>Extrae los archivos incluidos; null si el ejecutable no lleva ninguno (compilación de depuración).</summary>
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
            // Es una carpeta temporal: si algo la tiene abierta, Windows la limpiará.
        }
    }
}
