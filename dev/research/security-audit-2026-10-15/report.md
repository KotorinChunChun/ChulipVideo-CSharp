# ChulipVideo セキュリティ診断・改善実装レポート

診断日: 2026-10-15。対象: `0c053f3` / main / v0.1.0、開始時clean。動画/TSV/設定のローカル入力、外部FFmpeg起動、エクスポート、管理pipeを静的診断した。

## 指摘と改善

|ID|重大度・成立条件|証拠|改善・今回の扱い|
|---|---|---|---|
|CHV-01|中。同一利用者の悪意あるpipeクライアントによる巨大要求/応答を読まない接続で、メモリ消費や管理受付停止。|`src/ChulipVideo/Integration/ManagementEndpoint.cs:45` は読取り長上限なし、`:86` は応答のdeadlineなし。|要求/応答の上限と書込み期限、過大requestId拒否を追加して共通互換試験。高/致命的ではないため今回未実装。|
|CHV-02|低/運用。信頼できない動画と古い外部FFmpegの組合せではcodec側の欠陥の影響を受ける可能性。現在の具体的CVE成立は確認していない。|`src/ChulipVideo/Media/FfmpegProcess.cs:24` で選択した実行ファイルへ入力を渡す。|対応FFmpeg版の運用基準、外部実行プロセスの制限を別途検討。未知の脆弱性を確定事項にしない。|

FFmpeg引数は`ArgumentList`、`UseShellExecute=false`（同ファイル:26）、待機30秒とstderr上限（:40、:67）がある。出力は既存ファイル拒否、一時領域から移動し衝突時に上書きしない（`src/ChulipVideo/Export/ExportService.cs:29`、`:80`）。管理pipeはCurrentUserOnlyと読取り2秒期限を持つ。高/致命的の確定事項は見つからなかった。

## 依存・秘密・検証

製品csprojに外部PackageReferenceなし。追跡テキスト38ファイルをPEM秘密鍵/GitHub/AWS/OpenAI代表パターンで値非表示検査し一致0。過去履歴・無視対象・一般パスワードは未検査。

- [x] 攻撃面/改善案/保留理由の記録、レポート差分検査。
- 製品コードの変更なし。文書のみのため形式的なビルド・GUI試験は実行しない。
- 実ユーザー動画、破損codecのfuzz、別ユーザーIPC、FFmpeg実バイナリの監査は未実施。導入・公開・親gitlink変更なし。

## 追記 2026-11-01 依存の追加（v0.2.2 P9）

AIによる説明文（既定は無効）のため、製品csprojに初めて外部PackageReferenceを追加した。

| パッケージ | 版 | 経路 | 用途 |
| --- | --- | --- | --- |
| Anthropic | 13.3.0 | 直接 | Claude APIの呼び出し（AI機能の中だけ） |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | 推移 | Anthropicが参照 |

`dotnet list package --vulnerable --include-transitive`で既知の脆弱性なし（2026-11-01、nuget.org）。APIキーはWindows資格情報マネージャーに保存し、設定ファイル・ログへ書かない。送信は利用者が有効にし、実行ごとに確認した場合だけで、送る画像はマスク適用後に限る。OCR・音声合成はWindows標準の機能でオフラインに動き、外部へ送らない。
