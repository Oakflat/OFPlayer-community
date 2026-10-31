param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$communityRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $communityRoot
$previousCargoTarget = $env:CARGO_TARGET_DIR
try {
    $env:CARGO_TARGET_DIR = Join-Path $communityRoot '.artifacts/native'
    & npm --prefix (Join-Path $communityRoot 'web') ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Web dependencies failed to restore.' }
    & npm --prefix (Join-Path $communityRoot 'web') run build
    if ($LASTEXITCODE -ne 0) { throw 'Embedded player pages failed to build.' }
    $webDestination = Join-Path $communityRoot 'src-winui/OFPlayer.Community/Assets/web'
    New-Item -ItemType Directory -Path $webDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $communityRoot 'web/dist') -Force |
        Copy-Item -Destination $webDestination -Recurse -Force
    $cargoArguments = @('build','--workspace','--locked')
    if ($Configuration -eq 'Release') { $cargoArguments += '--release' }
    & cargo @cargoArguments
    if ($LASTEXITCODE -ne 0) { throw 'Community native host failed to build.' }
    & dotnet build src-winui/OFPlayer.Community -c $Configuration -p:Platform=x64 -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Community WinUI failed to build.' }
    Write-Output (Join-Path $communityRoot ".artifacts/dotnet/OFPlayer.Community/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/OFPlayer.Community.exe")
}
finally {
    $env:CARGO_TARGET_DIR = $previousCargoTarget
    Pop-Location
}
