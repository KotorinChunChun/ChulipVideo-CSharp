using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

public static class ExportTests
{
    public static async Task RunAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "書き出し検証 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourceFrames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(sourceFrames);
        using (var bitmap = new Bitmap(80, 60))
        {
            using var graphics = Graphics.FromImage(bitmap);
            for (int i = 0; i < 12; i++)
            {
                graphics.Clear(i < 4 ? Color.Red : i < 8 ? Color.Lime : Color.Blue);
                ExportService.SavePng(bitmap, Path.Combine(sourceFrames, $"source_{i:D3}.png"), 3);
            }
        }
        string source = Path.Combine(directory, "元動画 ' test.mkv");
        await RunProcessAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i",
            Path.Combine(sourceFrames, "source_%03d.png"), "-c:v", "ffv1", source]);
        var media = new FfmpegService(ffmpeg);
        MediaInfo info = await media.ProbeAsync(source);
        Require(info.Width == 80 && info.Height == 60 && Math.Abs(info.Fps - 10) < .01, "素材の解像度・FPS");

        var events = new EventLog([
            new(0,0,15,15,"L","None"), new(.1,1,18,15,"None","None"),
            new(.2,2,20,15,"L-Click","None"), new(.3,3,22,15,"None","None"),
            new(.4,4,25,15,"None","Ctrl,C"), new(.5,5,28,15,"None","Ctrl,C"),
            new(.6,6,30,15,"None","None"), new(.8,8,32,15,"None","Ctrl,C"),
            new(.9,9,35,15,"None","None"), new(1.0,10,38,15,"R-Click","None"), new(1.1,11,40,15,"None","None")
        ]);
        Require(EventLog.DisplayText("LM", "Shift,Ctrl,a") == "Ctrl + Shift + 左クリック + 中クリック + A", "旧形式と修飾キー順序");
        Require(EventLog.DisplayText("None", "Ctrl,Shift") == "", "修飾キーのみは非表示");
        Require(events.Captions.Any(c => c.Text == "左ダブルクリック"), "旧クリック形式から新形式へのダブルクリック");
        Require(events.Captions.Count(c => c.Text == "Ctrl + C") == 2, "離してから同じキーを押し直すと別履歴");
        Require(Math.Abs(events.Captions.First(c => c.Text == "Ctrl + C").HeldUntil - .6) < 1e-8, "キーを離すまで保持");
        var options = new OverlayOptions { FontSize = 10, MaxStack = 1, FadeDuration = .4, PointerSize = 5 };
        Require(events.ActiveCaptions(.59, .4, 1).Single().Text == "Ctrl + C", "キー保持中の表示");
        Require(events.ActiveCaptions(2, .4, 3).Count == 0, "キー履歴の期限");
        var crop = new CropRect(-5, -3, 51, 39);
        string tsv = events.ToTsv(.45, 1, 10, crop);
        Require(tsv.Contains("0.000\t0\t30\t18\tNone\tCtrl,C", StringComparison.Ordinal), "TSV開始状態とクロップ後の座標");
        Require(!tsv.Contains("R-Click", StringComparison.Ordinal), "TSVの終了時刻は含まない");
        string tsvPath = Path.Combine(directory, "入力.tsv");
        File.WriteAllText(tsvPath, "\uFEFF" + tsv + "不正行\nNaN\t0\t1\t2\tNone\tNone\n", new UTF8Encoding(false));
        var loaded = EventLog.Load(tsvPath);
        Require(loaded.Count == 5 && loaded.Events[0].Timestamp == 0, "BOM・不正行・NaNを含むTSV読込");
        Require(EventLog.Load(Path.Combine(directory, "存在しない.tsv")).Count == 0, "TSVなし動画");
        string srt = events.ToSubtitles(.45, 1, options, false), vtt = events.ToSubtitles(.45, 1, options, true);
        Require(srt.StartsWith("1\n00:00:00,000 -->", StringComparison.Ordinal) && srt.Contains("Ctrl + C", StringComparison.Ordinal), "SRTの開始時刻・範囲前からの保持");
        Require(vtt.StartsWith("WEBVTT\n\n00:00:00.000 -->", StringComparison.Ordinal), "WebVTTの形式");

        using (Bitmap original = await media.ReadFrameAsync(info, 0))
        using (Bitmap raw = OverlayRenderer.Render(original, crop, .1, events, options, false))
        {
            Require(raw.Width == 51 && raw.Height == 39, "クロップ寸法を正確に保持");
            Require(raw.GetPixel(0, 0).ToArgb() == Color.Black.ToArgb(), "枠外を黒で埋める");
            Require(raw.GetPixel(5, 3).R == 255 && raw.GetPixel(5, 3).G == 0, "クロップ原点の画素");
            for (int level = 0; level <= 9; level++)
            {
                string png = Path.Combine(directory, $"圧縮{level}.png");
                ExportService.SavePng(raw, png, level);
                using var decoded = new Bitmap(png);
                Require(Equal(raw, decoded), $"PNG圧縮レベル{level}の画素一致");
            }
            Require(new FileInfo(Path.Combine(directory, "圧縮9.png")).Length < new FileInfo(Path.Combine(directory, "圧縮0.png")).Length, "PNG圧縮設定が実データに反映");
            using Bitmap renderedA = OverlayRenderer.Render(original, crop, .25, events, options);
            using Bitmap later = OverlayRenderer.Render(original, crop, 1, events, options);
            using Bitmap renderedB = OverlayRenderer.Render(original, crop, .25, events, options);
            Require(Equal(renderedA, renderedB), "前後シーク後も同じ描画");
            Require(!Equal(raw, renderedA), "入力オーバーレイの描画");
            using var smaller = new Bitmap(original, 40, 30);
            using Bitmap preview = OverlayRenderer.RenderPreview(smaller, info, .25, events, options, crop);
            Require(preview.Width == 40 && preview.Height == 30 && !Equal(smaller, preview), "縮小プレビューの描画");
        }

        var service = new ExportService(media);
        var settings = new AppSettings { Overlay = options, StableOnly = true, SkipDuplicates = true, ExportTsv = true, ExportSrt = true, ExportVtt = true };
        string pngOutput = Path.Combine(directory, "静止画像");
        ExportResult pngResult = await service.ExportAsync(new(info, crop, 0, 1.2, pngOutput, ExportKind.PngSequence, settings), events, null, CancellationToken.None);
        Require(pngResult.FrameCount == 3 && Directory.GetFiles(pngOutput, "*.png").Length == 3, "静止フレーム・重複除外がオーバーレイ前の画素を比較");
        Require(File.Exists(Path.Combine(pngOutput, "operations.tsv")) && File.Exists(Path.Combine(pngOutput, "operations.srt")) && File.Exists(Path.Combine(pngOutput, "operations.vtt")), "PNGの入力ログ・字幕出力");
        string allOutput = Path.Combine(directory, "全フレーム");
        ExportResult allResult = await service.ExportAsync(new(info, crop, 0, 1.2, allOutput, ExportKind.PngSequence,
            settings with { StableOnly = false, SkipDuplicates = false }), events, null, CancellationToken.None);
        Require(allResult.FrameCount == 12, "全フレーム出力・終了時刻の重複なし");
        string stableOutput = Path.Combine(directory, "静止重複あり");
        ExportResult stableResult = await service.ExportAsync(new(info, crop, 0, 1.2, stableOutput, ExportKind.PngSequence,
            settings with { StableOnly = true, SkipDuplicates = false }), events, null, CancellationToken.None);
        Require(stableResult.FrameCount == 8, "前後完全一致による遷移フレーム除外");

        foreach (ExportKind kind in new[] { ExportKind.Mp4, ExportKind.Gif })
        {
            string movie = Path.Combine(directory, "出力-" + kind + "." + kind.ToString().ToLowerInvariant());
            ExportResult result = await service.ExportAsync(new(info, crop, .2, 1, movie, kind, settings with { BurnOverlay = true }), events, null, CancellationToken.None);
            Require(result.FrameCount == 8, kind + "のフレーム数");
            MediaInfo movieInfo = await media.ProbeAsync(movie);
            Require(movieInfo.Width == crop.Width && movieInfo.Height == crop.Height, kind + "の奇数寸法を保持");
            Require(Math.Abs(movieInfo.Duration - .8) < .12, kind + "の再生時間");
            Require(File.Exists(Path.ChangeExtension(movie, ".tsv")) && File.Exists(Path.ChangeExtension(movie, ".srt")) && File.Exists(Path.ChangeExtension(movie, ".vtt")), kind + "の字幕出力");
        }

        // 0.35秒を選んだプレビューは0.30秒のフレーム。0.33秒の入力を先頭へ先取りしない。
        var fractionalEvents = new EventLog([
            new(.3,3,10,10,"None","None"), new(.33,3,15,15,"None","Ctrl,C"),
            new(.41,4,20,20,"None","None"), new(.97,9,25,25,"None","Shift,Del")
        ]);
        var fractionalOptions = options with { ShowPointer = false, ShowClicks = false };
        var fractionalSettings = settings with { Overlay = fractionalOptions, StableOnly = false, SkipDuplicates = false, BurnOverlay = true };
        CropRect full = CropRect.Full(info);
        foreach (ExportKind kind in new[] { ExportKind.PngSequence, ExportKind.Mp4, ExportKind.Gif })
        {
            string target = Path.Combine(directory, "非格子-" + kind + (kind == ExportKind.PngSequence ? "" : "." + kind.ToString().ToLowerInvariant()));
            ExportResult result = await service.ExportAsync(new(info, full, .35, .96, target, kind, fractionalSettings), fractionalEvents, null, CancellationToken.None);
            Require(result.FrameCount == 7, kind + "非格子区間を0.30～1.00秒へ広げた全7フレーム");
            string sidecar = kind == ExportKind.PngSequence ? Path.Combine(target, "operations") : Path.ChangeExtension(target, null);
            string actualTsv = await File.ReadAllTextAsync(sidecar + ".tsv");
            string actualSrt = await File.ReadAllTextAsync(sidecar + ".srt");
            string actualVtt = await File.ReadAllTextAsync(sidecar + ".vtt");
            Require(actualTsv == fractionalEvents.ToTsv(.3, 1, info.Fps, full), kind + "のTSVが実開始・終了と一致");
            Require(actualTsv.Contains("0.030\t0\t15\t15\tNone\tCtrl,C", StringComparison.Ordinal) &&
                actualTsv.Contains("0.670\t6\t25\t25\tNone\tShift,Del", StringComparison.Ordinal), kind + "のTSVは0.30秒基準で拡張した終端の入力を保持");
            Require(actualSrt == fractionalEvents.ToSubtitles(.3, 1, fractionalOptions, false) &&
                actualSrt.StartsWith("1\n00:00:00,030 -->", StringComparison.Ordinal), kind + "のSRTは実開始から0.03秒後");
            Require(actualVtt == fractionalEvents.ToSubtitles(.3, 1, fractionalOptions, true) &&
                actualVtt.StartsWith("WEBVTT\n\n00:00:00.030 -->", StringComparison.Ordinal), kind + "のVTTは実開始から0.03秒後");
            if (kind == ExportKind.PngSequence)
            {
                string[] frames = Directory.GetFiles(target, "*.png").Order(StringComparer.Ordinal).ToArray();
                using Bitmap first = new(frames[0]);
                using Bitmap sourceFirst = await media.ReadFrameAsync(info, .35);
                using Bitmap expected = OverlayRenderer.Render(sourceFirst, full, info.FrameTime(.35), fractionalEvents, fractionalOptions);
                Require(Equal(first, expected), "非格子PNGの先頭演出が単一画像・プレビューと同じ");
                using Bitmap second = new(frames[1]);
                using Bitmap sourceSecond = await media.ReadFrameAsync(info, .4);
                using Bitmap expectedSecond = OverlayRenderer.Render(sourceSecond, full, info.FrameTime(.4), fractionalEvents, fractionalOptions);
                Require(Equal(second, expectedSecond), "非格子PNGの次フレームで入力演出を表示");
            }
            else
            {
                MediaInfo exported = await media.ProbeAsync(target);
                Require(Math.Abs(exported.Duration - .7) < .015, kind + "非格子区間の出力時間");
                using Bitmap first = await media.ReadFrameAsync(exported, 0);
                Require(IsSolidRed(first), kind + "先頭フレームへ0.33秒の演出を先取りしない");
            }
        }

        string invalid = Path.Combine(directory, "空区間");
        await ExpectFailureAsync<ArgumentException>(() => service.ExportAsync(new(info, full, .35, .35, invalid, ExportKind.PngSequence, settings), events, null, CancellationToken.None));
        await ExpectFailureAsync<ArgumentException>(() => service.ExportAsync(new(info, full, info.Duration, info.Duration + .001, invalid, ExportKind.PngSequence, settings), events, null, CancellationToken.None));
        Require(!Directory.Exists(invalid), "元の空区間・動画外開始を最後のフレームへ変換しない");

        // Matroskaのミリ秒PTSはNTSCの格子と一致しない。末尾は実PTSを基準にする。
        string terminalSource = Path.Combine(directory, "実PTS末尾.mkv");
        await RunProcessAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "30000/1001", "-i",
            Path.Combine(sourceFrames, "source_%03d.png"), "-c:v", "ffv1", terminalSource]);
        MediaInfo terminalInfo = await media.ProbeAsync(terminalSource);
        Require(terminalInfo.FinalFrameTime.HasValue && Math.Abs(terminalInfo.LastFrameTime * terminalInfo.Fps - Math.Round(terminalInfo.LastFrameTime * terminalInfo.Fps)) > 1e-5,
            "最終PTSがFPS格子と異なる回帰素材");
        double lastTime = terminalInfo.LastFrameTime;
        var terminalEvents = new EventLog([new(lastTime,11,10,10,"None","None"), new(lastTime + .0001,11,10,10,"None","Ctrl,C")]);
        string terminalOutput = Path.Combine(directory, "実PTS最後の1枚");
        var terminalResult = await service.ExportAsync(new(terminalInfo, CropRect.Full(terminalInfo), lastTime + .01, terminalInfo.Duration,
            terminalOutput, ExportKind.PngSequence, fractionalSettings), terminalEvents, null, CancellationToken.None);
        Require(terminalResult.FrameCount == 1, "実PTSを起点とした最終1フレーム");
        using (Bitmap terminalFrame = new(Directory.GetFiles(terminalOutput, "*.png").Single()))
        using (Bitmap rawTerminal = await media.ReadFrameAsync(terminalInfo, terminalInfo.Duration))
        using (Bitmap expectedTerminal = OverlayRenderer.Render(rawTerminal, CropRect.Full(terminalInfo), lastTime, terminalEvents, fractionalOptions))
            Require(Equal(terminalFrame, expectedTerminal), "末尾の演出は格子の再丸めではなく実PTSを使用");
        Require(await File.ReadAllTextAsync(Path.Combine(terminalOutput, "operations.tsv")) ==
            terminalEvents.ToTsv(lastTime, terminalInfo.Duration, terminalInfo.Fps, CropRect.Full(terminalInfo)), "末尾TSVの基準も実PTSに一致");

        string collision = Path.Combine(directory, "既存.mp4");
        await File.WriteAllTextAsync(collision, "元データ");
        await ExpectFailureAsync<IOException>(() => service.ExportAsync(new(info, crop, 0, 1, collision, ExportKind.Mp4, settings), events, null, CancellationToken.None));
        Require(await File.ReadAllTextAsync(collision) == "元データ", "既存ファイルを上書きしない");
        string sidecarCollision = Path.Combine(directory, "字幕衝突.mp4");
        await File.WriteAllTextAsync(Path.ChangeExtension(sidecarCollision, ".tsv"), "元字幕");
        await ExpectFailureAsync<IOException>(() => service.ExportAsync(new(info, crop, 0, 1, sidecarCollision, ExportKind.Mp4, settings), events, null, CancellationToken.None));
        Require(!File.Exists(sidecarCollision) && await File.ReadAllTextAsync(Path.ChangeExtension(sidecarCollision, ".tsv")) == "元字幕", "字幕衝突時に何も上書きしない");
        foreach (ExportKind kind in new[] { ExportKind.PngSequence, ExportKind.Mp4 })
        {
            string canceled = Path.Combine(directory, "中止-" + kind + (kind == ExportKind.Mp4 ? ".mp4" : ""));
            using var cancel = new CancellationTokenSource();
            await ExpectFailureAsync<OperationCanceledException>(() => service.ExportAsync(new(info, crop, 0, 1.2, canceled, kind,
                settings with { StableOnly = false, SkipDuplicates = false }), events, new InlineProgress(p => { if (p.Completed >= 2) cancel.Cancel(); }), cancel.Token));
            Require(!File.Exists(canceled) && !Directory.Exists(canceled), kind + "中止後に未完成出力を残さない");
        }
        Require(Directory.GetDirectories(directory, ".chulip-export-*").Length == 0, "作業用フォルダーの後始末");
        Console.WriteLine("書き出し検証: 成功（PNG圧縮0～9、静止/重複、TSV/SRT/VTT、MP4/GIF、枠外、字幕、非格子区間、取消、既存保護）");
    }

    private static bool Equal(Bitmap left, Bitmap right)
    {
        if (left.Size != right.Size) return false;
        for (int y = 0; y < left.Height; y++)
            for (int x = 0; x < left.Width; x++) if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }

    private static bool IsSolidRed(Bitmap image)
    {
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Color color = image.GetPixel(x, y);
                if (color.R < 240 || color.G > 12 || color.B > 12) return false;
            }
        return true;
    }

    private static void Require(bool passed, string message) { if (!passed) throw new InvalidOperationException("書き出し検証失敗: " + message); }
    private static async Task ExpectFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("想定した例外が発生しませんでした: " + typeof(T).Name);
    }
    private sealed class InlineProgress(Action<ExportProgress> action) : IProgress<ExportProgress> { public void Report(ExportProgress value) => action(value); }
    private static async Task RunProcessAsync(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("FFmpegを起動できません。");
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
    }
}
