using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P6の無操作区間のカット・早送り（R7）を検証する。</summary>
internal static class IdleTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Mapping();
        Detection();
        Sidecars();
        Settings(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { Timeline(directory); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("無操作区間の表示の検証に失敗しました。", failure);
        Console.WriteLine("PASS 無操作区間: 写像（カット/早送り/余白/短い区間）・検出（入力/移動/画面変化）・TSV/字幕の時刻・設定・タイムライン");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static void Mapping()
    {
        var cut = new TimeMap(0, 20, [(2, 10)], new IdleSettings { Mode = "Cut", Margin = .5 });
        Check(cut.Changed.Single() == new TimeMap.Segment(2.5, 9.5, 0), "前後の余白を残した内側をカット");
        Check(Near(cut.ToOutput(1), 1) && Near(cut.ToOutput(5), 2.5) && Near(cut.ToOutput(9.5), 2.5) && Near(cut.ToOutput(12), 5) && Near(cut.OutputDuration, 13), "カットの写像");
        var fast = new TimeMap(0, 20, [(2, 10)], new IdleSettings { Mode = "Speed", Margin = .5, Speed = 4 });
        Check(Near(fast.ToOutput(5), 3.125) && Near(fast.ToOutput(9.5), 4.25) && Near(fast.OutputDuration, 14.75), "早送りの写像");
        Check(new TimeMap(0, 20, [(2, 10)], new IdleSettings { Mode = "Off" }).IsIdentity, "Offは写像しない");
        Check(new TimeMap(0, 20, [(2, 2.8)], new IdleSettings { Mode = "Cut", Margin = .5 }).IsIdentity, "余白を除くと残らない区間は対象外");
        var range = new TimeMap(5, 20, [(0, 8), (12, 30)], new IdleSettings { Mode = "Cut", Margin = .5 });
        Check(range.Changed.Count == 2 && Near(range.Changed[0].Start, 5) && Near(range.Changed[1].End, 20) && Near(range.ToOutput(5), 0), "書き出し区間の外へははみ出さない");
        double previous = -1;
        for (double s = 0; s <= 20; s += .05) { double o = fast.ToOutput(s); Check(o >= previous - 1e-12, "写像は単調"); previous = o; }
    }

    private static void Detection()
    {
        var media = new MediaInfo("idle.mp4", 320, 180, 20, 10);
        var changes = new float[200];
        changes[50] = .5f;
        var profile = new MotionProfile(media, 0, changes, StabilitySettings.Standard);
        var events = new EventLog([
            new(0, 0, 10, 10, "None", "None"), new(1, 10, 10, 10, "L-Click", "None"), new(1.1, 11, 10, 10, "None", "None"),
            new(15, 150, 10, 10, "None", "Ctrl,S"), new(15.1, 151, 10, 10, "None", "None")
        ]);
        var idle = IdleDetector.Detect(events, profile, new IdleSettings { MinSeconds = 3 });
        Check(idle.Count == 3 && Near(idle[0].Start, 1.2) && Near(idle[0].End, 5) && Near(idle[1].Start, 5.1) && Near(idle[1].End, 15) && Near(idle[2].Start, 15.2) && Near(idle[2].End, 20),
            "入力・画面の変化で区切った無操作区間: " + string.Join(", ", idle));
        var moved = new EventLog([.. events.Events, new(8, 80, 50, 50, "None", "None"), new(8.5, 85, 51, 50, "None", "None")]);
        var split = IdleDetector.Detect(moved, profile, new IdleSettings { MinSeconds = 3 });
        Check(split.Any(r => Near(r.Start, 8.1) && Near(r.End, 15)) && !split.Any(r => Near(r.Start, 5.1)), "3px超のポインター移動は操作、1pxの揺れは無視: " + string.Join(", ", split));
    }

    private static void Sidecars()
    {
        var events = new EventLog([new(1, 10, 10, 10, "None", "Ctrl,C"), new(1.2, 12, 10, 10, "None", "None"), new(5, 50, 10, 10, "None", "Ctrl,V"), new(5.2, 52, 10, 10, "None", "None"), new(12, 120, 10, 10, "None", "Ctrl,S"), new(12.2, 122, 10, 10, "None", "None")]);
        var map = new TimeMap(0, 20, [(2, 11)], new IdleSettings { Mode = "Cut", Margin = .5 });
        string tsv = events.ToTsv(0, 20, 10, new CropRect(0, 0, 100, 100), null, map.ToOutput);
        Check(tsv.Contains("2.500\t25\t10\t10\tNone\tCtrl,V", StringComparison.Ordinal) && tsv.Contains("4.000\t40\t10\t10\tNone\tCtrl,S", StringComparison.Ordinal), "TSVの時刻を出力の時刻へ写す: " + tsv);
        var options = new OverlayOptions { FadeDuration = .3, MaxStack = 1 };
        string srt = events.ToSubtitles(0, 20, options, false, map.ToOutput);
        Check(srt.Contains("Ctrl + C", StringComparison.Ordinal) && srt.Contains("Ctrl + S", StringComparison.Ordinal) && !srt.Contains("Ctrl + V", StringComparison.Ordinal), "カットされた区間だけの字幕は出さない: " + srt);
        Check(srt.Contains("00:00:04,000 -->", StringComparison.Ordinal), "後の字幕は出力の時刻");
        Check(events.ToSubtitles(0, 20, options, false, null) == events.ToSubtitles(0, 20, options, false), "写像なしの字幕は従来と同じ");
    }

    private static void Settings(string directory)
    {
        string path = Path.Combine(directory, "idle-settings.json");
        File.WriteAllText(path, "{\"Idle\":{\"Mode\":\"Skip\",\"MinSeconds\":0,\"Margin\":-3,\"Speed\":1000}}");
        var idle = new SettingsStore(path).Load().Idle;
        Check(idle is { Mode: "Off", MinSeconds: .5, Margin: 0, Speed: 64 } && !idle.Enabled, "無操作区間の設定の補正");
    }

    private static void Timeline(string directory)
    {
        var media = new MediaInfo(Path.Combine(directory, "無操作.mp4"), 320, 180, 20, 10);
        var settings = new AppSettings { Idle = new IdleSettings { Mode = "Cut", MinSeconds = 3, Margin = .5 } };
        using var form = new MainForm(new SettingsStore(Path.Combine(directory, "idle-main.json")), settings, null);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        typeof(MainForm).GetField("current", Hidden)!.SetValue(form, media);
        typeof(MainForm).GetField("events", Hidden)!.SetValue(form, new EventLog([new(0, 0, 1, 1, "None", "None"), new(10, 100, 1, 1, "L-Click", "None"), new(10.1, 101, 1, 1, "None", "None")]));
        var profile = new MotionProfile(media, 0, new float[200], StabilitySettings.Standard);
        typeof(MainForm).GetMethod("ApplyIdle", Hidden)!.Invoke(form, [profile]);
        var ranges = ((Timeline)typeof(MainForm).GetField("timeline", Hidden)!.GetValue(form)!).IdleRanges;
        Check(ranges.Count == 2 && Near(ranges[0].Start, .5) && Near(ranges[0].End, 9.5) && Near(ranges[1].Start, 10.7) && Near(ranges[1].End, 19.5), "カットされる部分をタイムラインへ: " + string.Join(", ", ranges));
        foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
            (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "無操作区間検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(160, 90))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 100; i++) { graphics.Clear(i < 50 ? Color.Gray : Color.Teal); ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1); }
        string source = Path.Combine(directory, "待ち時間.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var events = new EventLog([new(0, 0, 10, 10, "None", "None"), new(.5, 5, 10, 10, "L-Click", "None"), new(.6, 6, 10, 10, "None", "None")]);
        foreach (var (mode, label) in new[] { ("Cut", "カット"), ("Speed", "早送り") })
        {
            var idle = new IdleSettings { Mode = mode, MinSeconds = 2, Margin = .5, Speed = 4 };
            var profile = await MotionProfile.MeasureAsync(service, info, 0, info.Duration, CropRect.Full(info), StabilitySettings.Standard);
            var expected = new TimeMap(0, info.Duration, IdleDetector.Detect(events, profile, idle), idle);
            Check(!expected.IsIdentity, label + "の対象区間がある");
            string movie = Path.Combine(directory, label + ".mp4");
            var settings = new AppSettings { Idle = idle, ExportTsv = true, ExportSrt = false, ExportVtt = false };
            var result = await new ExportService(service).ExportAsync(new ExportRequest(info, CropRect.Full(info), 0, info.Duration, movie, ExportKind.Mp4, settings), events, null, CancellationToken.None);
            var output = await service.ProbeAsync(movie);
            Check(Math.Abs(output.Duration - expected.OutputDuration) < .15 && output.Duration < info.Duration - 3, $"{label}で短くなる: 出力{output.Duration:0.00}秒、写像{expected.OutputDuration:0.00}秒");
            Check(Math.Abs(result.FrameCount - expected.OutputDuration * info.Fps) <= 2, $"{label}の出力フレーム数: {result.FrameCount}");
            using (Bitmap last = await service.ReadFrameAsync(output, output.LastFrameTime))
                Check(last.GetPixel(80, 45).G > 100 && last.GetPixel(80, 45).R < 60, label + "でも画面が変わった後の映像は残る");
            Check(File.ReadAllText(Path.ChangeExtension(movie, ".tsv")).Contains("0.500\t5\t10\t10\tL-Click", StringComparison.Ordinal), label + "の前のクリックの時刻はそのまま");
        }
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 無操作区間出力: MP4のカット・早送りで写像どおりの長さ・変化後の映像の保持・TSVの時刻");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("無操作区間検証失敗: " + label); }
}
