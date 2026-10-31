using System.Globalization;
using System.Net;
using System.Text;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Steps;

[Flags]
public enum StepDocumentFormat { None = 0, Markdown = 1, Html = 2, Word = 4, Pdf = 8 }

public sealed record StepExportOptions(string Title, StepDocumentFormat Formats, string ParentDirectory);

/// <summary>出力先フォルダー、作成したファイル、PDFを作れなかった場合の理由。</summary>
public sealed record StepExportResult(string Directory, IReadOnlyList<string> Files, string? PdfWarning);

/// <summary>1ページに載せる手順（書き出し時点の番号・説明・画像）。</summary>
public sealed record StepPage(int Number, string Description, string ImageFile, int Width, int Height);

/// <summary>手順を番号・説明・画像付きの手順書（Markdown・HTML・Word・PDF）として書き出す。</summary>
public static class StepDocumentWriter
{
    public const string ImageFolder = "images", BaseName = "手順書";

    public static async Task<StepExportResult> ExportAsync(FfmpegService service, MediaInfo media, EventLog events, StepDocument document, AppSettings settings,
        IReadOnlyList<MaskRegion> masks, StepExportOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (options.Formats == StepDocumentFormat.None) throw new ArgumentException("書き出す形式を選んでください。");
        var steps = document.Steps.Where(s => s.Enabled).ToArray();
        if (steps.Length == 0) throw new ArgumentException("手順書に含める手順がありません。");
        string parent = Path.GetFullPath(options.ParentDirectory);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("保存先フォルダーがありません: " + parent);
        string target = Path.Combine(parent, SafeName($"{Path.GetFileNameWithoutExtension(media.Path)}_{BaseName}_{DateTime.Now:yyyyMMdd_HHmmss}"));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("保存先は既に存在します: " + target);
        // 一時フォルダーで作り、完成してから移す（途中で失敗・中断しても不完全な手順書を残さない）。
        string stage = Path.Combine(parent, ".chulip-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stage, ImageFolder));
        try
        {
            var pages = new List<StepPage>();
            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"手順の画像を作成しています… {i + 1} / {steps.Length}");
                using Bitmap image = await StepDetector.RenderStepAsync(service, media, events, steps[i], i + 1, document.Crop, settings, masks, ct).ConfigureAwait(false);
                string file = $"{ImageFolder}/step_{i + 1:D3}.png";
                ExportService.SavePng(image, Path.Combine(stage, file.Replace('/', Path.DirectorySeparatorChar)), settings.PngCompression);
                pages.Add(new(i + 1, steps[i].Description, file, image.Width, image.Height));
            }
            string title = string.IsNullOrWhiteSpace(options.Title) ? Path.GetFileNameWithoutExtension(media.Path) + " の手順" : options.Title.Trim();
            var files = new List<string>();
            var info = new DocumentInfo(title, Path.GetFileName(media.Path), DateTime.Now);
            if (options.Formats.HasFlag(StepDocumentFormat.Markdown)) files.Add(Write(stage, BaseName + ".md", Markdown(info, pages)));
            string? html = options.Formats.HasFlag(StepDocumentFormat.Html) || options.Formats.HasFlag(StepDocumentFormat.Pdf) ? Write(stage, BaseName + ".html", Html(info, pages)) : null;
            if (options.Formats.HasFlag(StepDocumentFormat.Html)) files.Add(html!);
            if (options.Formats.HasFlag(StepDocumentFormat.Word))
            {
                progress?.Report("Word文書を作成しています…");
                string docx = Path.Combine(stage, BaseName + ".docx");
                DocxWriter.Write(docx, info, pages, stage);
                files.Add(docx);
            }
            string? warning = null;
            if (options.Formats.HasFlag(StepDocumentFormat.Pdf))
            {
                progress?.Report("PDFを作成しています…");
                string pdf = Path.Combine(stage, BaseName + ".pdf");
                if (EdgePdf.FindEdge() is not { } edge) warning = "Microsoft Edgeが見つからないため、PDFは作成していません。HTMLをブラウザーで開いて印刷してください。";
                else
                {
                    try { await EdgePdf.PrintAsync(edge, html!, pdf, Path.Combine(stage, ".edge-profile"), ct).ConfigureAwait(false); files.Add(pdf); }
                    catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { warning = "PDFを作成できませんでした: " + ex.Message; }
                }
                TryDelete(Path.Combine(stage, ".edge-profile"));
                if (!options.Formats.HasFlag(StepDocumentFormat.Html)) File.Delete(html!);
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return new(target, files.Select(f => Path.Combine(target, Path.GetRelativePath(stage, f))).ToArray(), warning);
        }
        finally { TryDelete(stage); }
    }

    public sealed record DocumentInfo(string Title, string VideoFile, DateTime Created);

    public static string Markdown(DocumentInfo info, IReadOnlyList<StepPage> pages)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(info.Title).Append("\n\n");
        text.Append("- 元動画: ").Append(info.VideoFile).Append('\n');
        text.Append("- 作成日: ").Append(info.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- 手順数: ").Append(pages.Count.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        foreach (var page in pages)
        {
            text.Append("## 手順 ").Append(page.Number.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
            if (page.Description.Length > 0) text.Append(page.Description.ReplaceLineEndings("  \n")).Append("\n\n");
            text.Append("![手順 ").Append(page.Number.ToString(CultureInfo.InvariantCulture)).Append("](").Append(page.ImageFile).Append(")\n\n");
        }
        return text.ToString();
    }

    public static string Html(DocumentInfo info, IReadOnlyList<StepPage> pages)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(E(info.Title)).Append("</title>\n<style>\n")
            .Append("body{font-family:\"Yu Gothic UI\",\"Meiryo\",sans-serif;color:#222;line-height:1.6;max-width:960px;margin:24px auto;padding:0 16px}\n")
            .Append("h1{font-size:24px;border-bottom:3px solid #e53935;padding-bottom:4px}\n.meta{color:#666;font-size:13px;margin:0 0 16px}\n")
            .Append(".step{break-inside:avoid;page-break-inside:avoid;border-top:1px solid #ddd;padding-top:12px;margin:20px 0}\n")
            .Append(".step h2{font-size:18px;margin:0 0 6px}\n.step p{margin:0 0 8px;white-space:pre-wrap}\n.step img{max-width:100%;height:auto;border:1px solid #ccc}\n")
            .Append("@media print{body{max-width:none;margin:0}.step{margin:12px 0}}\n</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(E(info.Title)).Append("</h1>\n<p class=\"meta\">元動画: ").Append(E(info.VideoFile))
            .Append("　作成日: ").Append(info.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("　手順数: ").Append(pages.Count.ToString(CultureInfo.InvariantCulture)).Append("</p>\n");
        foreach (var page in pages)
        {
            string number = page.Number.ToString(CultureInfo.InvariantCulture);
            html.Append("<section class=\"step\">\n<h2>手順 ").Append(number).Append("</h2>\n");
            if (page.Description.Length > 0) html.Append("<p>").Append(E(page.Description)).Append("</p>\n");
            html.Append("<img src=\"").Append(E(page.ImageFile)).Append("\" alt=\"手順 ").Append(number)
                .Append("\" width=\"").Append(page.Width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(page.Height.ToString(CultureInfo.InvariantCulture)).Append("\">\n</section>\n");
        }
        return html.Append("</body>\n</html>\n").ToString();
    }

    private static string Write(string directory, string name, string content)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Trim().TrimEnd('.');
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
