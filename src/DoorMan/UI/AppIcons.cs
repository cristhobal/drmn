namespace DoorMan;

/// <summary>
/// Iconos instalados junto al ejecutable en «iconos»: carpeta bloqueada vacía (folder_locked) o con
/// contenido (filled_locked). Hay dos estilos, como las carpetas del sistema: el de Windows 11 (sin
/// prefijo, el predeterminado) y el de Windows 10 (win10_*), que también usa Windows Server. Las
/// desbloqueadas usan el icono normal de Windows. Si faltan, se usa el candado de shell32.dll.
/// </summary>
internal static class AppIcons
{
    const int Shell32LockIndex = 47;
    // Windows 11 es la versión 10.0 con número de compilación 22000 o posterior.
    static readonly string StylePrefix = Environment.OSVersion.Version.Build >= 22000 ? "" : "win10_";
    static Icon? _lock;

    static string? _iconDir;

    /// <summary>
    /// Instalada, la aplicación guarda los iconos en una subcarpeta de «iconos» con la huella de su
    /// contenido (ver Installer.CopyIcons); en la carpeta de compilación están directamente en «iconos».
    /// </summary>
    static string IconDir => _iconDir ??= FindIconDir();

    static string FindIconDir()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "iconos");
        if (!Directory.Exists(root))
            return root;
        return new DirectoryInfo(root).GetDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, "filled_locked.ico")))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.FullName)
            .FirstOrDefault() ?? root;
    }

    public static (string File, int Index) LockedFolderIcon(bool hasContent)
    {
        var file = IconFile(hasContent ? "filled_locked.ico" : "folder_locked.ico");
        return File.Exists(file) ? (file, 0) : (Path.Combine(Environment.SystemDirectory, "shell32.dll"), Shell32LockIndex);
    }

    /// <summary>Referencia «ruta,índice» para desktop.ini y el registro.</summary>
    public static string LockedFolderIconResource(bool hasContent)
    {
        var (file, index) = LockedFolderIcon(hasContent);
        return $"{file},{index}";
    }

    /// <summary>Icono de la aplicación y de sus opciones en el menú contextual.</summary>
    public static string LockResource => LockedFolderIconResource(hasContent: true);

    public static Icon Lock => _lock ??= LoadLock();

    static Icon LoadLock()
    {
        var file = IconFile("filled_locked.ico");
        if (File.Exists(file))
            return new Icon(file);
        return Icon.ExtractIcon(Path.Combine(Environment.SystemDirectory, "shell32.dll"), Shell32LockIndex, 32) ?? SystemIcons.Shield;
    }

    /// <summary>El icono con el estilo de este sistema; si no está, el predeterminado.</summary>
    static string IconFile(string name)
    {
        var styled = Path.Combine(IconDir, StylePrefix + name);
        return File.Exists(styled) ? styled : Path.Combine(IconDir, name);
    }
}
