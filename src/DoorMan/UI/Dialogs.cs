namespace DoorMan.UI;

/// <summary>Mensajes con TaskDialog (el cuadro de mensaje nativo de Windows) y botones siempre en español.</summary>
internal static class Dialogs
{
    const string Caption = "DoorMan";

    public static void Info(string heading, string text) => Show(heading, text, TaskDialogIcon.Information);

    public static void Success(string heading, string text) => Show(heading, text, TaskDialogIcon.ShieldSuccessGreenBar);

    public static void Warning(string heading, string text) => Show(heading, text, TaskDialogIcon.Warning);

    public static void Error(string heading, string text) => Show(heading, text, TaskDialogIcon.Error);

    public static bool Confirm(string heading, string text, string acceptText)
    {
        var accept = new TaskDialogButton(acceptText);
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { accept, new TaskDialogButton("Cancelar") },
            DefaultButton = accept,
            AllowCancel = true,
        };
        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) == accept;
    }

    static void Show(string heading, string text, TaskDialogIcon icon)
    {
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = icon,
            Buttons = { new TaskDialogButton("Aceptar") },
            AllowCancel = true,
        };
        TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }
}
