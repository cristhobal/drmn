using System.Media;
using DoorMan.Crypto;

namespace DoorMan.UI;

/// <summary>Asks for the password of a locked folder. If it is wrong, it says so and lets the user try again.</summary>
internal sealed class UnlockForm : DialogBase
{
    readonly Func<string, KeyMaterial> _openKey;
    readonly TextBox _password;
    readonly Label _error;
    readonly Button _ok;
    readonly Button _cancel;
    bool _busy;

    public KeyMaterial? Key { get; private set; }

    public UnlockForm(string folderName, Func<string, KeyMaterial> openKey) : base("DoorMan")
    {
        _openKey = openKey;
        AddInstruction(Strings.FolderIsProtected(folderName));
        AddText(Strings.EnterPasswordToOpen);
        _password = AddPasswordField(Strings.PasswordLabel);
        _error = AddErrorLabel();

        _cancel = AddButton(Strings.Cancel, DialogResult.Cancel);
        _ok = AddButton(Strings.OK);
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
            _error.Text = Strings.WrongPasswordTryAgain;
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
            _error.Text = Strings.CheckingPassword;
    }
}
