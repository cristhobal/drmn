namespace DoorMan.UI;

/// <summary>
/// Native Windows notifications (the notification area ones, which Windows 10/11 show as system notifications).
/// They are only used for errors and warnings: the normal result is already visible in the folder icon and needs no
/// message.
/// </summary>
internal static class Notifications
{
    // Windows limits for the notification title and text.
    const int MaxTitle = 63;
    const int MaxText = 255;
    const int VisibleMilliseconds = 8000;

    public static void Error(string title, string text) => Show(title, text, ToolTipIcon.Error);

    public static void Warning(string title, string text) => Show(title, text, ToolTipIcon.Warning);

    public static void Info(string title, string text) => Show(title, text, ToolTipIcon.Info);

    /// <summary>
    /// Shows the notification and removes it when it is closed (or after a few seconds). Outside the agent it waits
    /// until then, because the process ends right after and, when its icon is removed, Windows would remove the
    /// notification too. In the agent it does not wait: its message loop keeps running.
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
