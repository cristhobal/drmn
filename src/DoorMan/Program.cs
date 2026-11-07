using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Shell;
using DoorMan.UI;

namespace DoorMan;

static class Program
{
    /// <summary>
    /// Without arguments it installs DoorMan (when run from the downloaded file) or opens the main window. File
    /// Explorer runs it with <c>--protect</c>, <c>--open</c> or <c>--lock</c> and the folder path; those commands
    /// are carried out by the background agent (<c>--startup</c> at sign-in, or <c>--agent</c>).
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        args = Commands.NormalizeArguments(args);
        var command = args.Length > 0 ? args[0] : "";
        ApplicationConfiguration.Initialize();

        if (command is "--startup" or "--agent")
        {
            // Fully in the background: no windows except the ones the user asks for (passwords, warnings).
            try
            {
                AgentHost.Run();
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        try
        {
            if (Commands.FromArguments(args) is { } action)
            {
                // Normally the agent does it; if it cannot be started, it is done right here.
                if (!AgentClient.TrySend(args))
                    action();
                return 0;
            }
            switch (command)
            {
                case "":
                    // Double-clicking the downloaded file installs (or updates) DoorMan. Once installed, it opens the window.
                    if (!Installer.IsRunningInstalled)
                        return InstallInteractively();
                    Application.Run(new MainForm());
                    break;
                case "--register":
                    ShellIntegration.Register();
                    break;
                case "--unregister":
                    ShellIntegration.Unregister();
                    break;
                case "--install":
                    if (Installer.IsWrongArchitecture)
                    {
                        ShowWrongArchitecture();
                        return 1;
                    }
                    return Installer.Install();
                case "--trust-certificate" when args.Length == 2:
                    // Started by the installer with administrator rights (UAC).
                    ContextMenuPackage.TrustCertificate(args[1]);
                    break;
                case "--uninstall":
                    var locked = AppState.Sync().Count(f => f.Locked);
                    if (!Dialogs.Confirm(Strings.UninstallQuestion,
                            locked == 0 ? Strings.UninstallText : Strings.UninstallLockedText(locked),
                            Strings.Uninstall))
                        return 1;
                    Installer.Uninstall();
                    Dialogs.Info(Strings.UninstalledTitle, Strings.UninstalledText);
                    break;
                default:
                    Dialogs.Info(Strings.UsageTitle,
                        "DoorMan.exe [--protect | --open | --lock] \"folder\"\n" +
                        "DoorMan.exe [--register | --unregister]\n" +
                        "DoorMan.exe [--install | --uninstall | --startup | --agent]");
                    return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        return 1;
    }

    /// <summary>
    /// What someone who downloads DoorMan and double-clicks it sees: a confirmation, the installation (with the
    /// UAC prompt for the Windows 11 context menu) and a final message explaining how to get started.
    /// </summary>
    static int InstallInteractively()
    {
        if (Installer.IsWrongArchitecture)
        {
            ShowWrongArchitecture();
            return 1;
        }
        bool update = Installer.IsInstalled;
        var text = update ? Strings.UpdateText : Strings.InstallText;
        if (ContextMenuPackage.IsSupported)
            text += "\n\n" + Strings.InstallPermissionNote;
        if (!Dialogs.Confirm(update ? Strings.UpdateTitle : Strings.InstallTitle, text,
                update ? Strings.Update : Strings.Install))
            return 1;

        int result;
        using (new CursorScope())
            result = Installer.Install();
        if (result != 0)
        {
            Dialogs.Info(Strings.InstallFailedTitle, Strings.InstallFailedText);
            return result;
        }
        Dialogs.Info(update ? Strings.UpdatedTitle : Strings.InstalledTitle, Strings.InstalledText);
        return 0;
    }

    static void ShowWrongArchitecture() =>
        Dialogs.Info(Strings.WrongArchitectureTitle,
            Strings.WrongArchitectureText(Installer.ExpectedDownload, Installer.DownloadUrl));

    /// <summary>Wait cursor while installing, which has no window of its own.</summary>
    sealed class CursorScope : IDisposable
    {
        readonly Cursor? _previous = Cursor.Current;
        public CursorScope() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = _previous;
    }

    /// <summary>Errors are reported with a Windows notification, without windows that get in the way.</summary>
    public static void ShowError(Exception ex)
    {
        switch (ex)
        {
            case InvalidDataException:
                Notifications.Error(Strings.ErrorOpeningProtectedFolder, ex.Message);
                break;
            case UserFacingException or IOException or UnauthorizedAccessException:
                Notifications.Error(Strings.ErrorCompletingOperation, ex.Message);
                break;
            default:
                Notifications.Error(Strings.UnexpectedError, ex.Message);
                break;
        }
    }
}
