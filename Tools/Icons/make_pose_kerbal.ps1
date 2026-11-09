# Draws the pose controls window's kerbal: full body, facing the viewer, dark with grey outlines like the Kerbals tab
# icon (Assets/MunShot/UI/Icons/tab-kerbals.png). Drawn on a 200 x 300 design canvas (the one PoseJoints' dot spots
# use), scaled up for a sharp image.
# Run: powershell -ExecutionPolicy Bypass -File Tools/Icons/make_pose_kerbal.ps1 Assets/MunShot/UI/Icons/pose-kerbal.png
param([string]$Out = "Assets/MunShot/UI/Icons/pose-kerbal.png", [int]$Scale = 3)

Add-Type -AssemblyName System.Drawing
$S = [single]$Scale
$bmp = New-Object System.Drawing.Bitmap ([int](200 * $S)), ([int](300 * $S)), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$g.ScaleTransform($S, $S)

$fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x16, 0x1B, 0x21))
$grey = [System.Drawing.Color]::FromArgb(255, 0x8A, 0x90, 0x99)
$greyBrush = New-Object System.Drawing.SolidBrush $grey
$dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x5F, 0x66, 0x70))

function Pen([single]$width, $color) {
    $p = New-Object System.Drawing.Pen $color, $width
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    return $p
}
$outline = Pen 3 $grey
$thin = Pen 2 $grey

function Shape($path) { $g.FillPath($fill, $path); $g.DrawPath($outline, $path) }
function Pt([single]$x, [single]$y) { New-Object System.Drawing.PointF $x, $y }
function NewPath { New-Object System.Drawing.Drawing2D.GraphicsPath }
function Ellipse([single]$cx, [single]$cy, [single]$rx, [single]$ry) {
    $p = NewPath; $p.AddEllipse($cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry); return $p
}
function RoundRect([single]$l, [single]$t, [single]$r, [single]$b, [single]$rad) {
    $p = NewPath; $d = 2 * $rad
    $p.AddArc($l, $t, $d, $d, 180, 90); $p.AddArc($r - $d, $t, $d, $d, 270, 90)
    $p.AddArc($r - $d, $b - $d, $d, $d, 0, 90); $p.AddArc($l, $b - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}
# A limb: a thick grey stroke with a thinner dark one inside, so it reads as an outlined tube
function Limb($points, [single]$width) {
    $pts = [System.Drawing.PointF[]]$points
    $g.DrawLines((Pen $width $grey), $pts)
    $g.DrawLines((Pen ($width - 6) $fill.Color), $pts)
}
# x mirrored for the kerbal's left side (the drawing's right)
function MX([single]$x, [single]$side) { if ($side -lt 0) { $x } else { 200 - $x } }

foreach ($side in -1, 1) {
    # Legs and boots
    Limb @((Pt (MX 86 $side) 200), (Pt (MX 83 $side) 228), (Pt (MX 82 $side) 250)) 30
    $boot = NewPath
    $boot.AddBezier((Pt (MX 67 $side) 246), (Pt (MX 62 $side) 262), (Pt (MX 56 $side) 280), (Pt (MX 70 $side) 281))
    $boot.AddLine((Pt (MX 70 $side) 281), (Pt (MX 97 $side) 281))
    $boot.AddBezier((Pt (MX 97 $side) 281), (Pt (MX 104 $side) 281), (Pt (MX 101 $side) 260), (Pt (MX 98 $side) 246))
    $boot.CloseFigure(); Shape $boot
    $g.DrawLine($thin, (Pt (MX 65 $side) 254), (Pt (MX 99 $side) 254))

    # Arms and gloves: a mitten with a thumb
    Limb @((Pt (MX 66 $side) 150), (Pt (MX 45 $side) 174), (Pt (MX 37 $side) 196)) 28
    $g.DrawLine($thin, (Pt (MX 27 $side) 194), (Pt (MX 46 $side) 199))
    Shape (Ellipse (MX 47 $side) 206 6 9)
    Shape (Ellipse (MX 35 $side) 211 13 14)
}

# The suit: body, chest box with its buttons, belt and buckle
Shape (RoundRect 58 132 142 210 30)
Shape (RoundRect 85 148 115 170 4)
foreach ($x in 93, 107) { $g.FillEllipse($greyBrush, $x - 3, 156, 6, 6) }
$g.DrawLine($outline, (Pt 62 192), (Pt 138 192))
Shape (RoundRect 93 187 107 197 2)

# Helmet: antenna nubs and side lamps behind the glass, then the glass, the head inside and the neck ring in front
foreach ($side in -1, 1) {
    $nub = NewPath
    $nub.AddPolygon([System.Drawing.PointF[]]@((Pt (MX 60 $side) 30), (Pt (MX 54 $side) 18), (Pt (MX 63 $side) 13), (Pt (MX 71 $side) 24)))
    Shape $nub
    $l = MX 33 $side; $r = MX 47 $side
    Shape (RoundRect ([Math]::Min($l, $r)) 58 ([Math]::Max($l, $r)) 92 4)
    foreach ($y in 67, 81) { $g.FillEllipse($dim, (MX 40 $side) - 3.5, $y - 3.5, 7, 7) }
}
Shape (Ellipse 100 76 60 60)

$head = NewPath
$head.AddLine((Pt 70 106), (Pt 68 58))
$head.AddBezier((Pt 68 58), (Pt 67 32), (Pt 82 24), (Pt 100 24))
$head.AddBezier((Pt 100 24), (Pt 118 24), (Pt 133 32), (Pt 132 58))
$head.AddLine((Pt 132 58), (Pt 130 106))
$head.AddBezier((Pt 130 106), (Pt 130 116), (Pt 124 120), (Pt 114 120))
$head.AddLine((Pt 114 120), (Pt 86 120))
$head.AddBezier((Pt 86 120), (Pt 76 120), (Pt 70 116), (Pt 70 106))
$head.CloseFigure(); Shape $head

# Big eyes looking ahead, and a wide grin
foreach ($x in 88, 112) {
    Shape (Ellipse $x 64 11 12)
    $g.FillEllipse($greyBrush, $x - 4.5, 62, 9, 9)
}
$grin = NewPath
$grin.AddLine((Pt 80 90), (Pt 120 90))
$grin.AddBezier((Pt 120 90), (Pt 118 107), (Pt 82 107), (Pt 80 90))
$grin.CloseFigure(); Shape $grin
$g.DrawLine($thin, (Pt 84 95), (Pt 116 95))

Shape (Ellipse 100 133 38 8)

$g.Dispose()
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$bmp.Save((Resolve-Path -LiteralPath $dir).Path + "\" + (Split-Path -Leaf $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Wrote $Out"
