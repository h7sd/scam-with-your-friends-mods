<# Renders the original phone/play icon used by TikTok Pocket. #>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconOutput = Join-Path ([IO.Path]::GetFullPath($ProjectDirectory)) 'tiktok-mod/assets/tiktok-pocket.png'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($iconOutput)) | Out-Null
function New-RoundedIconPath {
    param([single]$X,[single]$Y,[single]$Width,[single]$Height,[single]$Radius)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = 2 * $Radius
    $path.AddArc($X,$Y,$diameter,$diameter,180,90)
    $path.AddArc($X+$Width-$diameter,$Y,$diameter,$diameter,270,90)
    $path.AddArc($X+$Width-$diameter,$Y+$Height-$diameter,$diameter,$diameter,0,90)
    $path.AddArc($X,$Y+$Height-$diameter,$diameter,$diameter,90,90)
    $path.CloseFigure()
    return $path
}
$iconBitmap = [Drawing.Bitmap]::new(256,256,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [Drawing.Graphics]::FromImage($iconBitmap)
$iconBlack = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#101116'))
$iconWhite = [Drawing.SolidBrush]::new([Drawing.Color]::White)
$iconPink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#fe2c55'))
$iconCyan = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#25f4ee'))
$iconPinkPen = [Drawing.Pen]::new($iconPink,9)
$iconCyanPen = [Drawing.Pen]::new($iconCyan,9)
$iconPaths = [Collections.Generic.List[Drawing.Drawing2D.GraphicsPath]]::new()
try {
    $iconGraphics.Clear([Drawing.Color]::Transparent)
    $iconGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $iconGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $background = New-RoundedIconPath 4 4 248 248 58
    $pinkPhone = New-RoundedIconPath 62 34 140 192 28
    $cyanPhone = New-RoundedIconPath 54 28 140 192 28
    $notch = New-RoundedIconPath 105 40 38 7 3.5
    $pinkLine = New-RoundedIconPath 96 172 57 6 3
    $cyanLine = New-RoundedIconPath 96 185 40 6 3
    foreach($path in @($background,$pinkPhone,$cyanPhone,$notch,$pinkLine,$cyanLine)){$iconPaths.Add($path)}
    $iconGraphics.FillPath($iconBlack,$background)
    $iconGraphics.DrawPath($iconPinkPen,$pinkPhone)
    $iconGraphics.FillPath($iconBlack,$cyanPhone)
    $iconGraphics.DrawPath($iconCyanPen,$cyanPhone)
    $iconGraphics.FillPath($iconCyan,$notch)
    $iconGraphics.FillPolygon($iconWhite,[Drawing.PointF[]]@([Drawing.PointF]::new(103,85),[Drawing.PointF]::new(103,153),[Drawing.PointF]::new(157,119)))
    $iconGraphics.FillPath($iconPink,$pinkLine)
    $iconGraphics.FillPath($iconCyan,$cyanLine)
    $iconGraphics.FillEllipse($iconWhite,119,201,10,10)
    $iconBitmap.Save($iconOutput,[Drawing.Imaging.ImageFormat]::Png)
}
finally {
    foreach($path in $iconPaths){$path.Dispose()}
    foreach($item in @($iconPinkPen,$iconCyanPen,$iconBlack,$iconWhite,$iconPink,$iconCyan,$iconGraphics,$iconBitmap)){$item.Dispose()}
}
Write-Output "Created original TikTok Pocket icon: $iconOutput"
