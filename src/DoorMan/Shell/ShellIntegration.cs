using System.Runtime.InteropServices;
using DoorMan.Core;
using Microsoft.Win32;

namespace DoorMan.Shell;

/// <summary>
/// File Explorer integration through the current user's registry (no administrator rights needed):
/// <list type="bullet">
/// <item>“Protect with password” in the context menu of every folder.</item>
/// <item>“Lock”, only on unlocked folders (filtered with AppliesTo).</item>
/// <item>“Unlock”, only on locked folders.</item>
/// <item>The .doorman extension of the encrypted file, which asks for the password when opened.</item>
/// </list>
/// The default verb of folders is never changed: it is a global setting and, on special folders (Pictures,
/// Documents…), it left Explorer unable to open them.
/// AppliesTo compares with System.ParsingPath, because System.ItemPathDisplay is localized
/// (“C:\Usuarios\…\Imágenes”) and does not match the real path.
/// </summary>
internal static class ShellIntegration
{
    const string DirectoryShell = @"Software\Classes\Directory\shell";
    const string ProtectVerb = "DoorMan.Protect";
    const string RelockVerb = "DoorMan.Lock";
    const string OpenVerb = "DoorMan.Unlock";
    const string ProgId = "DoorMan.Vault";
    /// <summary>Registry keys of versions before 3.0, removed when registering.</summary>
    static readonly string[] LegacyKeys =
    [
        $@"{DirectoryShell}\DoorMan.Proteger",
        $@"{DirectoryShell}\DoorMan.Rebloquear",
        $@"{DirectoryShell}\DoorMan.Abrir",
        @"Software\Classes\DoorMan.Boveda",
    ];

    /// <summary>
    /// Class of the native extension (DoorManShell.dll). The desktop.ini of locked folders refers to it, so
    /// Explorer asks for the password before showing their content.
    /// </summary>
    public const string LockedFolderClsid = "{142BC879-C3C7-4D93-B1E6-51CF20CC661E}";
    /// <summary>
    /// The same class for locked folders with content. In large icon views Explorer shows the icon registered for
    /// the class (DefaultIcon) rather than the one in desktop.ini, so each icon needs its own class.
    /// </summary>
    public const string LockedFilledFolderClsid = "{4FB75F15-2E04-47FB-8F21-40CE81A144A5}";
    public const string ExtensionFileName = "DoorManShell.dll";

    public static string LockedClsid(bool hasContent) => hasContent ? LockedFilledFolderClsid : LockedFolderClsid;
    static string ClsidKey(bool hasContent) => @"Software\Classes\CLSID\" + LockedClsid(hasContent);

    static string ExePath => Environment.ProcessPath!;

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key is not null;
        }
    }

    const string SettingsKey = @"Software\DoorMan";
    const string PackagedMenuValue = "PackagedMenu";
    const string LegacyPackagedMenuValue = "MenuWindows11";

    /// <summary>
    /// Whether the Windows 11 context menu package is installed (ContextMenuPackage). Its command shows up in both
    /// menus, the modern one and “Show more options”, so the registry entries would be repeated (see Refresh).
    /// </summary>
    public static bool UsesPackagedMenu
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue(PackagedMenuValue) is 1;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.DeleteValue(LegacyPackagedMenuValue, throwOnMissingValue: false);
            if (value)
                key.SetValue(PackagedMenuValue, 1, RegistryValueKind.DWord);
            else
                key.DeleteValue(PackagedMenuValue, throwOnMissingValue: false);
        }
    }

    public static void Register()
    {
        foreach (var legacy in LegacyKeys)
            Registry.CurrentUser.DeleteSubKeyTree(legacy, throwOnMissingSubKey: false);
        if (!UsesPackagedMenu)
            WriteVerb(ProtectVerb, Strings.ProtectWithPassword, "--protect", AppIcons.ProtectResource, appliesTo: null);

        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FolderLocker.VaultExtension}"))
            ext.SetValue("", ProgId);
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", Strings.ProtectedFolderType);
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue("", AppIcons.LockResource);
            using (var open = prog.CreateSubKey(@"shell\open"))
                open.SetValue("", Strings.OpenProtectedFolder);
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" --open \"%1\"");
        }

        var extension = Path.Combine(Path.GetDirectoryName(ExePath)!, ExtensionFileName);
        if (File.Exists(extension))
        {
            foreach (var hasContent in new[] { false, true })
            {
                using var clsid = Registry.CurrentUser.CreateSubKey(ClsidKey(hasContent));
                clsid.SetValue("", "DoorMan");
                using (var icon = clsid.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", AppIcons.LockedFolderIconResource(hasContent));
                using var server = clsid.CreateSubKey("InProcServer32");
                server.SetValue("", extension);
                server.SetValue("ThreadingModel", "Apartment");
            }
        }

        AppState.Sync();
        ShellNotify.AssociationsChanged();
    }

    public static void Unregister()
    {
        Refresh([]);
        Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{ProtectVerb}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{FolderLocker.VaultExtension}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey(hasContent: false), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey(hasContent: true), throwOnMissingSubKey: false);
        foreach (var legacy in LegacyKeys)
            Registry.CurrentUser.DeleteSubKeyTree(legacy, throwOnMissingSubKey: false);
        ShellNotify.AssociationsChanged();
    }

    /// <summary>
    /// Adjusts the context menu to the current list of folders: “Protect with password” only on folders that are
    /// not protected, “Unlock” on locked ones and “Lock” on unlocked ones.
    /// </summary>
    public static void Refresh(IReadOnlyCollection<ProtectedFolder> folders)
    {
        if (!IsRegistered)
            folders = [];
        var locked = folders.Where(f => f.Locked).Select(f => f.Path).ToList();
        var unlocked = folders.Where(f => !f.Locked).Select(f => f.Path).ToList();
        WriteUnlockedList(unlocked);

        // With the package, “Protect” and “Lock” come from its command. Locked folders are the exception: their
        // desktop.ini ties them to DoorMan's extension and Windows shows them no packaged commands, so “Unlock”
        // stays in the registry (under “Show more options”, without being repeated).
        var packaged = UsesPackagedMenu;
        if (packaged)
            Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{ProtectVerb}", throwOnMissingSubKey: false);
        else if (IsRegistered)
            WriteVerb(ProtectVerb, Strings.ProtectWithPassword, "--protect", AppIcons.ProtectResource,
                folders.Count > 0 ? AppliesToAllExcept(folders.Select(f => f.Path)) : null);

        if (unlocked.Count > 0 && !packaged)
            WriteVerb(RelockVerb, Strings.LockVerb, "--lock", AppIcons.RelockResource, AppliesToAny(unlocked));
        else
            Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{RelockVerb}", throwOnMissingSubKey: false);

        if (locked.Count > 0)
            WriteVerb(OpenVerb, Strings.UnlockVerb, "--open", AppIcons.UnlockResource, AppliesToAny(locked));
        else
            Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{OpenVerb}", throwOnMissingSubKey: false);
    }

    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorMan");
    static string UnlockedListPath => Path.Combine(DataDir, "unlocked.txt");
    const string LegacyUnlockedListName = "desbloqueadas.txt";

    /// <summary>
    /// The Windows 11 context menu (MenuCommand, in DoorManShell.dll) recognizes locked folders by their
    /// encrypted file, and unlocked ones by this list (one path per line, UTF-8).
    /// </summary>
    static void WriteUnlockedList(IEnumerable<string> unlocked)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = UnlockedListPath + ".tmp";
            File.WriteAllLines(tmp, unlocked, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, UnlockedListPath, overwrite: true);
            File.Delete(Path.Combine(DataDir, LegacyUnlockedListName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It only affects the Windows 11 menu text; it is written again on the next change.
        }
    }

    static void WriteVerb(string verb, string text, string argument, string icon, string? appliesTo)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{DirectoryShell}\{verb}");
        key.SetValue("MUIVerb", text);
        key.SetValue("Icon", icon);
        if (appliesTo is null)
            key.DeleteValue("AppliesTo", throwOnMissingValue: false);
        else
            key.SetValue("AppliesTo", appliesTo);
        using var cmd = key.CreateSubKey("command");
        cmd.SetValue("", $"\"{ExePath}\" {argument} \"%1\"");
    }

    static string AppliesToAny(IEnumerable<string> paths) =>
        string.Join(" OR ", paths.Select(p => $"System.ParsingPath:=\"{p}\""));

    static string AppliesToAllExcept(IEnumerable<string> paths) =>
        string.Join(" AND ", paths.Select(p => $"System.ParsingPath:<>\"{p}\""));
}

internal static class ShellNotify
{
    const int SHCNE_UPDATEDIR = 0x00001000;
    const int SHCNE_UPDATEITEM = 0x00002000;
    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0x0000;
    const uint SHCNF_PATHW = 0x0005;
    const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, string? item2);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    static extern void SHChangeNotifyPtr(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void ItemChanged(string folder)
    {
        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, folder, null);
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, folder, null);
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null)
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, parent, null);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize;
        public uint dwMask;
        public IntPtr pvid;
        public string? pszWebViewTemplate;
        public uint cchWebViewTemplate;
        public string? pszWebViewTemplateVersion;
        public string? pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        public string? pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        public string? pszLogo;
        public uint cchLogo;
    }

    const uint FCSM_INFOTIP = 0x00000004;
    const uint FCSM_ICONFILE = 0x00000010;
    const uint FCS_FORCEWRITE = 0x00000002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS settings, string path, uint readWrite);

    /// <summary>
    /// Writes the folder icon and description with the official shell function. Unlike editing desktop.ini by
    /// hand, it invalidates Explorer's cache and the icon changes right away.
    /// </summary>
    public static void SetFolderIcon(string folder, (string File, int Index) icon, string infoTip)
    {
        var settings = new SHFOLDERCUSTOMSETTINGS
        {
            dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(),
            dwMask = FCSM_ICONFILE | FCSM_INFOTIP,
            pszIconFile = icon.File,
            iIconIndex = icon.Index,
            pszInfoTip = infoTip,
        };
        SHGetSetFolderCustomSettings(ref settings, folder, FCS_FORCEWRITE);
        ItemChanged(folder);
    }

    /// <summary>
    /// Removes the custom icon and description with the shell function, so that Explorer forgets the cached one
    /// and shows the normal Windows icon again.
    /// </summary>
    public static void ClearFolderIcon(string folder)
    {
        var settings = new SHFOLDERCUSTOMSETTINGS
        {
            dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(),
            dwMask = FCSM_ICONFILE | FCSM_INFOTIP,
            pszIconFile = null,
            pszInfoTip = null,
        };
        SHGetSetFolderCustomSettings(ref settings, folder, FCS_FORCEWRITE);
        ItemChanged(folder);
    }

    public static void AssociationsChanged() =>
        SHChangeNotifyPtr(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
}
