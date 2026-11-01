namespace CarpetaProtegida.UI;

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
    /// Muestra la notificación y espera a que se cierre (o unos segundos), porque el proceso termina
    /// justo después y, al quitar su icono, Windows retiraría también la notificación.
    /// </summary>
    static void Show(string title, string text, ToolTipIcon icon)
    {
        using var tray = new NotifyIcon
        {
            Icon = AppIcons.Lock,
            Text = "Carpeta protegida",
            Visible = true,
        };
        using var timer = new System.Windows.Forms.Timer { Interval = VisibleMilliseconds };
        using var context = new ApplicationContext();
        tray.BalloonTipClosed += (_, _) => context.ExitThread();
        tray.BalloonTipClicked += (_, _) => context.ExitThread();
        timer.Tick += (_, _) => context.ExitThread();
        tray.ShowBalloonTip(VisibleMilliseconds, Truncate(title, MaxTitle), Truncate(text, MaxText), icon);
        timer.Start();
        Application.Run(context);
        tray.Visible = false;
    }

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
