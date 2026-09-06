$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path $PSScriptRoot '../src/assets/music-icons/source'
$sourceText = Get-Content -Raw -LiteralPath (Join-Path $sourceDirectory 'user-icons.svg')
$document = [xml]$sourceText
$elements = @($document.DocumentElement.ChildNodes | Where-Object { $_.LocalName -ne 'defs' })
$groups = Get-Content -Raw -LiteralPath (Join-Path $sourceDirectory 'user-icon-groups.json') | ConvertFrom-Json
$icons = [ordered]@{}
foreach ($group in $groups) {
  # Preserve the supplied contours; center them in a 20-unit optical box.
  $scale = 20.0 / [Math]::Max($group.bounds.width, $group.bounds.height)
  $tx = 12 - ($group.bounds.x + $group.bounds.width / 2) * $scale
  $ty = 12 - ($group.bounds.y + $group.bounds.height / 2) * $scale
  $culture = [System.Globalization.CultureInfo]::InvariantCulture
  $transform = 'matrix({0} 0 0 {0} {1} {2})' -f $scale.ToString('F8', $culture), $tx.ToString('F8', $culture), $ty.ToString('F8', $culture)
  $paths = @()
  foreach ($index in $group.indices) {
    $element = $elements[$index]
    $nodes = if ($element.LocalName -eq 'path') { @($element) } else { @($element.SelectNodes('.//*[local-name()="path"]')) }
    foreach ($node in $nodes) {
      $paths += [ordered]@{ d = $node.GetAttribute('d'); transform = $transform; fill = 'currentColor'; stroke = 'currentColor'; 'stroke-width' = [Math]::Round(0.55 / $scale, 8) }
    }
  }
  $icons[$group.name] = $paths
}
$json = ConvertTo-Json -InputObject $icons -Depth 8
$output = "// Generated from the user-supplied grouped SVG. Do not redraw these contours.`nimport type { MusicIconPath } from './musicIconPaths'`n`nexport const userMusicIconPaths = $json satisfies Record<string, MusicIconPath[]>`n"
Set-Content -LiteralPath (Join-Path $PSScriptRoot '../src/components/icons/userMusicIconPaths.ts') -Value $output -Encoding utf8 -NoNewline

# Standalone copy keeps the user's composition and group structure intact.
$adjusted = $sourceText -replace 'fill: #[0-9a-fA-F]{6};', 'fill: currentColor; stroke: currentColor; stroke-width: 2.5; stroke-linejoin: round; stroke-linecap: round;'
$adjusted = $adjusted -replace '<svg ', '<svg color="#625d69" '
Set-Content -LiteralPath (Join-Path $sourceDirectory '../icon-adjusted.svg') -Value $adjusted -Encoding utf8
Write-Output "Imported $($icons.Count) user icons; original SVG paths preserved."
