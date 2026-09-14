# Rebuild the website's local image set from tracked project artwork.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$AssetRoot = Join-Path $ProjectRoot 'website\assets'
$GeneratedRoot = Join-Path $ProjectRoot 'output\imagegen\dcexpo-2026-09-14'

[System.IO.Directory]::CreateDirectory($AssetRoot) | Out-Null

function Get-JpegCodec {
    return [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/jpeg' } |
        Select-Object -First 1
}

function Export-Jpeg {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateRange(1, 100)][long]$Quality = 91
    )

    $SourcePath = [System.IO.Path]::GetFullPath($Source)
    if (-not [System.IO.File]::Exists($SourcePath)) {
        throw "Source image was not found: $SourcePath"
    }

    $Image = [System.Drawing.Image]::FromFile($SourcePath)
    $Parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
    $Parameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new(
        [System.Drawing.Imaging.Encoder]::Quality,
        $Quality
    )
    try {
        $Image.Save($Destination, (Get-JpegCodec), $Parameters)
    }
    finally {
        $Parameters.Dispose()
        $Image.Dispose()
    }
}

function Copy-Image {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $SourcePath = [System.IO.Path]::GetFullPath($Source)
    if (-not [System.IO.File]::Exists($SourcePath)) {
        throw "Source image was not found: $SourcePath"
    }
    [System.IO.File]::Copy($SourcePath, $Destination, $true)
}

function Export-SquarePng {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateRange(16, 1024)][int]$Size
    )

    $SourcePath = [System.IO.Path]::GetFullPath($Source)
    if (-not [System.IO.File]::Exists($SourcePath)) {
        throw "Source image was not found: $SourcePath"
    }

    $Image = [System.Drawing.Image]::FromFile($SourcePath)
    $Bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    )
    $Bitmap.SetResolution(96, 96)
    $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    try {
        $Graphics.Clear([System.Drawing.Color]::Transparent)
        $Graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $Graphics.DrawImage($Image, 0, 0, $Size, $Size)
        $Bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $Graphics.Dispose()
        $Bitmap.Dispose()
        $Image.Dispose()
    }
}

$Sources = @{
    Room = Join-Path $GeneratedRoot 'background.png'
    Dolls = Join-Path $GeneratedRoot 'web-exhibitor.png'
    Pattern = Join-Path $GeneratedRoot 'background-geometric-1920x1080-under-1mb.jpg'
    Studio = Join-Path $GeneratedRoot 'roil-icon-black-700x700.png'
    Title = Join-Path $ProjectRoot 'Assets\Resources\Visitor\title-logo-v2.png.bytes'
    KeyVisual = Join-Path $ProjectRoot 'Assets\Art\KeyVisual\MawarimiKeyVisual-v2.png'
}

Export-Jpeg -Source $Sources.Room -Destination (Join-Path $AssetRoot 'room.jpg') -Quality 91
Export-Jpeg -Source $Sources.Dolls -Destination (Join-Path $AssetRoot 'dolls.jpg') -Quality 91
Copy-Image -Source $Sources.Pattern -Destination (Join-Path $AssetRoot 'pattern.jpg')
Copy-Image -Source $Sources.Studio -Destination (Join-Path $AssetRoot 'studio.png')
Copy-Image -Source $Sources.Title -Destination (Join-Path $AssetRoot 'title.png')
Export-Jpeg -Source $Sources.KeyVisual -Destination (Join-Path $AssetRoot 'keyvisual.jpg') -Quality 91
Export-SquarePng -Source $Sources.Studio -Destination (Join-Path $AssetRoot 'favicon.png') -Size 128

Get-ChildItem -LiteralPath $AssetRoot -File |
    Sort-Object Name |
    Select-Object Name, Length
