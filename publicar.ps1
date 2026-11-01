# Genera publish\DoorMan.exe (no necesita .NET instalado) y publish\DoorManShell.dll,
# la extensión del Explorador que pide la contraseña antes de abrir una carpeta bloqueada.
$ErrorActionPreference = "Stop"
& "$PSScriptRoot\herramientas\generar-iconos.ps1"
& "$PSScriptRoot\native\DoorManShell\build.ps1"
# Primer dotnet que tenga un SDK instalado (el del PATH puede ser solo el entorno de ejecución).
$dotnet = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") |
    Where-Object { $_ -and (Test-Path $_) -and (& $_ --list-sdks 2>$null) } | Select-Object -First 1
if (-not $dotnet) { throw "No se encontró ningún SDK de .NET." }
& $dotnet publish "$PSScriptRoot\src\DoorMan" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$PSScriptRoot\publish"
if ($LASTEXITCODE -ne 0) { throw "La publicación ha fallado." }
