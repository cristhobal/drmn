namespace DoorMan.UI;

/// <summary>
/// Notificaciones nativas de Windows (las del área de notificaciones, que Windows 10/11 muestra como
/// avisos del sistema). Se usan solo para errores y advertencias: el resultado normal ya se ve en el
/// icono de la carpeta y no necesita ningún mensaje.
/// </summary>
internal static class Notifications
{
    // Límites de Windows para el título y el texto de la notificación.
    const int MaxTitle = 63;
    const int MaxText = 255;
    const int VisibleMilliseconds = 8000;

    public static void Error(string title, string text) => Show(title, text, ToolTipIcon.Error);

    public static void Warning(string title, string text) => Show(title, text, ToolTipIcon.Warning);

    public static void Info(string title, string text) => Show(title, text, ToolTipIcon.Info);

    /// <summary>
    /// Muestra la notificación y la retira cuando se cierra (o a los pocos segundos). Fuera del agente
    /// espera hasta entonces, porque el proceso termina justo después y, al quitar su icono, Windows
    /// retiraría también la notificación. En el agente no espera: su bucle de mensajes sigue en marcha.
    /// </summary>
    static void Show(string title, string text, ToolTipIcon icon)
    {
        var tray = new NotifyIcon
        {
            Icon = AppIcons.Lock,
            Text = "DoorMan",
            Visible = true,
        };
        var timer = new System.Windows.Forms.Timer { Interval = VisibleMilliseconds };
        bool running = Application.MessageLoop;
        var context = running ? null : new ApplicationContext();
        bool closed = false;
        void Close()
        {
            if (closed)
                return;
            closed = true;
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            context?.ExitThread();
        }
        tray.BalloonTipClosed += (_, _) => Close();
        tray.BalloonTipClicked += (_, _) => Close();
        timer.Tick += (_, _) => Close();
        tray.ShowBalloonTip(VisibleMilliseconds, Truncate(title, MaxTitle), Truncate(text, MaxText), icon);
        timer.Start();
        if (context is not null)
        {
            Application.Run(context);
            context.Dispose();
        }
    }

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
