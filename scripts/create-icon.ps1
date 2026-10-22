param(
    [string]$AssetsDirectory = (Join-Path $PSScriptRoot '..\src\VocabCollector.Desktop\Assets')
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script with powershell.exe -NoProfile -STA -File scripts/create-icon.ps1.'
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assets = [IO.Path]::GetFullPath($AssetsDirectory)
$dictionary = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'AppIcon.xaml')))
$drawing = $dictionary['BrandMark'].Drawing

function Get-IconPng([int]$Size) {
    # Supersampling keeps the small Explorer and taskbar representations smooth.
    $renderSize = $Size * 4
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new(($renderSize / 512.0), ($renderSize / 512.0)))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($renderSize, $renderSize, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)
    $reduced = [Windows.Media.Imaging.TransformedBitmap]::new($render, [Windows.Media.ScaleTransform]::new(0.25, 0.25))
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($reduced))
    $memory = [IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        return ,$memory.ToArray()
    }
    finally { $memory.Dispose() }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Get-IconPng $_) })
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frames[$index].Length)
        $writer.Write([UInt32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $output.ToArray())
}
finally {
    $writer.Dispose()
    $output.Dispose()
}
[IO.File]::WriteAllBytes((Join-Path $assets 'app-icon.png'), (Get-IconPng 512))
[IO.File]::WriteAllBytes((Join-Path $assets 'app-icon-256.png'), $frames[$sizes.Count - 1])

# Check both the ICO directory and each embedded PNG dimensions, without trusting filenames.
$iconBytes = [IO.File]::ReadAllBytes((Join-Path $assets 'app.ico'))
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($iconBytes))
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne $sizes.Count) {
        throw 'Invalid Windows icon header.'
    }
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $width = [int]$reader.ReadByte()
        $height = [int]$reader.ReadByte()
        if ($width -eq 0) { $width = 256 }
        if ($height -eq 0) { $height = 256 }
        $null = $reader.ReadUInt16()
        $planes = $reader.ReadUInt16()
        $bits = $reader.ReadUInt16()
        $length = $reader.ReadUInt32()
        $position = $reader.ReadUInt32()
        if ($width -ne $sizes[$index] -or $height -ne $width -or $planes -ne 1 -or $bits -ne 32 -or ($position + $length) -gt $iconBytes.Length) {
            throw "Invalid icon entry at $index."
        }
        $imageStream = [IO.MemoryStream]::new($iconBytes, $position, $length)
        try {
            $image = [Windows.Media.Imaging.PngBitmapDecoder]::new($imageStream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            if ($image.Frames[0].PixelWidth -ne $width -or $image.Frames[0].PixelHeight -ne $height) {
                throw "PNG dimensions do not match icon entry $index."
            }
        }
        finally { $imageStream.Dispose() }
    }
}
finally { $reader.Dispose() }
Write-Output "Original artwork rendered and verified: ICO sizes $($sizes -join ', '); PNG previews 256 and 512."
