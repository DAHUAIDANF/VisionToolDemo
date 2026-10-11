Add-Type -AssemblyName System.Drawing
function New-TestImg($w,$h,$name){
  $bmp = New-Object System.Drawing.Bitmap($w,$h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(235,235,235))
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180,120,40), [Math]::Max(1,[int]($w/400)))
  $pen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80,160,220), 1)
  for($i=1;$i -lt 4;$i++){
    $x=[int]($w*$i/4); $y=[int]($h*$i/4)
    $g.DrawLine($pen,$x,0,$x,$h)
    $g.DrawLine($pen,0,$y,$w,$y)
  }
  foreach($p in @(@(0.25,0.25),@(0.75,0.25),@(0.25,0.75),@(0.75,0.75),@(0.5,0.5))){
    $cx=[int]($w*$p[0]); $cy=[int]($h*$p[1])
    $r=[Math]::Max(4,[int]($w/120))
    $g.DrawEllipse($pen2,$cx-$r,$cy-$r,2*$r,2*$r)
    $g.DrawLine($pen2,$cx-$r*2,$cy,$cx+$r*2,$cy)
    $g.DrawLine($pen2,$cx,$cy-$r*2,$cx,$cy+$r*2)
  }
  $g.Dispose()
  $bmp.Save("C:\Users\huang\Desktop\VisionToolDemo\_roi_test\$name",[System.Drawing.Imaging.ImageFormat]::Bmp)
  $bmp.Dispose()
  Write-Output "$name  ${w}x${h}"
}
New-TestImg 640 640 "t_square_640x640.bmp"
New-TestImg 1920 1080 "t_hd_1920x1080.bmp"
New-TestImg 400 1000 "t_tall_400x1000.bmp"
New-TestImg 1200 300 "t_wide_1200x300.bmp"
New-TestImg 100 500 "t_narrow_100x500.bmp"
New-TestImg 60 60 "t_small_60x60.bmp"
Get-ChildItem "C:\Users\huang\Desktop\VisionToolDemo\_roi_test" | Select-Object Name, Length
