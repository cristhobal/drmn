namespace CarpetaProtegida.UI;

/// <summary>«Contraseña» y «Confirmar contraseña»; «Aceptar» solo se habilita si ambas están escritas y coinciden.</summary>
internal sealed class SetPasswordForm : DialogBase
{
    readonly TextBox _password;
    readonly TextBox _confirm;
    readonly Label _error;
    readonly Button _ok;

    public string Password => _password.Text;

    public SetPasswordForm(string folderName, string? note = null) : base("Proteger con contraseña")
    {
        AddInstruction($"Proteger «{folderName}» con contraseña");
        if (note is not null)
            AddText(note);
        AddText("Se cifrarán todos los archivos y subcarpetas. Si olvidas la contraseña, no podrás recuperar el contenido.");

        _password = AddPasswordField("&Contraseña:");
        _confirm = AddPasswordField("C&onfirmar contraseña:");
        var show = AddRow(new CheckBox { Text = "&Mostrar contraseñas", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        _error = AddErrorLabel();

        var cancel = AddButton("Cancelar", DialogResult.Cancel);
        _ok = AddButton("Aceptar", DialogResult.OK);
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
        _error.Text = bothFilled && !match ? "Las contraseñas no coinciden." : "";
    }
}
