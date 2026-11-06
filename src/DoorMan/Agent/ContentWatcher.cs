using System.Collections.Concurrent;
using DoorMan.Core;

namespace DoorMan.Agent;

/// <summary>
/// Mantiene al día el icono de las carpetas desbloqueadas: vacía o con contenido. Vigila el primer
/// nivel de cada carpeta gestionada y, cuando se añade o se quita algo, vuelve a elegir el icono.
/// La lista de carpetas se relee cuando cambia el índice (lo puede cambiar otro proceso).
/// </summary>
internal sealed class ContentWatcher
{
    static readonly TimeSpan IndexCheck = TimeSpan.FromSeconds(3);
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);

    readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _indexTimer;
    readonly System.Windows.Forms.Timer _settleTimer;
    readonly SynchronizationContext _ui;
    DateTime _indexStamp;

    /// <remarks>Se crea en el hilo de la interfaz del agente.</remarks>
    public ContentWatcher()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Sin hilo de interfaz.");
        _indexTimer = new System.Windows.Forms.Timer { Interval = (int)IndexCheck.TotalMilliseconds };
        _indexTimer.Tick += (_, _) => RefreshFolders();
        _settleTimer = new System.Windows.Forms.Timer { Interval = (int)Settle.TotalMilliseconds };
        _settleTimer.Tick += (_, _) => ApplyChanges();
        RefreshFolders();
        _indexTimer.Start();
    }

    void RefreshFolders()
    {
        var stamp = AppState.IndexTimestamp();
        if (stamp == _indexStamp)
            return;
        _indexStamp = stamp;

        var folders = AppState.ReadIndex().Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _watchers.Keys.Where(f => !folders.Contains(f)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var folder in folders.Where(f => !_watchers.ContainsKey(f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += (_, e) => OnChange(folder, e.Name);
                watcher.Deleted += (_, e) => OnChange(folder, e.Name);
                watcher.Renamed += (_, e) => { OnChange(folder, e.OldName); OnChange(folder, e.Name); };
                // Si se pierden avisos (búfer lleno), se revisa la carpeta igualmente.
                watcher.Error += (_, _) => OnChange(folder, null);
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    /// <summary>Llega desde un hilo del vigilante: solo se anota y se atiende en el hilo de la interfaz.</summary>
    void OnChange(string folder, string? name)
    {
        if (name is not null && FolderLocker.IsOwnEntry(name))
            return;
        _changed[folder] = 0;
        _ui.Post(_ =>
        {
            // Se reinicia la espera: una copia de muchos archivos se atiende una sola vez al final.
            _settleTimer.Stop();
            _settleTimer.Start();
        }, null);
    }

    void ApplyChanges()
    {
        _settleTimer.Stop();
        foreach (var folder in _changed.Keys.ToList())
        {
            _changed.TryRemove(folder, out _);
            // Mientras se bloquea o desbloquea, el archivo cifrado existe y la carpeta cuenta como bloqueada.
            if (Directory.Exists(folder) && !FolderLocker.IsLocked(folder))
                FolderLocker.ApplyUnlockedAppearance(folder);
        }
    }
}
