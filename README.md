# ChulipVideo

動画から手順書用の画像を切り出す、C#/.NET 10 Windows Formsアプリです。Python版の編集・画像抽出機能を独立して移植しました。

## 主な機能

- 動画の再生・逆再生、速度変更、区間・ループ・往復再生。
- トリム、ピクセル単位のクロップ、拡大・パン、Undo/Redo、解像度プリセット。
- 現在画像のコピー・PNG保存、PNG連番、静止フレーム抽出・重複除外、MP4/GIF出力。
- 同名TSVからマウス・クリック・キー字幕を表示。TSV/SRT/VTT出力と映像への焼き込み。
- 動画別設定の保存とPython版設定の読み込み。

録画はCapchunScreen、外部ウィンドウ操作はChunapToolを利用する構成です。ChulipVideoには含めていません。

## 起動

Windowsと.NET 10 SDK、FFmpegが必要です。PowerShell 7で実行します。

```powershell
cd C:\develop\KotorinToys\tools\ChulipVideo
./dev/scripts/start.ps1
```

この開発環境では移植元にあったFFmpegを`.local/ffmpeg.exe`へ複製しています。Python本体への実行時依存はありません。別環境では「設定」でFFmpeg実行ファイルを指定するか、`CHULIPVIDEO_FFMPEG`環境変数にパスを設定してください。

[操作・設定・開発手順](docs/guide.md) / [移植要求](dev/history/v0.1.0/v0.1.0-req.md) / [実装・検証報告](dev/history/v0.1.0/v0.1.0-imp.md)

## KotorinToys管理連携（2026-10-15追加）

本体の一覧からの開発版起動・既存画面表示・状態確認・正常停止に対応しました。上記移植時点の未登録範囲を、この追加で更新します。配布・正式インストール・外部公開は未実施です。[管理連携と検証用起動](docs/kotorintoys-integration.md)を参照してください。
