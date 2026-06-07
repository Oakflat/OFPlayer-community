$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logoPath = Join-Path $scriptDir 'logo.svg'
[xml] $logoXml = Get-Content -Path $logoPath -Raw

$viewBox = ($logoXml.DocumentElement.GetAttribute('viewBox') -split '\s+').ForEach({ [double] $_ })
$logoWidth = $viewBox[2]
$logoHeight = $viewBox[3]

function Convert-ToColor {
  param([string] $Value)

  return [System.Windows.Media.ColorConverter]::ConvertFromString($Value)
}

function New-SolidBrush {
  param(
    [byte] $Alpha,
    [byte] $Red,
    [byte] $Green,
    [byte] $Blue
  )

  $brush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromArgb($Alpha, $Red, $Green, $Blue)
  )
  $brush.Freeze()
  return $brush
}

function New-GradientBrush {
  param(
    [System.Windows.Media.Color] $StartColor,
    [System.Windows.Media.Color] $EndColor,
    [System.Windows.Point] $StartPoint,
    [System.Windows.Point] $EndPoint,
    [System.Windows.Media.BrushMappingMode] $MappingMode =
      [System.Windows.Media.BrushMappingMode]::RelativeToBoundingBox
  )

  $brush = [System.Windows.Media.LinearGradientBrush]::new()
  $brush.MappingMode = $MappingMode
  $brush.StartPoint = $StartPoint
  $brush.EndPoint = $EndPoint
  $brush.GradientStops.Add([System.Windows.Media.GradientStop]::new($StartColor, 0))
  $brush.GradientStops.Add([System.Windows.Media.GradientStop]::new($EndColor, 1))
  $brush.Freeze()
  return $brush
}

function New-SvgLogoDrawing {
  $gradients = @{}

  foreach ($gradient in $logoXml.SelectNodes("//*[local-name()='linearGradient']")) {
    $brush = [System.Windows.Media.LinearGradientBrush]::new()
    $brush.MappingMode = [System.Windows.Media.BrushMappingMode]::Absolute
    $brush.StartPoint = [System.Windows.Point]::new(
      [double] $gradient.GetAttribute('x1'),
      [double] $gradient.GetAttribute('y1')
    )
    $brush.EndPoint = [System.Windows.Point]::new(
      [double] $gradient.GetAttribute('x2'),
      [double] $gradient.GetAttribute('y2')
    )

    foreach ($stop in $gradient.SelectNodes("*[local-name()='stop']")) {
      $offsetText = $stop.GetAttribute('offset')
      $offset = if ($offsetText.EndsWith('%')) {
        [double] $offsetText.TrimEnd('%') / 100
      } else {
        [double] $offsetText
      }

      $brush.GradientStops.Add(
        [System.Windows.Media.GradientStop]::new(
          (Convert-ToColor $stop.GetAttribute('stop-color')),
          $offset
        )
      )
    }

    $brush.Freeze()
    $gradients[$gradient.GetAttribute('id')] = $brush
  }

  $group = [System.Windows.Media.DrawingGroup]::new()

  foreach ($path in $logoXml.SelectNodes("//*[local-name()='path']")) {
    $fill = $path.GetAttribute('fill')
    $gradientId = [regex]::Match($fill, 'url\(#(.+)\)').Groups[1].Value
    $geometry = [System.Windows.Media.Geometry]::Parse($path.GetAttribute('d'))
    $drawing = [System.Windows.Media.GeometryDrawing]::new($gradients[$gradientId], $null, $geometry)
    $group.Children.Add($drawing)
  }

  foreach ($circle in $logoXml.SelectNodes("//*[local-name()='circle']")) {
    $fill = $circle.GetAttribute('fill')
    $gradientId = [regex]::Match($fill, 'url\(#(.+)\)').Groups[1].Value
    $geometry = [System.Windows.Media.EllipseGeometry]::new(
      [System.Windows.Point]::new([double] $circle.GetAttribute('cx'), [double] $circle.GetAttribute('cy')),
      [double] $circle.GetAttribute('r'),
      [double] $circle.GetAttribute('r')
    )
    $drawing = [System.Windows.Media.GeometryDrawing]::new($gradients[$gradientId], $null, $geometry)
    $group.Children.Add($drawing)
  }

  $group.Freeze()
  return $group
}

$logoDrawing = New-SvgLogoDrawing

function Draw-Logo {
  param(
    [System.Windows.Media.DrawingContext] $Context,
    [double] $X,
    [double] $Y,
    [double] $Width,
    [double] $Height,
    [double] $Opacity = 1
  )

  $scale = [Math]::Min($Width / $script:logoWidth, $Height / $script:logoHeight)
  $drawWidth = $script:logoWidth * $scale
  $drawHeight = $script:logoHeight * $scale
  $offsetX = $X + (($Width - $drawWidth) / 2)
  $offsetY = $Y + (($Height - $drawHeight) / 2)

  $Context.PushOpacity($Opacity)
  $Context.PushTransform([System.Windows.Media.TranslateTransform]::new($offsetX, $offsetY))
  $Context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
  $Context.DrawDrawing($script:logoDrawing)
  $Context.Pop()
  $Context.Pop()
  $Context.Pop()
}

function Draw-Line {
  param(
    [System.Windows.Media.DrawingContext] $Context,
    [System.Windows.Media.Color] $Color,
    [double] $Opacity,
    [double] $X1,
    [double] $Y1,
    [double] $X2,
    [double] $Y2,
    [double] $Width
  )

  $brush = [System.Windows.Media.SolidColorBrush]::new($Color)
  $brush.Opacity = $Opacity
  $pen = [System.Windows.Media.Pen]::new($brush, $Width)
  $pen.Freeze()
  $Context.DrawLine($pen, [System.Windows.Point]::new($X1, $Y1), [System.Windows.Point]::new($X2, $Y2))
}

function Save-Asset {
  param(
    [int] $Width,
    [int] $Height,
    [string] $Path,
    [scriptblock] $Draw
  )

  $visual = [System.Windows.Media.DrawingVisual]::new()
  $context = $visual.RenderOpen()
  & $Draw $context
  $context.Close()

  $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
    $Width,
    $Height,
    96,
    96,
    [System.Windows.Media.PixelFormats]::Pbgra32
  )
  $bitmap.Render($visual)

  $encoder = [System.Windows.Media.Imaging.BmpBitmapEncoder]::new()
  $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))

  $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
  try {
    $encoder.Save($stream)
  } finally {
    $stream.Dispose()
  }
}

$cyan = Convert-ToColor '#29abe2'
$aqua = Convert-ToColor '#09d6e9'
$deep = Convert-ToColor '#2e3192'
$ink = Convert-ToColor '#0d151b'
$panel = Convert-ToColor '#172b30'
$light = Convert-ToColor '#f8fbfc'
$lightAlt = Convert-ToColor '#edf6f8'

Save-Asset 150 57 (Join-Path $scriptDir 'nsis-header.bmp') {
  param($context)
  $context.DrawRectangle(
    (New-GradientBrush $light $lightAlt ([System.Windows.Point]::new(0, 0)) ([System.Windows.Point]::new(1, 0))),
    $null,
    [System.Windows.Rect]::new(0, 0, 150, 57)
  )
  Draw-Logo $context 12 10 37 34 1
  Draw-Line $context $cyan 0.8 58 18 136 18 3.5
  Draw-Line $context $aqua 0.7 70 30 144 30 3.5
  Draw-Line $context $deep 0.18 58 42 126 42 1.5
}

Save-Asset 164 314 (Join-Path $scriptDir 'nsis-sidebar.bmp') {
  param($context)
  $context.DrawRectangle(
    (New-GradientBrush $ink $panel ([System.Windows.Point]::new(0, 0)) ([System.Windows.Point]::new(0, 1))),
    $null,
    [System.Windows.Rect]::new(0, 0, 164, 314)
  )
  Draw-Logo $context -92 18 278 254 0.34
  Draw-Logo $context 42 106 82 75 1
  Draw-Line $context $aqua 0.95 24 250 94 250 4
  Draw-Line $context (Convert-ToColor '#00a99d') 0.78 24 262 126 262 4
  Draw-Line $context $cyan 0.88 24 276 116 276 4
}

Save-Asset 493 58 (Join-Path $scriptDir 'wix-banner.bmp') {
  param($context)
  $context.DrawRectangle(
    (New-GradientBrush $light $lightAlt ([System.Windows.Point]::new(0, 0)) ([System.Windows.Point]::new(1, 0))),
    $null,
    [System.Windows.Rect]::new(0, 0, 493, 58)
  )
  Draw-Logo $context 18 9 42 38 1
  Draw-Line $context $cyan 0.72 76 18 452 18 4
  Draw-Line $context $aqua 0.68 108 31 474 31 4
  Draw-Line $context $deep 0.16 76 43 418 43 1.5
}

Save-Asset 493 312 (Join-Path $scriptDir 'wix-dialog.bmp') {
  param($context)
  $context.DrawRectangle((New-SolidBrush 255 248 251 252), $null, [System.Windows.Rect]::new(0, 0, 493, 312))
  $context.DrawRectangle(
    (New-GradientBrush $ink $panel ([System.Windows.Point]::new(0, 0)) ([System.Windows.Point]::new(0, 1))),
    $null,
    [System.Windows.Rect]::new(0, 0, 176, 312)
  )
  $context.PushClip([System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new(0, 0, 176, 312)))
  Draw-Logo $context -122 14 330 301 0.35
  Draw-Logo $context 48 108 84 77 1
  Draw-Line $context $aqua 0.86 28 248 104 248 4
  Draw-Line $context (Convert-ToColor '#00a99d') 0.7 28 262 132 262 4
  Draw-Line $context $cyan 0.82 28 276 116 276 4
  $context.Pop()
  Draw-Line $context (Convert-ToColor '#dce7ea') 1 176 0 176 312 1
}
