using System.Diagnostics;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.3.0 P2のステップ抽出・保存・手順書エディターを検証する。</summary>
internal static class StepTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        FrameSelection();
        Descriptions();
        Store(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { Editor(directory); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("手順書エディターの検証に失敗しました。", failure);
        Console.WriteLine("PASS 手順: 直前・直後の落ち着いたフレーム・説明文・保存と補正・エディターの削除/結合/並べ替え/追加");
    }

    private static MediaInfo Media(string path = "steps.mp4") => new(path, 640, 360, 10, 10);

    private static void FrameSelection()
    {
        var media = Media();
        var changes = new float[100];
        changes[17] = changes[18] = changes[19] = .5f; // 1.7〜1.9秒は画面が動いている
        changes[33] = changes[34] = .5f;               // 文字入力の後、3.3〜3.4秒に画面が動く
        var profile = new MotionProfile(media, 0, changes, StabilitySettings.Standard);
        InputAction[] actions =
        [
            new(InputActionKind.Click, 2.0, 2.1, new(100, 100), null, "左クリック", []),
            new(InputActionKind.Text, 2.5, 3.0, new(100, 100), null, "文字入力", ["A", "B"]),
            new(InputActionKind.Wheel, 5.0, 5.0, new(10, 10), null, "ホイール▼", [])
        ];
        var steps = StepDetector.FromActions(actions, profile, media, new StepSettings()).ToArray();
        Check(steps.Length == 2, "ホイールは既定で手順にしない");
        Check(Near(steps[0].FrameTime, 1.6), "動いているフレームを避けて操作直前の落ち着いたフレーム: " + steps[0].FrameTime);
        Check(Near(steps[1].FrameTime, 3.5), "文字入力は入力後に落ち着いたフレーム: " + steps[1].FrameTime);
        Check(steps[0].X == 100 && steps[0].Kind == "Click" && steps[0].AfterFrameTime is null, "操作位置と種類");
        var fallback = StepDetector.FromActions(actions, null, media, new StepSettings { IncludeWheel = true, SaveAfter = true }).ToArray();
        Check(fallback.Length == 3 && Near(fallback[0].FrameTime, 1.8), "動き量がなければ設定の秒数だけ前");
        Check(fallback[0].AfterFrameTime is double after && Near(after, 2.6), "操作後の画像も保持");
        Check(Near(fallback[1].FrameTime, 3.5), "文字入力の既定は入力終了の0.5秒後");
        var early = StepDetector.FromActions([new(InputActionKind.Click, .05, .1, new(1, 1), null, "左クリック", [])], null, media, new StepSettings()).Single();
        Check(early.FrameTime == 0, "動画の先頭より前へ出ない");
    }

    private static void Descriptions()
    {
        Check(StepDetector.Describe(new(InputActionKind.Shortcut, 0, 0, Point.Empty, null, "Ctrl + S", ["Ctrl", "S"])) == "Ctrl + S を押します。", "ショートカットの説明");
        Check(StepDetector.Describe(new(InputActionKind.Click, 0, 0, Point.Empty, null, "Ctrl + 左クリック", ["Ctrl"])) == "Ctrl を押しながら、この位置をクリックします。", "修飾キー付きクリックの説明");
        Check(StepDetector.Describe(new(InputActionKind.Drag, 0, 0, Point.Empty, new(5, 5), "ドラッグ", [])) == "ここから矢印の位置までドラッグします。", "ドラッグの説明");
    }

    private static void Store(string directory)
    {
        var media = Media(Path.Combine(directory, "手順の動画.mp4"));
        string path = StepStore.PathFor(media.Path);
        Check(path.EndsWith("手順の動画.chulip.steps.json", StringComparison.Ordinal), "手順ファイル名");
        if (File.Exists(path)) File.Delete(path);
        Check(StepStore.Load(media, out var none) is null && none is null, "手順ファイルなし");
        var document = new StepDocument { Start = 1, End = 5, Crop = new(10, 10, 100, 100), Steps = [new StepItem { Kind = "Click", Time = 2, FrameTime = 1.8, X = 50, Y = 60, Description = "説明" }] };
        StepStore.Save(media, document);
        var loaded = StepStore.Load(media, out var warning);
        Check(warning is null && loaded!.Steps.Single().Description == "説明" && loaded.Crop == new CropRect(10, 10, 100, 100) && loaded.VideoWidth == 640, "保存と復元");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"VideoWidth\":640,\"VideoHeight\":360,\"Duration\":10,\"Steps\":[null,{\"Time\":99,\"FrameTime\":-5,\"X\":9999,\"Y\":-3,\"Description\":null,\"Id\":\"\"}]}");
        var normalized = StepStore.Load(media, out warning)!.Steps.Single();
        Check(warning is null && normalized.Time == 10 && normalized.FrameTime == 0 && normalized.X == 639 && normalized.Y == 0 && normalized.Description == "" && normalized.Id.Length > 0, "範囲外・nullの補正");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"VideoWidth\":1920,\"VideoHeight\":1080,\"Duration\":10,\"Steps\":[]}");
        Check(StepStore.Load(media, out warning) is not null && warning?.Contains("異なります", StringComparison.Ordinal) == true, "動画の差し替えを警告");
        File.WriteAllText(path, "{\"SchemaVersion\":99}");
        Check(StepStore.Load(media, out warning) is null && warning?.Contains("形式 99", StringComparison.Ordinal) == true, "未対応の形式を読まない");
        File.WriteAllText(path, "{");
        Check(StepStore.Load(media, out warning) is null && warning is not null, "壊れた手順ファイル");
        Check(!Directory.GetFiles(directory, "*.tmp").Any(), "手順保存の一時ファイル回収");
    }

    private static void Editor(string directory)
    {
        // FFmpegは呼ばない。画像の取得は起動できない実行ファイルで失敗させ、編集操作だけを確かめる。
        string fake = Path.Combine(directory, "起動できない.exe");
        File.WriteAllText(fake, "");
        var media = Media(Path.Combine(directory, "エディター.mp4"));
        using var form = new StepEditorForm(new FfmpegService(fake), media, EventLog.Empty, new AppSettings(), 0, 10, CropRect.Full(media), 4);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        form.Size = form.MinimumSize;
        LayoutAll(form);
        foreach (var control in Descendants(form).Where(c => c is ButtonBase or TextBox or ListView))
        {
            var rect = control.Bounds;
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            {
                Check(control.Width > 0 && control.Height > 0 && parent.ClientRectangle.Contains(rect), $"最小サイズでも操作部品が欠けない: {control.Text}{control.AccessibleName}");
                rect.Offset(parent.Location);
            }
        }
        var document = form.Document;
        for (int i = 0; i < 4; i++) document.Steps.Add(new StepItem { Kind = "Click", Time = i, FrameTime = i, Label = "左クリック", Description = "手順" + i });
        Invoke(form, "RefreshList", 0);
        var list = Field<ListView>(form, "list");
        Check(list.Items.Count == 4 && list.Items[0].SubItems[0].Text == "1" && list.Items[3].SubItems[3].Text == "手順3", "一覧の番号と説明");
        Select(list, 1);
        Invoke(form, "MoveSelected", 1);
        Check(document.Steps.Select(s => s.Description).SequenceEqual(["手順0", "手順2", "手順1", "手順3"]) && list.SelectedIndices[0] == 2, "下へ移動");
        Select(list, 0, 1);
        Invoke(form, "MergeSelected");
        Check(document.Steps.Count == 3 && document.Steps[0].Description == "手順0\n手順2" && document.Steps[0].DescriptionSource == "Manual", "選択した手順を結合");
        Select(list, 2);
        Invoke(form, "DeleteSelected");
        Check(document.Steps.Select(s => s.Description).SequenceEqual(["手順0\n手順2", "手順1"]), "選択した手順を削除");
        Invoke(form, "AddAtCurrent");
        Check(document.Steps.Count == 3 && document.Steps[2].Kind == "Manual" && document.Steps[2].Time == 4 && document.Steps[2].X == 320, "現在位置に手動の手順を追加");
        Select(list, 0);
        Field<CheckBox>(form, "enabled").Checked = false;
        Check(!document.Steps[0].Enabled && list.Items[0].SubItems[0].Text == "除外" && list.Items[1].SubItems[0].Text == "1", "除外した手順は番号を詰める");
        Select(list, 1);
        Field<TextBox>(form, "description").Text = "書き換えた説明";
        Check(document.Steps[1].Description == "書き換えた説明" && document.Steps[1].DescriptionSource == "Manual", "説明文の編集");
        Invoke(form, "ShiftFrame", .1);
        Check(Near(document.Steps[1].FrameTime, 1.1), "画像の時刻を0.1秒後へ");
        Check(form.Dirty && form.Text.Contains('*'), "未保存の表示");
        Check((bool)Invoke(form, "Save")! && !form.Dirty && File.Exists(StepStore.PathFor(media.Path)), "エディターから保存");
        form.Dispose();
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "手順抽出検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        // 0.9〜1.1秒に枠が動き、1.2秒にクリックする。直前の落ち着いた0.8秒の画面を選ぶこと。
        using (var bitmap = new Bitmap(160, 90))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 30; i++)
            {
                graphics.Clear(Color.Gray);
                int x = i < 9 ? 10 : i <= 11 ? 10 + (i - 8) * 20 : 70;
                graphics.FillRectangle(Brushes.White, x, 20, 30, 30);
                ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1);
            }
        string source = Path.Combine(directory, "操作.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source })
            start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画の作成"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var events = new EventLog([new(1.2, 12, 85, 35, "L-Click", "None"), new(1.3, 13, 85, 35, "None", "None"), new(2.0, 20, 85, 35, "None", "Ctrl,S"), new(2.1, 21, 85, 35, "None", "None")]);
        var settings = new AppSettings();
        var document = await StepDetector.DetectAsync(service, info, events, 0, info.Duration, CropRect.Full(info), settings);
        Check(document.Steps.Count == 2, "クリックとショートカットの2手順: " + document.Steps.Count);
        Check(Near(document.Steps[0].FrameTime, .8), "動いている0.9〜1.1秒を避けた0.8秒: " + document.Steps[0].FrameTime);
        Check(Near(document.Steps[1].FrameTime, 1.9) && document.Steps[1].Description == "Ctrl + S を押します。", "ショートカットの画像（範囲内で最も遅い落ち着いたフレーム）と説明");
        using (Bitmap image = await StepDetector.RenderImageAsync(service, info, events, CropRect.Full(info), settings, document.Steps[0].FrameTime))
            Check(image.GetPixel(15, 25).ToArgb() == Color.White.ToArgb() && image.GetPixel(75, 25).ToArgb() != Color.White.ToArgb(), "選んだ時刻の画面を画像にする");
        Directory.Delete(directory, true);
        Console.WriteLine("PASS 手順抽出: 実動画で操作直前の落ち着いた画面を選択・説明文・画像化");
    }

    private static void LayoutAll(Control control) { control.PerformLayout(); foreach (Control child in control.Controls) LayoutAll(child); }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void Select(ListView list, params int[] indices)
    {
        list.SelectedIndices.Clear();
        foreach (int index in indices) list.SelectedIndices.Add(index);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("手順検証失敗: " + label); }
}
