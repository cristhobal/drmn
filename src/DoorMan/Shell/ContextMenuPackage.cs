using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DoorMan.Shell;

/// <summary>
/// DoorMan's option in the modern Windows 11 context menu. That menu only shows commands of apps with package
/// identity, so a sparse package (DoorMan.msix) is installed that points to the install folder and declares
/// MenuCommand from DoorManShell.dll. The registry entries (ShellIntegration) are still used under “Show more
/// options” and on Windows 10.
/// <para>
/// Windows requires the package to be signed with a certificate trusted by the whole machine: adding it needs
/// administrator rights (UAC) once per version. The certificate is created when publishing and its private key is
/// destroyed right after signing.
/// </para>
/// </summary>
internal static class ContextMenuPackage
{
    public const string PackageFileName = "DoorMan.msix";
    public const string CertificateFileName = "DoorMan.cer";
    const string PackageName = "DoorMan.ContextMenu";
    /// <summary>Package and certificate names of versions before 3.0, removed when installing.</summary>
    const string LegacyPackageName = "DoorMan.MenuContextual";
    const string LegacyCertificateSubject = "CN=DoorMan Paquete Local";
    const int ErrorCancelled = 1223;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Lets the next process started come to the foreground. Without this, the UAC prompt of a process started in
    /// the background shows up minimized in the taskbar and is easy to miss.
    /// </summary>
    public static void AllowNextProcessForeground() => AllowSetForegroundWindow(-1); // ASFW_ANY

    /// <summary>Trusts the certificate (with UAC if needed) and registers the package for the current user.</summary>
    /// <returns>Whether the package is installed. If not, <paramref name="warning"/> explains why when the user
    /// should be told (it is null on Windows 10 or when there is no package).</returns>
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
                using var elevated = Process.Start(new ProcessStartInfo(exePath, $"--trust-certificate \"{certificate}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                })!;
                elevated.WaitForExit();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return Strings.PackageUacDeclined;
            }
            if (!IsTrusted(certificate))
                return Strings.PackageCertificateFailed;
        }

        var error = RunPowerShell(
            RemovePackagesScript + "; " +
            $"Add-AppxPackage -Path {Quote(package)} -ExternalLocation {Quote(installDir)}");
        installed = error is null;
        return error is null ? null : Strings.PackageFailed(error);
    }

    public static void Uninstall()
    {
        if (IsSupported)
            RunPowerShell(RemovePackagesScript);
    }

    static string RemovePackagesScript =>
        $"Get-AppxPackage | Where-Object {{ $_.Name -in '{PackageName}', '{LegacyPackageName}' }} | Remove-AppxPackage";

    /// <summary>
    /// Run with administrator rights: adds the certificate to the machine's “Trusted People” and removes those of
    /// earlier DoorMan versions, whose private key no longer exists.
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
                if ((old.Subject == certificate.Subject || old.Subject == LegacyCertificateSubject)
                    && old.Thumbprint != certificate.Thumbprint)
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

    /// <returns>Null on success; otherwise, the error message.</returns>
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
            return process.ExitCode == 0 ? null : (error.Trim() is { Length: > 0 } e ? e : $"exit code {process.ExitCode}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
