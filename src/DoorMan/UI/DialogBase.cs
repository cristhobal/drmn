namespace DoorMan.UI;

/// <summary>
/// Dialog that looks like the Windows ones: a white area with the main instruction in blue and a gray bottom strip
/// with the buttons aligned to the right.
/// </summary>
internal class DialogBase : Form
{
    protected const int ContentWidth = 380;
    protected readonly TableLayoutPanel Content;
    protected readonly FlowLayoutPanel Buttons;

    protected DialogBase(string title)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9F);
        Text = title;
        Icon = AppIcons.Lock;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;

        var outer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        Content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(20, 16, 20, 14),
            BackColor = SystemColors.Window,
        };
        Content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));
        Buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = SystemColors.Control,
        };
        outer.Controls.Add(Content, 0, 0);
        outer.Controls.Add(Buttons, 0, 1);
        Controls.Add(outer);
        ResumeLayout(false);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // It may be opened by the background process, which Windows does not let come to the front by itself.
        Shell.ForegroundHelper.Bring(Handle);
        Activate();
    }

    protected Label AddInstruction(string text) => AddRow(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        Font = new Font(Font.FontFamily, 12F),
        ForeColor = Color.FromArgb(0, 51, 153),
        Margin = new Padding(0, 0, 0, 10),
        UseMnemonic = false,
    });

    protected Label AddText(string text, int bottomMargin = 12) => AddRow(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        Margin = new Padding(0, 0, 0, bottomMargin),
        UseMnemonic = false,
    });

    protected TextBox AddPasswordField(string label)
    {
        AddRow(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
        return AddRow(new TextBox
        {
            UseSystemPasswordChar = true,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, 10),
            MaxLength = 1024,
        });
    }

    /// <summary>Red error label that keeps its space even when empty.</summary>
    protected Label AddErrorLabel() => AddRow(new Label
    {
        AutoSize = true,
        MinimumSize = new Size(ContentWidth, Font.Height * 2),
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = Color.FromArgb(196, 43, 28),
        Margin = new Padding(0, 0, 0, 0),
        UseMnemonic = false,
    });

    /// <summary>Adds buttons from right to left: the first one ends up on the far right.</summary>
    protected Button AddButton(string text, DialogResult result = DialogResult.None)
    {
        var button = new Button
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(88, 26),
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        Buttons.Controls.Add(button);
        return button;
    }

    protected T AddRow<T>(T control) where T : Control
    {
        // Explicit position: the automatic placement of TableLayoutPanel mixes up the rows.
        int row = Content.RowCount;
        Content.RowCount = row + 1;
        Content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Content.Controls.Add(control, 0, row);
        return control;
    }
}
