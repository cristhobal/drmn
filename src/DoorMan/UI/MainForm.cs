using DoorMan.Core;
using DoorMan.Shell;

namespace DoorMan.UI;

/// <summary>Window shown when the installed app is opened directly: turns the integration on or off and lists the folders.</summary>
internal sealed class MainForm : DialogBase
{
    readonly Label _status;
    readonly ListView _list;
    readonly Button _toggle;

    public MainForm() : base("DoorMan")
    {
        MinimizeBox = true;
        AddInstruction(Strings.MainHeading);
        AddText(Strings.MainHelp);
        _status = AddText("");
        AddRow(new Label { Text = Strings.ProtectedFoldersLabel, AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
        _list = AddRow(new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Width = ContentWidth,
            Height = 160,
            Margin = Padding.Empty,
        });
        _list.Columns.Add(Strings.FolderColumn, 270);
        _list.Columns.Add(Strings.StatusColumn, 100);
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string path)
                ExplorerWindows.Show(Path.GetDirectoryName(path) ?? path);
        };

        var close = AddButton(Strings.Close, DialogResult.Cancel);
        _toggle = AddButton("");
        _toggle.Click += (_, _) => Toggle();
        CancelButton = close;
        RefreshView();
    }

    void Toggle()
    {
        if (ShellIntegration.IsRegistered)
        {
            if (!Dialogs.Confirm(Strings.RemoveFromExplorerQuestion, Strings.RemoveFromExplorerText, Strings.Remove))
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
            ShellIntegration.Register(); // Updates the program path in case it was moved.
        _status.Text = registered ? Strings.IntegrationOn : Strings.IntegrationOff;
        _toggle.Text = registered ? Strings.RemoveFromExplorer : Strings.AddToExplorer;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var folder in AppState.Sync())
            _list.Items.Add(new ListViewItem([folder.Path, folder.Locked ? Strings.Locked : Strings.Unlocked]) { Tag = folder.Path });
        _list.EndUpdate();
    }
}
