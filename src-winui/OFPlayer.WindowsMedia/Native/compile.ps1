param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$nativeOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$nativeVisualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $nativeVisualStudio) { throw 'The Video continuous source requires the Visual C++ x64 build tools.' }
$nativeVcvars = Join-Path $nativeVisualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$nativeSource = Join-Path $PSScriptRoot 'ContinuousSource.cpp'
$nativeCommand = @"
@echo off
call "$nativeVcvars" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++20 /EHsc /O2 /MT /LD /W4 /utf-8 "$nativeSource" /Fo"$nativeOutput\ContinuousSource.obj" /Fe"$nativeOutput\OFPlayer.Video.MediaSource.dll" /link windowsapp.lib mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib propsys.lib /IMPLIB:"$nativeOutput\OFPlayer.Video.MediaSource.lib"
exit /b %errorlevel%
"@
$nativeCommandPath = Join-Path $nativeOutput 'build-native.cmd'
[IO.File]::WriteAllText($nativeCommandPath, $nativeCommand, [Text.Encoding]::Default)
Push-Location $nativeOutput
try {
    & cmd.exe /d /c $nativeCommandPath
    if ($LASTEXITCODE -ne 0) { throw "Video continuous source build failed ($LASTEXITCODE)." }
} finally { Pop-Location }
