using System.Security.Cryptography;
using System.Text;
using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Crypto;
using DoorMan.Shell;
using DoorMan.UI;

namespace DoorMan;

/// <summary>
/// Las acciones del menú contextual del Explorador. Normalmente las ejecuta el agente en segundo
/// plano (<see cref="AgentHost"/>), que es quien atiende a los programas que abren archivos de las
/// carpetas bloqueadas.
/// </summary>
internal static class Commands
{
    /// <summary>La acción que corresponde a los argumentos de la línea de órdenes, o <c>null</c> si no son válidos.</summary>
    public static Action? FromArguments(string[] args) => args switch
    {
        ["--proteger", var path] => () => Protect(path),
        ["--abrir", var path] => () => Open(path),
        ["--abrir", var path, "--ventana", var w] when long.TryParse(w, out var window) => () => Open(path, new IntPtr(window)),
        ["--rebloquear", var path] => () => Relock(path),
        _ => null,
    };

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
        AfterLock(folder, result);
    }

    /// <summary>
    /// Doble clic sobre una carpeta bloqueada (la extensión del Explorador cancela la entrada y llama aquí),
    /// «Abrir carpeta protegida» o su archivo .doorman: pide la contraseña y muestra la carpeta.
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
        UnlockSession? session = null;
        if (AgentHost.Current is { } agent && (session = agent.Provider.BeginSession(folder)) is null)
            return; // Ya se está pidiendo la contraseña de esta carpeta.
        if (TryUnlock(folder, session))
            ExplorerWindows.Show(folder, window);
    }

    /// <summary>
    /// Un programa intenta leer un archivo de una carpeta bloqueada: se pide la contraseña y, si es
    /// correcta, la carpeta se desbloquea y el programa recibe el archivo. No se abre ninguna ventana
    /// del Explorador.
    /// </summary>
    public static void UnlockForProgram(string folder, UnlockSession session) => TryUnlock(folder, session);

    /// <summary>Pide la contraseña y descifra la carpeta.</summary>
    /// <param name="session">
    /// Los programas que esperan algún archivo de la carpeta (solo en el agente). Se cierra al
    /// terminar, se desbloquee o no.
    /// </param>
    /// <returns><c>true</c> si la carpeta ha quedado desbloqueada.</returns>
    static bool TryUnlock(string folder, UnlockSession? session)
    {
        bool unlocked = false;
        try
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
                ProgressForm.Run("DoorMan", $"Descifrando «{DisplayName(folder)}»…", progress =>
                {
                    FolderLocker.Unlock(folder, key, progress, session is null ? null : session.Extracted);
                    return true;
                });
                AppState.MarkUnlocked(folder, key);
            }
            unlocked = true;
            return true;
        }
        finally
        {
            session?.Finish(unlocked);
        }
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
                    AfterLock(folder, result);
                }
                return;
            }
        }

        // Sin la clave guardada (otro usuario de Windows, carpeta movida…) hay que elegir contraseña.
        Protect(folder, "No se ha encontrado la contraseña anterior de esta carpeta. Elige una contraseña para volver a bloquearla.");
    }

    static void AfterLock(string folder, LockResult result)
    {
        AppState.MarkLocked(folder);
        AgentHost.Current?.Watch(folder, result.InUse);
        ExplorerWindows.RefreshParentViews(folder);
        QuickAccess.Refresh(folder);
        ReportLocked(folder, result);
    }

    /// <summary>
    /// Si todo ha ido bien no se muestra nada: el icono de la carpeta ya indica que está bloqueada.
    /// Solo se avisa si quedaron archivos abiertos o sin borrar.
    /// </summary>
    static void ReportLocked(string folder, LockResult result)
    {
        if (result.NotDeleted.Count > 0)
            Notifications.Warning("Quedan elementos sin eliminar",
                $"«{DisplayName(folder)}» está cifrada, pero {Describe(result.NotDeleted)} estaba(n) en uso. Ciérralo(s) y elimínalo(s) a mano.");
        else if (result.InUse.Count > 0)
            Notifications.Warning("Hay archivos abiertos",
                $"«{DisplayName(folder)}» está cifrada, pero {Describe(result.InUse)} está(n) abierto(s). Su contenido se quitará en cuanto lo(s) cierres.");
    }

    static string Describe(IReadOnlyList<string> items)
    {
        var first = $"«{Path.GetFileName(items[0].TrimEnd('\\'))}»";
        return items.Count > 1 ? $"{first} y {items.Count - 1} elemento(s) más" : first;
    }

    static string DisplayName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    /// <summary>
    /// Evita que dos ventanas trabajen a la vez sobre la misma carpeta (p. ej. por un doble clic
    /// repetido), dentro del agente y entre procesos.
    /// </summary>
    sealed class FolderGuard : IDisposable
    {
        static readonly HashSet<string> Busy = new(StringComparer.OrdinalIgnoreCase);
        readonly Mutex _mutex;
        readonly string _folder;

        FolderGuard(Mutex mutex, string folder) => (_mutex, _folder) = (mutex, folder);

        public static FolderGuard? TryAcquire(string folder)
        {
            // El mutex no basta dentro del agente: el mismo hilo puede volver a entrar mientras muestra una ventana.
            lock (Busy)
                if (!Busy.Add(folder))
                    return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..32];
            var mutex = new Mutex(false, @"Local\DoorMan." + hash);
            try
            {
                if (mutex.WaitOne(0))
                    return new FolderGuard(mutex, folder);
            }
            catch (AbandonedMutexException)
            {
                return new FolderGuard(mutex, folder);
            }
            mutex.Dispose();
            lock (Busy)
                Busy.Remove(folder);
            return null;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            lock (Busy)
                Busy.Remove(_folder);
        }
    }
}
