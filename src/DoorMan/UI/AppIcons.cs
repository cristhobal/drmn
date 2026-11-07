namespace DoorMan;

/// <summary>
/// Icons installed next to the executable in “icons”: an empty locked folder (folder_locked) or one with content
/// (filled_locked). There are two styles, like the system folders: Windows 11 (no prefix, the default) and Windows
/// 10 (win10_*), which Windows Server also uses. Unlocked folders use the normal Windows icon. The app and the menu
/// options use icons of their own for each action (lock_closed, lock_open, shield_lock). If they are missing, the
/// padlock from shell32.dll is used.
/// </summary>
internal static class AppIcons
{
    const int Shell32LockIndex = 47;
    // Windows 11 is version 10.0 with build number 22000 or later.
    static readonly string StylePrefix = Environment.OSVersion.Version.Build >= 22000 ? "" : "win10_";
    static Icon? _lock;

    static string? _iconDir;

    /// <summary>
    /// Once installed, the app keeps the icons in a subfolder of “icons” named after a hash of their content (see
    /// Installer.CopyIcons); in the build folder they are directly in “icons”.
    /// </summary>
    static string IconDir => _iconDir ??= FindIconDir();

    static string FindIconDir()
    {
        var root = Path.Combine(AppContext.BaseDirectory, Shell.Installer.IconsDirName);
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

    /// <summary>“path,index” reference for desktop.ini and the registry.</summary>
    public static string LockedFolderIconResource(bool hasContent)
    {
        var (file, index) = LockedFolderIcon(hasContent);
        return $"{file},{index}";
    }

    /// <summary>
    /// The app icon (a closed padlock): windows, notifications and the encrypted file. Only folders get folder icons.
    /// </summary>
    public static string LockResource => IconResource(AppIconFile);

    /// <summary>Icon of each context menu option, representing its action.</summary>
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

    /// <summary>The icon in the style of this system; if it is missing, the default one.</summary>
    static string IconFile(string name)
    {
        var styled = Path.Combine(IconDir, StylePrefix + name);
        return File.Exists(styled) ? styled : Path.Combine(IconDir, name);
    }
}
