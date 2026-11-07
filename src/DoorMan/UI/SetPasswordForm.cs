namespace DoorMan.UI;

/// <summary>“Password” and “Confirm password”; “OK” is only enabled when both are filled in and match.</summary>
internal sealed class SetPasswordForm : DialogBase
{
    readonly TextBox _password;
    readonly TextBox _confirm;
    readonly Label _error;
    readonly Button _ok;

    public string Password => _password.Text;

    public SetPasswordForm(string folderName, string? note = null) : base(Strings.ProtectWithPassword)
    {
        AddInstruction(Strings.ProtectFolderHeading(folderName));
        if (note is not null)
            AddText(note);
        AddText(Strings.ProtectWarning);

        _password = AddPasswordField(Strings.PasswordLabel);
        _confirm = AddPasswordField(Strings.ConfirmPasswordLabel);
        var show = AddRow(new CheckBox { Text = Strings.ShowPasswords, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        _error = AddErrorLabel();

        var cancel = AddButton(Strings.Cancel, DialogResult.Cancel);
        _ok = AddButton(Strings.OK, DialogResult.OK);
        _ok.Enabled = false;
        AcceptButton = _ok;
        CancelButton = cancel;

        _password.TextChanged += (_, _) => UpdateState();
        _confirm.TextChanged += (_, _) => UpdateState();
        show.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !show.Checked;
            _confirm.UseSystemPasswordChar = !show.Checked;
        };
    }

    void UpdateState()
    {
        bool bothFilled = _password.TextLength > 0 && _confirm.TextLength > 0;
        bool match = bothFilled && string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal);
        _ok.Enabled = match;
        _error.Text = bothFilled && !match ? Strings.PasswordsDoNotMatch : "";
    }
}
