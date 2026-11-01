using System.Diagnostics;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P1の基盤（操作抽出・フレーム比較・動き量・合成処理の一本化）を検証する。</summary>
internal static class FoundationTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Actions();
        Comparer();
        Settings(directory);
        SingleComposerPath();
        Console.WriteLine("PASS 基盤: 操作抽出・許容差つき比較・設定補正・原子的書込み・合成処理の一本化");
    }

    private static void Actions()
    {
        var log = new EventLog([
            new(.1, 1, 10, 10, "L-Click", "None"), new(.2, 2, 10, 10, "None", "None"),
            new(.3, 3, 11, 10, "L-Click", "None"), new(.35, 3, 11, 10, "None", "None"),
            new(1.0, 10, 40, 40, "R-Click", "None"), new(1.1, 11, 40, 40, "None", "None"),
            new(2.0, 20, 50, 50, "L-Click", "None"), new(2.2, 22, 100, 52, "L-Click", "None"), new(2.3, 23, 101, 52, "None", "None"),
            new(3.0, 30, 60, 60, "None", "Ctrl"), new(3.1, 31, 60, 60, "None", "Ctrl,S"), new(3.2, 32, 60, 60, "None", "None"),
            new(4.0, 40, 70, 70, "None", "a"), new(4.1, 41, 70, 70, "None", "None"), new(4.2, 42, 70, 70, "None", "Shift,b"), new(4.3, 43, 70, 70, "None", "None"),
            new(6.0, 60, 70, 70, "None", "c"), new(6.1, 61, 70, 70, "None", "None"),
            new(7.0, 70, 70, 70, "None", "Shift"), new(7.1, 71, 70, 70, "None", "None"),
            new(8.0, 80, 80, 80, "Wheel-Down", "None"), new(8.1, 81, 80, 80, "None", "None"), new(8.5, 85, 80, 80, "Wheel-Down", "None"), new(8.6, 86, 80, 80, "None", "None"),
            new(9.0, 90, 90, 90, "None", "Enter"), new(9.1, 91, 90, 90, "None", "None")
        ]);
        var actions = InputActions.Extract(log);
        string kinds = string.Join(",", actions.Select(a => a.Kind));
        Check(kinds == "DoubleClick,RightClick,Drag,Shortcut,Text,Text,Wheel,Shortcut", "操作の種類と順序: " + kinds);
        Check(actions[0].Time == .1 && actions[0].Point == new Point(10, 10) && actions[0].Label == "左ダブルクリック", "ダブルクリックは最初の押下の時刻・位置");
        Check(actions[2].Point == new Point(50, 50) && actions[2].EndPoint == new Point(100, 52) && actions[2].Label == "ドラッグ", "ドラッグの始点と押下中の最後の座標");
        Check(actions[3].Label == "Ctrl + S" && actions[3].Time == 3.1, "ショートカットの表示と時刻");
        Check(actions[4].Keys.SequenceEqual(["A", "B"]) && actions[4].EndTime == 4.2, "1.5秒以内の文字入力を結合（Shiftは文字扱い）");
        Check(actions[5].Keys.SequenceEqual(["C"]), "間隔の空いた文字入力は別の操作");
        Check(actions[6].Time == 8.0 && actions[6].EndTime == 8.5, "連続するホイールを結合");
        Check(actions[7].Label == "Enter", "単独の特殊キー");
        Check(InputActions.Extract(log, 1, 3).Select(a => a.Kind).SequenceEqual([InputActionKind.RightClick, InputActionKind.Drag]), "区間で絞り込む");
        // CapchunWindowの記号・テンキーの名前と、入力中のBSは1件の文字入力にまとめる。
        var typed = InputActions.Extract(new EventLog([
            new(0, 0, 1, 1, "None", "Slash"), new(.1, 1, 1, 1, "None", "None"), new(.2, 2, 1, 1, "None", "Num1"), new(.3, 3, 1, 1, "None", "None"),
            new(.4, 4, 1, 1, "None", "BS"), new(.5, 5, 1, 1, "None", "None"), new(.6, 6, 1, 1, "None", "Shift,Semicolon"), new(.7, 7, 1, 1, "None", "None"),
            new(3, 30, 1, 1, "None", "BS"), new(3.1, 31, 1, 1, "None", "None"), new(4, 40, 1, 1, "None", "Ctrl,Slash"), new(4.1, 41, 1, 1, "None", "None")
        ]));
        Check(typed.Count == 3 && typed[0].Kind == InputActionKind.Text && typed[0].Keys.SequenceEqual(["Slash", "Num1", "BS", "Semicolon"]), "記号・テンキー・入力中のBSを文字入力へまとめる");
        Check(typed[1] is { Kind: InputActionKind.Shortcut, Label: "BS" } && typed[2] is { Kind: InputActionKind.Shortcut, Label: "Ctrl + Slash" }, "単独のBSと修飾キー付きの記号はショートカット");
        var held = new EventLog([new(0, 0, 5, 5, "L-Click", "None"), new(.5, 5, 5, 5, "L-Click", "None")]);
        Check(InputActions.Extract(held).Single().Kind == InputActionKind.Click, "ログ末尾まで押されたままのクリック");
    }

    private static void Comparer()
    {
        var random = new Random(7);
        byte[] a = new byte[100 * 100 * 4];
        random.NextBytes(a);
        for (int i = 3; i < a.Length; i += 4) a[i] = 255;
        byte[] noisy = (byte[])a.Clone();
        for (int i = 0; i < noisy.Length; i++) if (i % 4 != 3) noisy[i] = (byte)Math.Clamp(noisy[i] + random.Next(-4, 5), 0, 255);
        Check(FrameComparer.IsSame(a, a, StabilitySettings.Exact), "同一画像は完全一致");
        Check(!FrameComparer.IsSame(a, noisy, StabilitySettings.Exact) && FrameComparer.IsSame(a, noisy, StabilitySettings.Standard), "±4のノイズは標準で同じ、完全一致では違う");
        byte[] changed = (byte[])a.Clone();
        for (int p = 0; p < 100; p++) changed[p * 4] = (byte)(changed[p * 4] ^ 0x80);
        Check(Math.Abs(FrameComparer.ChangedRatio(a, changed, 8) - .01) < 1e-9, "1%の画素変化を測る");
        Check(!FrameComparer.IsSame(a, changed, StabilitySettings.Standard) && !FrameComparer.IsSame(a, changed, StabilitySettings.Loose), "1%の変化は標準・ゆるいでも別画像");
        byte[] alpha = (byte[])a.Clone(); alpha[3] = 0;
        Check(FrameComparer.ChangedRatio(a, alpha, 0) == 0, "αは比べない");
        try { FrameComparer.ChangedRatio(a, new byte[8], 0); Check(false, "寸法違いを拒否"); } catch (ArgumentException) { }
    }

    private static void Settings(string directory)
    {
        string path = Path.Combine(directory, "stability-settings.json");
        File.WriteAllText(path, "{\"Stability\":{\"ChannelThreshold\":999,\"MaxChangedRatio\":-1}}");
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Check(loaded.Stability.ChannelThreshold == 255 && loaded.Stability.MaxChangedRatio == 0, "許容差の範囲補正");
        File.WriteAllText(path, "{\"Stability\":null}");
        Check(store.Load().Stability.ChannelThreshold == 8, "許容差がnullなら既定値");
        File.WriteAllText(path, "{}");
        var defaults = store.Load();
        Check(defaults.Stability.ChannelThreshold == 8 && Math.Abs(defaults.Stability.MaxChangedRatio - .0005) < 1e-12, "旧設定ファイルは標準の許容差で読む");
        store.Save(defaults);
        Check(!File.ReadAllText(path).Contains("IsExact", StringComparison.Ordinal), "判定用の派生値は保存しない");
        Check(!Directory.GetFiles(directory, "*.tmp").Any(), "原子的書込みの一時ファイル回収");
    }

    // マスク（P3）の適用漏れを防ぐため、出力側からOverlayRendererを直接呼ぶ箇所がないことを検査する。
    private static void SingleComposerPath()
    {
        string? root = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tool.json"))) { root = directory.FullName; break; }
        if (root is null) { Console.WriteLine("SKIP 合成処理の一本化: リポジトリの外で実行"); return; }
        string[] allowed = ["FrameComposer.cs", "OverlayRenderer.cs"];
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowed.Contains(Path.GetFileName(f)) && File.ReadAllText(f).Contains("OverlayRenderer.", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray();
        Check(offenders.Length == 0, "OverlayRendererを直接呼ぶ出力: " + string.Join(", ", offenders));
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "動き量検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(160, 90))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 20; i++)
            {
                graphics.Clear(Color.Gray);
                if (i >= 10) graphics.FillRectangle(Brushes.White, 100, 10, 40, 40);
                ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1);
            }
        string source = Path.Combine(directory, "動き.mkv");
        await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source]);
        var media = new FfmpegService(ffmpeg);
        var info = await media.ProbeAsync(source);
        var profile = await MotionProfile.MeasureAsync(media, info, 0, info.Duration, CropRect.Full(info), StabilitySettings.Standard);
        Check(profile.Changes.Count == 20, "全フレームの動き量: " + profile.Changes.Count);
        Check(profile.Changes.Select((c, i) => (c, i)).Where(x => x.c > .001).Select(x => x.i).SequenceEqual([10]), "画面の変化を1フレームだけ検出");
        Check(profile.LatestSteady(.5, .95) is double before && Math.Abs(before - .9) < 1e-9, "変化直前の落ち着いたフレーム");
        Check(profile.EarliestSteady(1.0, 1.5) is double after && Math.Abs(after - 1.1) < 1e-9, "変化直後の落ち着いたフレーム");
        Check(profile.LatestSteady(1.0, 1.0) is null, "変化したフレームだけの範囲は該当なし");
        var outside = await MotionProfile.MeasureAsync(media, info, 0, info.Duration, new CropRect(0, 0, 80, 90), StabilitySettings.Standard);
        Check(outside.Changes.All(c => c == 0), "クロップ外の変化は数えない");
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 動き量: 縮小計測・変化の検出・前後の落ち着いたフレーム・クロップ範囲");
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

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("基盤検証失敗: " + label); }
}
