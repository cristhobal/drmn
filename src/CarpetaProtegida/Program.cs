using CarpetaProtegida.Core;
using CarpetaProtegida.Shell;
using CarpetaProtegida.UI;

namespace CarpetaProtegida;

static class Program
{
    /// <summary>
    /// Sin argumentos abre la ventana principal. Desde el Explorador se invoca con
    /// <c>--proteger</c>, <c>--abrir</c> o <c>--rebloquear</c> y la ruta de la carpeta.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (command == "--inicio")
        {
            // Inicio de sesión: completamente en segundo plano, sin ventanas ni mensajes de error.
            try
            {
                Installer.RunAtStartup();
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();

        try
        {
            switch (command)
            {
                case "":
                    Application.Run(new MainForm());
                    break;
                case "--proteger" when args.Length == 2:
                    Commands.Protect(args[1]);
                    break;
                case "--abrir" when args.Length == 2:
                    Commands.Open(args[1]);
                    break;
                case "--abrir" when args.Length == 4 && args[2] == "--ventana" && long.TryParse(args[3], out var window):
                    Commands.Open(args[1], new IntPtr(window));
                    break;
                case "--rebloquear" when args.Length == 2:
                    Commands.Relock(args[1]);
                    break;
                case "--registrar":
                    ShellIntegration.Register();
                    break;
                case "--desregistrar":
                    ShellIntegration.Unregister();
                    break;
                case "--instalar":
                    return Installer.Install();
                case "--desinstalar":
                    var locked = AppState.Sync().Count(f => f.Locked);
                    if (!Dialogs.Confirm("¿Desinstalar Carpeta Protegida?",
                            locked == 0
                                ? "Se quitarán las opciones del menú contextual y el inicio automático."
                                : $"Hay {locked} carpeta(s) bloqueada(s). Seguirán cifradas y no podrás abrirlas hasta volver a instalar la aplicación.",
                            "Desinstalar"))
                        return 1;
                    Installer.Uninstall();
                    Dialogs.Info("Carpeta Protegida se ha desinstalado", "Ya no aparecerá en el menú contextual ni se iniciará con Windows.");
                    break;
                default:
                    Dialogs.Info("Uso de Carpeta Protegida",
                        "CarpetaProtegida.exe [--proteger | --abrir | --rebloquear] \"carpeta\"\n" +
                        "CarpetaProtegida.exe [--registrar | --desregistrar]\n" +
                        "CarpetaProtegida.exe [--instalar | --desinstalar | --inicio]");
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
