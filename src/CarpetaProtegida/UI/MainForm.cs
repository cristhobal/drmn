using CarpetaProtegida.Core;
using CarpetaProtegida.Shell;

namespace CarpetaProtegida.UI;

/// <summary>Ventana que aparece al abrir la aplicación directamente: activa la integración y lista las carpetas.</summary>
internal sealed class MainForm : DialogBase
{
    readonly Label _status;
    readonly ListView _list;
    readonly Button _toggle;

    public MainForm() : base("Carpeta Protegida")
    {
        MinimizeBox = true;
        AddInstruction("Protege carpetas con contraseña");
        AddText("Haz clic derecho sobre una carpeta en el Explorador de archivos y elige «Proteger con contraseña». " +
                "En Windows 11, la opción está dentro de «Mostrar más opciones».");
        _status = AddText("");
        AddRow(new Label { Text = "Carpetas protegidas:", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
        _list = AddRow(new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Width = ContentWidth,
            Height = 160,
            Margin = Padding.Empty,
        });
        _list.Columns.Add("Carpeta", 270);
        _list.Columns.Add("Estado", 100);
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string path)
                ExplorerWindows.Show(Path.GetDirectoryName(path) ?? path);
        };

        var close = AddButton("Cerrar", DialogResult.Cancel);
        _toggle = AddButton("");
        _toggle.Click += (_, _) => Toggle();
        CancelButton = close;
        RefreshView();
    }

    void Toggle()
    {
        if (ShellIntegration.IsRegistered)
        {
            if (!Dialogs.Confirm("¿Quitar Carpeta Protegida del Explorador?",
                    "Las carpetas bloqueadas seguirán cifradas. Para abrirlas tendrás que volver a activar la integración.",
                    "Quitar"))
                return;
            ShellIntegration.Unregister();
        }
        else
        {
            ShellIntegration.Register();
        }
        RefreshView();
    }

    void RefreshView()
    {
        bool registered = ShellIntegration.IsRegistered;
        if (registered)
            ShellIntegration.Register(); // Actualiza la ruta del programa por si se ha movido.
        _status.Text = registered
            ? "La integración con el Explorador está activada."
            : "La integración con el Explorador está desactivada. Actívala para ver las opciones en el menú contextual.";
        _toggle.Text = registered ? "Quitar del Explorador" : "Activar en el Explorador";

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var folder in AppState.Sync())
            _list.Items.Add(new ListViewItem([folder.Path, folder.Locked ? "Bloqueada" : "Desbloqueada"]) { Tag = folder.Path });
        _list.EndUpdate();
    }
}
