using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.3.0 P3のマスク（R4）を、描画・保存・編集・全出力経路で検証する。</summary>
internal static class MaskTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Color Blue = Color.FromArgb(0, 0, 255);

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Rendering();
        Composer();
        Storage(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { CanvasEditing(); ListEditing(); MainWiring(directory); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("マスク編集の検証に失敗しました。", failure);
        Console.WriteLine("PASS マスク: 3方式・時間の境界・縮小表示・元画像の保護・保存と補正・キャンバス編集・一覧・主画面の出力指定");
    }

    private static Bitmap Pattern(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, Color.FromArgb(255, (x * 7) % 256, (y * 11) % 256, ((x + y) * 5) % 256));
        return bitmap;
    }

    private static MaskRegion Fill(CropRect rect, double start = 1, double end = 2) =>
        new() { Rect = rect, Start = start, End = end, Mode = MaskMode.Fill, Color = "#0000FF" };

    private static void Rendering()
    {
        using var source = Pattern(100, 60);
        var before = source.GetPixel(30, 20);
        var rect = new CropRect(20, 10, 40, 30);
        using (var masked = MaskRenderer.Apply(source, [Fill(rect)], 1))
        {
            Check(masked.GetPixel(20, 10).ToArgb() == Blue.ToArgb() && masked.GetPixel(59, 39).ToArgb() == Blue.ToArgb(), "塗りつぶしは矩形の端まで");
            Check(masked.GetPixel(19, 10) == source.GetPixel(19, 10) && masked.GetPixel(60, 39) == source.GetPixel(60, 39), "矩形の外は変えない");
        }
        Check(source.GetPixel(30, 20) == before, "元の画像を書き換えない");
        Check(!MaskRenderer.HasActive([Fill(rect)], .999) && MaskRenderer.HasActive([Fill(rect)], 1) && !MaskRenderer.HasActive([Fill(rect)], 2), "開始を含み終了を含まない");
        Check(!MaskRenderer.HasActive([Fill(rect) with { Enabled = false }], 1.5), "無効なマスクは描かない");
        using (var mosaic = MaskRenderer.Apply(source, [Fill(rect) with { Mode = MaskMode.Mosaic, Strength = 10 }], 1))
        {
            Check(mosaic.GetPixel(20, 10) == mosaic.GetPixel(29, 19) && mosaic.GetPixel(20, 10) != mosaic.GetPixel(30, 10), "モザイクはブロック内が同じ色");
            Check(Enumerable.Range(20, 40).Count(x => mosaic.GetPixel(x, 25) != source.GetPixel(x, 25)) > 30, "モザイクで元の画素が読めない");
            Check(mosaic.GetPixel(10, 5) == source.GetPixel(10, 5), "モザイクの外は変えない");
        }
        using (var blur = MaskRenderer.Apply(source, [Fill(rect) with { Mode = MaskMode.Blur, Strength = 6 }], 1))
        {
            Check(Enumerable.Range(25, 30).Count(x => blur.GetPixel(x, 25) != source.GetPixel(x, 25)) > 20, "ぼかしで画素が変わる");
            Check(blur.GetPixel(70, 50) == source.GetPixel(70, 50), "ぼかしの外は変えない");
        }
        using var half = new Bitmap(50, 30);
        using (var preview = MaskRenderer.Apply(half, [Fill(rect)], 1, .5f, .5f))
            Check(preview.GetPixel(10, 5).ToArgb() == Blue.ToArgb() && preview.GetPixel(29, 19).ToArgb() == Blue.ToArgb() && preview.GetPixel(30, 20).ToArgb() != Blue.ToArgb(), "縮小表示では矩形も縮小");
    }

    private static void Composer()
    {
        using var source = Pattern(100, 60);
        var masks = new[] { Fill(new(20, 10, 40, 30)) };
        var crop = new CropRect(10, 5, 80, 50);
        using (var output = FrameComposer.Compose(source, new(crop, 1.5, EventLog.Empty, new()) { Masks = masks }))
            Check(output.GetPixel(10, 5).ToArgb() == Blue.ToArgb() && output.GetPixel(9, 5) == source.GetPixel(19, 10), "出力ではクロップ後の位置にマスク");
        using (var raw = FrameComposer.Compose(source, new(crop, 1.5, EventLog.Empty, new()) { Masks = masks, DrawOverlay = false }))
            Check(raw.GetPixel(10, 5).ToArgb() == Blue.ToArgb(), "演出なしの画像にもマスク");
        using var small = Pattern(50, 30);
        using (var preview = FrameComposer.ComposePreview(small, new MediaInfo("p.mp4", 100, 60, 10, 10), new(crop, 1.5, EventLog.Empty, new()) { Masks = masks }))
            Check(preview.GetPixel(12, 7).ToArgb() == Blue.ToArgb(), "縮小プレビューにもマスク");
        using (var outside = FrameComposer.Compose(source, new(crop, 2.5, EventLog.Empty, new()) { Masks = masks }))
            Check(outside.GetPixel(10, 5) == source.GetPixel(20, 10), "時間外はマスクしない");
    }

    private static void Storage(string directory)
    {
        var media = new MediaInfo(Path.Combine(directory, "マスク動画.mp4"), 640, 360, 10, 10);
        var normalized = SettingsStore.NormalizeMasks([
            null, new MaskRegion { Id = "", Rect = new(600, 300, 200, 200), Start = 5, End = 3, Strength = 999, Color = "red", Source = "Other", Note = null! },
            new MaskRegion { Rect = new(10, 10, 20, 20), Start = -1, End = 99, Mode = (MaskMode)42 }], media);
        Check(normalized.Count == 2, "nullのマスクを除く");
        var a = normalized[0];
        Check(a.Rect == new CropRect(440, 160, 200, 200) && a.End > a.Start && Math.Abs(a.End - 5.1) < 1e-9 && a.Strength == 256 && a.Color == "#000000" && a.Source == "Manual" && a.Note == "" && a.Id.Length > 0, "動画外・逆転・範囲外の補正");
        Check(normalized[1].Start == 0 && normalized[1].End == 10 && normalized[1].Mode == MaskMode.Mosaic, "時間と方式の補正");
        var settings = new VideoSettings { Crop = CropRect.Full(media), End = 10, Masks = [Fill(new(1, 2, 30, 40)), new MaskRegion { Rect = new(5, 5, 5, 5), Start = 0, End = 1, Mode = MaskMode.Blur, Source = "Suggested", Enabled = false }] };
        SettingsStore.SaveVideo(media, settings);
        string json = File.ReadAllText(SettingsStore.VideoPath(media.Path));
        Check(json.Contains("\"Fill\"", StringComparison.Ordinal) && json.Contains("\"Blur\"", StringComparison.Ordinal), "方式は名前で保存");
        var loaded = SettingsStore.LoadVideo(media, out var warning);
        Check(warning is null && loaded.Masks.Count == 2 && loaded.Masks[0].Rect == new CropRect(1, 2, 30, 40) && loaded.Masks[0].Mode == MaskMode.Fill
            && loaded.Masks[1].Source == "Suggested" && !loaded.Masks[1].Enabled, "マスクの保存と復元");
        File.WriteAllText(SettingsStore.VideoPath(media.Path), "{\"Crop\":{\"X\":0,\"Y\":0,\"Width\":10,\"Height\":10},\"End\":3}");
        Check(SettingsStore.LoadVideo(media, out _).Masks.Count == 0, "マスクのない旧設定");
    }

    private static void Mouse(VideoCanvas canvas, string method, MouseButtons button, int x, int y) =>
        typeof(VideoCanvas).GetMethod(method, Hidden)!.Invoke(canvas, [new MouseEventArgs(button, 1, x, y, 0)]);

    private static void CanvasEditing()
    {
        // 672×392に1280×720を表示すると倍率0.5、原点(16,16)。
        using var canvas = new VideoCanvas { Size = new Size(672, 392), VideoSize = new Size(1280, 720), Crop = new CropRect(0, 0, 400, 300) };
        var crop = canvas.Crop;
        var committed = new List<(int Index, CropRect Rect)>();
        int edited = 0;
        canvas.MaskCommitted += (_, e) => committed.Add((e.Index, e.Rect));
        canvas.MaskEdited += (_, _) => edited++;
        canvas.MaskMode = true;
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 116, 66);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 216, 116);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 216, 116);
        Check(committed.Single() == (-1, new CropRect(200, 100, 200, 100)), "左ドラッグでマスクを作成");
        Check(canvas.Crop == crop, "マスク編集中は切り出し枠を変えない");
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 300, 300);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 300, 300);
        Check(committed.Count == 1, "クリックだけでは作成しない");

        canvas.MaskRects = [new CropRect(200, 100, 200, 100)];
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 166, 91);
        Check(canvas.SelectedMask == 0, "クリックしたマスクを選ぶ");
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 176, 101);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 176, 101);
        Check(edited > 0 && committed[^1] == (0, new CropRect(220, 120, 200, 100)) && canvas.MaskRects[0] == new CropRect(220, 120, 200, 100), "マスクの移動");
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 226, 126);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 236, 136);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 236, 136);
        Check(committed[^1] == (0, new CropRect(220, 120, 220, 120)), "右下ハンドルでマスクを変形");
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 176, 101);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 1000, 1000);
        Check(canvas.MaskRects[0] == new CropRect(1060, 600, 220, 120), "マスクは動画の外へ出ない");
        typeof(VideoCanvas).GetMethod("OnKeyDown", Hidden)!.Invoke(canvas, [new KeyEventArgs(Keys.Escape)]);
        Check(canvas.MaskRects[0] == new CropRect(220, 120, 220, 120), "Escでドラッグ前へ戻す");
        int count = committed.Count;
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 1000, 1000);
        Check(committed.Count == count, "取り消したドラッグは確定しない");
        canvas.MaskMode = false;
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 116, 66);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 216, 116);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 216, 116);
        Check(committed.Count == count && canvas.Crop != crop, "通常モードでは切り出し枠を編集");
    }

    private static void ListEditing()
    {
        var media = new MediaInfo("list.mp4", 640, 360, 10, 10);
        var original = new List<MaskRegion> { Fill(new(0, 0, 10, 10), 1, 2), Fill(new(5, 5, 10, 10), 3, 4) };
        using var form = new MaskListForm(original, media, 6, 0);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        var grid = (DataGridView)typeof(MaskListForm).GetField("grid", Hidden)!.GetValue(form)!;
        Check(grid.Rows.Count == 2 && grid.Rows[1].Cells[4].Value as string == "塗りつぶし", "一覧に方式を表示");
        grid.Rows[0].Cells[4].Value = "ぼかし";
        grid.Rows[0].Cells[3].Value = "0:00:08.5";
        typeof(MaskListForm).GetMethod("ApplyRow", Hidden)!.Invoke(form, [0]);
        Check(form.Masks[0].Mode == MaskMode.Blur && form.Masks[0].End == 8.5 && original[0].Mode == MaskMode.Fill, "一覧の編集はコピーへ反映");
        grid.ClearSelection(); grid.Rows[1].Selected = true;
        typeof(MaskListForm).GetMethod("SetTime", Hidden)!.Invoke(form, [false]);
        Check(form.Masks[1].Start == 6 && form.Masks[1].End == 10, "開始を現在位置にすると逆転した終了を動画末尾へ");
        typeof(MaskListForm).GetMethod("DeleteSelected", Hidden)!.Invoke(form, []);
        Check(form.Masks.Count == 1 && original.Count == 2, "選択したマスクを削除");
    }

    private static void MainWiring(string directory)
    {
        string video = Path.Combine(directory, "主画面マスク.mp4");
        var media = new MediaInfo(video, 1280, 720, 10, 10);
        using var form = new MainForm(new SettingsStore(Path.Combine(directory, "main-mask-settings.json")), new AppSettings(), null);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        void Set(string name, object value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form, value);
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
        Set("current", media); Set("currentTime", 2.34); Set("rangeStart", 0d); Set("rangeEnd", 6d);
        var canvas = Get<VideoCanvas>("canvas");
        canvas.VideoSize = new Size(1280, 720); canvas.Crop = CropRect.Full(media);
        Get<CheckBox>("maskEdit").Checked = true;
        Check(canvas.MaskMode && canvas.ContextMenuStrip is not null, "「マスク」でキャンバスをマスク編集へ");
        var toClient = typeof(VideoCanvas).GetMethod("ToClient", Hidden)!;
        PointF Client(int x, int y) => ((RectangleF)toClient.Invoke(canvas, [new CropRect(x, y, 1, 1)])!).Location;
        var a = Client(100, 100); var b = Client(300, 200);
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, (int)Math.Round(a.X), (int)Math.Round(a.Y));
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, (int)Math.Round(b.X), (int)Math.Round(b.Y));
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, (int)Math.Round(b.X), (int)Math.Round(b.Y));
        var masks = Get<List<MaskRegion>>("masks");
        Check(masks.Count == 1 && Math.Abs(masks[0].Start - 2.3) < 1e-9 && masks[0].End == 6 && masks[0].Rect.Width > 150, "作成したマスクは現在位置から区間の終わりまで");
        Check(canvas.SelectedMask == 0 && canvas.MaskRects.Count == 1, "作成したマスクを選択して表示");
        var context = (RenderContext)typeof(MainForm).GetMethod("OutputContext", Hidden)!.Invoke(form, [CropRect.Full(media), 3.0])!;
        Check(context.Masks.Count == 1 && !ReferenceEquals(context.Masks[0], masks[0]), "プレビュー・コピー・PNG保存の合成にマスクの複製を渡す");
        Set("currentTime", 7.0);
        typeof(MainForm).GetMethod("SyncMasks", Hidden)!.Invoke(form, []);
        Check(canvas.MaskRects.Count == 0 && canvas.SelectedMask == -1, "時間外のマスクは表示しない");
        Set("currentTime", 3.0);
        typeof(MainForm).GetMethod("SyncMasks", Hidden)!.Invoke(form, []);
        typeof(MainForm).GetMethod("SaveVideo", Hidden)!.Invoke(form, []);
        Check(SettingsStore.LoadVideo(media, out _).Masks.Single().End == 6, "「保存」でマスクを動画別設定へ記録");
        canvas.SelectedMask = 0;
        typeof(MainForm).GetMethod("DeleteSelectedMask", Hidden)!.Invoke(form, []);
        Check(Get<List<MaskRegion>>("masks").Count == 0 && canvas.MaskRects.Count == 0, "選択したマスクを削除");
        foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
            (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "マスク出力検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        // 赤い画面の(40,20)-(80,50)だけが毎フレーム変わる。マスクで隠すと、見えている画像は全フレーム同じになる。
        using (var bitmap = new Bitmap(160, 90))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 10; i++)
            {
                graphics.Clear(Color.Red);
                using var brush = new SolidBrush(Color.FromArgb(255, i * 25, 255 - i * 25, 0));
                graphics.FillRectangle(brush, 40, 20, 40, 30);
                ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1);
            }
        string source = Path.Combine(directory, "機密.mkv");
        await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source]);
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var full = CropRect.Full(info);
        var window = new MaskRegion { Rect = new(40, 20, 40, 30), Start = .3, End = .7, Mode = MaskMode.Fill, Color = "#0000FF" };
        var export = new ExportService(service);
        var settings = new AppSettings { StableOnly = false, SkipDuplicates = false, ExportTsv = false, ExportSrt = false, ExportVtt = false };

        string png = Path.Combine(directory, "連番");
        await export.ExportAsync(new ExportRequest(info, full, 0, info.Duration, png, ExportKind.PngSequence, settings) { Masks = [window] }, EventLog.Empty, null, CancellationToken.None);
        var files = Directory.GetFiles(png, "*.png").Order(StringComparer.Ordinal).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            using var image = new Bitmap(files[i]);
            bool masked = image.GetPixel(60, 35).ToArgb() == Blue.ToArgb();
            Check(masked == (i is >= 3 and < 7), $"PNG連番 {i} 枚目のマスク: {masked}");
        }
        foreach (var kind in new[] { ExportKind.Mp4, ExportKind.Gif })
        {
            string movie = Path.Combine(directory, "出力." + kind.ToString().ToLowerInvariant());
            await export.ExportAsync(new ExportRequest(info, full, 0, info.Duration, movie, kind, settings) { Masks = [window] }, EventLog.Empty, null, CancellationToken.None);
            var movieInfo = await service.ProbeAsync(movie);
            using Bitmap inside = await service.ReadFrameAsync(movieInfo, .5);
            using Bitmap before = await service.ReadFrameAsync(movieInfo, .1);
            Color m = inside.GetPixel(60, 35), u = before.GetPixel(60, 35);
            Check(m.B > 200 && m.R < 60 && m.G < 60, kind + "の時間内はマスク: " + m);
            Check(!(u.B > 200 && u.R < 60 && u.G < 60), kind + "の時間外はマスクしない: " + u);
        }
        using (Bitmap step = await StepDetector.RenderImageAsync(service, info, EventLog.Empty, full, settings, .5, [window]))
            Check(step.GetPixel(60, 35).ToArgb() == Blue.ToArgb(), "手順書の画像にもマスク");

        string stable = Path.Combine(directory, "静止判定");
        var always = window with { Start = 0, End = info.Duration };
        var result = await export.ExportAsync(new ExportRequest(info, full, 0, info.Duration, stable, ExportKind.PngSequence,
            settings with { StableOnly = true, SkipDuplicates = true, Stability = StabilitySettings.Exact }) { Masks = [always] }, EventLog.Empty, null, CancellationToken.None);
        Check(result.FrameCount == 1, "静止・重複はマスク後の見えている画像で判定: " + result.FrameCount);
        Directory.Delete(directory, true);
        Console.WriteLine("PASS マスク出力: PNG連番・MP4・GIF・手順画像の時間内適用と時間外の非適用、マスク後の静止・重複判定");
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("FFmpegを起動できません。");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(error);
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("マスク検証失敗: " + label); }
}
