using System.Security.Cryptography;
using CarpetaProtegida.Core;
using CarpetaProtegida.Crypto;

namespace CarpetaProtegida.Tests;

public sealed class FolderLockerTests : IDisposable
{
    const int FastIterations = 10_000;
    readonly string _root = Path.Combine(Path.GetTempPath(), "cp-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _folder;

    public FolderLockerTests()
    {
        _folder = Path.Combine(_root, "Mis documentos privados");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        foreach (var e in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            e.Attributes = FileAttributes.Normal;
        new DirectoryInfo(_root).Attributes = FileAttributes.Normal;
        Directory.Delete(_root, recursive: true);
    }

    Dictionary<string, byte[]> CreateSampleContent()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["nota.txt"] = "Contraseña del banco: 1234 — confidencial"u8.ToArray(),
            [@"Fotos\vacaciones año 2025.jpg"] = RandomNumberGenerator.GetBytes(3 * ChunkFormat.ChunkSize + 777),
            [@"Fotos\Exactamente un bloque.bin"] = RandomNumberGenerator.GetBytes(ChunkFormat.ChunkSize),
            [@"Fotos\Anidada\profunda\vacío.txt"] = [],
            ["solo lectura.txt"] = "no tocar"u8.ToArray(),
        };
        foreach (var (rel, data) in files)
        {
            var path = Path.Combine(_folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        Directory.CreateDirectory(Path.Combine(_folder, @"Carpeta vacía\otra vacía"));
        File.SetAttributes(Path.Combine(_folder, "solo lectura.txt"), FileAttributes.ReadOnly);
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "nota.txt"), new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc));
        return files;
    }

    [Fact]
    public void Lock_LeavesOnlyCiphertext_AndUnlockRestoresEverything()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("secreta ñ", FastIterations);

        var result = FolderLocker.Lock(_folder, key);

        Assert.Empty(result.NotDeleted);
        Assert.True(FolderLocker.IsLocked(_folder));
        var remaining = Directory.GetFileSystemEntries(_folder).Select(p => Path.GetFileName(p)!).Order().ToArray();
        Assert.Equal(["Desbloquear carpeta.cpbloq", "desktop.ini"], remaining);
        var vaultBytes = File.ReadAllBytes(Path.Combine(_folder, FolderLocker.VaultFileName));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("Contraseña"u8));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("nota.txt"u8));

        using var opened = FolderLocker.OpenKey(_folder, "secreta ñ");
        FolderLocker.Unlock(_folder, opened);

        Assert.False(FolderLocker.IsLocked(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, "desktop.ini")));
        foreach (var (rel, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, rel)));
        Assert.True(Directory.Exists(Path.Combine(_folder, @"Carpeta vacía\otra vacía")));
        Assert.True(File.GetAttributes(Path.Combine(_folder, "solo lectura.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_folder, "nota.txt")));
        Assert.False(new DirectoryInfo(_folder).Attributes.HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void WrongPassword_IsRejected_AndFolderStaysLocked()
    {
        CreateSampleContent();
        using var key = KeyMaterial.Create("correcta", FastIterations);
        FolderLocker.Lock(_folder, key);

        Assert.Throws<WrongPasswordException>(() => FolderLocker.OpenKey(_folder, "incorrecta"));
        Assert.Throws<WrongPasswordException>(() => FolderLocker.OpenKey(_folder, "Correcta"));
        Assert.True(FolderLocker.IsLocked(_folder));
        using var _ = FolderLocker.OpenKey(_folder, "correcta");
    }

    [Fact]
    public void Relock_WithSameKey_KeepsTheSamePassword()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("misma", FastIterations);
        FolderLocker.Lock(_folder, key);
        using (var opened = FolderLocker.OpenKey(_folder, "misma"))
            FolderLocker.Unlock(_folder, opened);

        File.WriteAllText(Path.Combine(_folder, "nuevo.txt"), "añadido mientras estaba abierta");
        var reloaded = KeyMaterial.FromParts(key.Envelope.ToArray(), key.DataKey.ToArray());
        FolderLocker.Lock(_folder, reloaded);

        using var again = FolderLocker.OpenKey(_folder, "misma");
        FolderLocker.Unlock(_folder, again);
        Assert.Equal("añadido mientras estaba abierta", File.ReadAllText(Path.Combine(_folder, "nuevo.txt")));
        Assert.Equal(files["nota.txt"], File.ReadAllBytes(Path.Combine(_folder, "nota.txt")));
    }

    [Fact]
    public void FileInUse_AbortsLock_WithoutTouchingTheFolder()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        using (new FileStream(Path.Combine(_folder, "nota.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => FolderLocker.Lock(_folder, key));

        Assert.False(FolderLocker.IsLocked(_folder));
        foreach (var (rel, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, rel)));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public void TamperedVault_FailsToUnlock_AndRollsBack()
    {
        CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        var vault = Path.Combine(_folder, FolderLocker.VaultFileName);
        var bytes = File.ReadAllBytes(vault);
        bytes[^100] ^= 0x01; // último bloque: algunos archivos ya se habrán restaurado
        File.SetAttributes(vault, FileAttributes.Normal); // la bóveda está oculta y es de sistema
        File.WriteAllBytes(vault, bytes);

        using var opened = FolderLocker.OpenKey(_folder, "x");
        Assert.Throws<InvalidDataException>(() => FolderLocker.Unlock(_folder, opened));

        Assert.True(FolderLocker.IsLocked(_folder));
        var remaining = Directory.GetFileSystemEntries(_folder).Select(p => Path.GetFileName(p)!).Order().ToArray();
        Assert.Equal(["Desbloquear carpeta.cpbloq", "desktop.ini"], remaining);
    }

    [Fact]
    public void TruncatedVault_IsDetected()
    {
        CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        var vault = Path.Combine(_folder, FolderLocker.VaultFileName);
        using (var fs = new FileStream(vault, FileMode.Open))
            fs.SetLength(fs.Length - ChunkFormat.TagSize - 50);

        using var opened = FolderLocker.OpenKey(_folder, "x");
        Assert.Throws<InvalidDataException>(() => FolderLocker.Unlock(_folder, opened));
        Assert.True(FolderLocker.IsLocked(_folder));
    }

    [Fact]
    public void ExistingDesktopIni_IsPreserved()
    {
        File.WriteAllText(Path.Combine(_folder, "desktop.ini"), "[.ShellClassInfo]\r\nIconResource=original.ico,0\r\n");
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);

        Assert.Contains("original.ico", File.ReadAllText(Path.Combine(_folder, "desktop.ini")));
    }

    [Fact]
    public void EmptyFolder_RoundTrips()
    {
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void SymbolicLinks_AreRefused()
    {
        var target = Path.Combine(_root, "fuera");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_folder, "enlace"), target);
        }
        catch (IOException)
        {
            return; // Crear enlaces requiere el modo de desarrollador o privilegios de administrador.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        using var key = KeyMaterial.Create("x", FastIterations);
        Assert.Throws<UserFacingException>(() => FolderLocker.Lock(_folder, key));
        Assert.False(FolderLocker.IsLocked(_folder));
    }
}

public sealed class FolderRulesTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Users")]
    public void SystemLocations_AreRefused(string path) =>
        Assert.Throws<UserFacingException>(() => FolderRules.EnsureCanProtect(path));

    [Fact]
    public void UserProfileAndAppData_AreRefused()
    {
        Assert.Throws<UserFacingException>(() => FolderRules.EnsureCanProtect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Throws<UserFacingException>(() => FolderRules.EnsureCanProtect(Path.GetTempPath()));
    }

    [Fact]
    public void Paths_AreComparedCaseInsensitively()
    {
        Assert.True(PathUtil.IsSameOrInside(@"c:\datos\FOTOS\2024", @"C:\Datos\fotos"));
        Assert.False(PathUtil.IsSameOrInside(@"C:\Datos\fotos2", @"C:\Datos\fotos"));
    }
}
