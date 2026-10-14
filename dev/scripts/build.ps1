param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
dotnet build (Join-Path $root 'src/ChulipVideo/ChulipVideo.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
