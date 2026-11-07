using System.Runtime.ExceptionServices;
using DoorMan.Core;

namespace DoorMan.UI;

/// <summary>Runs a long operation in the background while showing a progress bar.</summary>
internal sealed class ProgressForm : DialogBase
{
    readonly Func<IProgress<ProgressInfo>, object?> _work;
    readonly ProgressBar _bar;
    readonly Label _detail;
    object? _result;
    ExceptionDispatchInfo? _error;
    bool _done;

    ProgressForm(string title, string instruction, Func<IProgress<ProgressInfo>, object?> work) : base(title)
    {
        _work = work;
        ControlBox = false;
        AddInstruction(instruction);
        _bar = AddRow(new ProgressBar { Width = ContentWidth, Maximum = 1000, Style = ProgressBarStyle.Marquee, Margin = new Padding(0, 0, 0, 8) });
        _detail = AddRow(new Label { AutoSize = false, Width = ContentWidth, Height = Font.Height + 4, AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty });
        AddText(Strings.DoNotClose, bottomMargin: 0);
        Buttons.Visible = false;
        FormClosing += (_, e) => e.Cancel |= !_done;
    }

    public static T Run<T>(string title, string instruction, Func<IProgress<ProgressInfo>, T> work)
    {
        using var form = new ProgressForm(title, instruction, p => work(p));
        form.ShowDialog();
        form._error?.Throw();
        return (T)form._result!;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var progress = new Progress<ProgressInfo>(OnProgress);
        try
        {
            _result = await Task.Run(() => _work(progress));
        }
        catch (Exception ex)
        {
            _error = ExceptionDispatchInfo.Capture(ex);
        }
        _done = true;
        Close();
    }

    void OnProgress(ProgressInfo info)
    {
        if (info.Total > 0)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Clamp(info.Done * 1000 / info.Total, 0, 1000);
        }
        _detail.Text = info.CurrentItem ?? "";
    }
}
