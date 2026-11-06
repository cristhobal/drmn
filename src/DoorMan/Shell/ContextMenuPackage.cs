using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DoorMan.Shell;

/// <summary>
/// La opción de DoorMan en el menú contextual moderno de Windows 11. Ese menú solo muestra comandos de
/// aplicaciones con identidad de paquete, así que se instala un paquete disperso (DoorMan.msix) que apunta a
/// la carpeta de instalación y declara MenuCommand de DoorManShell.dll. Las opciones del registro
/// (ShellIntegration) siguen en «Mostrar más opciones» y en Windows 10.
/// <para>
/// Windows exige que el paquete esté firmado con un certificado de confianza para todo el equipo: añadirlo
/// pide permisos de administrador (UAC) una vez por versión. El certificado se genera al publicar y su clave
/// privada se destruye tras firmar (herramientas\generar-paquete.ps1).
/// </para>
/// </summary>
internal static class ContextMenuPackage
{
    public const string PackageFileName = "DoorMan.msix";
    public const string CertificateFileName = "DoorMan.cer";
    const string PackageName = "DoorMan.MenuContextual";
    const int ErrorCancelled = 1223;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Cede el primer plano al proceso que se lance a continuación. Sin esto, el aviso de UAC de un proceso
    /// lanzado en segundo plano aparece minimizado en la barra de tareas y es fácil no verlo.
    /// </summary>
    public static void AllowNextProcessForeground() => AllowSetForegroundWindow(-1); // ASFW_ANY

    /// <summary>Confía en el certificado (con UAC si hace falta) y registra el paquete para el usuario actual.</summary>
    /// <returns>Si el paquete ha quedado instalado. Si no, <paramref name="warning"/> explica el motivo cuando
    /// hay que avisar al usuario (es null en Windows 10 o si no hay paquete).</returns>
    public static bool Install(string installDir, string exePath, out string? warning)
    {
        warning = TryInstall(installDir, exePath, out var installed);
        return installed;
    }

    static string? TryInstall(string installDir, string exePath, out bool installed)
    {
        installed = false;
        var package = Path.Combine(installDir, PackageFileName);
        var certificate = Path.Combine(installDir, CertificateFileName);
        if (!IsSupported || !File.Exists(package) || !File.Exists(certificate))
            return null;

        if (!IsTrusted(certificate))
        {
            try
            {
                AllowNextProcessForeground();
                using var elevated = Process.Start(new ProcessStartInfo(exePath, $"--confiar-certificado \"{certificate}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                })!;
                elevated.WaitForExit();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return "No se concedieron permisos de administrador: las opciones de DoorMan seguirán en «Mostrar más opciones».";
            }
            if (!IsTrusted(certificate))
                return "No se pudo añadir el certificado de DoorMan: las opciones seguirán en «Mostrar más opciones».";
        }

        var error = RunPowerShell(
            $"Get-AppxPackage -Name '{PackageName}' | Remove-AppxPackage; " +
            $"Add-AppxPackage -Path {Quote(package)} -ExternalLocation {Quote(installDir)}");
        installed = error is null;
        return error is null ? null : $"No se pudo registrar el menú contextual de Windows 11: {error}";
    }

    public static void Uninstall()
    {
        if (IsSupported)
            RunPowerShell($"Get-AppxPackage -Name '{PackageName}' | Remove-AppxPackage");
    }

    /// <summary>
    /// Ejecutado con permisos de administrador: añade el certificado a «Personas de confianza» del equipo y
    /// quita los de versiones anteriores de DoorMan (mismo sujeto), cuya clave ya no existe.
    /// </summary>
    public static void TrustCertificate(string certificatePath)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var old in store.Certificates)
        {
            using (old)
            {
                if (old.Subject == certificate.Subject && old.Thumbprint != certificate.Thumbprint)
                    store.Remove(old);
            }
        }
        store.Add(certificate);
    }

    static bool IsTrusted(string certificatePath)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
        try
        {
            return found.Count > 0;
        }
        finally
        {
            foreach (var c in found)
                c.Dispose();
        }
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <returns>Null si ha ido bien; si no, el mensaje de error.</returns>
    static string? RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'; " + script));
        var info = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            output.Wait();
            process.WaitForExit();
            return process.ExitCode == 0 ? null : (error.Trim() is { Length: > 0 } e ? e : $"código {process.ExitCode}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
