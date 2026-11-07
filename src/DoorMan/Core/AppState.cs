using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoorMan.Crypto;
using DoorMan.Shell;

namespace DoorMan.Core;

internal sealed record ProtectedFolder(string Path, bool Locked);

/// <summary>
/// Per-user state in %LOCALAPPDATA%\DoorMan: the list of managed folders and, while a folder is unlocked, its key
/// protected with DPAPI so that “Lock” can use the same password again. The key is deleted when locking.
/// </summary>
internal static class AppState
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorMan");
    static string IndexPath => Path.Combine(Dir, "folders.json");
    static string KeysDir => Path.Combine(Dir, "keys");
    // Names used by versions before 3.0, moved to the current ones the first time.
    static string LegacyIndexPath => Path.Combine(Dir, "carpetas.json");
    static string LegacyKeysDir => Path.Combine(Dir, "claves");

    public static void MarkLocked(string folder) => Update(set =>
    {
        set.Add(folder);
        DeleteKey(folder);
    });

    public static void MarkUnlocked(string folder, KeyMaterial key) => Update(set =>
    {
        set.Add(folder);
        SaveKey(folder, key);
    });

    /// <summary>Checks the list against the disk and updates the context menu.</summary>
    public static IReadOnlyList<ProtectedFolder> Sync()
    {
        IReadOnlyList<ProtectedFolder> result = [];
        Update(_ => { }, folders => result = folders);
        return result;
    }

    /// <summary>The managed folders, without checking them or taking the lock (for the watcher).</summary>
    public static IReadOnlyCollection<string> ReadIndex()
    {
        try
        {
            return Load();
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static DateTime IndexTimestamp() => File.Exists(IndexPath) ? File.GetLastWriteTimeUtc(IndexPath) : default;

    public static KeyMaterial? TryLoadKey(string folder)
    {
        MigrateLegacyFiles();
        var path = KeyPath(folder);
        if (!File.Exists(path))
            return null;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(folder), DataProtectionScope.CurrentUser);
            return KeyMaterial.FromParts(data[..KeyMaterial.EnvelopeSize], data[KeyMaterial.EnvelopeSize..]);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    static void Update(Action<HashSet<string>> change, Action<IReadOnlyList<ProtectedFolder>>? after = null)
    {
        using var mutex = new Mutex(false, @"Local\DoorMan.State");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException(Strings.SettingsUnavailable);
        }
        catch (AbandonedMutexException)
        {
            // The other process ended without releasing it; now it is ours.
        }
        try
        {
            MigrateLegacyFiles();
            var set = Load();
            change(set);
            var folders = Prune(set);
            Save(set);
            ShellIntegration.Refresh(folders);
            after?.Invoke(folders);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Moves the folder list and the keys of versions before 3.0 to their current names.</summary>
    static void MigrateLegacyFiles()
    {
        try
        {
            if (File.Exists(LegacyIndexPath) && !File.Exists(IndexPath))
                File.Move(LegacyIndexPath, IndexPath);
            if (Directory.Exists(LegacyKeysDir) && !Directory.Exists(KeysDir))
                Directory.Move(LegacyKeysDir, KeysDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is tried again next time.
        }
    }

    static List<ProtectedFolder> Prune(HashSet<string> set)
    {
        var result = new List<ProtectedFolder>();
        foreach (var folder in set.ToList())
        {
            bool locked = Directory.Exists(folder) && FolderLocker.IsLocked(folder);
            bool unlocked = !locked && Directory.Exists(folder) && File.Exists(KeyPath(folder));
            if (locked)
                FolderLocker.RefreshLockedAppearance(folder);
            else if (unlocked)
                FolderLocker.RemoveUnlockedAppearance(folder); // normal Windows icon (earlier versions used their own)
            if (locked || unlocked)
            {
                result.Add(new ProtectedFolder(folder, locked));
            }
            else
            {
                set.Remove(folder);
                DeleteKey(folder);
                if (Directory.Exists(folder))
                    FolderLocker.RemoveUnlockedAppearance(folder);
            }
        }
        return result.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(IndexPath))
                set.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(IndexPath)) ?? []);
        }
        catch (JsonException)
        {
            // Damaged list: it is rebuilt with the folders registered from now on.
        }
        return set;
    }

    static void Save(HashSet<string> set)
    {
        Directory.CreateDirectory(Dir);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(set.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    static void SaveKey(string folder, KeyMaterial key)
    {
        Directory.CreateDirectory(KeysDir);
        var plain = new byte[KeyMaterial.EnvelopeSize + KeyMaterial.KeySize];
        key.Envelope.CopyTo(plain, 0);
        key.DataKey.CopyTo(plain, KeyMaterial.EnvelopeSize);
        try
        {
            File.WriteAllBytes(KeyPath(folder), ProtectedData.Protect(plain, Entropy(folder), DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    static void DeleteKey(string folder)
    {
        var path = KeyPath(folder);
        if (File.Exists(path))
            File.Delete(path);
    }

    static string KeyPath(string folder) =>
        Path.Combine(KeysDir, Convert.ToHexString(SHA256.HashData(Entropy(folder)))[..32] + ".bin");

    static byte[] Entropy(string folder) => Encoding.UTF8.GetBytes(folder.ToUpperInvariant());
}
