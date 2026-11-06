# Compila DoorManShell.dll (x64, CRT estática: no necesita los redistribuibles de Visual C++).
# -Clsid y -Output permiten compilar versiones de prueba con otra clase y otro nombre de archivo.
param([string]$Clsid, [string]$Output = "bin\DoorManShell.dll")
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No se encontraron las herramientas de C++ de Visual Studio." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$define = ""
if ($Clsid) {
    $b = ([guid]$Clsid).ToByteArray()
    $d1 = [BitConverter]::ToUInt32($b, 0); $d2 = [BitConverter]::ToUInt16($b, 4); $d3 = [BitConverter]::ToUInt16($b, 6)
    $rest = ($b[8..15] | ForEach-Object { "0x{0:x2}" -f $_ }) -join ","
    $define = "/D`"LOCKED_CLSID={{0x{0:x8},0x{1:x4},0x{2:x4},{{{3}}}}}`"" -f $d1, $d2, $d3, $rest
}
New-Item -ItemType Directory -Force "$PSScriptRoot\bin" | Out-Null
Push-Location $PSScriptRoot
try {
    # vcvars64 avisa en stderr de que no encuentra vswhere en el PATH; es inofensivo.
    cmd /c "call `"$vcvars`" >nul 2>&1 && cl /nologo /LD /O2 /MT /W4 /WX /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE $define DoorManShell.cpp /Fobin\ /Fe`"$Output`" /link /DEF:DoorManShell.def"
    if ($LASTEXITCODE -ne 0) { throw "La compilación de la extensión ha fallado." }
} finally {
    Pop-Location
}
