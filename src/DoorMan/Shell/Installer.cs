using System.Diagnostics;
using System.Runtime.InteropServices;
using DoorMan.Core;
using Microsoft.Win32;

namespace DoorMan.Shell;

/// <summary>
/// Per-user installation (no administrator rights) in %LOCALAPPDATA%\Programs\DoorMan: copies the executable,
/// turns on the File Explorer integration, adds the agent to sign-in (and starts it) and registers DoorMan in the
/// installed apps list of Windows Settings.
/// </summary>
internal static class Installer
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DoorMan";
    const string ValueName = "DoorMan";
    public const string IconsDirName = "icons";
    /// <summary>Icon folder of versions before 3.0.</summary>
    const string LegacyIconsDirName = "iconos";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DoorMan");
    static string InstalledExe => Path.Combine(InstallDir, "DoorMan.exe");

    /// <summary>Whether this process is the installed copy (and not, for example, the file just downloaded).</summary>
    public static bool IsRunningInstalled => PathUtil.AreSame(Environment.ProcessPath!, InstalledExe);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>
    /// The Explorer extension must match the architecture of Windows, and each executable carries its own: a build
    /// for another processor (the 32-bit one on 64-bit Windows, the x64 one on ARM) cannot be installed.
    /// </summary>
    public static bool IsWrongArchitecture => RuntimeInformation.OSArchitecture != RuntimeInformation.ProcessArchitecture;

    /// <summary>The file to download for this computer.</summary>
    public static string ExpectedDownload =>
        $"drmn-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}.exe";

    public const string DownloadUrl = "https://github.com/cristhobal/drmn/releases/latest";

    /// <returns>The exit code: 0 if everything went well.</returns>
    public static int Install()
    {
        var current = Environment.ProcessPath!;
        if (!PathUtil.AreSame(current, InstalledExe))
        {
            // Copy and continue from the installed copy, so that the registry points to it.
            Directory.CreateDirectory(InstallDir);
            // A running installed copy would lock the file: it is closed before replacing it.
            StopOtherInstances();
            File.Copy(current, InstalledExe, overwrite: true);
            // The files that go next to the executable are inside it; in a debug build they are loose beside it.
            using (var payload = Payload.Extract())
            {
                var source = payload?.Directory ?? Path.GetDirectoryName(current)!;
                CopyExtension(source);
                CopyIcons(source);
                CopyPackage(source);
            }
            // The installed copy may ask for UAC (ContextMenuPackage): let its prompt come to the front.
            ContextMenuPackage.AllowNextProcessForeground();
            using var child = Process.Start(InstalledExe, "--install")!;
            child.WaitForExit();
            return child.ExitCode;
        }

        ShellIntegration.Register();
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, $"\"{InstalledExe}\" --startup");
        using (var app = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            app.SetValue("DisplayName", "DoorMan");
            app.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            app.SetValue("Publisher", "DoorMan");
            app.SetValue("DisplayIcon", InstalledExe);
            app.SetValue("InstallLocation", InstallDir);
            app.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            app.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            app.SetValue("NoModify", 1, RegistryValueKind.DWord);
            app.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        RefreshIconCache();
        // Modern Windows 11 context menu: if it fails, the options stay under “Show more options”.
        // With the package, the registry entries are not needed (they would be repeated under “Show more options”).
        ShellIntegration.UsesPackagedMenu = ContextMenuPackage.Install(InstallDir, InstalledExe, out var warning);
        AppState.Sync();
        if (warning is not null)
            UI.Notifications.Error(Strings.ContextMenuTitle, warning);
        // The agent serves locked folders from now on, without waiting for the next sign-in.
        Process.Start(new ProcessStartInfo(InstalledExe, "--agent") { UseShellExecute = false })?.Dispose();
        return 0;
    }

    public static void Uninstall()
    {
        // Icons of unlocked folders point to the installation: they are removed. Locked folders are left as they
        // are so that their encrypted data is not touched.
        foreach (var folder in AppState.Sync().Where(f => !f.Locked))
            FolderLocker.RemoveUnlockedAppearance(folder.Path);
        ShellIntegration.Unregister();
        // The certificate stays on the machine (removing it would need UAC); without its private key it signs nothing else.
        ContextMenuPackage.Uninstall();
        ShellIntegration.UsesPackagedMenu = false;
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        StopOtherInstances();

        // An executable cannot delete itself: it is deleted when this process ends.
        if (PathUtil.AreSame(Environment.ProcessPath!, InstalledExe))
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.Dispose();
    }

    /// <summary>
    /// Asks Explorer to renew its icon cache and repaint the folders: otherwise it could keep showing the icons it
    /// had cached for the same paths.
    /// </summary>
    static void RefreshIconCache()
    {
        try
        {
            using var refresh = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "ie4uinit.exe"), "-show") { CreateNoWindow = true, UseShellExecute = false });
            refresh?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        foreach (var folder in AppState.Sync())
            ShellNotify.ItemChanged(folder.Path);
        ShellNotify.AssociationsChanged();
    }

    /// <summary>
    /// Copies the icons to “icons\&lt;hash&gt;”, a subfolder that changes whenever the icons change. Explorer caches
    /// icons by path: if the same file were overwritten, it would keep showing the old one. With a new path, locked
    /// folders are moved to it when syncing and Explorer reads the icons again.
    /// </summary>
    static void CopyIcons(string sourceDir)
    {
        var source = Path.Combine(sourceDir, IconsDirName);
        if (!Directory.Exists(source))
            return;
        var icons = Directory.GetFiles(source, "*.ico").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (icons.Count == 0)
            return;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var icon in icons)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(icon)));
            hash.AppendData(File.ReadAllBytes(icon));
        }
        var root = Path.Combine(InstallDir, IconsDirName);
        var target = Path.Combine(root, Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant());
        Directory.CreateDirectory(target);
        foreach (var icon in icons)
            File.Copy(icon, Path.Combine(target, Path.GetFileName(icon)), overwrite: true);

        // Icons of earlier versions.
        foreach (var old in Directory.GetFiles(root, "*.ico"))
            TryDelete(() => File.Delete(old));
        foreach (var old in Directory.GetDirectories(root).Where(d => !PathUtil.AreSame(d, target)))
            TryDelete(() => Directory.Delete(old, recursive: true));
        var legacy = Path.Combine(InstallDir, LegacyIconsDirName);
        if (Directory.Exists(legacy))
            TryDelete(() => Directory.Delete(legacy, recursive: true));
        Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
    }

    static void CopyPackage(string sourceDir)
    {
        foreach (var name in new[] { ContextMenuPackage.PackageFileName, ContextMenuPackage.CertificateFileName })
        {
            var source = Path.Combine(sourceDir, name);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(InstallDir, name), overwrite: true);
        }
    }

    static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use: it will be deleted on the next installation.
        }
    }

    /// <summary>
    /// Copies the Explorer extension. If Explorer has it loaded it cannot be overwritten, but it can be renamed:
    /// the old one is moved aside and deleted on a later installation.
    /// </summary>
    static void CopyExtension(string sourceDir)
    {
        var source = Path.Combine(sourceDir, ShellIntegration.ExtensionFileName);
        var target = Path.Combine(InstallDir, ShellIntegration.ExtensionFileName);
        if (!File.Exists(source))
            return;
        foreach (var old in Directory.GetFiles(InstallDir, ShellIntegration.ExtensionFileName + ".*.old"))
            TryDelete(() => File.Delete(old));
        if (File.Exists(target))
            File.Move(target, $"{target}.{DateTime.UtcNow.Ticks}.old");
        File.Copy(source, target);
    }

    static void StopOtherInstances()
    {
        foreach (var running in Process.GetProcessesByName("DoorMan"))
        {
            using (running)
            {
                if (running.Id == Environment.ProcessId || !PathUtil.AreSame(running.MainModule?.FileName ?? "", InstalledExe))
                    continue;
                running.Kill();
                running.WaitForExit(5000);
            }
        }
    }
}
