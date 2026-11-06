# Genera publish\DoorMan.msix y publish\DoorMan.cer: el paquete disperso que muestra la opción de DoorMan en el
# menú contextual moderno de Windows 11 (ver native\DoorManShell\paquete\AppxManifest.xml).
#
# Windows solo instala un paquete así si está firmado con un certificado de confianza para todo el equipo. Cada
# vez se crea un certificado nuevo, se firma el paquete y se borra la clave privada en el acto: nadie puede
# firmar nada más con él. El instalador añade el .cer a las personas de confianza del equipo (pide UAC) y quita
# los de versiones anteriores.
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$publisher = "CN=DoorMan Paquete Local"
$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$sdk = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw "No se encontró el SDK de Windows (makeappx.exe, signtool.exe)." }
$makeappx = Join-Path $sdk.FullName "x64\makeappx.exe"
$signtool = Join-Path $sdk.FullName "x64\signtool.exe"

# Versión del paquete: la de la aplicación (DoorMan.csproj) con cuatro números.
[xml]$project = Get-Content (Join-Path $root "src\DoorMan\DoorMan.csproj")
$version = ([version]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)).ToString(3) + ".0"

$staging = Join-Path $root "native\DoorManShell\bin\paquete"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $staging "Assets") | Out-Null
(Get-Content (Join-Path $root "native\DoorManShell\paquete\AppxManifest.xml") -Raw -Encoding utf8).
    Replace('$VERSION$', $version).Replace('$PUBLISHER$', $publisher) |
    Set-Content (Join-Path $staging "AppxManifest.xml") -Encoding utf8

$source = [System.Drawing.Image]::FromFile((Join-Path $root "icons\filled_locked.png"))
try {
    foreach ($size in 44, 50, 150) {
        $logo = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($logo)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $logo.Save((Join-Path $staging "Assets\logo$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $logo.Dispose()
    }
} finally {
    $source.Dispose()
}

$msix = Join-Path $Output "DoorMan.msix"
$cer = Join-Path $Output "DoorMan.cer"
& $makeappx pack /d $staging /p $msix /o /nv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "No se pudo crear el paquete." }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(20) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
try {
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el paquete." }
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
} finally {
    # La clave privada se destruye y el certificado sale de todos los almacenes del usuario (Windows
    # también lo copia en «Entidades de certificación intermedias»).
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
    Get-ChildItem Cert:\CurrentUser -Recurse | Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
        ForEach-Object { Remove-Item $_.PSPath -Force }
}
