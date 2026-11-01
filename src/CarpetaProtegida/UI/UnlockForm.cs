using System.Media;
using CarpetaProtegida.Crypto;

namespace CarpetaProtegida.UI;

/// <summary>Pide la contraseña de una carpeta bloqueada. Si es incorrecta, lo indica y permite reintentar.</summary>
internal sealed class UnlockForm : DialogBase
{
    readonly Func<string, KeyMaterial> _openKey;
    readonly TextBox _password;
    readonly Label _error;
    readonly Button _ok;
    readonly Button _cancel;
    bool _busy;

    public KeyMaterial? Key { get; private set; }

    public UnlockForm(string folderName, Func<string, KeyMaterial> openKey) : base("Carpeta protegida")
    {
        _openKey = openKey;
        AddInstruction($"«{folderName}» está protegida con contraseña");
        AddText("Escribe la contraseña para abrir la carpeta.");
        _password = AddPasswordField("&Contraseña:");
        _error = AddErrorLabel();

        _cancel = AddButton("Cancelar", DialogResult.Cancel);
        _ok = AddButton("Aceptar");
        _ok.Enabled = false;
        AcceptButton = _ok;
        CancelButton = _cancel;

        _password.TextChanged += (_, _) => _ok.Enabled = _password.TextLength > 0 && !_busy;
        _ok.Click += async (_, _) => await TryUnlockAsync();
        FormClosing += (_, e) => e.Cancel |= _busy;
    }

    async Task TryUnlockAsync()
    {
        var password = _password.Text;
        SetBusy(true);
        try
        {
            Key = await Task.Run(() => _openKey(password));
            _busy = false;
            DialogResult = DialogResult.OK;
        }
        catch (WrongPasswordException)
        {
            _error.Text = "La contraseña no es correcta. Inténtalo de nuevo.";
            SystemSounds.Hand.Play();
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
        finally
        {
            if (DialogResult != DialogResult.OK)
            {
                SetBusy(false);
                _password.Focus();
                _password.SelectAll();
            }
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _password.Enabled = !busy;
        _cancel.Enabled = !busy;
        _ok.Enabled = !busy && _password.TextLength > 0;
        if (busy)
            _error.Text = "Comprobando la contraseña…";
    }
}
