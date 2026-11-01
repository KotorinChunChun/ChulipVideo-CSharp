using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Recognition;
using ChulipVideo.Steps;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P8の文字認識（R9）を検証する。OCRの言語がない環境では、OCRを使う検証を明示してスキップする。</summary>
internal static class RecognitionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Analysis();
        Settings(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { SuggestedColumn(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("マスク候補の表示の検証に失敗しました。", failure);
        if (OcrService.Unavailable() is string reason) { Console.WriteLine("SKIP 文字認識の実読み取り: " + reason); return; }
        using var image = new Bitmap(480, 200);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Yu Gothic UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawRectangle(Pens.Gray, 190, 80, 100, 44);
            g.DrawString("保存", font, Brushes.Black, 212, 86);
            g.DrawString("user01@example.com", font, Brushes.Black, 20, 150);
        }
        var lines = await OcrService.RecognizeAsync(image);
        var save = OcrAnalysis.LineAt(lines, new Point(240, 102));
        Check(save is not null && OcrAnalysis.Clean(save.Text) == "保存", "クリック位置の日本語を1語として読む: " + string.Join(" / ", lines.Select(l => $"{l.Text}@{l.Bounds}")));
        Check(lines.Any(l => OcrAnalysis.Sensitive(l.Text, []) == "メールアドレス"), "画像のメールアドレスを機密として判定");
        Console.WriteLine($"PASS 文字認識: 言語 {OcrService.LanguageTag}・日本語の行の読み取り・対象行の選択・機密の判定");
    }

    private static void Analysis()
    {
        Check(OcrService.Join(["保", "存"]) == "保存" && OcrService.Join(["Save", "As"]) == "Save As" && OcrService.Join(["名前", "を", "保存", "(S)"]) == "名前を保存(S)", "日本語の間の空白を詰め、英字の間は残す");
        OcrLine[] lines = [new("ファイル", new(0, 0, 80, 20)), new("保存", new(100, 40, 60, 24)), new("|", new(300, 40, 4, 24)), new("閉じる", new(400, 40, 60, 24))];
        Check(OcrAnalysis.LineAt(lines, new(120, 50))?.Text == "保存", "クリック点を含む行");
        Check(OcrAnalysis.LineAt(lines, new(200, 50))?.Text == "保存", "含む行がなければ60px以内で最も近い行");
        Check(OcrAnalysis.LineAt(lines, new(302, 50))?.Text != "|" && OcrAnalysis.LineAt(lines, new(250, 150)) is null, "記号だけの行と遠い行は選ばない");
        Check(OcrAnalysis.Clean(" [保存] ") == "保存", "前後の記号を除く");
        Check(OcrAnalysis.Describe("Click", "保存") == "「保存」をクリックします。" && OcrAnalysis.Describe("RightClick", "a") == "「a」を右クリックします。" && OcrAnalysis.Describe("Shortcut", "a") is null, "対象名の説明文（キー操作は対象外）");
        Check(OcrAnalysis.Sensitive("連絡先 taro.yamada@example.co.jp", []) == "メールアドレス", "メールアドレス");
        Check(OcrAnalysis.Sensitive("TEL 03-1234-5678", []) == "電話番号" && OcrAnalysis.Sensitive("〒 123-4567 東京都", []) == "郵便番号", "電話番号・郵便番号");
        Check(OcrAnalysis.Sensitive("4111 1111 1111 1111", []) == "13〜16桁の数字" && OcrAnalysis.Sensitive("12345", []) is null, "長い番号だけを判定");
        Check(OcrAnalysis.Sensitive("株式会社 サンプル 様", ["株式会社サンプル"]) == "指定語「株式会社サンプル」" && OcrAnalysis.Sensitive("保存", ["株式会社サンプル"]) is null, "指定語は空白を無視して探す");
    }

    private static void Settings(string directory)
    {
        string path = Path.Combine(directory, "recognition-settings.json");
        File.WriteAllText(path, "{\"Recognition\":{\"Keywords\":[\" 顧客A \",null,\"\",\"顧客A\",\"顧客B\"]}}");
        Check(new SettingsStore(path).Load().Recognition.Keywords.SequenceEqual(["顧客A", "顧客B"]), "指定語の空白・重複・空行の補正");
        File.WriteAllText(path, "{}");
        Check(new SettingsStore(path).Load().Recognition.Keywords.Count == 0, "旧設定は指定語なし");
    }

    private static void SuggestedColumn()
    {
        var media = new MediaInfo("s.mp4", 640, 360, 10, 10);
        using var form = new MaskListForm([new MaskRegion { Rect = new(1, 1, 10, 10), End = 1, Source = "Suggested", Enabled = false, Note = "候補: メールアドレス" }], media, 0, 0);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        var grid = (DataGridView)typeof(MaskListForm).GetField("grid", Hidden)!.GetValue(form)!;
        Check(grid.Rows[0].Cells[8].Value as string == "OCR候補" && grid.Rows[0].Cells[0].Value is false, "OCR候補は無効の状態で出どころを表示");
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        if (OcrService.Unavailable() is string reason) { Console.WriteLine("SKIP 文字認識の実動画検証: " + reason); return; }
        string directory = Path.Combine(outputDirectory, "文字認識検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(640, 360))
        using (var g = Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Yu Gothic UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
            g.Clear(Color.White);
            g.DrawRectangle(Pens.Gray, 280, 150, 100, 44);
            g.DrawString("保存", font, Brushes.Black, 302, 156);
            g.DrawString("mail: hanako@example.com", font, Brushes.Black, 30, 40);
            for (int i = 0; i < 30; i++) ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1);
        }
        string source = Path.Combine(directory, "画面.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var settings = new AppSettings();
        StepItem Step() { var s = new StepItem { Kind = "Click", FrameTime = 1, Time = 1.2, X = 330, Y = 172, Description = "この位置をクリックします。" }; s.Annotations = StepDetector.DefaultAnnotations(s, info); return s; }
        var step = Step();
        int updated = await StepRecognizer.DescribeAsync(service, info, EventLog.Empty, [step], settings, []);
        Check(updated == 1 && step.Description == "「保存」をクリックします。" && step.DescriptionSource == "Ocr", "対象名を読んで説明文にする: " + step.Description);
        var box = step.Annotations.Single(a => a.Kind == AnnotationKind.Box);
        Check(box.X > 280 && box.X < 310 && box.Width < 90, "強調枠を読んだ文字の範囲へ合わせる: " + box);
        var hidden = Step();
        await StepRecognizer.DescribeAsync(service, info, EventLog.Empty, [hidden], settings, [new MaskRegion { Rect = new(281, 151, 98, 42), Start = 0, End = 3, Mode = MaskMode.Fill }]);
        Check(hidden.Description == "この位置をクリックします。", "マスクで隠した文字は説明文に使わない: " + hidden.Description);
        var suggestions = await MaskSuggester.SuggestAsync(service, info, 0, info.Duration, []);
        var mail = suggestions.SingleOrDefault(s => s.Note.Contains("メールアドレス", StringComparison.Ordinal));
        Check(mail is { Enabled: false, Source: "Suggested", Mode: MaskMode.Fill } && mail.Rect.X <= 40 && mail.Rect.Y <= 52 && mail.Rect.Y + mail.Rect.Height >= 70 && mail.Start == 0 && mail.End == info.Duration,
            "メールアドレスを無効の候補として、続けて見つかった時間をまとめる: " + string.Join(" / ", suggestions.Select(s => $"{s.Note}@{s.Rect} {s.Start}-{s.End}")));
        Check(!suggestions.Any(s => s.Note.Contains("保存", StringComparison.Ordinal)), "機密でない文字は候補にしない");
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 文字認識出力: 実動画で対象名の説明文・強調枠・マスクで隠した文字は読まない・マスク候補");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("文字認識検証失敗: " + label); }
}
