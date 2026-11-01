using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChulipVideo.Recognition;

/// <summary>OCRで読んだ1行。Boundsは読ませた画像の座標。</summary>
public sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>
/// Windows標準のOCR（Windows.Media.Ocr、オフライン）で画像の文字を読む（R9）。
/// 日本語のOCRがあれば日本語、なければWindowsの表示言語を使う。どちらもなければ使えない理由を返す。
/// </summary>
public static class OcrService
{
    public static string? Unavailable()
    {
        try { return CreateEngine() is null ? "この Windows には文字認識（OCR）の言語がありません。「設定」→「時刻と言語」で日本語のOCRを追加してください。" : null; }
        catch (Exception ex) when (ex is TypeLoadException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException) { return "文字認識（OCR）を利用できません: " + ex.Message; }
    }

    public static string? LanguageTag => CreateEngine()?.RecognizerLanguage.LanguageTag;

    private static OcrEngine? CreateEngine()
    {
        var japanese = new Language("ja");
        return OcrEngine.IsLanguageSupported(japanese) ? OcrEngine.TryCreateFromLanguage(japanese) : OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public static async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap image, CancellationToken ct = default)
    {
        var engine = CreateEngine() ?? throw new InvalidOperationException(Unavailable());
        // OCRに渡せる大きさを超える画像は縮小し、結果の座標を元へ戻す。
        double scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(image.Width, image.Height));
        using Bitmap source = scale < 1 ? new Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))) : (Bitmap)image.Clone();
        using var stream = new MemoryStream();
        source.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        using var random = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random).AsTask(ct).ConfigureAwait(false);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var bounds = line.Words.Select(w => w.BoundingRect).Aggregate((a, b) =>
            {
                double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
                return new Windows.Foundation.Rect(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
            });
            var rect = Rectangle.FromLTRB((int)Math.Floor(bounds.X / scale), (int)Math.Floor(bounds.Y / scale),
                (int)Math.Ceiling((bounds.X + bounds.Width) / scale), (int)Math.Ceiling((bounds.Y + bounds.Height) / scale));
            lines.Add(new(Join(line.Words.Select(w => w.Text)), rect));
        }
        return lines;
    }

    /// <summary>日本語は1文字ずつ単語に分かれるため、全角・かな・漢字の間の空白を詰める。英数字どうしの空白は残す。</summary>
    public static string Join(IEnumerable<string> words)
    {
        var text = new StringBuilder();
        string previous = "";
        foreach (string word in words.Where(w => w.Length > 0))
        {
            if (text.Length > 0 && !(IsWide(previous[^1]) || IsWide(word[0]))) text.Append(' ');
            text.Append(word);
            previous = word;
        }
        return text.ToString();
    }

    private static bool IsWide(char c) => c >= 0x3000 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;
}

/// <summary>OCRの結果から、手順の対象名とマスク候補を作る（R9）。</summary>
public static class OcrAnalysis
{
    /// <summary>クリック点の周りで読ませる範囲の大きさ（元動画のピクセル）。</summary>
    public const int LabelAreaWidth = 480, LabelAreaHeight = 200, NearDistance = 60;

    /// <summary>クリック点を含む行、なければNearDistance以内で最も近い行。座標は同じ画像の座標。</summary>
    public static OcrLine? LineAt(IReadOnlyList<OcrLine> lines, Point point)
    {
        var inside = lines.FirstOrDefault(l => Rectangle.Inflate(l.Bounds, 2, 2).Contains(point) && Clean(l.Text).Length > 0);
        if (inside is not null) return inside;
        return lines.Where(l => Clean(l.Text).Length > 0).Select(l => (Line: l, Distance: Distance(l.Bounds, point)))
            .Where(x => x.Distance <= NearDistance).OrderBy(x => x.Distance).Select(x => x.Line).FirstOrDefault();
    }

    public static string Clean(string text) => text.Trim().Trim('|', '_', '-', '・', '.', ':', '：', '[', ']', '(', ')', '（', '）').Trim();

    private static double Distance(Rectangle r, Point p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right), dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>OCRで読んだ対象名を使う説明文。対象名がない種類はnull。</summary>
    public static string? Describe(string kind, string label) => kind switch
    {
        "Click" => $"「{label}」をクリックします。",
        "DoubleClick" => $"「{label}」をダブルクリックします。",
        "RightClick" => $"「{label}」を右クリックします。",
        "MiddleClick" => $"「{label}」を中クリックします。",
        "Drag" => $"「{label}」をドラッグします。",
        _ => null
    };

    private static readonly (string Name, Regex Pattern)[] Detectors =
    [
        ("メールアドレス", new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+", RegexOptions.CultureInvariant)),
        ("電話番号", new(@"0\d{1,4}-\d{1,4}-\d{3,4}", RegexOptions.CultureInvariant)),
        ("郵便番号", new(@"〒\s?\d{3}-\d{4}", RegexOptions.CultureInvariant)),
        ("13〜16桁の数字", new(@"(?<!\d)(?:\d[ -]?){12,15}\d(?!\d)", RegexOptions.CultureInvariant))
    ];

    /// <summary>行の文字に機密らしい内容があれば、その種類（指定語は語そのもの）を返す。</summary>
    public static string? Sensitive(string text, IEnumerable<string> keywords)
    {
        string compact = text.Replace(" ", "", StringComparison.Ordinal);
        foreach (var (name, pattern) in Detectors)
            if (pattern.IsMatch(text) || pattern.IsMatch(compact)) return name;
        foreach (string keyword in keywords.Select(k => k.Trim()).Where(k => k.Length > 0))
            if (compact.Contains(keyword.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)) return "指定語「" + keyword + "」";
        return null;
    }
}
