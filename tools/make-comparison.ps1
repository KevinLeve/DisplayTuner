# Makes a split before/after image for the docs: left half untouched, right half with a preset applied using the same
# color matrix and black-level curve as the app (src/DisplayTuner.cs). Screenshots can't show the real filter, so this simulates it.
#   powershell -File tools\make-comparison.ps1 -Source frame.png -Out docs\compare.jpg -Label "True Black" -Contrast 107 -Saturation 115 -BlackLevel 85
param(
    [Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Out, [Parameter(Mandatory)][string]$Label,
    [int]$Brightness = 0, [int]$Contrast = 100, [int]$Saturation = 100, [int]$BlackLevel = 0, [int]$Warmth = 0,
    [int]$Width = 1280, [string]$Credit = '',
    [switch]$Full   # apply the preset to the whole frame, no divider or labels (for the webpage's drag slider)
)
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Sim {
  // Same math as Filter.Matrix and Filter.SetBlackLevel in the app
  public static void Apply(Bitmap b, int x0, int bri, int con, int sat, int blk, int warm) {
    float s = sat / 100f, c = con / 100f, br = bri / 200f, w = warm / 100f;
    const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
    float[,] S = { { lr*(1-s)+s, lr*(1-s), lr*(1-s) }, { lg*(1-s), lg*(1-s)+s, lg*(1-s) }, { lb*(1-s), lb*(1-s), lb*(1-s)+s } };
    float[] gain = w >= 0 ? new[] { 1f, 1 - 0.10f * w, 1 - 0.45f * w } : new[] { 1 + 0.35f * w, 1 + 0.08f * w, 1f };
    var m = new float[3, 3]; var off = new float[3];
    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = S[i, j] * c * gain[j];
    for (int j = 0; j < 3; j++) off[j] = (0.5f - 0.5f * c + br) * gain[j];
    double extra = Math.Max(0, blk - 60) / 40.0, k = Math.Min(0.97, blk / 100.0 * 0.7 + extra * 0.25), t = 0.22 + extra * 0.08;
    var lut = new byte[256];
    for (int i = 0; i < 256; i++) { double x = i / 255.0, y = x; if (x < t) { double d = 1 - x / t; y = x * (1 - k * d * d); } lut[i] = (byte)Math.Round(Math.Max(0, Math.Min(1, y)) * 255); }
    var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[bd.Stride * b.Height]; Marshal.Copy(bd.Scan0, px, 0, px.Length);
    for (int y = 0; y < b.Height; y++)
      for (int x = x0; x < b.Width; x++) {
        int i = y * bd.Stride + x * 4;
        float r = px[i+2] / 255f, g = px[i+1] / 255f, bl = px[i] / 255f;
        float[] o = new float[3];
        for (int j = 0; j < 3; j++) o[j] = r * m[0, j] + g * m[1, j] + bl * m[2, j] + off[j];
        px[i+2] = lut[Clamp(o[0])]; px[i+1] = lut[Clamp(o[1])]; px[i] = lut[Clamp(o[2])];
      }
    Marshal.Copy(px, 0, bd.Scan0, px.Length); b.UnlockBits(bd);
  }
  static int Clamp(float v) { int n = (int)Math.Round(v * 255); return n < 0 ? 0 : n > 255 ? 255 : n; }
}
'@
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($Source)
$h = [int]($src.Height * $Width / $src.Width)
$b = New-Object System.Drawing.Bitmap $Width, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($b); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($src, 0, 0, $Width, $h); $src.Dispose()
$half = [int]($Width / 2)
$noChange = $Brightness -eq 0 -and $Contrast -eq 100 -and $Saturation -eq 100 -and $BlackLevel -eq 0 -and $Warmth -eq 0
if (-not $noChange) { [Sim]::Apply($b, $(if ($Full) { 0 } else { $half }), $Brightness, $Contrast, $Saturation, $BlackLevel, $Warmth) }
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
if ($Full) { $Label = $null }
# divider and labels (split mode only)
if ($Label) {
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), $half - 1, 0, 2, $h)
$font = New-Object System.Drawing.Font 'Segoe UI Semibold', 15
function Tag($text, $x, $right) {
    $sz = $g.MeasureString($text, $font); $w = $sz.Width + 22; $px = if ($right) { $x - $w - 14 } else { $x + 14 }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $r = 15; $y = 14; $hh = 32
    $path.AddArc($px, $y, $r * 2, $hh, 90, 180); $path.AddArc($px + $w - $r * 2, $y, $r * 2, $hh, 270, 180); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 0, 0, 0))), $path)
    $g.DrawString($text, $font, [System.Drawing.Brushes]::White, $px + 11, $y + 3)
}
Tag 'Natural' 0 $false
Tag $Label $Width $true
}
if ($Credit) {
    $cf = New-Object System.Drawing.Font 'Segoe UI', 10
    $cs = $g.MeasureString($Credit, $cf)
    $g.DrawString($Credit, $cf, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))), $Width - $cs.Width - 10, $h - $cs.Height - 6)
}
$enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]85)
$b.Save($Out, $enc, $ep); $b.Dispose()
"Written $Out ($([int]((Get-Item $Out).Length / 1KB)) KB)"
