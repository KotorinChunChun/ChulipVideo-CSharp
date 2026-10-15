param([string]$Ffmpeg)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$Ffmpeg) { $Ffmpeg=Join-Path $root '.local/ffmpeg.exe' }
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'src/ChulipVideo/ChulipVideo.csproj')
$version=[string]$project.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw '製品版を取得できません。'}
$output=Join-Path $root ('dev/history/v'+$version+'/logs/test-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output | Out-Null
dotnet run --project (Join-Path $root 'tests/ChulipVideo.Tests/ChulipVideo.Tests.csproj') -c Debug --artifacts-path (Join-Path $root 'artifacts/tests') -- $Ffmpeg $output 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if($LASTEXITCODE -ne 0) { throw 'テストに失敗しました。' }
