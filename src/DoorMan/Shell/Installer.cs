using System.Diagnostics;
using DoorMan.Core;
using Microsoft.Win32;

namespace DoorMan.Shell;

/// <summary>
/// Instalación por usuario (sin administrador) en %LOCALAPPDATA%\Programs\DoorMan:
/// copia el ejecutable, activa la integración con el Explorador, añade el agente al inicio de
/// sesión (y lo arranca) y lo registra en «Aplicaciones instaladas» de Configuración.
/// </summary>
internal static class Installer
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DoorMan";
    const string ValueName = "DoorMan";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DoorMan");
    static string InstalledExe => Path.Combine(InstallDir, "DoorMan.exe");

    /// <returns>El código de salida: 0 si todo ha ido bien.</returns>
    public static int Install()
    {
        var current = Environment.ProcessPath!;
        if (!PathUtil.AreSame(current, InstalledExe))
        {
            // Copiar y continuar desde la copia instalada, para que el registro apunte a ella.
            Directory.CreateDirectory(InstallDir);
            // Una copia instalada en ejecución bloquearía el archivo: se cierra antes de sustituirla.
            StopOtherInstances();
            File.Copy(current, InstalledExe, overwrite: true);
            CopyExtension(Path.GetDirectoryName(current)!);
            CopyIcons(Path.GetDirectoryName(current)!);
            CopyPackage(Path.GetDirectoryName(current)!);
            // La copia instalada puede pedir UAC (ContextMenuPackage): que su aviso salga delante.
            ContextMenuPackage.AllowNextProcessForeground();
            using var child = Process.Start(InstalledExe, "--instalar")!;
            child.WaitForExit();
            return child.ExitCode;
        }

        ShellIntegration.Register();
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, $"\"{InstalledExe}\" --inicio");
        using (var app = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            app.SetValue("DisplayName", "DoorMan");
            app.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            app.SetValue("Publisher", "DoorMan");
            app.SetValue("DisplayIcon", InstalledExe);
            app.SetValue("InstallLocation", InstallDir);
            app.SetValue("UninstallString", $"\"{InstalledExe}\" --desinstalar");
            app.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            app.SetValue("NoModify", 1, RegistryValueKind.DWord);
            app.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        RefreshIconCache();
        // Menú contextual moderno de Windows 11: si falla, las opciones siguen en «Mostrar más opciones».
        // Con el paquete, las opciones del registro sobran (saldrían repetidas en «Mostrar más opciones»).
        ShellIntegration.UsesPackagedMenu = ContextMenuPackage.Install(InstallDir, InstalledExe, out var warning);
        AppState.Sync();
        if (warning is not null)
            UI.Notifications.Error("Menú contextual de Windows 11", warning);
        // El agente atiende a las carpetas bloqueadas desde ya, sin esperar al próximo inicio de sesión.
        Process.Start(new ProcessStartInfo(InstalledExe, "--agente") { UseShellExecute = false })?.Dispose();
        return 0;
    }

    public static void Uninstall()
    {
        // Los iconos de las carpetas desbloqueadas apuntan a la instalación: se quitan. Las bloqueadas
        // se dejan intactas para no tocar sus datos cifrados.
        foreach (var folder in AppState.Sync().Where(f => !f.Locked))
            FolderLocker.RemoveUnlockedAppearance(folder.Path);
        ShellIntegration.Unregister();
        // El certificado se queda en el equipo (quitarlo pediría UAC); sin su clave privada no firma nada más.
        ContextMenuPackage.Uninstall();
        ShellIntegration.UsesPackagedMenu = false;
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        StopOtherInstances();

        // Un ejecutable no puede borrarse a sí mismo: se borra cuando este proceso termina.
        if (PathUtil.AreSame(Environment.ProcessPath!, InstalledExe))
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.Dispose();
    }

    /// <summary>
    /// Los iconos se reinstalan con la misma ruta, así que el Explorador seguiría mostrando la copia
    /// que guardó en su caché: se le pide que la renueve y que vuelva a pintar las carpetas.
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
    /// Copia los iconos a «iconos\&lt;huella&gt;», una subcarpeta que cambia cuando cambian los iconos.
    /// El Explorador guarda en caché los iconos por ruta: si se sobrescribiera el mismo archivo,
    /// seguiría mostrando la versión antigua. Con una ruta nueva, las carpetas protegidas se
    /// actualizan a ella al sincronizar y el Explorador vuelve a leer los iconos.
    /// </summary>
    static void CopyIcons(string sourceDir)
    {
        var source = Path.Combine(sourceDir, "iconos");
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
        var root = Path.Combine(InstallDir, "iconos");
        var target = Path.Combine(root, Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant());
        Directory.CreateDirectory(target);
        foreach (var icon in icons)
            File.Copy(icon, Path.Combine(target, Path.GetFileName(icon)), overwrite: true);

        // Iconos de versiones anteriores.
        foreach (var old in Directory.GetFiles(root, "*.ico"))
            TryDelete(() => File.Delete(old));
        foreach (var old in Directory.GetDirectories(root).Where(d => !PathUtil.AreSame(d, target)))
            TryDelete(() => Directory.Delete(old, recursive: true));
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
            // En uso: se borrará en la próxima instalación.
        }
    }

    /// <summary>
    /// Copia la extensión del Explorador. Si el Explorador la tiene cargada no se puede sobrescribir,
    /// pero sí renombrar: la antigua se aparta y se borra en una instalación posterior.
    /// </summary>
    static void CopyExtension(string sourceDir)
    {
        var source = Path.Combine(sourceDir, ShellIntegration.ExtensionFileName);
        var target = Path.Combine(InstallDir, ShellIntegration.ExtensionFileName);
        if (!File.Exists(source))
            return;
        foreach (var old in Directory.GetFiles(InstallDir, ShellIntegration.ExtensionFileName + ".*.old"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sigue cargada en el Explorador; se borrará la próxima vez.
            }
        }
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
