using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Shell;
using DoorMan.UI;

namespace DoorMan;

static class Program
{
    /// <summary>
    /// Sin argumentos abre la ventana principal. Desde el Explorador se invoca con
    /// <c>--proteger</c>, <c>--abrir</c> o <c>--rebloquear</c> y la ruta de la carpeta; estas órdenes
    /// las ejecuta el agente en segundo plano (<c>--inicio</c> al iniciar sesión, o <c>--agente</c>).
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        ApplicationConfiguration.Initialize();

        if (command is "--inicio" or "--agente")
        {
            // Completamente en segundo plano: sin ventanas salvo las que pide el usuario (contraseñas, avisos).
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
                // Lo normal es que lo haga el agente; si no se puede arrancar, se hace aquí mismo.
                if (!AgentClient.TrySend(args))
                    action();
                return 0;
            }
            switch (command)
            {
                case "":
                    // Doble clic en el archivo descargado: se instala (o se actualiza). Instalado, abre la ventana.
                    if (!Installer.IsRunningInstalled)
                        return InstallInteractively();
                    Application.Run(new MainForm());
                    break;
                case "--registrar":
                    ShellIntegration.Register();
                    break;
                case "--desregistrar":
                    ShellIntegration.Unregister();
                    break;
                case "--instalar":
                    if (Installer.IsWrongArchitecture)
                    {
                        ShowWrongArchitecture();
                        return 1;
                    }
                    return Installer.Install();
                case "--confiar-certificado" when args.Length == 2:
                    // Lo lanza el instalador con permisos de administrador (UAC).
                    ContextMenuPackage.TrustCertificate(args[1]);
                    break;
                case "--desinstalar":
                    var locked = AppState.Sync().Count(f => f.Locked);
                    if (!Dialogs.Confirm("¿Desinstalar DoorMan?",
                            locked == 0
                                ? "Se quitarán las opciones del menú contextual y el inicio automático."
                                : $"Hay {locked} carpeta(s) bloqueada(s). Seguirán cifradas y no podrás abrirlas hasta volver a instalar la aplicación.",
                            "Desinstalar"))
                        return 1;
                    Installer.Uninstall();
                    Dialogs.Info("DoorMan se ha desinstalado", "Ya no aparecerá en el menú contextual ni se iniciará con Windows.");
                    break;
                default:
                    Dialogs.Info("Uso de DoorMan",
                        "DoorMan.exe [--proteger | --abrir | --rebloquear] \"carpeta\"\n" +
                        "DoorMan.exe [--registrar | --desregistrar]\n" +
                        "DoorMan.exe [--instalar | --desinstalar | --inicio | --agente]");
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
    /// Lo que ve quien descarga DoorMan.exe y hace doble clic: una confirmación, la instalación (con el aviso de
    /// UAC del menú de Windows 11) y un mensaje final que explica cómo empezar.
    /// </summary>
    static int InstallInteractively()
    {
        if (Installer.IsWrongArchitecture)
        {
            ShowWrongArchitecture();
            return 1;
        }
        bool update = Installer.IsInstalled;
        if (!Dialogs.Confirm(update ? "Actualizar DoorMan" : "Instalar DoorMan",
                (update
                    ? "Se sustituirá la versión instalada por esta. Tus carpetas protegidas no cambian."
                    : "DoorMan protege tus carpetas con contraseña desde el Explorador de archivos.") +
                "\n\nWindows pedirá permiso una vez para añadir las opciones de DoorMan al menú contextual.",
                update ? "Actualizar" : "Instalar"))
            return 1;

        int result;
        using (new CursorScope())
            result = Installer.Install();
        if (result != 0)
        {
            Dialogs.Info("No se pudo instalar DoorMan", "La instalación no ha terminado. Vuelve a intentarlo.");
            return result;
        }
        Dialogs.Info(update ? "DoorMan se ha actualizado" : "DoorMan está instalado",
            "Haz clic derecho en una carpeta y elige «Proteger con contraseña».\n\n" +
            "Para abrir una carpeta protegida, haz doble clic en ella y escribe la contraseña. " +
            "DoorMan se inicia solo con Windows; puedes desinstalarlo desde «Aplicaciones instaladas».");
        return 0;
    }

    static void ShowWrongArchitecture() =>
        Dialogs.Info("Descarga la versión de 64 bits",
            "Este Windows es de 64 bits y este archivo es la versión de 32 bits de DoorMan.\n\n" +
            $"Descarga «DoorMan-x64.exe» desde {Installer.DownloadUrl}");

    /// <summary>Cursor de espera mientras dura la instalación, que no tiene ventana propia.</summary>
    sealed class CursorScope : IDisposable
    {
        readonly Cursor _previous = Cursor.Current;
        public CursorScope() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = _previous;
    }

    /// <summary>Los errores se avisan con una notificación de Windows, sin ventanas que interrumpan.</summary>
    public static void ShowError(Exception ex)
    {
        switch (ex)
        {
            case InvalidDataException:
                Notifications.Error("No se puede abrir la carpeta protegida", ex.Message);
                break;
            case UserFacingException or IOException or UnauthorizedAccessException:
                Notifications.Error("No se puede completar la operación", ex.Message);
                break;
            default:
                Notifications.Error("Se ha producido un error inesperado", ex.Message);
                break;
        }
    }
}
