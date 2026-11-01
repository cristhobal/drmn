using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarpetaProtegida.Crypto;
using CarpetaProtegida.Shell;

namespace CarpetaProtegida.Core;

internal sealed record ProtectedFolder(string Path, bool Locked);

/// <summary>
/// Estado por usuario en %LOCALAPPDATA%\CarpetaProtegida: la lista de carpetas gestionadas y,
/// mientras una carpeta está desbloqueada, su clave protegida con DPAPI para poder
/// «Volver a bloquear» con la misma contraseña. La clave se borra al bloquear.
/// </summary>
internal static class AppState
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarpetaProtegida");
    static string IndexPath => Path.Combine(Dir, "carpetas.json");
    static string KeysDir => Path.Combine(Dir, "claves");

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

    /// <summary>Revisa la lista contra el disco y actualiza el menú contextual.</summary>
    public static IReadOnlyList<ProtectedFolder> Sync()
    {
        IReadOnlyList<ProtectedFolder> result = [];
        Update(_ => { }, folders => result = folders);
        return result;
    }

    /// <summary>Las carpetas gestionadas, sin comprobarlas ni tomar el bloqueo (para el vigilante).</summary>
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
        using var mutex = new Mutex(false, @"Local\CarpetaProtegida.Estado");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("No se pudo acceder a la configuración de Carpeta Protegida.");
        }
        catch (AbandonedMutexException)
        {
            // El otro proceso terminó sin liberarlo; ahora es nuestro.
        }
        try
        {
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
                FolderLocker.ApplyUnlockedAppearance(folder); // vacía o con contenido puede haber cambiado
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
            // Índice dañado: se reconstruye con lo que se vaya registrando.
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
