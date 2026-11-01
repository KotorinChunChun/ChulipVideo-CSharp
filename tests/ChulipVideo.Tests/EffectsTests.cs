using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P5のポインター平滑化（R8）と自動ズーム（R6）を検証する。</summary>
internal static class EffectsTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Smoothing();
        Camera();
        Composition();
        Settings(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { MainPreview(directory); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("演出の表示の検証に失敗しました。", failure);
        Console.WriteLine("PASS 演出: ポインター平滑化（補間・クリック前後の固定・範囲外）・自動ズーム（寄り/戻り/結合/パン/範囲内）・拡大合成・TSV座標・設定・枠表示");
    }

    private static bool Near(PointF? p, float x, float y, float tolerance = .01f) => p is PointF v && Math.Abs(v.X - x) <= tolerance && Math.Abs(v.Y - y) <= tolerance;

    private static void Smoothing()
    {
        var log = new EventLog([new(0, 0, 0, 0, "None", "None"), new(1, 10, 100, 0, "None", "None"), new(2, 20, 100, 100, "None", "None"), new(3, 30, -1, -1, "None", "None")]);
        Check(log.PointerAt(-.1, "Weak") is null, "最初の記録より前は描かない");
        Check(Near(log.PointerAt(.5, "Off"), 0, 0) && Near(log.PointerAt(.5, "Weak"), 50, 0), "Offは直前の座標、Weakは直線補間");
        var strong = log.PointerAt(.5, "Strong");
        Check(strong is PointF s && s.X > 30 && s.X < 70 && Math.Abs(s.Y) < 15, "Strongは曲線で補間: " + strong);
        Check(Near(log.PointerAt(1, "Strong"), 100, 0, .5f) && Near(log.PointerAt(2, "Weak"), 100, 100), "記録の時刻では記録どおり");
        Check(Near(log.PointerAt(2.5, "Weak"), 100, 100) && Near(log.PointerAt(2.5, "Strong"), 100, 100), "範囲外(-1)の手前は補間しない");
        var clicked = new EventLog([new(0, 0, 0, 0, "None", "None"), new(1, 10, 100, 0, "L-Click", "None"), new(1.5, 15, 100, 0, "None", "None")]);
        Check(Near(clicked.PointerAt(.95, "Weak"), 0, 0) && Near(clicked.PointerAt(.85, "Weak"), 85, 0), "押下の前後0.1秒は記録どおりに固定");
        var jitter = new EventLog([new(0, 0, 0, 0, "None", "None"), new(.1, 1, 1, 0, "None", "None"), new(.2, 2, 0, 1, "None", "None"), new(1, 10, 100, 0, "None", "None")]);
        var calm = jitter.PointerAt(.15, "Strong");
        Check(calm is PointF c && c.X > 5, "Strongは2px未満の揺れを除いて目的地へ進む: " + calm);
        using var frame = new Bitmap(120, 40);
        var options = new OverlayOptions { ShowKeys = false, ShowClicks = false };
        using var off = FrameComposer.Compose(frame, new(new CropRect(0, 0, 120, 40), .5, log, options));
        using var weak = FrameComposer.Compose(frame, new(new CropRect(0, 0, 120, 40), .5, log, options with { PointerSmoothing = "Weak" }));
        Check(!SameImage(off, weak), "平滑化したポインターを描く");
    }

    private static void Camera()
    {
        var crop = new CropRect(0, 0, 1000, 500);
        var zoom = new ZoomSettings { Enabled = true, Scale = 2 };
        var single = new CameraTrack(crop, zoom, [(5.0, new PointF(800, 100))]);
        Check(single.ViewAt(4.0) == new RectangleF(0, 0, 1000, 500) && single.ViewAt(4.4) == new RectangleF(0, 0, 1000, 500), "寄り始める前は全体");
        Check(single.ViewAt(5.0) == new RectangleF(500, 0, 500, 250), "クリックの時刻には倍率2で、範囲内に収めて寄る: " + single.ViewAt(5.0));
        var half = single.ViewAt(4.65);
        Check(Math.Abs(half.Width - 1000 / 1.5f) < .5f, "寄る途中はなめらかに倍率が変わる: " + half);
        Check(single.ViewAt(6.2).Width == 500 && single.ViewAt(6.8) == new RectangleF(0, 0, 1000, 500), "保持の後に戻る");
        var merged = new CameraTrack(crop, zoom, [(5.0, new PointF(200, 250)), (6.0, new PointF(800, 250))]);
        Check(merged.ViewAt(5.6).Width == 500 && Math.Abs(merged.ViewAt(5.7).X - 250) < .5f, "近いクリックは寄ったまま中心だけを移す: " + merged.ViewAt(5.7));
        var apart = new CameraTrack(crop, zoom, [(1.0, new PointF(500, 250)), (10.0, new PointF(500, 250))]);
        Check(apart.ViewAt(5) == new RectangleF(0, 0, 1000, 500) && apart.ViewAt(10).Width == 500, "離れたクリックの間は全体に戻る");
        Check(new CameraTrack(new CropRect(0, 0, 500, 500), zoom, [(1.0, new PointF(800, 100))]).IsIdentity, "切り出し範囲の外のクリックでは寄らない");
        Check(new CameraTrack(crop, zoom with { Enabled = false }, [(1.0, new PointF(500, 250))]).IsIdentity, "無効なら寄らない");
        var busy = new CameraTrack(new CropRect(50, 20, 640, 360), zoom with { Scale = 3 }, Enumerable.Range(0, 8).Select(i => (i * .7, new PointF(i % 2 == 0 ? 60 : 680, i * 50))));
        for (double t = 0; t < 8; t += .05)
        {
            var v = busy.ViewAt(t);
            Check(v.X >= 50 - .01 && v.Y >= 20 - .01 && v.Right <= 690.01 && v.Bottom <= 380.01 && Math.Abs(v.Width / v.Height - 640f / 360) < .001, $"{t:0.00}秒の範囲は切り出しの内側で同じ縦横比: {v}");
        }
        var fromLog = CameraTrack.FromEvents(new EventLog([new(1, 10, 800, 100, "L-Click", "None"), new(1.1, 11, 800, 100, "None", "None"), new(3, 30, 10, 10, "None", "Ctrl,S")]), crop, zoom);
        Check(fromLog.ViewAt(1).Width == 500 && !fromLog.IsIdentity, "入力ログのクリックで寄る（キー操作では寄らない）");
    }

    private static void Composition()
    {
        using var frame = new Bitmap(200, 100);
        using (var g = Graphics.FromImage(frame)) { g.Clear(Color.Red); g.FillRectangle(Brushes.Blue, 100, 0, 100, 100); }
        var crop = new CropRect(0, 0, 200, 100);
        var log = new EventLog([new(0, 0, 150, 25, "None", "None")]);
        var options = new OverlayOptions { ShowKeys = false, ShowClicks = false, PointerShape = "square", PointerSize = 6, PointerColor = "#00FF00", PointerFill = "#00FF00" };
        using var zoomed = FrameComposer.Compose(frame, new(crop, 0, log, options) { View = new RectangleF(100, 0, 100, 50) });
        Check(zoomed.Width == 200 && zoomed.Height == 100 && IsBlue(zoomed.GetPixel(10, 80)) && IsBlue(zoomed.GetPixel(190, 10)), "表示範囲を出力全体へ拡大");
        Check(zoomed.GetPixel(100, 50).G > 200 && zoomed.GetPixel(100, 50).R < 60, "ポインターは表示範囲の座標へ変換して描く");
        using var plain = FrameComposer.Compose(frame, new(crop, 0, log, options));
        using var whole = FrameComposer.Compose(frame, new(crop, 0, log, options) { View = new RectangleF(0, 0, 200, 100) });
        Check(SameImage(plain, whole) && plain.GetPixel(150, 25).G > 200, "全体の表示範囲はズームなしと同じ");
        var tsvLog = new EventLog([new(1, 10, 150, 25, "L-Click", "None"), new(2, 20, -1, -1, "None", "None")]);
        string tsv = tsvLog.ToTsv(1, 3, 10, crop, _ => new RectangleF(100, 0, 100, 50));
        Check(tsv.Contains("0.000\t0\t100\t50\tL-Click\tNone", StringComparison.Ordinal) && tsv.Contains("1.000\t10\t-1\t-1", StringComparison.Ordinal), "TSVは出力動画の座標へ変換し、範囲外は-1のまま");
        Check(tsvLog.ToTsv(1, 3, 10, crop, null) == tsvLog.ToTsv(1, 3, 10, crop), "ズームなしのTSVは従来と同じ");
    }

    private static bool IsBlue(Color c) => c.B > 200 && c.R < 60;

    private static bool SameImage(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    private static void Settings(string directory)
    {
        string path = Path.Combine(directory, "effects-settings.json");
        File.WriteAllText(path, "{\"Zoom\":{\"Enabled\":true,\"Scale\":99,\"Lead\":-1,\"ZoomIn\":0,\"Pan\":1e100},\"Overlay\":{\"PointerSmoothing\":\"Wobbly\"}}");
        var loaded = new SettingsStore(path).Load();
        Check(loaded.Zoom is { Enabled: true, Scale: 4, Lead: 0, ZoomIn: .05, Pan: 5 } && loaded.Overlay.PointerSmoothing == "Off", "ズーム・平滑化の補正");
        File.WriteAllText(path, "{}");
        var defaults = new SettingsStore(path).Load();
        Check(!defaults.Zoom.Enabled && defaults.Overlay.PointerSmoothing == "Off", "旧設定ではズーム・平滑化を使わない");
    }

    private static void MainPreview(string directory)
    {
        var media = new MediaInfo(Path.Combine(directory, "演出.mp4"), 1000, 500, 10, 10);
        var settings = new AppSettings();
        using var form = new MainForm(new SettingsStore(Path.Combine(directory, "effects-main.json")), settings, null);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        void Set(string name, object value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form, value);
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
        Set("current", media); Set("currentTime", 5.0);
        Set("events", new EventLog([new(5, 50, 800, 100, "L-Click", "None"), new(5.1, 51, 800, 100, "None", "None")]));
        var canvas = Get<VideoCanvas>("canvas");
        canvas.VideoSize = new Size(1000, 500); canvas.Crop = CropRect.Full(media);
        var show = Get<CheckBox>("showEffects");
        show.Checked = true;
        Check(canvas.ViewRect is null, "ズームが無効なら枠を出さない");
        settings.Zoom.Enabled = true; settings.Zoom.Scale = 2;
        typeof(MainForm).GetMethod("SyncEffects", Hidden)!.Invoke(form, []);
        Check(canvas.ViewRect == new RectangleF(500, 0, 500, 250), "現在時刻で写る範囲を枠で示す: " + canvas.ViewRect);
        Set("currentTime", 9.0);
        typeof(MainForm).GetMethod("SyncEffects", Hidden)!.Invoke(form, []);
        Check(canvas.ViewRect == new RectangleF(0, 0, 1000, 500), "寄っていない時刻は切り出し範囲の全体");
        show.Checked = false;
        Check(canvas.ViewRect is null, "「演出を表示」を外すと枠を消す");
        foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
            (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "ズーム検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(320, 180))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 30; i++) { graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 160, 0, 160, 180); ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1); }
        string source = Path.Combine(directory, "ズーム.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var events = new EventLog([new(0, 0, 240, 90, "None", "None"), new(1.5, 15, 240, 90, "L-Click", "None"), new(1.6, 16, 240, 90, "None", "None")]);
        var settings = new AppSettings { ExportTsv = true, ExportSrt = false, ExportVtt = false, BurnOverlay = false, Zoom = new ZoomSettings { Enabled = true, Scale = 2 } };
        string movie = Path.Combine(directory, "寄る.mp4");
        await new ExportService(service).ExportAsync(new ExportRequest(info, CropRect.Full(info), 0, info.Duration, movie, ExportKind.Mp4, settings), events, null, CancellationToken.None);
        var output = await service.ProbeAsync(movie);
        using (Bitmap before = await service.ReadFrameAsync(output, .2))
        using (Bitmap during = await service.ReadFrameAsync(output, 1.5))
        {
            Check(before.GetPixel(20, 90).R > 200 && before.GetPixel(20, 90).B < 60, "寄る前は左半分が赤");
            Check(during.GetPixel(20, 90).B > 200 && during.GetPixel(20, 90).R < 60, "クリックの時刻には右半分へ寄って全体が青");
        }
        string tsv = await File.ReadAllTextAsync(Path.ChangeExtension(movie, ".tsv"));
        Check(tsv.Contains("1.500\t15\t160\t90\tL-Click", StringComparison.Ordinal) && tsv.Contains("0.000\t0\t240\t90", StringComparison.Ordinal), "書き出したTSVは出力動画の座標: " + tsv);
        string png = Path.Combine(directory, "連番");
        await new ExportService(service).ExportAsync(new ExportRequest(info, CropRect.Full(info), 1.4, 1.6, png, ExportKind.PngSequence, settings with { StableOnly = false, SkipDuplicates = false }), events, null, CancellationToken.None);
        using (var still = new Bitmap(Directory.GetFiles(png, "*.png").Order(StringComparer.Ordinal).First()))
            Check(still.GetPixel(20, 90).R > 200, "PNG連番にはズームを使わない");
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 演出出力: MP4の自動ズーム（寄る前・クリック時）・TSVの座標変換・PNG連番は対象外");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("演出検証失敗: " + label); }
}
