# Genera icons\generados\*.ico con todos los tamaños que usa Windows a partir de los PNG originales
# de icons\ (en alta resolución, 1024 px o más). Sin los tamaños pequeños, el Explorador reduce la
# imagen grande sobre la marcha y se ve pixelada.
#
# folder_locked / filled_locked son los de Windows 11; los win10_* imitan las carpetas de Windows 10
# (la aplicación elige según el sistema, ver AppIcons).
#
# Cada tamaño se obtiene reduciendo a la mitad de forma progresiva con bicúbica de alta calidad,
# que conserva mucho mejor los bordes finos que una única reducción de 256 a 16.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "icons"
$target = Join-Path $source "generados"
$sizes = 256, 128, 96, 64, 48, 40, 32, 24, 20, 16

function Resize([System.Drawing.Bitmap]$image, [int]$size) {
    $result = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($result)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # sin halos en los bordes
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $image.Width, $image.Height,
        [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $g.Dispose()
    return $result
}

# Máscara de enfoque suave: al reducir, la bicúbica deja los bordes algo blandos. Se aplica sobre
# los colores premultiplicados por el alfa para no crear halos en el contorno transparente.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Enfoque
{
    public static void Aplicar(Bitmap bmp, double cantidad)
    {
        int w = bmp.Width, h = bmp.Height, n = w * h * 4;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[n];
        Marshal.Copy(data.Scan0, px, 0, n);
        var pre = new double[n];
        for (int i = 0; i < n; i += 4)
        {
            double a = px[i + 3] / 255.0;
            for (int c = 0; c < 3; c++) pre[i + c] = px[i + c] * a;
            pre[i + 3] = px[i + 3];
        }
        var sharp = new double[n];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            for (int c = 0; c < 4; c++)
            {
                // Desenfoque 3x3 (1-2-1) como referencia de la máscara.
                double sum = 0, weight = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = Math.Min(w - 1, Math.Max(0, x + dx)), yy = Math.Min(h - 1, Math.Max(0, y + dy));
                    double k = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
                    sum += pre[(yy * w + xx) * 4 + c] * k;
                    weight += k;
                }
                sharp[i + c] = pre[i + c] + cantidad * (pre[i + c] - sum / weight);
            }
        }
        for (int i = 0; i < n; i += 4)
        {
            double alpha = Math.Max(0, Math.Min(255, sharp[i + 3]));
            px[i + 3] = (byte)Math.Round(alpha);
            for (int c = 0; c < 3; c++)
                px[i + c] = alpha <= 0 ? (byte)0 : (byte)Math.Round(Math.Max(0, Math.Min(255, sharp[i + c] * 255.0 / alpha)));
        }
        Marshal.Copy(px, 0, data.Scan0, n);
        bmp.UnlockBits(data);
    }
}
"@

function Scale([System.Drawing.Bitmap]$original, [int]$size) {
    $current = $original
    while ($current.Width -ge $size * 2) {
        $half = Resize $current ([int]($current.Width / 2))
        if ($current -ne $original) { $current.Dispose() }
        $current = $half
    }
    if ($current.Width -ne $size) {
        $final = Resize $current $size
        if ($current -ne $original) { $current.Dispose() }
        $current = $final
    }
    # Más enfoque cuanto más pequeño; 256 y 128 quedan como el original.
    $amount = if ($size -le 32) { 0.6 } elseif ($size -le 96) { 0.4 } else { 0 }
    if ($amount -gt 0) { [Enfoque]::Aplicar($current, $amount) }
    return $current
}

function Read-Source([string]$path) {
    # Se copia a un Bitmap propio en 32 bpp ARGB para no dejar el archivo bloqueado.
    $loaded = [System.Drawing.Image]::FromFile($path)
    try {
        if ($loaded.Width -ne $loaded.Height) { throw "$path no es cuadrado ($($loaded.Width)x$($loaded.Height))" }
        if ($loaded.Width -lt 256) { throw "$path mide $($loaded.Width) px; hacen falta al menos 256" }
        $bitmap = New-Object System.Drawing.Bitmap $loaded.Width, $loaded.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($loaded, 0, 0, $loaded.Width, $loaded.Height)
        $g.Dispose()
        return $bitmap
    } finally { $loaded.Dispose() }
}

New-Item -ItemType Directory -Force $target | Out-Null
# Sin restos de iconos cuyo PNG ya no existe: se instalan todos los .ico de esta carpeta.
Get-ChildItem $target -Filter *.ico | Remove-Item
foreach ($file in Get-ChildItem $source -Filter *.png) {
    $original = Read-Source $file.FullName
    $sourceSize = $original.Width
    $images = foreach ($size in $sizes) {
        $bitmap = Scale $original $size
        $png = New-Object IO.MemoryStream
        $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($bitmap -ne $original) { $bitmap.Dispose() }
        , @($size, $png.ToArray())
    }
    $original.Dispose()

    # Formato ICO: cabecera, una entrada de 16 bytes por imagen y después los PNG.
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $size = $image[0]; $data = $image[1]
        $dim = if ($size -ge 256) { 0 } else { $size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($image in $images) { $w.Write($image[1]) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $target ($file.BaseName + ".ico")), $out.ToArray())
    "{0}.ico: {1} tamaños (origen {2} px)" -f $file.BaseName, $images.Count, $sourceSize
}
