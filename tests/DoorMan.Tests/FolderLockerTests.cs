using System.Security.Cryptography;
using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Crypto;

namespace DoorMan.Tests;

// Las pruebas cambian FolderLocker.UsePlaceholders: no pueden ejecutarse en paralelo.
[CollectionDefinition("FolderLocker", DisableParallelization = true)]
public sealed class FolderLockerCollection;

[Collection("FolderLocker")]
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
        FolderLocker.UsePlaceholders = true;
        CloudFiles.Unregister(_folder);
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

    void AssertRestored(Dictionary<string, byte[]> files)
    {
        Assert.False(FolderLocker.IsLocked(_folder));
        Assert.False(CloudFiles.IsSyncRoot(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, "desktop.ini")));
        foreach (var (rel, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, rel)));
        Assert.True(Directory.Exists(Path.Combine(_folder, @"Carpeta vacía\otra vacía")));
        Assert.True(File.GetAttributes(Path.Combine(_folder, "solo lectura.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_folder, "nota.txt")));
        Assert.False(new DirectoryInfo(_folder).Attributes.HasFlag(FileAttributes.ReadOnly));
        // Ni marcadores ni restos de la carpeta temporal del desbloqueo.
        foreach (var e in new DirectoryInfo(_folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }))
            Assert.False(e.Attributes.HasFlag(FileAttributes.ReparsePoint), e.FullName);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(_folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true })
            .Select(p => Path.GetRelativePath(_folder, p)).Order());
    }

    [Fact]
    public void Lock_LeavesPlaceholdersWithoutContent_AndUnlockRestoresEverything()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("secreta ñ", FastIterations);

        var result = FolderLocker.Lock(_folder, key);

        Assert.Empty(result.NotDeleted);
        Assert.Empty(result.InUse);
        Assert.True(FolderLocker.IsLocked(_folder));
        Assert.True(CloudFiles.IsSyncRoot(_folder));
        foreach (var (rel, data) in files)
        {
            var path = Path.Combine(_folder, rel);
            Assert.Equal(data.Length, new FileInfo(path).Length);
            if (data.Length > 0)
            {
                Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(path));
                // Sin el agente nadie entrega el contenido: no se puede leer.
                Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(path));
            }
        }
        var vaultBytes = File.ReadAllBytes(Path.Combine(_folder, FolderLocker.VaultFileName));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("Contraseña"u8));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("nota.txt"u8));

        using var opened = FolderLocker.OpenKey(_folder, "secreta ñ");
        FolderLocker.Unlock(_folder, opened);

        AssertRestored(files);
    }

    [Fact]
    public void WithoutPlaceholders_LockLeavesOnlyCiphertext_AndUnlockRestoresEverything()
    {
        FolderLocker.UsePlaceholders = false;
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("secreta ñ", FastIterations);

        var result = FolderLocker.Lock(_folder, key);

        Assert.Empty(result.NotDeleted);
        Assert.True(FolderLocker.IsLocked(_folder));
        var remaining = Directory.GetFileSystemEntries(_folder).Select(p => Path.GetFileName(p)!).Order().ToArray();
        Assert.Equal(["Desbloquear carpeta.doorman", "desktop.ini"], remaining);

        using var opened = FolderLocker.OpenKey(_folder, "secreta ñ");
        FolderLocker.Unlock(_folder, opened);

        AssertRestored(files);
    }

    /// <summary>
    /// Un programa lee un archivo de la carpeta bloqueada: Windows se lo pide al proveedor, que
    /// desbloquea la carpeta (aquí sin ventana, como si se hubiera escrito la contraseña) y se lo entrega.
    /// </summary>
    [Fact]
    public void ReadingAPlaceholder_UnlocksTheFolder_AndTheProgramGetsTheFile()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);

        int prompts = 0;
        var provider = new CloudProvider((folder, session) =>
        {
            Interlocked.Increment(ref prompts);
            Task.Run(() =>
            {
                bool ok = false;
                try
                {
                    using var opened = FolderLocker.OpenKey(folder, "x");
                    FolderLocker.Unlock(folder, opened, extracted: session.Extracted);
                    ok = true;
                }
                finally
                {
                    session.Finish(ok);
                }
            });
        });
        provider.Connect(_folder);
        try
        {
            var big = @"Fotos\vacaciones año 2025.jpg";
            // Dos programas a la vez: una sola petición de contraseña y los dos reciben su archivo.
            var reads = new[] { big, "nota.txt" }.Select(rel => Task.Run(() => File.ReadAllBytes(Path.Combine(_folder, rel)))).ToArray();
            Assert.True(Task.WaitAll(reads, TimeSpan.FromSeconds(60)));
            Assert.Equal(files[big], reads[0].Result);
            Assert.Equal(files["nota.txt"], reads[1].Result);
            Assert.Equal(1, prompts);
            SpinWait.SpinUntil(() => !FolderLocker.IsLocked(_folder), TimeSpan.FromSeconds(30));
        }
        finally
        {
            provider.Disconnect(_folder);
        }
        AssertRestored(files);
    }

    [Fact]
    public void CancellingThePassword_DeniesTheProgram_AndKeepsTheFolderLocked()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);

        var provider = new CloudProvider((_, session) => Task.Run(() => session.Finish(unlocked: false)));
        provider.Connect(_folder);
        try
        {
            Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(Path.Combine(_folder, "nota.txt")));
        }
        finally
        {
            provider.Disconnect(_folder);
        }
        Assert.True(FolderLocker.IsLocked(_folder));
        Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(Path.Combine(_folder, "nota.txt")));

        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        AssertRestored(files);
    }

    [Fact]
    public void FileOpenWhileLocking_IsEmptiedOnceClosed()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        var path = Path.Combine(_folder, "nota.txt");

        LockResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = FolderLocker.Lock(_folder, key);
            Assert.Equal(["nota.txt"], result.InUse);
            Assert.Equal(PlaceholderState.Hydrated, CloudFiles.GetState(path));
            Assert.False(CloudFiles.TryDehydrate(path));
        }
        // En la práctica lo hace el agente, que está conectado a la carpeta.
        using (CloudFiles.TryConnectForConversion(_folder))
            Assert.True(CloudFiles.TryDehydrate(path));
        Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(path));

        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        AssertRestored(files);
    }

    [Fact]
    public void PlaceholdersRenamedOrDeletedWhileLocked_AreRestoredFromTheVault()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        File.Move(Path.Combine(_folder, "nota.txt"), Path.Combine(_folder, "renombrada.txt"));
        File.SetAttributes(Path.Combine(_folder, "solo lectura.txt"), FileAttributes.Normal);
        File.Delete(Path.Combine(_folder, "solo lectura.txt"));

        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        AssertRestored(files);
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
        Assert.True(CloudFiles.IsSyncRoot(_folder));
        Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(Path.Combine(_folder, "nota.txt")));
        Assert.Empty(Directory.GetDirectories(_folder, ".doorman-*", new EnumerationOptions { AttributesToSkip = 0 }));
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

    [Theory]
    [InlineData(false, "folder_locked.ico")]
    [InlineData(true, "filled_locked.ico")]
    public void LockedIcon_DependsOnContent(bool withContent, string expectedIcon)
    {
        if (withContent)
            File.WriteAllText(Path.Combine(_folder, "nota.txt"), "hola");
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        var ini = File.ReadAllText(Path.Combine(_folder, "desktop.ini"));
        Assert.Contains(expectedIcon, ini);
        // En las vistas grandes el Explorador usa el icono de la clase: cada caso lleva la suya.
        Assert.Contains($"CLSID={DoorMan.Shell.ShellIntegration.LockedClsid(withContent)}", ini);

        // Al ponerse al día (agente), el icono no debe cambiar.
        FolderLocker.RefreshLockedAppearance(_folder);
        Assert.Contains(expectedIcon, File.ReadAllText(Path.Combine(_folder, "desktop.ini")));
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
