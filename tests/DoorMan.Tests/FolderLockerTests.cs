using System.Security.Cryptography;
using DoorMan.Agent;
using DoorMan.Core;
using DoorMan.Crypto;

namespace DoorMan.Tests;

// The tests change FolderLocker.UsePlaceholders: they cannot run in parallel.
[CollectionDefinition("FolderLocker", DisableParallelization = true)]
public sealed class FolderLockerCollection;

[Collection("FolderLocker")]
public sealed class FolderLockerTests : IDisposable
{
    const int FastIterations = 10_000;
    readonly string _root = Path.Combine(Path.GetTempPath(), "doorman-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _folder;

    public FolderLockerTests()
    {
        // Non-ASCII names on purpose: paths and contents must survive any Unicode.
        _folder = Path.Combine(_root, "Private documents ñ");
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
            ["note.txt"] = "Bank password: 1234 — confidential"u8.ToArray(),
            [@"Photos\holidays 2025 été.jpg"] = RandomNumberGenerator.GetBytes(3 * ChunkFormat.ChunkSize + 777),
            [@"Photos\Exactly one chunk.bin"] = RandomNumberGenerator.GetBytes(ChunkFormat.ChunkSize),
            [@"Photos\Nested\deep\empty 空.txt"] = [],
            ["read only.txt"] = "do not touch"u8.ToArray(),
        };
        foreach (var (rel, data) in files)
        {
            var path = Path.Combine(_folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        Directory.CreateDirectory(Path.Combine(_folder, @"Empty folder\another empty"));
        File.SetAttributes(Path.Combine(_folder, "read only.txt"), FileAttributes.ReadOnly);
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "note.txt"), new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc));
        return files;
    }

    void AssertRestored(Dictionary<string, byte[]> files)
    {
        Assert.False(FolderLocker.IsLocked(_folder));
        Assert.False(CloudFiles.IsSyncRoot(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, "desktop.ini")));
        foreach (var (rel, data) in files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, rel)));
        Assert.True(Directory.Exists(Path.Combine(_folder, @"Empty folder\another empty")));
        Assert.True(File.GetAttributes(Path.Combine(_folder, "read only.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_folder, "note.txt")));
        Assert.False(new DirectoryInfo(_folder).Attributes.HasFlag(FileAttributes.ReadOnly));
        // Neither placeholders nor leftovers of the temporary unlock folder.
        foreach (var e in new DirectoryInfo(_folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }))
            Assert.False(e.Attributes.HasFlag(FileAttributes.ReparsePoint), e.FullName);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(_folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true })
            .Select(p => Path.GetRelativePath(_folder, p)).Order());
    }

    [Fact]
    public void Lock_LeavesPlaceholdersWithoutContent_AndUnlockRestoresEverything()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("secret ñ", FastIterations);

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
                // Without the agent nobody hands over the content: it cannot be read.
                Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(path));
            }
        }
        var vaultBytes = File.ReadAllBytes(Path.Combine(_folder, FolderLocker.VaultFileName));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("Bank password"u8));
        Assert.Equal(-1, vaultBytes.AsSpan().IndexOf("note.txt"u8));

        using var opened = FolderLocker.OpenKey(_folder, "secret ñ");
        FolderLocker.Unlock(_folder, opened);

        AssertRestored(files);
    }

    [Fact]
    public void WithoutPlaceholders_LockLeavesOnlyCiphertext_AndUnlockRestoresEverything()
    {
        FolderLocker.UsePlaceholders = false;
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("secret ñ", FastIterations);

        var result = FolderLocker.Lock(_folder, key);

        Assert.Empty(result.NotDeleted);
        Assert.True(FolderLocker.IsLocked(_folder));
        var remaining = Directory.GetFileSystemEntries(_folder).Select(p => Path.GetFileName(p)!).Order().ToArray();
        Assert.Equal(["desktop.ini", "Unlock folder.doorman"], remaining);

        using var opened = FolderLocker.OpenKey(_folder, "secret ñ");
        FolderLocker.Unlock(_folder, opened);

        AssertRestored(files);
    }

    /// <summary>Folders locked by versions before 3.0 have their encrypted file under the old Spanish name.</summary>
    [Fact]
    public void FolderLockedByAnEarlierVersion_IsRecognizedAndUnlocks()
    {
        FolderLocker.UsePlaceholders = false;
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        File.Move(Path.Combine(_folder, FolderLocker.VaultFileName), Path.Combine(_folder, FolderLocker.LegacyVaultFileName));

        Assert.True(FolderLocker.IsLocked(_folder));
        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);

        Assert.False(File.Exists(Path.Combine(_folder, FolderLocker.LegacyVaultFileName)));
        AssertRestored(files);
    }

    /// <summary>
    /// An app reads a file of the locked folder: Windows asks the provider, which unlocks the folder (here without a
    /// window, as if the password had been typed) and hands the file over.
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
            var big = @"Photos\holidays 2025 été.jpg";
            // Two apps at once: a single password prompt and both get their file.
            var reads = new[] { big, "note.txt" }.Select(rel => Task.Run(() => File.ReadAllBytes(Path.Combine(_folder, rel)))).ToArray();
            Assert.True(Task.WaitAll(reads, TimeSpan.FromSeconds(60)));
            Assert.Equal(files[big], reads[0].Result);
            Assert.Equal(files["note.txt"], reads[1].Result);
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
            Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(Path.Combine(_folder, "note.txt")));
        }
        finally
        {
            provider.Disconnect(_folder);
        }
        Assert.True(FolderLocker.IsLocked(_folder));
        Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(Path.Combine(_folder, "note.txt")));

        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        AssertRestored(files);
    }

    [Fact]
    public void FileOpenWhileLocking_IsEmptiedOnceClosed()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        var path = Path.Combine(_folder, "note.txt");

        LockResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = FolderLocker.Lock(_folder, key);
            Assert.Equal(["note.txt"], result.InUse);
            Assert.Equal(PlaceholderState.Hydrated, CloudFiles.GetState(path));
            Assert.False(CloudFiles.TryDehydrate(path));
        }
        // In practice the agent does it, since it is connected to the folder.
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
        File.Move(Path.Combine(_folder, "note.txt"), Path.Combine(_folder, "renamed.txt"));
        File.SetAttributes(Path.Combine(_folder, "read only.txt"), FileAttributes.Normal);
        File.Delete(Path.Combine(_folder, "read only.txt"));

        using var opened = FolderLocker.OpenKey(_folder, "x");
        FolderLocker.Unlock(_folder, opened);
        AssertRestored(files);
    }

    [Fact]
    public void WrongPassword_IsRejected_AndFolderStaysLocked()
    {
        CreateSampleContent();
        using var key = KeyMaterial.Create("correct", FastIterations);
        FolderLocker.Lock(_folder, key);

        Assert.Throws<WrongPasswordException>(() => FolderLocker.OpenKey(_folder, "wrong"));
        Assert.Throws<WrongPasswordException>(() => FolderLocker.OpenKey(_folder, "Correct"));
        Assert.True(FolderLocker.IsLocked(_folder));
        using var _ = FolderLocker.OpenKey(_folder, "correct");
    }

    [Fact]
    public void Relock_WithSameKey_KeepsTheSamePassword()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("same", FastIterations);
        FolderLocker.Lock(_folder, key);
        using (var opened = FolderLocker.OpenKey(_folder, "same"))
            FolderLocker.Unlock(_folder, opened);

        File.WriteAllText(Path.Combine(_folder, "new.txt"), "added while it was open");
        var reloaded = KeyMaterial.FromParts(key.Envelope.ToArray(), key.DataKey.ToArray());
        FolderLocker.Lock(_folder, reloaded);

        using var again = FolderLocker.OpenKey(_folder, "same");
        FolderLocker.Unlock(_folder, again);
        Assert.Equal("added while it was open", File.ReadAllText(Path.Combine(_folder, "new.txt")));
        Assert.Equal(files["note.txt"], File.ReadAllBytes(Path.Combine(_folder, "note.txt")));
    }

    [Fact]
    public void FileInUse_AbortsLock_WithoutTouchingTheFolder()
    {
        var files = CreateSampleContent();
        using var key = KeyMaterial.Create("x", FastIterations);
        using (new FileStream(Path.Combine(_folder, "note.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
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
        bytes[^100] ^= 0x01; // last chunk: some files will already have been restored
        File.SetAttributes(vault, FileAttributes.Normal); // the vault is hidden and a system file
        File.WriteAllBytes(vault, bytes);

        using var opened = FolderLocker.OpenKey(_folder, "x");
        Assert.Throws<InvalidDataException>(() => FolderLocker.Unlock(_folder, opened));

        Assert.True(FolderLocker.IsLocked(_folder));
        Assert.True(CloudFiles.IsSyncRoot(_folder));
        Assert.Equal(PlaceholderState.Dehydrated, CloudFiles.GetState(Path.Combine(_folder, "note.txt")));
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
            File.WriteAllText(Path.Combine(_folder, "note.txt"), "hello");
        using var key = KeyMaterial.Create("x", FastIterations);
        FolderLocker.Lock(_folder, key);
        var ini = File.ReadAllText(Path.Combine(_folder, "desktop.ini"));
        Assert.Contains(expectedIcon, ini);
        // In large icon views Explorer uses the icon of the class: each case has its own.
        Assert.Contains($"CLSID={DoorMan.Shell.ShellIntegration.LockedClsid(withContent)}", ini);

        // When it is brought up to date (agent), the icon must not change.
        FolderLocker.RefreshLockedAppearance(_folder);
        Assert.Contains(expectedIcon, File.ReadAllText(Path.Combine(_folder, "desktop.ini")));
    }

    [Fact]
    public void SymbolicLinks_AreRefused()
    {
        var target = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_folder, "link"), target);
        }
        catch (IOException)
        {
            return; // Creating links needs developer mode or administrator rights.
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
        Assert.True(PathUtil.IsSameOrInside(@"c:\data\PHOTOS\2024", @"C:\Data\photos"));
        Assert.False(PathUtil.IsSameOrInside(@"C:\Data\photos2", @"C:\Data\photos"));
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void ArgumentsOfEarlierVersions_AreStillAccepted()
    {
        Assert.Equal(["--open", @"C:\A b", "--window", "42"], Commands.NormalizeArguments(["--abrir", @"C:\A b", "--ventana", "42"]));
        Assert.Equal(["--lock", @"C:\x"], Commands.NormalizeArguments(["--REBLOQUEAR", @"C:\x"]));
        Assert.Equal(["--startup"], Commands.NormalizeArguments(["--inicio"]));
        Assert.NotNull(Commands.FromArguments(["--proteger", @"C:\x"]));
        Assert.NotNull(Commands.FromArguments(["--protect", @"C:\x"]));
        Assert.Null(Commands.FromArguments(["--protect"]));
    }
}

// Strings.Current is global: these tests cannot run alongside the others that show texts.
[Collection("FolderLocker")]
public sealed class StringsTests
{
    [Theory]
    [InlineData("en-US", "English")]
    [InlineData("es-CL", "Spanish")]
    [InlineData("fr-CA", "French")]
    [InlineData("zh-CN", "Chinese")]
    [InlineData("zh-TW", "Chinese")]
    [InlineData("hi-IN", "Hindi")]
    [InlineData("de-DE", "English")]
    public void Language_FollowsTheWindowsDisplayLanguage(string culture, string expected) =>
        Assert.Equal(expected, Strings.FromCulture(new System.Globalization.CultureInfo(culture)).ToString());

    /// <summary>Every text exists in every language and every format has as many placeholders as arguments.</summary>
    [Fact]
    public void EveryTextIsTranslated_AndFormatsAreValid()
    {
        var previous = Strings.Current;
        try
        {
            var english = new Dictionary<string, string>();
            foreach (var language in Enum.GetValues<Language>())
            {
                Strings.Current = language;
                foreach (var method in typeof(Strings).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (method.ReturnType != typeof(string))
                        continue;
                    var args = method.GetParameters()
                        .Select(p => p.ParameterType == typeof(int) ? (object)7 : $"ARG{p.Position}").ToArray();
                    var text = (string)method.Invoke(null, args)!;
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{method.Name} ({language})");
                    for (int i = 0; i < args.Length; i++)
                        Assert.Contains(args[i].ToString()!, text);
                    Assert.DoesNotContain("{", text);
                    if (language == Language.English)
                        english[method.Name] = text;
                    else if (method.Name is not ("get_OK" or "Quoted" or "AndMore"))
                        Assert.True(text != english[method.Name] || text.All(c => c < 128 && !char.IsLetter(c)),
                            $"{method.Name} is not translated to {language}");
                }
            }
        }
        finally
        {
            Strings.Current = previous;
        }
    }
}
