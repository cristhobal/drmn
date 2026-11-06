using System.Runtime.InteropServices;
using DoorMan.Core;
using Microsoft.Win32;

namespace DoorMan.Shell;

/// <summary>
/// Integración con el Explorador mediante el registro del usuario actual (no requiere administrador):
/// <list type="bullet">
/// <item>«Proteger con contraseña» en el menú contextual de todas las carpetas.</item>
/// <item>«Bloquear», solo en las carpetas desbloqueadas (filtrado con AppliesTo).</item>
/// <item>«Desbloquear», solo en las carpetas bloqueadas.</item>
/// <item>La extensión .doorman del archivo cifrado, que pide la contraseña al abrirlo: es lo que el
/// usuario ve al entrar en una carpeta bloqueada.</item>
/// </list>
/// Nunca se cambia el verbo predeterminado de las carpetas: es un ajuste global y, en las carpetas
/// especiales (Imágenes, Documentos…), dejaba el Explorador sin poder abrirlas.
/// AppliesTo compara con System.ParsingPath, porque System.ItemPathDisplay está traducido
/// («C:\Usuarios\…\Imágenes») y no coincide con la ruta real.
/// </summary>
internal static class ShellIntegration
{
    const string DirectoryShell = @"Software\Classes\Directory\shell";
    const string ProtectVerb = "DoorMan.Proteger";
    const string RelockVerb = "DoorMan.Rebloquear";
    const string OpenVerb = "DoorMan.Abrir";
    const string ProgId = "DoorMan.Boveda";

    /// <summary>
    /// Clase de la extensión nativa (DoorManShell.dll). El desktop.ini de las carpetas
    /// bloqueadas la referencia, y el Explorador pide la contraseña antes de mostrar su contenido.
    /// </summary>
    public const string LockedFolderClsid = "{142BC879-C3C7-4D93-B1E6-51CF20CC661E}";
    /// <summary>
    /// La misma clase para las carpetas bloqueadas con contenido. En las vistas grandes el Explorador
    /// muestra el icono registrado para la clase (DefaultIcon) y no el del desktop.ini, así que cada
    /// icono necesita su propia clase.
    /// </summary>
    public const string LockedFilledFolderClsid = "{4FB75F15-2E04-47FB-8F21-40CE81A144A5}";
    public const string ExtensionFileName = "DoorManShell.dll";

    public static string LockedClsid(bool hasContent) => hasContent ? LockedFilledFolderClsid : LockedFolderClsid;
    static string ClsidKey(bool hasContent) => @"Software\Classes\CLSID\" + LockedClsid(hasContent);

    static string ExePath => Environment.ProcessPath!;
    static string LockIcon => AppIcons.LockResource;

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{DirectoryShell}\{ProtectVerb}\command");
            return key is not null;
        }
    }

    public static void Register()
    {
        WriteVerb(ProtectVerb, "Proteger con contraseña", "--proteger", appliesTo: null);

        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FolderLocker.VaultExtension}"))
            ext.SetValue("", ProgId);
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Carpeta protegida con contraseña");
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue("", LockIcon);
            using (var open = prog.CreateSubKey(@"shell\open"))
                open.SetValue("", "Abrir carpeta protegida");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" --abrir \"%1\"");
        }

        var extension = Path.Combine(Path.GetDirectoryName(ExePath)!, ExtensionFileName);
        if (File.Exists(extension))
        {
            foreach (var hasContent in new[] { false, true })
            {
                using var clsid = Registry.CurrentUser.CreateSubKey(ClsidKey(hasContent));
                clsid.SetValue("", "DoorMan");
                using (var icon = clsid.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", AppIcons.FolderIconResource(locked: true, hasContent));
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
        ShellNotify.AssociationsChanged();
    }

    /// <summary>
    /// Ajusta el menú contextual a la lista actual de carpetas: «Proteger con contraseña» solo en las
    /// que no están protegidas, «Desbloquear» en las bloqueadas y «Bloquear» en las desbloqueadas.
    /// </summary>
    public static void Refresh(IReadOnlyCollection<ProtectedFolder> folders)
    {
        if (!IsRegistered)
            folders = [];
        var locked = folders.Where(f => f.Locked).Select(f => f.Path).ToList();
        var unlocked = folders.Where(f => !f.Locked).Select(f => f.Path).ToList();

        if (IsRegistered)
            WriteVerb(ProtectVerb, "Proteger con contraseña", "--proteger",
                folders.Count > 0 ? AppliesToAllExcept(folders.Select(f => f.Path)) : null);

        if (unlocked.Count > 0)
            WriteVerb(RelockVerb, "Bloquear", "--rebloquear", AppliesToAny(unlocked));
        else
            Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{RelockVerb}", throwOnMissingSubKey: false);

        if (locked.Count > 0)
            WriteVerb(OpenVerb, "Desbloquear", "--abrir", AppliesToAny(locked));
        else
            Registry.CurrentUser.DeleteSubKeyTree($@"{DirectoryShell}\{OpenVerb}", throwOnMissingSubKey: false);
    }

    static void WriteVerb(string verb, string text, string argument, string? appliesTo)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{DirectoryShell}\{verb}");
        key.SetValue("MUIVerb", text);
        key.SetValue("Icon", LockIcon);
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
    /// Escribe el icono y la descripción de la carpeta con la función oficial del shell. A diferencia
    /// de editar desktop.ini a mano, invalida la caché del Explorador y el icono cambia al momento.
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

    public static void AssociationsChanged() =>
        SHChangeNotifyPtr(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
}
