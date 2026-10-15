# ChulipVideo C#版の配布手順

Windows 11 x64向けの自己完結型ZIPを作ります。.NET Desktop Runtime 10 x64は同梱し、FFmpegは利用者の既存の実行ファイルを指定して使用します。インストーラーは生成しません。

## 配布前の準備

1. `src/ChulipVideo/ChulipVideo.csproj`と`tool.json`の版をそろえます。
2. `dev/history/vX.Y.Z/`の要求・実装報告・検証結果を更新します。実デスクトップでの確認と自動テストの結果を区別して記録します。
3. .NET 10 SDKとPowerShell 7を使用し、変更に対応するビルド・テストを完了します。

```powershell
cd C:\develop\KotorinToys\tools\ChulipVideo
./dev/scripts/build.ps1
./dev/scripts/test.ps1 -Ffmpeg 'C:\任意の場所\ffmpeg.exe'
```

## ZIPの生成

```powershell
cd C:\develop\KotorinToys\tools\ChulipVideo
./dev/scripts/package.ps1
```

スクリプトはcsprojの版を読み取り、`dev/history/vX.Y.Z/logs/package-日時-識別子/`を新規作成します。`dotnet publish -c Release -r win-x64 --self-contained true`を実行し、ビルド中間物もこの実行専用フォルダーへ分けます。既存の成果物や実行中の開発EXEを上書きせず、失敗時のログも残します。初回はNuGetから.NETランタイムパックを取得する場合があります。

| 生成物 | 用途 |
| --- | --- |
| `ChulipVideo-CSharp-vX.Y.Z-win-x64.zip` | 利用者へ配布するZIP |
| `SHA256SUMS.txt` | 配布ZIPのSHA-256 |
| `release-notes.md` | 公開本文の原稿。版の操作ガイド・実装報告へのリンクを含む |
| `package-manifest.json` | 元コミット・未コミット変更・ランタイム版・同梱全ファイルのサイズとSHA-256 |
| `verification.log` | 成果物の検査結果 |
| `publish.log`、`package-error.log` | 発行ログ、失敗した場合のエラー |
| `publish/`、`payload/`、`build/` | 発行物・検査対象の配布内容・ビルド中間物 |

ZIPにはアプリの実行物、依存関係表から許可したランタイム、.NETのライセンス・第三者通知、配布用README、`docs/guide.md`、`docs/kotorintoys-integration.md`、`tool.json`、`config/shortcuts.json`を含めます。製品のルートにLICENSE/NOTICEがある場合はそれも含めます。`dev/`、`.local/`、試験素材、デバッグシンボル、ユーザー設定、FFmpegは含めません。

配布用`tool.json`の実行先だけを`ChulipVideo.exe`へ変更し、.NET同梱の説明を反映します。リポジトリの`tool.json`や親KotorinToysのカタログ・公開状態は変更しません。

スクリプトは自己完結型の必須DLL・ランタイム設定・製品版を検査し、ZIPを再読込して全項目の相対パス・サイズ・SHA-256をpayloadの台帳と照合します。検査成功は配布内容の整合性を示します。画面操作や動画の書き出しが正常であることは、別途実施した当該版のテスト結果で確認してください。

## 公開

公開先リポジトリ、対象コミット、タグ、公開版かプレリリースかを確定してから公開します。生成直前の`package-manifest.json`で元コミットと未コミット変更を確認し、公開対象ソースと配布物を一致させてください。

公開先が確定したら、`package.ps1 -RepositoryUrl 'https://github.com/所有者/リポジトリ'`で最終成果物を生成します。引数は確定した実際のURLに置き換えてください。指定がない場合、本文のリンクはローカル確認用になります。URLを指定すると公開先のタグ`vX.Y.Z`にある操作ガイドと実装報告へのリンクを生成します。

公開用の成果物はZIPと`SHA256SUMS.txt`です。`release-notes.md`は公開本文に使用し、リンク先が閲覧できることを確認します。本文の検証説明は対象版の報告に合わせます。`publish/`、`payload/`、`build/`、生ログは公開添付しません。

このスクリプトはGitHubリポジトリの作成、push、タグ作成、release公開、親KotorinToysへの導入登録を行いません。ChulipVideoの独立Git管理を維持し、単体配布と親カタログの更新を別の作業として扱います。

公開後は添付ZIPを取得し、同じreleaseの`SHA256SUMS.txt`と照合します。PowerShellでの確認例です。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\ChulipVideo-CSharp-vX.Y.Z-win-x64.zip'
```

利用者向けの起動、FFmpeg指定、設定の保存先、更新、アンインストールの手順はZIP内のREADMEに含まれます。
