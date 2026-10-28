param([string]$mode, [string]$dir, [string]$out, [string]$spec)
Add-Type -AssemblyName System.Drawing
$files = Get-ChildItem $dir -File | Sort-Object Name
function Load($f) {
  $img = [Drawing.Image]::FromFile($f)
  if ($img.PropertyIdList -contains 0x112) {
    $o = $img.GetPropertyItem(0x112).Value[0]
    switch ($o) { 3 { $img.RotateFlip('Rotate180FlipNone') } 6 { $img.RotateFlip('Rotate90FlipNone') } 8 { $img.RotateFlip('Rotate270FlipNone') } }
    $img.RemovePropertyItem(0x112)
  }
  return $img
}
$jpg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | ? { $_.MimeType -eq 'image/jpeg' }
function SaveJpg($bmp, $path, $q) { $ep = New-Object Drawing.Imaging.EncoderParameters 1; $ep.Param[0] = New-Object Drawing.Imaging.EncoderParameter([Drawing.Imaging.Encoder]::Quality, [long]$q); $bmp.Save($path, $jpg, $ep) }

if ($mode -eq 'sheet') {   # spec = "start,count"
  $s, $c = $spec.Split(',') | % { [int]$_ }
  $cell = 300; $cols = 4; $sel = $files[$s..([Math]::Min($files.Count, $s + $c) - 1)]
  $rows = [Math]::Ceiling($sel.Count / $cols)
  $bmp = New-Object Drawing.Bitmap ($cols * $cell), ($rows * $cell)
  $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::Gray); $g.InterpolationMode = 'HighQualityBicubic'
  $font = New-Object Drawing.Font('Arial', [single]20, [Drawing.FontStyle]::Bold)
  for ($i = 0; $i -lt $sel.Count; $i++) {
    $img = Load $sel[$i].FullName
    $k = [Math]::Min(($cell - 6) / $img.Width, ($cell - 6) / $img.Height)
    $w = [int]($img.Width * $k); $h = [int]($img.Height * $k)
    $x = ($i % $cols) * $cell + [int](($cell - $w) / 2); $y = [Math]::Floor($i / $cols) * $cell + [int](($cell - $h) / 2)
    $g.DrawImage($img, $x, $y, $w, $h)
    $g.FillRectangle([Drawing.Brushes]::Black, ($i % $cols) * $cell, [Math]::Floor($i / $cols) * $cell, 40, 30)
    $g.DrawString(($s + $i), $font, [Drawing.Brushes]::Yellow, ($i % $cols) * $cell, [Math]::Floor($i / $cols) * $cell)
    "$($s+$i) $($sel[$i].Name) $($img.Width)x$($img.Height)"
    $img.Dispose()
  }
  SaveJpg $bmp $out 80
}
elseif ($mode -eq 'view') {  # spec = index; draws 10% grid
  $img = Load $files[[int]$spec].FullName
  $k = [Math]::Min(700 / $img.Width, 700 / $img.Height)
  $bmp = New-Object Drawing.Bitmap ([int]($img.Width * $k)), ([int]($img.Height * $k))
  $g = [Drawing.Graphics]::FromImage($bmp); $g.DrawImage($img, 0, 0, $bmp.Width, $bmp.Height)
  $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(160, 255, 0, 255)), 1; $font = New-Object Drawing.Font('Arial', [single]11, [Drawing.FontStyle]::Bold)
  for ($t = 1; $t -lt 10; $t++) {
    $x = $bmp.Width * $t / 10; $y = $bmp.Height * $t / 10
    $g.DrawLine($pen, $x, 0, $x, $bmp.Height); $g.DrawLine($pen, 0, $y, $bmp.Width, $y)
    $g.DrawString("$($t*10)", $font, [Drawing.Brushes]::Magenta, $x + 2, 2); $g.DrawString("$($t*10)", $font, [Drawing.Brushes]::Magenta, 2, $y + 2)
  }
  SaveJpg $bmp $out 80
  "$($files[[int]$spec].Name) $($img.Width)x$($img.Height)"
}
elseif ($mode -eq 'fix') {  # spec = file with lines: index rot left top right bottom  (rot 0/90/180/270 clockwise; crop in % of rotated image)
  New-Item -ItemType Directory -Force $out | Out-Null
  $plan = @{}; Get-Content $spec | ? { $_ -match '\S' } | % { $p = $_ -split '\s+'; $plan[[int]$p[0]] = $p }
  for ($i = 0; $i -lt $files.Count; $i++) {
    $img = Load $files[$i].FullName
    $p = $plan[$i]
    if ($p) {
      switch ([int]$p[1]) { 90 { $img.RotateFlip('Rotate90FlipNone') } 180 { $img.RotateFlip('Rotate180FlipNone') } 270 { $img.RotateFlip('Rotate270FlipNone') } }
      $l = [int]($img.Width * [double]$p[2] / 100); $t = [int]($img.Height * [double]$p[3] / 100)
      $r = [int]($img.Width * [double]$p[4] / 100); $b = [int]($img.Height * [double]$p[5] / 100)
      $bmp = New-Object Drawing.Bitmap ($r - $l), ($b - $t)
      $g = [Drawing.Graphics]::FromImage($bmp); $g.DrawImage($img, (New-Object Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height), $l, $t, ($r - $l), ($b - $t), 'Pixel'); $g.Dispose()
    } else { $bmp = New-Object Drawing.Bitmap $img }
    $name = '{0:D2}.jpg' -f ($i + 1)
    SaveJpg $bmp (Join-Path $out $name) 92
    "$name <- $($files[$i].Name) $($bmp.Width)x$($bmp.Height)$(if($p){' fixed'})"
    $bmp.Dispose(); $img.Dispose()
  }
}
