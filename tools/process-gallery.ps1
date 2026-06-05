#requires -Version 5.1
<#
.SYNOPSIS
    Post-processes the gallery theme screenshots for the README / SHOWCASE.

.DESCRIPTION
    Run this after (re)capturing any screenshot into the gallery folder. The
    raw captures include the Windows accent (DWM rounded-corner) border, which
    shows as orange corner triangles. This script:

      1. Masks every theme PNG to a crisp, anti-aliased rounded rectangle
         (radius 10 = the app's design corner), making everything outside the
         rounded shape transparent — so the accent corners are removed and the
         corners are smooth.
      2. Regenerates preview.png — a diagonal light/dark Windows 11 composite
         (light upper-left, dark lower-right) sitting on a soft shadow mist
         that fades to transparent, for the README hero image.

    Idempotent: re-running on already-rounded images is harmless. preview.png
    is skipped by the rounding pass and rebuilt fresh.

.PARAMETER Dir
    Gallery folder. Defaults to ..\gallery relative to this script.

.EXAMPLE
    ./tools/process-gallery.ps1
#>
param(
  [string]$Dir = (Join-Path $PSScriptRoot '..\gallery'),
  # Rebuild only preview.png from the existing win11-light/dark screenshots,
  # skipping the round/de-orange pass over every theme shot. Handy when
  # tuning the diagonal/shadow without re-touching the rest of the gallery.
  [switch]$PreviewOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$Dir = (Resolve-Path $Dir).Path

# --- tunables ---------------------------------------------------------------
$Radius      = 10                 # corner radius (px) for every screenshot
$Pad         = 75                 # preview.png margin around the menu (px)
$ShadowAlpha = 180                # 0-255 darkness of the shadow before blur
$BlurScale   = 26                 # downscale factor for the blur; larger =
                                  # wider, softer mist (roughly the px the
                                  # glow feathers past the menu edge)
$ShadowPasses = 2                 # extra downscale/upscale passes smooth the
                                  # falloff so the mist reads as a gradient,
                                  # not a single hard-edged ring

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $d = 2 * $r
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x,        $y,        $d, $d, 180, 90)
  $p.AddArc($x+$w-$d,  $y,        $d, $d, 270, 90)
  $p.AddArc($x+$w-$d,  $y+$h-$d,  $d, $d,   0, 90)
  $p.AddArc($x,        $y+$h-$d,  $d, $d,  90, 90)
  $p.CloseFigure()
  $p
}

# --- 1) round every theme screenshot (skip the generated preview) -----------
if (-not $PreviewOnly) {
Get-ChildItem $Dir -Filter *.png | Where-Object { $_.Name -ne 'preview.png' } | ForEach-Object {
  $src = [System.Drawing.Bitmap]::FromFile($_.FullName)
  $w = $src.Width; $h = $src.Height
  $out = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $out.SetResolution($src.HorizontalResolution, $src.VerticalResolution)
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $path = New-RoundRect 0 0 $w $h $Radius
  $brush = New-Object System.Drawing.TextureBrush($src)
  $g.FillPath($brush, $path)
  $brush.Dispose(); $path.Dispose(); $g.Dispose(); $src.Dispose()
  $out.Save($_.FullName, [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
  Write-Host "rounded $($_.Name)"
}
}

# --- 2) regenerate preview.png (diagonal light/dark Win11 + shadow mist) -----
$light = [System.Drawing.Bitmap]::FromFile((Join-Path $Dir 'win11-light.png'))
$dark  = [System.Drawing.Bitmap]::FromFile((Join-Path $Dir 'win11-dark.png'))
$W = $light.Width; $H = $light.Height

$menu = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mg = [System.Drawing.Graphics]::FromImage($menu)
$mg.SmoothingMode   = 'AntiAlias'
$mg.PixelOffsetMode = 'HighQuality'
# Light fills the whole menu; the dark half is then painted into the
# lower-right triangle with an ANTI-ALIASED path edge. This is the
# smooth-diagonal fix: GDI+ region clipping (SetClip) has hard, aliased
# edges, so the old SetClip + DrawImage left a jagged staircase down the
# hypotenuse. FillPath with a TextureBrush honours SmoothingMode.AntiAlias
# and feathers the diagonal against the light pixels already underneath.
# The brush samples the dark bitmap 1:1 (same size as the canvas), so it
# stays pixel-aligned with no offset transform.
$mg.DrawImage($light, 0, 0, $W, $H)
$tri = New-Object System.Drawing.Drawing2D.GraphicsPath
$tri.AddPolygon([System.Drawing.PointF[]]@(
  (New-Object System.Drawing.PointF([single]$W,[single]0)),
  (New-Object System.Drawing.PointF([single]$W,[single]$H)),
  (New-Object System.Drawing.PointF([single]0,[single]$H))))
$darkBrush = New-Object System.Drawing.TextureBrush($dark)
$mg.FillPath($darkBrush, $tri)
$darkBrush.Dispose(); $tri.Dispose()
$mg.Dispose(); $light.Dispose(); $dark.Dispose()

$CW = $W + 2*$Pad; $CH = $H + 2*$Pad
$shape = New-Object System.Drawing.Bitmap($CW, $CH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($shape); $sg.SmoothingMode = 'AntiAlias'
$rp = New-RoundRect $Pad $Pad $W $H $Radius
$sg.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($ShadowAlpha,0,0,0))), $rp)
$sg.Dispose()

# Blur the shadow shape by repeated downscale -> upscale. Each pass is a
# cheap box-ish blur; stacking a couple widens the spread and smooths the
# falloff into a soft gradient (closer to a Gaussian) instead of one hard
# ring. With BlurScale=26 the mist feathers ~30-40 px past the menu edge,
# fading to transparent around the middle of the 75 px margin.
$blur = $shape
for ($pass = 0; $pass -lt $ShadowPasses; $pass++) {
  $sw = [Math]::Max(1, [int]($CW / $BlurScale))
  $sh = [Math]::Max(1, [int]($CH / $BlurScale))
  $small = New-Object System.Drawing.Bitmap($sw, $sh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gd = [System.Drawing.Graphics]::FromImage($small)
  $gd.InterpolationMode = 'HighQualityBilinear'; $gd.PixelOffsetMode = 'HighQuality'
  $gd.DrawImage($blur, 0, 0, $sw, $sh); $gd.Dispose()
  $up = New-Object System.Drawing.Bitmap($CW, $CH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gu = [System.Drawing.Graphics]::FromImage($up)
  $gu.InterpolationMode = 'HighQualityBicubic'; $gu.PixelOffsetMode = 'HighQuality'
  $gu.DrawImage($small, 0, 0, $CW, $CH); $gu.Dispose(); $small.Dispose()
  if (-not [object]::ReferenceEquals($blur, $shape)) { $blur.Dispose() }
  $blur = $up
}
if (-not [object]::ReferenceEquals($blur, $shape)) { $shape.Dispose() }

$final = New-Object System.Drawing.Bitmap($CW, $CH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$fg = [System.Drawing.Graphics]::FromImage($final); $fg.Clear([System.Drawing.Color]::Transparent)
$fg.DrawImage($blur, 0, 0); $fg.DrawImage($menu, $Pad, $Pad)
$fg.Dispose(); $blur.Dispose(); $menu.Dispose()
$final.Save((Join-Path $Dir 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $final.Dispose()
Write-Host "rebuilt preview.png  ${CW}x${CH}"
