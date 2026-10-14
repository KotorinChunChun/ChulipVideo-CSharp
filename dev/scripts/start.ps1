param([string]$Video,[string]$Ffmpeg,[string]$Settings)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
& (Join-Path $PSScriptRoot 'build.ps1')
$appArgs=@()
if (!$Ffmpeg -and (Test-Path (Join-Path $root '.local/ffmpeg.exe'))) { $Ffmpeg=Join-Path $root '.local/ffmpeg.exe' }
if ($Ffmpeg) { $appArgs+=@('--ffmpeg',('"'+$Ffmpeg+'"')) }
if ($Settings) { $appArgs+=@('--settings',('"'+$Settings+'"')) }
if ($Video) { $appArgs+=@('--video',('"'+$Video+'"')) }
$launch=@{FilePath=(Join-Path $root 'src/ChulipVideo/bin/Debug/net10.0-windows/ChulipVideo.exe');WorkingDirectory=$root}
if($appArgs.Count) {$launch.ArgumentList=$appArgs}
Start-Process @launch
