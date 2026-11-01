using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.UI;

namespace ChulipVideo.Tests;

/// <summary>FFmpeg自動検索の範囲と、不正な色設定で描画が止まらないことを検証する。FFmpeg実体は使わない。</summary>
internal static class SafetyTests
{
    public static void Run(string directory)
    {
        // 8.3形式の短いパスはGetFullPathで長い形式へ展開されるため、比較の前にそろえておく。
        string root = Path.GetFullPath(Path.Combine(directory, "safety-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            FfmpegSearch(root);
            ColorParsing();
            SettingsColors(root);
            Exception? failure = null;
            var thread = new Thread(() => { try { CanvasWithInvalidColors(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); thread.Join();
            if (failure is not null) throw new Exception("不正色でのキャンバス描画に失敗しました。", failure);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS 安全性: FFmpeg開発探索の範囲・色の検証と補正・不正色での描画継続");
    }

    private static void FfmpegSearch(string root)
    {
        // リポジトリ直下（tool.json）の.localは開発用として見つける。
        string repo = Path.Combine(root, "repo");
        string bin = Path.Combine(repo, "artifacts", "bin", "debug");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(repo, ".local"));
        File.WriteAllText(Path.Combine(repo, "tool.json"), "{}");
        string expected = Path.Combine(repo, ".local", "ffmpeg.exe");
        File.WriteAllText(expected, "");
        Check(FfmpegService.FindDevelopmentExecutable(bin) == expected, "リポジトリ直下の.local/ffmpeg.exeを検出");

        // リポジトリに.localがなければ、その外側（C:\直下に相当）の.localへ遡らない。
        File.Delete(expected);
        Directory.CreateDirectory(Path.Combine(root, ".local"));
        File.WriteAllText(Path.Combine(root, ".local", "ffmpeg.exe"), "");
        Check(FfmpegService.FindDevelopmentExecutable(bin) is null, "リポジトリ外側の.localを探さない");

        // 試験素材自体が実リポジトリ内に置かれる場合、その外側には正規のtool.jsonがある。
        // 素材内の目印のない.localを無視し、素材を置く前からの探索結果だけを許可する。
        string? enclosingExecutable = FfmpegService.FindDevelopmentExecutable(Directory.GetParent(root)!.FullName);
        string loose = Path.Combine(root, "loose", "bin");
        Directory.CreateDirectory(loose);
        string? looseExecutable = FfmpegService.FindDevelopmentExecutable(loose);
        Check(looseExecutable != Path.Combine(root, ".local", "ffmpeg.exe"), "目印のない親の.localを探さない");
        Check(looseExecutable == enclosingExecutable, "試験素材外の正規リポジトリの探索結果を維持");

        // より近い目印ができたら、その場所にFFmpegがなくても上位の正規リポジトリへ遡らない。
        string nearestRepo = Path.Combine(root, "loose");
        File.WriteAllText(Path.Combine(nearestRepo, "tool.json"), "{}");
        Check(FfmpegService.FindDevelopmentExecutable(loose) is null, "最寄りの目印にFFmpegがなければ探索を終了");
        Directory.CreateDirectory(Path.Combine(nearestRepo, ".local"));
        string nearestExecutable = Path.Combine(nearestRepo, ".local", "ffmpeg.exe");
        File.WriteAllText(nearestExecutable, "");
        Check(FfmpegService.FindDevelopmentExecutable(loose) == nearestExecutable, "複数の目印があっても最寄りのリポジトリだけを使用");

        // カレントディレクトリの.localは自動検索の対象外。
        string previous = Environment.CurrentDirectory;
        string? previousEnvironment = Environment.GetEnvironmentVariable("CHULIPVIDEO_FFMPEG");
        string previousPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("CHULIPVIDEO_FFMPEG", null);
            Environment.SetEnvironmentVariable("PATH", "");
            Environment.CurrentDirectory = root;
            string? found = FfmpegService.FindExecutable();
            Check(found is null || !found.StartsWith(root, StringComparison.OrdinalIgnoreCase), "カレントディレクトリの.localを使わない");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Environment.SetEnvironmentVariable("CHULIPVIDEO_FFMPEG", previousEnvironment);
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    private static void ColorParsing()
    {
        Check(ColorText.IsValid("#12aBEF") && ColorText.IsValid(" #000000 "), "#RRGGBBを受け付ける");
        foreach (string bad in new[] { "#GGGGGG", "#12345", "#FFF", "red", "123456", "#1234567" })
        {
            Check(!ColorText.IsValid(bad), "不正色を拒否: " + bad);
            Check(ColorText.Parse(bad, Color.Lime) == Color.Lime, "不正色は例外にせず代替色: " + bad);
        }
        Check(ColorText.IsValid("", true) && !ColorText.IsValid("", false), "空欄は任意項目だけ許可");
        Check(ColorText.Parse("", Color.Lime, true) == Color.Empty && ColorText.Parse("", Color.Lime) == Color.Lime, "空欄の扱い");
        Check(ColorText.Parse("#102030", Color.Lime).ToArgb() == Color.FromArgb(255, 0x10, 0x20, 0x30).ToArgb(), "色の解析");

        var options = new OverlayOptions { PointerColor = "#GGGGGG", PointerFill = "#XYZXYZ", LeftColor = "#12345", TextColor = "bad", BackgroundColor = "#-12345", OutlineColor = "#QQQQQQ" };
        var events = new EventLog([new(0, 0, 10, 10, "L-Click", "Ctrl,C")]);
        var info = new MediaInfo("color.mp4", 64, 48, 1, 10);
        using var frame = new Bitmap(64, 48);
        using var preview = OverlayRenderer.RenderPreview(frame, info, 0, events, options);
        using var output = OverlayRenderer.Render(frame, CropRect.Full(info), 0, events, options);
        Check(preview.Width == 64 && output.Height == 48, "不正色でも入力表示を描画");
    }

    private static void SettingsColors(string root)
    {
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"CanvasColor\":\"#GGGGGG\",\"CropColor\":\"orange\",\"Overlay\":{\"PointerColor\":\"#12345\",\"PointerFill\":\"#ZZZZZZ\",\"TextColor\":\"#abcdef\",\"BackgroundColor\":\"\",\"OutlineColor\":\"#XYZ\"}}");
        var store = new SettingsStore(path);
        var settings = store.Load();
        Check(store.Warning is null, "不正色は設定全体を破棄せずに補正");
        Check(settings.CanvasColor == "#787878" && settings.CropColor == "#FF8C00", "画面色を既定へ補正");
        var o = settings.Overlay;
        Check(o.PointerColor == "#202020" && o.PointerFill == "" && o.OutlineColor == "", "入力表示色を既定へ補正");
        Check(o.TextColor == "#abcdef" && o.BackgroundColor == "", "正しい色と空欄指定を保持");
        File.WriteAllText(path, "{\"CanvasColor\":null,\"Overlay\":{\"LeftColor\":null,\"PointerFill\":null,\"OutlineColor\":null}}");
        o = store.Load().Overlay;
        Check(store.Warning is null && o.LeftColor == "#FF4444" && o.PointerFill == "" && o.OutlineColor == "", "null色を既定値・空欄へ補正");
    }

    private static void CanvasWithInvalidColors()
    {
        using var canvas = new VideoCanvas { Size = new Size(320, 200), VideoSize = new Size(640, 360), Crop = new CropRect(100, 80, 300, 180), CanvasColor = "#GGGGGG", CropColor = "#ZZZZZZ" };
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        typeof(VideoCanvas).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(canvas, [new PaintEventArgs(graphics, canvas.ClientRectangle)]);
        Check(bitmap.GetPixel(2, 2).ToArgb() == Color.FromArgb(120, 120, 120).ToArgb(), "不正なキャンバス色は既定背景で描画");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("安全性検証失敗: " + label); }
}
