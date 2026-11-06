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
                    Application.Run(new MainForm());
                    break;
                case "--registrar":
                    ShellIntegration.Register();
                    break;
                case "--desregistrar":
                    ShellIntegration.Unregister();
                    break;
                case "--instalar":
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
