using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P7の複数区間・マーカー・一括書き出し（R11）を検証する。</summary>
internal static class ClipTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Storage(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { MarkerKeys(directory); ListEditing(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("区間・マーカーの検証に失敗しました。", failure);
        Console.WriteLine("PASS 区間: 保存と補正・マーカーの追加と前後移動・タイムライン・一覧の編集/マーカー間の区間/反映/一括の指示");
    }

    private static void Storage(string directory)
    {
        var media = new MediaInfo(Path.Combine(directory, "区間.mp4"), 640, 360, 10, 10);
        var clips = SettingsStore.NormalizeClips([null, new ClipRegion { Id = "", Name = " ", Start = 8, End = 3, Crop = new CropRect(0, 0, 0, 0) }, new ClipRegion { Name = "後半", Start = -1, End = 99, Crop = new CropRect(10, 10, 100, 50) }], media);
        Check(clips.Count == 2 && clips[0] is { Name: "区間1", Start: 8, Crop: null } && Math.Abs(clips[0].End - 8.1) < 1e-9 && clips[0].Id.Length > 0, "名前・逆転・空のクロップの補正");
        Check(clips[1] is { Start: 0, End: 10 } && clips[1].Crop == new CropRect(10, 10, 100, 50), "範囲外の時刻の補正とクロップの保持");
        var markers = SettingsStore.NormalizeMarkers([new Marker { Time = 5.55, Label = null! }, null, new Marker { Time = 2, Label = "前" }], media);
        Check(markers.Count == 2 && markers[0].Label == "前" && Math.Abs(markers[1].Time - 5.5) < 1e-9 && markers[1].Label == "", "マーカーは時刻順・フレーム格子・名前の補正");
        SettingsStore.SaveVideo(media, new VideoSettings { Crop = CropRect.Full(media), End = 10, Clips = clips, Markers = markers });
        var loaded = SettingsStore.LoadVideo(media, out var warning);
        Check(warning is null && loaded.Clips.Count == 2 && loaded.Clips[1].Name == "後半" && loaded.Markers.Count == 2, "区間とマーカーの保存と復元");
    }

    private static void MarkerKeys(string directory)
    {
        var media = new MediaInfo(Path.Combine(directory, "マーカー.mp4"), 640, 360, 10, 10);
        using var form = new MainForm(new SettingsStore(Path.Combine(directory, "clip-main.json")), new AppSettings(), null);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        void Set(string name, object value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form, value);
        T Get<T>(string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
        void Call(string name, params object[] args) => typeof(MainForm).GetMethod(name, Hidden)!.Invoke(form, args);
        Set("current", media);
        foreach (double t in new[] { 6.04, 2.0, 6.0 }) { Set("currentTime", t); Call("AddMarker"); }
        var markers = Get<List<Marker>>("markers");
        Check(markers.Select(m => m.Time).SequenceEqual([2.0, 6.0]), "マーカーは時刻順で、同じフレームには重ねない");
        Check(Get<Timeline>("timeline").Markers.SequenceEqual([2.0, 6.0]), "タイムラインにマーカー");
        Set("currentTime", 4.0); Call("JumpMarker", 1);
        Check(Get<double>("currentTime") == 6.0, "次のマーカーへ");
        Call("JumpMarker", -1);
        Check(Get<double>("currentTime") == 2.0, "前のマーカーへ");
        Set("clips", new List<ClipRegion> { new() { Name = "a", Start = 1, End = 3 }, new() { Name = "b", Start = 4, End = 5, Enabled = false } });
        Call("SyncClips");
        Check(Get<Timeline>("timeline").ClipRanges.SequenceEqual([(1.0, 3.0)]), "タイムラインには有効な区間だけ");
        foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
            (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
    }

    private static void ListEditing()
    {
        var media = new MediaInfo("list.mp4", 640, 360, 10, 10);
        var original = new List<ClipRegion> { new() { Name = "最初", Start = 0, End = 2 } };
        var markers = new List<Marker> { new() { Time = 3, Label = "操作A" }, new() { Time = 7, Label = "操作B" } };
        using var form = new ClipListForm(original, markers, media, 1, 4, new CropRect(5, 5, 100, 100), 8, "");
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        void Call(string name) => typeof(ClipListForm).GetMethod(name, Hidden)!.Invoke(form, []);
        Call("AddCurrent");
        Check(form.Clips.Count == 2 && form.Clips[1] is { Name: "区間2", Start: 1, End: 4 } && form.Clips[1].Crop == new CropRect(5, 5, 100, 100), "現在の区間と切り出しを追加");
        Call("FromMarkers");
        Check(form.Clips.Count == 4 && form.Clips[2] is { Name: "操作A", Start: 3, End: 7 } && form.Clips[3] is { Name: "操作B", Start: 7, End: 10 }, "マーカー間と最後のマーカー〜末尾の区間");
        Call("AddMarker");
        Check(form.Markers.Count == 3 && form.Markers[^1].Time == 8, "現在位置にマーカー");
        var grid = (DataGridView)typeof(ClipListForm).GetField("clipGrid", Hidden)!.GetValue(form)!;
        grid.Rows[0].Cells[1].Value = "名前を変更"; grid.Rows[0].Cells[3].Value = "1.5";
        typeof(ClipListForm).GetMethod("ApplyClip", Hidden)!.Invoke(form, [0]);
        Check(form.Clips[0] is { Name: "名前を変更", End: 1.5 } && original[0].Name == "最初", "一覧の編集はコピーへ反映");
        grid.ClearSelection(); grid.Rows[2].Selected = true;
        Call("ApplySelected");
        Check(form.Apply?.Name == "操作A" && form.DialogResult == DialogResult.OK, "選んだ区間を主画面へ反映");
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "一括書き出し検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(160, 90))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 40; i++) { graphics.Clear(i < 20 ? Color.Gray : Color.Teal); ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1); }
        string source = Path.Combine(directory, "連続作業.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var events = new EventLog([new(.5, 5, 50, 40, "L-Click", "None"), new(.6, 6, 50, 40, "None", "None"), new(2.5, 25, 60, 40, "L-Click", "None"), new(2.6, 26, 60, 40, "None", "None")]);
        string output = Path.Combine(directory, "出力");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "連続作業_後半.mp4"), "既存");
        ClipRegion[] targets = [new() { Name = "前半", Start = 0, End = 2 }, new() { Name = "後半", Start = 2, End = 4, Crop = new CropRect(0, 0, 80, 90) }, new() { Name = "a:b?", Start = 1, End = 3 }];
        var settings = new AppSettings { ExportTsv = false, ExportSrt = false, ExportVtt = false, StableOnly = false, SkipDuplicates = false };
        var mask = new MaskRegion { Rect = new(0, 0, 20, 20), Start = 0, End = 4, Mode = MaskMode.Fill, Color = "#FF00FF" };
        var results = await ClipBatch.RunAsync(service, info, events, targets, new ClipBatchRequest(ClipBatchKind.Mp4 | ClipBatchKind.Steps, output),
            CropRect.Full(info), settings, [mask], null, CancellationToken.None);
        Check(results.Count == 6, "3区間×2形式の結果: " + results.Count);
        Check(results.Single(r => r.Clip == "後半" && r.Kind == "MP4") is { Succeeded: false } failed && failed.Message.Contains("既に存在", StringComparison.Ordinal), "既存のファイルは上書きせず失敗として残す");
        Check(File.ReadAllText(Path.Combine(output, "連続作業_後半.mp4")) == "既存", "既存のファイルを変えない");
        Check(results.Count(r => r.Succeeded) == 5, "失敗の後も残りを続ける: " + string.Join(" / ", results.Select(r => $"{r.Clip}:{r.Kind}:{r.Succeeded}:{r.Message}")));
        var first = await service.ProbeAsync(Path.Combine(output, "連続作業_前半.mp4"));
        Check(Math.Abs(first.Duration - 2) < .15, "区間の長さで書き出す");
        Check(File.Exists(Path.Combine(output, "連続作業_a_b_.mp4")), "区間名の使えない文字を置き換える");
        string steps = Path.Combine(output, "連続作業_後半_手順書");
        Check(Directory.Exists(steps) && File.ReadAllText(Path.Combine(steps, "手順書.md")).StartsWith("# 後半", StringComparison.Ordinal), "手順書は区間名の題名・フォルダー");
        using (var image = new Bitmap(Directory.GetFiles(Path.Combine(steps, "images"))[0]))
            Check(image.Width == 80 && image.GetPixel(5, 5).ToArgb() == Color.FromArgb(255, 0, 255).ToArgb(), "区間の切り出しとマスクを手順書にも使う");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            var canceled = await ClipBatch.RunAsync(service, info, events, targets, new ClipBatchRequest(ClipBatchKind.Gif, Path.Combine(directory, "素材")), CropRect.Full(info), settings, [], null, cancel.Token);
            Check(canceled.All(r => !r.Succeeded && r.Message == "中断しました。"), "中断した残りは中断として結果に残す");
        }
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 一括書き出し: 区間ごとのMP4・手順書・既存の保護と継続・名前の置換・区間の切り出しとマスク・中断");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("区間検証失敗: " + label); }
}
