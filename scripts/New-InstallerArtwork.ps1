param(
    [Parameter(Mandatory = $true)] [string]$IconPath,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$iconFile = (Resolve-Path $IconPath).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$pale = [System.Drawing.ColorTranslator]::FromHtml('#EDF4FF')
$ink = [System.Drawing.ColorTranslator]::FromHtml('#17233B')
$white = [System.Drawing.Color]::White
$icon = [System.Drawing.Icon]::new($iconFile, 256, 256)
$logo = $icon.ToBitmap()

function New-Canvas([int]$width, [int]$height) {
    return [System.Drawing.Bitmap]::new($width, $height)
}

function Set-Quality([System.Drawing.Graphics]$graphics) {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
}

try {
    # WiX places wizard text on the left of this 493x58 top strip.
    $banner = New-Canvas 493 58
    $g = [System.Drawing.Graphics]::FromImage($banner)
    try {
        Set-Quality $g
        $g.Clear($white)
        $background = [System.Drawing.SolidBrush]::new($pale)
        try { $g.FillRectangle($background, 0, 0, 493, 58) }
        finally { $background.Dispose() }
        $g.DrawImage($logo, 440, 8, 42, 42)
        $banner.Save((Join-Path $OutputDirectory 'CavdeckMsiBanner.bmp'),
            [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $g.Dispose(); $banner.Dispose() }

    # WiX's welcome/conclusion text overlays the right side; keep it clear.
    $dialog = New-Canvas 493 312
    $g = [System.Drawing.Graphics]::FromImage($dialog)
    try {
        Set-Quality $g
        $g.Clear($white)
        $background = [System.Drawing.SolidBrush]::new($pale)
        try { $g.FillRectangle($background, 0, 0, 172, 312) }
        finally { $background.Dispose() }
        $g.DrawImage($logo, 30, 100, 112, 112)
        $dialog.Save((Join-Path $OutputDirectory 'CavdeckMsiDialog.bmp'),
            [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $g.Dispose(); $dialog.Dispose() }

    $splash = New-Canvas 480 240
    $g = [System.Drawing.Graphics]::FromImage($splash)
    try {
        Set-Quality $g
        $g.Clear($white)
        $g.DrawImage($logo, 190, 36, 100, 100)
        $font = [System.Drawing.Font]::new('Segoe UI', 23,
            [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new($ink)
        $format = [System.Drawing.StringFormat]::new()
        try {
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $g.DrawString('Cavdeck', $font, $brush,
                [System.Drawing.RectangleF]::new(0, 151, 480, 42), $format)
        }
        finally { $format.Dispose(); $brush.Dispose(); $font.Dispose() }
        $splash.Save((Join-Path $OutputDirectory 'CavdeckSetupSplash.png'),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $g.Dispose(); $splash.Dispose() }
}
finally { $logo.Dispose(); $icon.Dispose() }

Write-Host "Installer artwork ready: $OutputDirectory"
