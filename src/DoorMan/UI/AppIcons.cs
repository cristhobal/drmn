namespace DoorMan;

/// <summary>
/// Iconos instalados junto al ejecutable en «iconos»: carpeta bloqueada vacía (folder_locked) o con
/// contenido (filled_locked). Hay dos estilos, como las carpetas del sistema: el de Windows 11 (sin
/// prefijo, el predeterminado) y el de Windows 10 (win10_*), que también usa Windows Server. Las
/// desbloqueadas usan el icono normal de Windows. La aplicación y las opciones del menú usan iconos
/// propios de la acción (lock_closed, lock_open, shield_lock; ver herramientas\dibujar-candados.ps1).
/// Si faltan, se usa el candado de shell32.dll.
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

    /// <summary>
    /// Icono de la aplicación (un candado cerrado): ventanas, avisos y el archivo cifrado. Solo las
    /// carpetas llevan iconos de carpeta.
    /// </summary>
    public static string LockResource => IconResource(AppIconFile);

    /// <summary>Icono de cada opción del menú contextual, representativo de la acción.</summary>
    public static string ProtectResource => IconResource("shield_lock.ico");
    public static string RelockResource => IconResource("lock_closed.ico");
    public static string UnlockResource => IconResource("lock_open.ico");

    const string AppIconFile = "lock_closed.ico";

    static string IconResource(string name)
    {
        var file = IconFile(name);
        return File.Exists(file) ? $"{file},0" : $"{Path.Combine(Environment.SystemDirectory, "shell32.dll")},{Shell32LockIndex}";
    }

    public static Icon Lock => _lock ??= LoadLock();

    static Icon LoadLock()
    {
        var file = IconFile(AppIconFile);
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
