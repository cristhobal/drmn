using System.Security.Cryptography;
using System.Text;
using CarpetaProtegida.Core;
using CarpetaProtegida.Crypto;
using CarpetaProtegida.Shell;
using CarpetaProtegida.UI;

namespace CarpetaProtegida;

/// <summary>Las acciones del menú contextual del Explorador.</summary>
internal static class Commands
{
    /// <summary>«Proteger con contraseña».</summary>
    public static void Protect(string path, string? note = null)
    {
        var folder = PathUtil.Normalize(path);
        using var guard = FolderGuard.TryAcquire(folder);
        if (guard is null)
            return;

        if (FolderLocker.IsLocked(folder))
        {
            Notifications.Info("Esta carpeta ya está protegida", "Ábrela con doble clic para escribir su contraseña.");
            return;
        }
        FolderRules.EnsureCanProtect(folder);

        string password;
        using (var dialog = new SetPasswordForm(DisplayName(folder), note))
        {
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            password = dialog.Password;
        }

        ExplorerWindows.LeaveFolder(folder);
        var result = ProgressForm.Run("Proteger con contraseña", $"Cifrando «{DisplayName(folder)}»…", progress =>
        {
            using var key = KeyMaterial.Create(password);
            return FolderLocker.Lock(folder, key, progress);
        });
        AppState.MarkLocked(folder);
        ExplorerWindows.RefreshParentViews(folder);
        ReportNotDeleted(folder, result);
    }

    /// <summary>
    /// Doble clic sobre una carpeta bloqueada (la extensión del Explorador cancela la entrada y llama aquí),
    /// «Abrir carpeta protegida» o su archivo .cpbloq: pide la contraseña y muestra la carpeta.
    /// </summary>
    public static void Open(string path, IntPtr window = default)
    {
        var folder = PathUtil.Normalize(path);
        if (File.Exists(folder) && folder.EndsWith(FolderLocker.VaultExtension, StringComparison.OrdinalIgnoreCase))
            folder = Path.GetDirectoryName(folder)!;

        if (!FolderLocker.IsLocked(folder))
        {
            // Ya no está bloqueada (p. ej. se desbloqueó desde otra ventana): se abre sin más.
            AppState.Sync();
            if (Directory.Exists(folder))
                ExplorerWindows.Show(folder, window);
            return;
        }
        if (TryUnlock(folder))
            ExplorerWindows.Show(folder, window);
    }

    /// <summary>Pide la contraseña y descifra la carpeta.</summary>
    /// <returns><c>true</c> si la carpeta ha quedado desbloqueada.</returns>
    public static bool TryUnlock(string folder)
    {
        using var guard = FolderGuard.TryAcquire(folder);
        if (guard is null || !FolderLocker.IsLocked(folder))
            return false;

        KeyMaterial key;
        using (var dialog = new UnlockForm(DisplayName(folder), password => FolderLocker.OpenKey(folder, password)))
        {
            if (dialog.ShowDialog() != DialogResult.OK || dialog.Key is null)
                return false;
            key = dialog.Key;
        }

        using (key)
        {
            ProgressForm.Run("Carpeta protegida", $"Descifrando «{DisplayName(folder)}»…", progress =>
            {
                FolderLocker.Unlock(folder, key, progress);
                return true;
            });
            AppState.MarkUnlocked(folder, key);
        }
        return true;
    }

    /// <summary>«Volver a bloquear»: usa la misma contraseña, guardada mientras la carpeta está abierta.</summary>
    public static void Relock(string path)
    {
        var folder = PathUtil.Normalize(path);
        KeyMaterial? key;
        using (var guard = FolderGuard.TryAcquire(folder))
        {
            if (guard is null)
                return;
            if (FolderLocker.IsLocked(folder))
            {
                Notifications.Info("Esta carpeta ya está bloqueada", "Ábrela con doble clic para escribir su contraseña.");
                return;
            }
            FolderRules.EnsureCanProtect(folder);

            key = AppState.TryLoadKey(folder);
            if (key is not null)
            {
                using (key)
                {
                    ExplorerWindows.LeaveFolder(folder);
                    var result = ProgressForm.Run("Volver a bloquear", $"Cifrando «{DisplayName(folder)}»…",
                        progress => FolderLocker.Lock(folder, key, progress));
                    AppState.MarkLocked(folder);
                    ExplorerWindows.RefreshParentViews(folder);
                    ReportNotDeleted(folder, result);
                }
                return;
            }
        }

        // Sin la clave guardada (otro usuario de Windows, carpeta movida…) hay que elegir contraseña.
        Protect(folder, "No se ha encontrado la contraseña anterior de esta carpeta. Elige una contraseña para volver a bloquearla.");
    }

    /// <summary>
    /// Si todo ha ido bien no se muestra nada: el icono de la carpeta ya indica que está bloqueada.
    /// Solo se avisa si quedaron elementos en uso sin borrar.
    /// </summary>
    static void ReportNotDeleted(string folder, LockResult result)
    {
        if (result.NotDeleted.Count == 0)
            return;
        var first = Path.GetFileName(result.NotDeleted[0].TrimEnd('\\'));
        var others = result.NotDeleted.Count > 1 ? $" y {result.NotDeleted.Count - 1} elemento(s) más" : "";
        Notifications.Warning("Quedan elementos sin eliminar",
            $"«{DisplayName(folder)}» está cifrada, pero «{first}»{others} estaba(n) en uso. Ciérralo(s) y elimínalo(s) a mano.");
    }

    static string DisplayName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    /// <summary>Evita que dos ventanas trabajen a la vez sobre la misma carpeta (p. ej. por un doble clic repetido).</summary>
    sealed class FolderGuard : IDisposable
    {
        readonly Mutex _mutex;

        FolderGuard(Mutex mutex) => _mutex = mutex;

        public static FolderGuard? TryAcquire(string folder)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..32];
            var mutex = new Mutex(false, @"Local\CarpetaProtegida." + hash);
            try
            {
                if (mutex.WaitOne(0))
                    return new FolderGuard(mutex);
            }
            catch (AbandonedMutexException)
            {
                return new FolderGuard(mutex);
            }
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
