[CmdletBinding()]
param([string]$RepositoryUrl)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $root 'src/ChulipVideo/ChulipVideo.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '製品版をcsprojから取得できません。' }
if ($RepositoryUrl) {
    $RepositoryUrl = $RepositoryUrl.TrimEnd('/') -replace '\.git$', ''
    if ($RepositoryUrl -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'RepositoryUrlには公開先GitHubリポジトリのHTTPS URLを指定してください。'
    }
}
$metadata = Get-Content -LiteralPath (Join-Path $root 'tool.json') -Raw | ConvertFrom-Json -AsHashtable
if ($metadata.version -ne $version) { throw 'csprojとtool.jsonの版が一致しません。' }
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop
$dotnetRoot = Split-Path $dotnet.Source -Parent
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $dotnetRoot $name) -PathType Leaf)) {
        throw ".NET配布元のライセンス・第三者通知がありません: $name"
    }
}

# 実行ごとに独立した保存先を作り、既存の成果物と実行中アプリを上書きしない。
$runId = 'package-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$run = Join-Path $root "dev/history/v$version/logs/$runId"
if (Test-Path -LiteralPath $run) { throw "保存先が既に存在します: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
$publish = Join-Path $run 'publish'
$payload = Join-Path $run 'payload'
New-Item -ItemType Directory -Path $publish, $payload | Out-Null

function Write-NewText([string]$Path, [string]$Text) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write($Text) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

function Copy-NewFile([string]$Source, [string]$Destination) {
    if (!(Test-Path -LiteralPath $Source -PathType Leaf)) { throw "必要なファイルがありません: $Source" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    [IO.File]::Copy($Source, $Destination, $false)
}

function Assert-File([string]$Directory, [string]$Relative) {
    if (!(Test-Path -LiteralPath (Join-Path $Directory $Relative) -PathType Leaf)) {
        throw "配布ファイルがありません: $Relative"
    }
}

try {
    Write-Host "配布作業フォルダー: $run"
    & $dotnet.Source publish $projectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false --artifacts-path (Join-Path $run 'build') -o $publish --nologo `
        2>&1 | Tee-Object -FilePath (Join-Path $run 'publish.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "自己完結型の発行に失敗しました (終了コード $LASTEXITCODE)。" }

    foreach ($required in @('ChulipVideo.exe', 'ChulipVideo.dll', 'ChulipVideo.deps.json',
            'ChulipVideo.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
            'System.Private.CoreLib.dll', 'System.Windows.Forms.dll')) {
        Assert-File $publish $required
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'ChulipVideo.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimeOptions = $runtimeConfig.runtimeOptions
    if ($runtimeOptions['framework'] -or $runtimeOptions['frameworks'] -or @($runtimeOptions['includedFrameworks']).Count -lt 2) {
        throw '.NET Desktop Runtimeが同梱されていません。'
    }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'ChulipVideo.dll')).FileVersion
    if ($actualVersion -ne "$version.0") { throw "発行されたDLLの版が一致しません: $actualVersion" }

    # 発行物の依存関係表にある実行用資産・その言語資産と起動ファイルだけを許可する。
    # ビルド出力に文書・設定・動画等が紛れた場合は自動採用せず、検査を失敗させる。
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('ChulipVideo.exe', 'ChulipVideo.dll', 'ChulipVideo.deps.json', 'ChulipVideo.runtimeconfig.json')) {
        [void]$allowed.Add($name)
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $publish 'ChulipVideo.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($target in $dependencies.targets.Values) {
        foreach ($library in $target.Values) {
            foreach ($section in @('runtime', 'native', 'resources', 'runtimeTargets')) {
                if (!$library.Contains($section)) { continue }
                foreach ($asset in $library[$section].GetEnumerator()) {
                    $assetPath = $asset.Key.Replace('\', '/')
                    $name = [IO.Path]::GetFileName($assetPath)
                    if ($section -eq 'resources') {
                        $locale = [string]$asset.Value['locale']
                        if ($locale -notmatch '^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$') { throw "不正な言語資産です: $assetPath" }
                        $name = "$locale/$name"
                    }
                    if ($name -match '\.(dll|exe)$') { [void]$allowed.Add($name) }
                }
            }
        }
    }
    $excluded = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\', '/')
        if ($file.Extension -eq '.pdb') { $excluded.Add($relative); continue }
        # .NET Desktop Runtimeの言語DLLはdeps.jsonに個別列挙されないため、
        # 有効なカルチャと、依存関係表にある本体DLLの組み合わせだけを認める。
        if ($relative -match '^(?<culture>[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*)/(?<assembly>[^/]+)\.resources\.dll$' -and
                $allowed.Contains($Matches.assembly + '.dll')) {
            [Globalization.CultureInfo]::GetCultureInfo($Matches.culture) | Out-Null
            [void]$allowed.Add($relative)
        }
        if (!$allowed.Contains($relative)) { throw "許可表にない発行ファイルです: $relative" }
        Copy-NewFile $file.FullName (Join-Path $payload $relative)
    }

    foreach ($relative in @('docs/guide.md', 'docs/kotorintoys-integration.md', 'config/shortcuts.json')) {
        Copy-NewFile (Join-Path $root $relative) (Join-Path $payload $relative)
    }
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        Copy-NewFile (Join-Path $dotnetRoot $name) (Join-Path $payload "runtime-licenses/$name")
    }
    foreach ($name in @('LICENSE', 'LICENSE.txt', 'LICENSE.md', 'NOTICE', 'NOTICE.txt')) {
        if (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf) {
            Copy-NewFile (Join-Path $root $name) (Join-Path $payload $name)
        }
    }
    $metadata.executable = 'ChulipVideo.exe'
    foreach ($runtime in $metadata.runtimes) {
        if ($runtime.id -eq 'dotnet-desktop') {
            $runtime.name = '.NET Desktop Runtime 10 (x64、ZIPに同梱)'
            $runtime.guide = 'このZIPは自己完結型です。.NET Desktop Runtimeと.NET SDKの別途インストールは不要です。'
        }
    }
    Write-NewText (Join-Path $payload 'tool.json') (($metadata | ConvertTo-Json -Depth 10) + "`n")

    $readme = @'
# ChulipVideo C#版 __VERSION__

動画から画像を切り出し、選択区間をPNG連番・MP4・GIFへ書き出すWindowsアプリです。

## 起動

1. ZIPを任意の書き込み可能なフォルダーへすべて展開します。
2. `ChulipVideo.exe`を実行します。EXE以外の同梱ファイルも同じ配置で残してください。
3. 動画を扱う前に、設定画面から既存の`ffmpeg.exe`を指定します。

Windows 11 x64向けです。.NET Desktop Runtime 10 x64を同梱しているため、.NETやSDKを別途インストールする必要はありません。インストーラーは使用しません。

## FFmpeg

FFmpegは同梱していません。利用者が用意した既存のFFmpegを設定画面で指定してください。MP4書き出しにはlibx264対応のFFmpegが必要です。環境変数`CHULIPVIDEO_FFMPEG`や起動引数`--ffmpeg "C:\任意の場所\ffmpeg.exe"`でも指定できます。

## 操作と設定

- [操作ガイド](docs/guide.md)：切り出し範囲、再生、演出、書き出し、ショートカット。
- [KotorinToys管理連携](docs/kotorintoys-integration.md)：管理機能と対応範囲。
- 共通設定：`%LOCALAPPDATA%\KotorinToys\ChulipVideo\settings.json`。
- 上部の「保存」で作る動画別設定：動画と同じ場所の`<動画名>.chulip.settings.json`。
- 試験時は`--settings-dir "C:\任意の設定フォルダー"`で共通設定の保存先を分けられます。

このZIPの生成だけではKotorinToysの導入カタログへの公開登録は行いません。`config/shortcuts.json`は管理連携用の予約ファイルです。実際のキー操作は操作ガイドを参照してください。

## 更新とアンインストール

更新時はアプリを終了し、新しいZIPを別フォルダーへ展開して起動します。古い版のファイルと混在させないでください。

アンインストールはアプリを終了して展開フォルダーを削除します。共通設定・動画別設定・入力動画・書き出したファイルは残ります。共通設定も不要な場合のみ、上記の共通設定フォルダーを利用者が削除してください。

## ランタイムのライセンス

同梱.NETの[ライセンス](runtime-licenses/LICENSE.txt)と[第三者通知](runtime-licenses/ThirdPartyNotices.txt)を参照してください。
'@
    Write-NewText (Join-Path $payload 'README.md') ($readme.Replace('__VERSION__', "v$version") + "`n")

    $notes = @'
# ChulipVideo C#版 v__VERSION__

Windows 11 x64向けの自己完結型ZIPです。ZIPをすべて展開して`ChulipVideo.exe`を起動してください。.NET Desktop Runtime 10 x64を同梱しています。

FFmpegは別途必要です。設定画面から、利用者が用意した既存のlibx264対応`ffmpeg.exe`を指定してください。

- 配布物：`ChulipVideo-CSharp-v__VERSION__-win-x64.zip`。
- 操作方法：[操作ガイド](__GUIDE_URL__)。ZIP内の`README.md`と`docs/guide.md`からも確認できます。
- 変更内容・検証結果：[v__VERSION__実装報告](__REPORT_URL__)。実施した検証と残る確認事項はこの版の報告を参照してください。
- 整合性確認：添付の`SHA256SUMS.txt`とZIPのSHA-256を照合してください。

インストーラーは含みません。KotorinToysの導入カタログへの登録・公開状態は、この単体ZIPの公開とは別に管理します。
'@
    $notesPath = Join-Path $run 'release-notes.md'
    $guideUrl = 'payload/docs/guide.md'
    $reportUrl = "../../v$version-imp.md"
    if ($RepositoryUrl) {
        $guideUrl = "$RepositoryUrl/blob/v$version/docs/guide.md"
        $reportUrl = "$RepositoryUrl/blob/v$version/dev/history/v$version/v$version-imp.md"
    }
    Write-NewText $notesPath ($notes.Replace('__VERSION__', $version).Replace('__GUIDE_URL__', $guideUrl).Replace('__REPORT_URL__', $reportUrl) + "`n")

    $files = @(foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(dev|\.local|bin|obj|tests|artifacts)(/|$)' -or $relative -match '(?i)(\.mp4|\.gif|\.tsv|\.pdb|settings\.json)$') {
            throw "配布対象外のファイルが混入しています: $relative"
        }
        [ordered]@{ path = $relative; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    & (Join-Path $PSScriptRoot 'test-public-payload-security.ps1') -Payload $payload -ReportDirectory (Join-Path $run 'security') | Out-Host
    $zipName = "ChulipVideo-CSharp-v$version-win-x64.zip"
    $zipPath = Join-Path $run $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    # ZIPを再読込し、項目数・相対パス・展開後バイト列をpayload台帳と照合する。
    $expected = @{}
    foreach ($file in $files) { $expected.Add($file.path, $file) }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne $files.Count) { throw 'ZIP内のファイル数が一致しません。' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (!$seen.Add($name) -or !$expected.ContainsKey($name)) { throw "ZIPに余分な項目があります: $name" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($hash -ne $expected[$name].sha256 -or $entry.Length -ne $expected[$name].length) {
                throw "ZIPとpayloadの内容が一致しません: $name"
            }
        }
    } finally { $zip.Dispose() }

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-NewText (Join-Path $run 'SHA256SUMS.txt') ("$zipHash  $zipName`n")
    $commit = (& git -C $root rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw '成果物の元コミットを取得できません。' }
    $changes = @(& git -C $root status --short)
    if ($LASTEXITCODE -ne 0) { throw '成果物の作業ツリー状態を取得できません。' }
    $manifest = [ordered]@{
        schemaVersion = 1; product = 'ChulipVideo-CSharp'; version = $version; runtimeIdentifier = 'win-x64'
        createdAt = (Get-Date).ToUniversalTime().ToString('o'); sourceCommit = $commit; sourceChanges = $changes
        repositoryUrl = $RepositoryUrl; releaseNotesLinks = $(if ($RepositoryUrl) { 'github-tag' } else { 'local-preview' })
        selfContained = $true; includedFrameworks = $runtimeOptions.includedFrameworks
        zip = [ordered]@{ name = $zipName; length = (Get-Item -LiteralPath $zipPath).Length; sha256 = $zipHash }
        verification = 'ZIPの全項目をpayloadのパス・サイズ・SHA-256と照合済み'
        excludedPublishFiles = @($excluded); files = $files
    }
    Write-NewText (Join-Path $run 'package-manifest.json') (($manifest | ConvertTo-Json -Depth 10) + "`n")
    Write-NewText (Join-Path $run 'verification.log') ("正常終了`n版: $version`nファイル数: $($files.Count)`nZIP: $zipName`nSHA-256: $zipHash`n")
    Write-Host "配布ZIPの生成・検査が完了しました: $zipPath"
    [pscustomobject]@{ RunDirectory = $run; Zip = $zipPath; Sha256 = $zipHash; Files = $files.Count; ReleaseNotes = $notesPath }
} catch {
    Write-NewText (Join-Path $run 'package-error.log') ($_ | Out-String)
    throw
}
