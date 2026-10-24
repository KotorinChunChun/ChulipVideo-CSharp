# ChulipVideo コードレビュー・改善実装レポート

レビュー日: 2026-10-24、修正: 2026-10-25。対象: `9899d23` / main / v0.2.1、開始時clean。`src/`・`tests/`・`dev/scripts/`の全C#/PowerShell（約4,700行）を静的に確認した。[2026-10-15のセキュリティ診断](../security-audit-2026-10-15/report.md)の続きとして、同診断に含まれない事項を扱う。

引数の`ArgumentList`渡し、設定と出力の一時ファイル経由の置換、既存出力の保護、`CurrentUserOnly`の管理pipe、タイムアウトと取消は良好。高/致命的の確定事項はなかった。

## 指摘と対応

|ID|重大度・成立条件|証拠（修正前）|対応|
|---|---|---|---|
|CR-01|中。FFmpeg未設定・`CHULIPVIDEO_FFMPEG`なし・実行ファイル横にもない状態で起動すると、カレントディレクトリと実行ファイルの全祖先の`.local\ffmpeg.exe`を実行する。Windows既定の`C:\`は`Authenticated Users:(AD)`でフォルダー作成可能なため、共有PCでは他の利用者が置いた`C:\.local\ffmpeg.exe`を自分の権限で起動しうる。|`src/ChulipVideo/Media/FfmpegService.cs:26`（カレントディレクトリ）、`:30-34`（祖先の遡り）。|修正済。開発用の`.local`探索はDebugビルドだけに限定し、`FindDevelopmentExecutable`で最初に見つけたリポジトリ直下（`tool.json`のある場所）だけを見る。カレントディレクトリは探さない。配布版（Release）は設定・環境変数・実行ファイル横・PATHのみ。|
|CR-02|中。`settings.json`の色に`#GGGGGG`等があると`ColorTranslator.FromHtml`が`FormatException`を投げ、`ArgumentException`だけを捕捉する3か所を素通りする。キャンバスの`OnPaint`で例外となり、プレビューが赤い×のまま描画不能になる。読込時は数値だけ補正し色は未検証だった。|`src/ChulipVideo/UI/VideoCanvas.cs:402`、`src/ChulipVideo/Export/OverlayRenderer.cs:151`、`src/ChulipVideo/UI/SettingsForm.cs:257`、`src/ChulipVideo/Core/SettingsStore.cs:24-25,45-48`。|修正済。`Core/ColorText`へ検証・解析を集約し、設定画面と同じ`#RRGGBB`だけを受け付ける。不正値は例外にせず代替色で描画し、読込時に既定色（任意項目は空欄）へ補正する。設定全体は破棄しない。|
|CR-03|低。二重起動時は既存画面の表示要求だけを送るため`--video`が無視される。実行ファイルのパスが異なると表示もされず無言で終了する。|`src/ChulipVideo/Program.cs:10`、`src/ChulipVideo/Integration/ManagementEndpoint.cs:100-124`。|未対応。管理連携の共通契約に関わるため本体側と合わせて検討する。|
|CR-04|低。保存ダイアログが`OverwritePrompt=true`で上書き確認した後、既存ファイルを拒否する。|`src/ChulipVideo/UI/MainForm.cs:272-274`、`:281`。|未対応。|
|CR-05|低。停止拒否条件に`rendering`を含むため、再生中は管理停止が断続的に拒否される。|`src/ChulipVideo/UI/MainForm.cs:46`。|未対応。`FormClosing`で後始末するため除外を検討する。|
|CR-06|低。`OnFormClosing`が終了理由に関係なく一度`e.Cancel=true`にするため、Windowsのシャットダウンを一時的に妨げる。|`src/ChulipVideo/UI/MainForm.cs:326`。|未対応。|

既知のCHV-01（管理pipeの読取り長上限なし）も未対応のまま。保守性として、`MainForm`の1行複数文の高密度な書式、`dotnet test`非対応の独自ハーネスとCIなし、FFmpegログ文字列の正規表現解析への依存を確認した。

## 検証

- [x] Debug・Releaseビルド: 警告0・エラー0（`TreatWarningsAsErrors`）。Releaseでは`#if DEBUG`の開発探索を除いた構成で成功。
- [x] `./dev/scripts/test.ps1 -Ffmpeg <FFmpeg 7.1>`: 既存の配置・キャンバス・時刻入力・管理・設定・メディア・書出し回帰と、新規`SafetyTests`がすべて成功。ログは`dev/history/v0.2.1/logs/test-20261025-001254/`（Git対象外）。
- [x] 新規`SafetyTests`（FFmpeg不要で常時実行）: リポジトリ直下`.local`の検出、リポジトリ外側・目印なしの親・カレントディレクトリの`.local`を探さないこと、`ColorText`の受理/拒否、不正色・null色の読込補正、不正色での入力表示描画とキャンバス描画。
- [x] 変異確認: 修正を一時的に元へ戻すと、祖先遡りでは「リポジトリ外側の.localを探さない」、`FromHtml`版キャンバスでは「不正色でのキャンバス描画」でそれぞれ失敗することを確認した。
- [x] 既存`SettingsTests`がnullの任意色で回帰（`NullReferenceException`）を検出し、修正後に成功した。
- 実GUI操作・実導入・別ユーザーでの`C:\.local`配置の実地確認は未実施。版は0.2.1のまま変更せず、Release公開は行っていない。
