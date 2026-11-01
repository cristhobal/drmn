namespace CarpetaProtegida;

/// <summary>
/// Iconos instalados junto al ejecutable en «iconos»: carpeta bloqueada o desbloqueada, vacía
/// (folder_*) o con contenido (filled_*). Si faltan, se usa el candado de shell32.dll.
/// </summary>
internal static class AppIcons
{
    const int Shell32LockIndex = 47;
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

    public static (string File, int Index) FolderIcon(bool locked, bool hasContent)
    {
        var file = Path.Combine(IconDir, $"{(hasContent ? "filled" : "folder")}_{(locked ? "locked" : "unlocked")}.ico");
        return File.Exists(file) ? (file, 0) : (Path.Combine(Environment.SystemDirectory, "shell32.dll"), Shell32LockIndex);
    }

    /// <summary>Referencia «ruta,índice» para desktop.ini y el registro.</summary>
    public static string FolderIconResource(bool locked, bool hasContent)
    {
        var (file, index) = FolderIcon(locked, hasContent);
        return $"{file},{index}";
    }

    /// <summary>Icono de la aplicación y de sus opciones en el menú contextual.</summary>
    public static string LockResource => FolderIconResource(locked: true, hasContent: true);

    public static Icon Lock => _lock ??= LoadLock();

    static Icon LoadLock()
    {
        var file = Path.Combine(IconDir, "filled_locked.ico");
        if (File.Exists(file))
            return new Icon(file);
        return Icon.ExtractIcon(Path.Combine(Environment.SystemDirectory, "shell32.dll"), Shell32LockIndex, 32) ?? SystemIcons.Shield;
    }
}
