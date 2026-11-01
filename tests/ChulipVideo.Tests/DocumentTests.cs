using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using ChulipVideo.UI;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P4の注釈（R5）と手順書の書き出し（R2）を検証する。</summary>
internal static class DocumentTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Rendering();
        Placement();
        Storage(directory);
        Texts();
        Docx(directory);
        Exception? failure = null;
        var thread = new Thread(() => { try { CanvasEditing(); EditorCommands(directory); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("注釈編集の検証に失敗しました。", failure);
        Console.WriteLine("PASS 手順書: 注釈の描画・自動配置・注目範囲・保存/補正・Markdown/HTML・docx構造・注釈の編集");
    }

    private static Bitmap Solid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }

    private static bool Reddish(Color c) => c.R > 180 && c.G < 90 && c.B < 90;

    private static void Rendering()
    {
        using var frame = Solid(200, 120, Color.White);
        var crop = new CropRect(20, 10, 160, 100);
        Annotation[] annotations =
        [
            new() { Kind = AnnotationKind.Box, X = 60, Y = 40, Width = 50, Height = 30, LineWidth = 3 },
            new() { Kind = AnnotationKind.Badge, X = 150, Y = 90, FontSize = 14 }
        ];
        using var output = FrameComposer.Compose(frame, new(crop, 0, EventLog.Empty, new()) { DrawOverlay = false, Annotations = annotations, StepNumber = 3 });
        Check(Reddish(output.GetPixel(40, 45)) && Reddish(output.GetPixel(65, 30)), "強調枠はクロップ後の位置に描く");
        Check(output.GetPixel(65, 45).ToArgb() == Color.White.ToArgb(), "強調枠の内側は塗らない");
        Check(Reddish(output.GetPixel(130 - 10, 80)) || Reddish(output.GetPixel(130, 80 - 10)), "番号バッジを塗る");
        using var plain = FrameComposer.Compose(frame, new(crop, 0, EventLog.Empty, new()) { DrawOverlay = false });
        Check(plain.GetPixel(40, 45).ToArgb() == Color.White.ToArgb(), "注釈を指定しなければ描かない");
        using var spot = FrameComposer.Compose(frame, new(crop, 0, EventLog.Empty, new())
            { DrawOverlay = false, Annotations = [new() { Kind = AnnotationKind.Spotlight, X = 60, Y = 40, Width = 50, Height = 30 }] });
        Check(spot.GetPixel(5, 5).R < 200 && spot.GetPixel(60, 45).ToArgb() == Color.White.ToArgb(), "強調は範囲の外だけを暗くする");
    }

    private static void Placement()
    {
        var media = new MediaInfo("p.mp4", 640, 360, 10, 10);
        var center = StepDetector.DefaultAnnotations(new StepItem { Kind = "Click", X = 300, Y = 200 }, media);
        Check(center.Count == 2 && center[0] is { Kind: AnnotationKind.Box, X: 240, Y: 180, Width: 120, Height: 40 } && center[1] is { Kind: AnnotationKind.Badge, X: 382, Y: 200 }, "中央のクリックは強調枠と、枠の右の番号");
        var corner = StepDetector.DefaultAnnotations(new StepItem { Kind = "RightClick", X = 630, Y = 355 }, media);
        Check(corner[0] is { X: 520, Y: 320 } && corner[1] is { X: 498, Y: 344 }, "画面端では枠を内側へ、番号を枠の左へ寄せ、切れないよう内側に収める");
        var drag = StepDetector.DefaultAnnotations(new StepItem { Kind = "Drag", X = 10, Y = 10, EndX = 100, EndY = 50 }, media);
        Check(drag[0] is { Kind: AnnotationKind.Arrow, X: 10, Y: 10, EndX: 100, EndY: 50 } && drag[1].Kind == AnnotationKind.Badge, "ドラッグは矢印");
        Check(StepDetector.DefaultAnnotations(new StepItem { Kind = "Shortcut", X = 1, Y = 1 }, media).Count == 0, "キー操作は注釈なし");
        var crop = new CropRect(100, 50, 400, 200);
        Check(StepDetector.FocusArea(new StepItem { X = 300, Y = 150 }, crop) == new CropRect(200, 100, 200, 100), "注目範囲は操作位置を中心に縦横1/2");
        Check(StepDetector.FocusArea(new StepItem { X = 0, Y = 0 }, crop) == new CropRect(100, 50, 200, 100), "注目範囲は切り出し範囲の中に収める");
        var detected = StepDetector.FromActions([new(InputActionKind.Click, 1, 1.1, new(300, 200), null, "左クリック", [])], null, media, new StepSettings()).Single();
        Check(detected.Annotations.Count == 2, "抽出した手順には注釈を自動配置");
    }

    private static void Storage(string directory)
    {
        var normalized = Annotation.Normalize([null, new Annotation { Kind = (AnnotationKind)99 }, new Annotation { X = 900, Y = -5, Width = 999, Color = "bad", LineWidth = 0, FontSize = 500, Text = null! }], 640, 360);
        Check(normalized.Count == 1 && normalized[0] is { X: 639, Y: 0, Width: 1, Color: "#E53935", LineWidth: 1, FontSize: 96, Text: "" }, "注釈の範囲・色・種類の補正");
        var media = new MediaInfo(Path.Combine(directory, "注釈.mp4"), 640, 360, 10, 10);
        var document = new StepDocument { Crop = CropRect.Full(media), End = 10, Steps = [new StepItem { Kind = "Click", Annotations = [new() { Kind = AnnotationKind.Arrow, X = 1, Y = 2, EndX = 30, EndY = 40, Color = "#00FF00" }] }] };
        StepStore.Save(media, document);
        Check(File.ReadAllText(StepStore.PathFor(media.Path)).Contains("\"Arrow\"", StringComparison.Ordinal), "注釈の種類は名前で保存");
        var loaded = StepStore.Load(media, out _)!.Steps.Single().Annotations.Single();
        Check(loaded is { Kind: AnnotationKind.Arrow, EndX: 30, EndY: 40, Color: "#00FF00" }, "注釈の保存と復元");
    }

    private static readonly StepPage[] Pages = [new(1, "「保存」を<クリック>します。", "images/step_001.png", 160, 90), new(2, "1行目\n2行目 & 続き", "images/step_002.png", 160, 90)];
    private static readonly StepDocumentWriter.DocumentInfo Info = new("試験の手順 <題名>", "操作 & 録画.mp4", new DateTime(2026, 10, 31));

    private static void Texts()
    {
        string md = StepDocumentWriter.Markdown(Info, Pages);
        Check(md.StartsWith("# 試験の手順 <題名>\n", StringComparison.Ordinal) && md.Contains("- 作成日: 2026-10-31", StringComparison.Ordinal) && md.Contains("- 手順数: 2", StringComparison.Ordinal), "Markdownの見出しと情報");
        Check(md.Contains("## 手順 2\n\n1行目  \n2行目 & 続き\n\n![手順 2](images/step_002.png)", StringComparison.Ordinal), "Markdownの改行と相対パスの画像");
        string html = StepDocumentWriter.Html(Info, Pages);
        Check(html.Contains("<title>試験の手順 &lt;題名&gt;</title>", StringComparison.Ordinal) && html.Contains("&lt;クリック&gt;", StringComparison.Ordinal) && !html.Contains("<クリック>", StringComparison.Ordinal), "HTMLは本文をエスケープ");
        Check(html.Contains("<img src=\"images/step_001.png\" alt=\"手順 1\" width=\"160\" height=\"90\">", StringComparison.Ordinal) && html.Contains("break-inside:avoid", StringComparison.Ordinal), "HTMLの画像と改ページ指定");
        Check(StepDocumentWriter.SafeName("a:b/c*d?.") == "a_b_c_d_", "フォルダー名に使えない文字を置き換える");
    }

    private static void Docx(string directory)
    {
        string root = Path.Combine(directory, "docx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        foreach (var page in Pages) { using var image = Solid(page.Width, page.Height, Color.Teal); ExportService.SavePng(image, Path.Combine(root, page.ImageFile.Replace('/', '\\')), 1); }
        string path = Path.Combine(root, "手順書.docx");
        DocxWriter.Write(path, Info, Pages, root);
        VerifyDocx(path, Pages.Length, "「保存」を<クリック>します。");
        Directory.Delete(root, true);
    }

    public static void VerifyDocx(string path, int images, string? expectedText = null)
    {
        using var zip = ZipFile.OpenRead(path);
        string[] required = ["[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/_rels/document.xml.rels"];
        foreach (string name in required) Check(zip.GetEntry(name) is not null, "docxの必須部品: " + name);
        XDocument Load(string name) { using var s = zip.GetEntry(name)!.Open(); return XDocument.Load(s); }
        var types = Load("[Content_Types].xml");
        Check(types.Root!.Elements().Any(e => (string?)e.Attribute("Extension") == "png"), "PNGのコンテンツ種別");
        var document = Load("word/document.xml");
        var relations = Load("word/_rels/document.xml.rels").Root!.Elements(Pkg + "Relationship").ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var blips = document.Descendants(A + "blip").Select(b => (string)b.Attribute(R + "embed")!).ToArray();
        Check(blips.Length == images && blips.All(id => relations.TryGetValue(id, out var target) && zip.GetEntry("word/" + target) is not null), "docxの画像と参照の対応");
        Check(document.Descendants(W + "sectPr").Any(), "docxのページ設定");
        if (expectedText is not null) Check(string.Concat(document.Descendants(W + "t").Select(t => t.Value)).Contains(expectedText, StringComparison.Ordinal), "docxの本文（XMLとして正しくエスケープ）");
    }

    private static void Mouse(AnnotationCanvas canvas, string method, int x, int y) =>
        typeof(AnnotationCanvas).GetMethod(method, Hidden)!.Invoke(canvas, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);

    private static void CanvasEditing()
    {
        // 208×108に200×100の画像を表示すると、倍率1・画像の左上が(4,4)。
        using var image = Solid(200, 100, Color.White);
        var annotations = new List<Annotation>();
        using var canvas = new AnnotationCanvas { Size = new Size(208, 108) };
        int changed = 0, completed = 0;
        canvas.AnnotationsChanged += (_, _) => changed++;
        canvas.ToolCompleted += (_, _) => completed++;
        canvas.SetStep(image, annotations, 1, new CropRect(100, 50, 200, 100));
        canvas.Tool = AnnotationTool.Box;
        Mouse(canvas, "OnMouseDown", 14, 14); Mouse(canvas, "OnMouseMove", 64, 44); Mouse(canvas, "OnMouseUp", 64, 44);
        Check(annotations.Single() is { Kind: AnnotationKind.Box, X: 110, Y: 60, Width: 50, Height: 30 } && changed == 1, "ドラッグで強調枠を元動画の座標で作る");
        Check(canvas.Tool == AnnotationTool.Select && completed == 1 && canvas.Selected == annotations[0], "作成後は選択に戻り、作った注釈を選ぶ");
        canvas.Tool = AnnotationTool.Arrow;
        Mouse(canvas, "OnMouseDown", 100, 80); Mouse(canvas, "OnMouseUp", 101, 81);
        Check(annotations.Count == 1 && canvas.Tool == AnnotationTool.Select, "短すぎる矢印は作らない");
        Mouse(canvas, "OnMouseDown", 30, 30); Mouse(canvas, "OnMouseMove", 40, 50); Mouse(canvas, "OnMouseUp", 40, 50);
        Check(annotations[0] is { X: 120, Y: 80 } && changed == 2, "選んだ注釈をドラッグで移動");
        canvas.Tool = AnnotationTool.Text;
        canvas.RequestText = () => "ここを確認";
        Mouse(canvas, "OnMouseDown", 150, 20);
        Check(annotations[^1] is { Kind: AnnotationKind.Text, Text: "ここを確認", X: 246, Y: 66 }, "文字の注釈を置く");
        canvas.Tool = AnnotationTool.Badge;
        Mouse(canvas, "OnMouseDown", 180, 90);
        Check(annotations[^1] is { Kind: AnnotationKind.Badge, X: 276, Y: 136 } && annotations.Count == 3, "番号を置く");
        canvas.DeleteSelected();
        Check(annotations.Count == 2 && canvas.Selected is null, "選んだ注釈を削除");
        Check(canvas.HitTest(new Point(5, 100)) is null, "何もない位置は選ばない");
    }

    private static void EditorCommands(string directory)
    {
        string fake = Path.Combine(directory, "起動できない.exe");
        File.WriteAllText(fake, "");
        var media = new MediaInfo(Path.Combine(directory, "注釈エディター.mp4"), 640, 360, 10, 10);
        using var form = new StepEditorForm(new FfmpegService(fake), media, EventLog.Empty, new AppSettings(), 0, 10, CropRect.Full(media), 0);
        typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
        form.Document.Steps.AddRange([new StepItem { Kind = "Click", X = 300, Y = 200, Enabled = false }, new StepItem { Kind = "Click", X = 100, Y = 100 }]);
        typeof(StepEditorForm).GetMethod("RefreshList", Hidden)!.Invoke(form, [1]);
        Check((int)typeof(StepEditorForm).GetMethod("NumberOf", Hidden)!.Invoke(form, [form.Document.Steps[1]])! == 1, "除外した手順を数えない番号");
        typeof(StepEditorForm).GetMethod("AutoAnnotate", Hidden)!.Invoke(form, []);
        Check(form.Document.Steps[1].Annotations.Count == 2 && form.Document.Steps[0].Annotations.Count == 0 && form.Dirty, "選んだ手順だけ注釈を自動配置");
        using var export = new StepExportForm("題名", directory, false, pdfAvailable: false);
        Check(export.Formats == (StepDocumentFormat.Markdown | StepDocumentFormat.Html | StepDocumentFormat.Word), "既定の形式（Edgeがなければ PDF を選べない）");
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "手順書検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(320, 180))
        using (var graphics = Graphics.FromImage(bitmap))
            for (int i = 0; i < 20; i++) { graphics.Clear(Color.White); graphics.FillRectangle(Brushes.Black, 200, 20, 60, 30); ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1); }
        string source = Path.Combine(directory, "手順の録画.mkv");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) { await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "素材動画"); }
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var document = new StepDocument
        {
            Crop = CropRect.Full(info), End = info.Duration,
            Steps =
            [
                new StepItem { Kind = "Click", FrameTime = .5, X = 80, Y = 90, Description = "左の位置をクリックします。" },
                new StepItem { Kind = "Click", FrameTime = 1.0, X = 10, Y = 10, Description = "除外する手順", Enabled = false },
                new StepItem { Kind = "Shortcut", FrameTime = 1.2, X = 0, Y = 0, Description = "Ctrl + S を押します。" }
            ]
        };
        document.Steps[0].Annotations = StepDetector.DefaultAnnotations(document.Steps[0], info);
        var mask = new MaskRegion { Rect = new(200, 20, 60, 30), Start = 0, End = info.Duration, Mode = MaskMode.Fill, Color = "#00FF00" };
        var settings = new AppSettings();
        bool edge = EdgePdf.FindEdge() is not null;
        var formats = StepDocumentFormat.Markdown | StepDocumentFormat.Html | StepDocumentFormat.Word | StepDocumentFormat.Pdf;
        var result = await StepDocumentWriter.ExportAsync(service, info, EventLog.Empty, document, settings, [mask], new("書き出し試験", formats, directory));
        Check(Path.GetFileName(result.Directory).StartsWith("手順の録画_手順書_", StringComparison.Ordinal) && Directory.Exists(result.Directory), "動画名_手順書_日時のフォルダー");
        var images = Directory.GetFiles(Path.Combine(result.Directory, "images"), "*.png").Order(StringComparer.Ordinal).ToArray();
        Check(images.Length == 2, "除外した手順を除いた2枚の画像");
        using (var first = new Bitmap(images[0]))
        {
            Check(first.GetPixel(230, 35).ToArgb() == Color.FromArgb(0, 255, 0).ToArgb(), "手順書の画像にマスク");
            Check(Reddish(first.GetPixel(20, 75)) || Reddish(first.GetPixel(25, 70)), "手順書の画像に強調枠");
        }
        string md = File.ReadAllText(Path.Combine(result.Directory, "手順書.md"));
        Check(md.Contains("## 手順 2\n\nCtrl + S を押します。", StringComparison.Ordinal) && !md.Contains("除外する手順", StringComparison.Ordinal), "除外した手順を飛ばして番号を振る");
        Check(File.Exists(Path.Combine(result.Directory, "手順書.html")), "HTML");
        VerifyDocx(Path.Combine(result.Directory, "手順書.docx"), 2, "左の位置をクリックします。");
        string pdf = Path.Combine(result.Directory, "手順書.pdf");
        if (edge && result.PdfWarning is null)
        {
            using var stream = File.OpenRead(pdf);
            var head = new byte[5]; stream.ReadExactly(head);
            Check(System.Text.Encoding.ASCII.GetString(head) == "%PDF-", "Edgeで作ったPDF");
        }
        else Check(!File.Exists(pdf) && result.PdfWarning is not null, "PDFを作れないときは理由を返す: " + result.PdfWarning);
        Check(Directory.GetDirectories(directory, ".chulip-export-*").Length == 0 && !Directory.Exists(Path.Combine(result.Directory, ".edge-profile")), "作業用フォルダーとEdgeの一時プロファイルを残さない");
        var focused = await StepDocumentWriter.ExportAsync(service, info, EventLog.Empty, document, settings with { Steps = new StepSettings { FocusImage = true } }, [],
            new("", StepDocumentFormat.Markdown, Path.Combine(directory, "素材")));
        using (var image = new Bitmap(Directory.GetFiles(Path.Combine(focused.Directory, "images"))[0]))
            Check(image.Width == 160 && image.Height == 90, "注目範囲の画像は縦横1/2");
        Check(File.ReadAllText(Path.Combine(focused.Directory, "手順書.md")).StartsWith("# 手順の録画 の手順", StringComparison.Ordinal), "題名が空なら動画名から作る");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            string cancelRoot = Path.Combine(directory, "中断");
            Directory.CreateDirectory(cancelRoot);
            try { await StepDocumentWriter.ExportAsync(service, info, EventLog.Empty, document, settings, [], new("", StepDocumentFormat.Markdown, cancelRoot), null, cancel.Token); Check(false, "中断を通知"); }
            catch (OperationCanceledException) { }
            Check(Directory.GetDirectories(cancelRoot).Length == 0, "中断したら何も残さない");
        }
        // 目視確認用。指定した場合だけ、書き出した手順書を残す。
        if (Environment.GetEnvironmentVariable("CHULIPVIDEO_DOCUMENT_EVIDENCE") is { Length: > 0 } evidence)
        {
            string kept = Path.Combine(evidence, Path.GetFileName(result.Directory));
            Directory.CreateDirectory(kept);
            foreach (string file in Directory.GetFiles(result.Directory, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(kept, Path.GetRelativePath(result.Directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, true);
            }
            Console.WriteLine("手順書の目視確認用の出力: " + kept);
        }
        Directory.Delete(directory, true);
        Console.WriteLine($"PASS 手順書出力: 画像のマスク・注釈・番号・Markdown・HTML・docx・{(edge ? "PDF" : "PDFなしの通知")}・注目範囲・中断の後始末");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("手順書検証失敗: " + label); }
}
