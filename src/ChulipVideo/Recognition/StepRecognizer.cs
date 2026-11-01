using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Recognition;

/// <summary>OCRで手順の対象名を読み、説明文と強調枠に使う（R9）。</summary>
public static class StepRecognizer
{
    /// <summary>クリック点を中心にした読み取り範囲（動画の中に収める）。</summary>
    public static CropRect LabelArea(StepItem step, MediaInfo media)
    {
        int width = Math.Min(OcrAnalysis.LabelAreaWidth, media.Width), height = Math.Min(OcrAnalysis.LabelAreaHeight, media.Height);
        return new(Math.Clamp(step.X - width / 2, 0, media.Width - width), Math.Clamp(step.Y - height / 2, 0, media.Height - height), width, height);
    }

    /// <summary>
    /// マウス操作の手順ごとに読み取り、対象名が分かったものの説明文と強調枠を更新する。更新した件数を返す。
    /// マスクをかけた画像を読むので、隠した内容は説明文に入らない。
    /// </summary>
    public static async Task<int> DescribeAsync(FfmpegService service, MediaInfo media, EventLog events, IEnumerable<StepItem> steps, AppSettings settings,
        IReadOnlyList<MaskRegion> masks, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var targets = steps.Where(s => OcrAnalysis.Describe(s.Kind, "x") is not null).ToArray();
        int updated = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"文字を読み取っています… {i + 1} / {targets.Length}");
            var step = targets[i];
            var area = LabelArea(step, media);
            using Bitmap frame = await service.ReadFrameAsync(media, step.FrameTime, ct).ConfigureAwait(false);
            using Bitmap image = FrameComposer.Compose(frame, new(area, step.FrameTime, events, settings.Overlay) { DrawOverlay = false, Masks = masks });
            var lines = await OcrService.RecognizeAsync(image, ct).ConfigureAwait(false);
            if (OcrAnalysis.LineAt(lines, new Point(step.X - area.X, step.Y - area.Y)) is not OcrLine line) continue;
            string label = OcrAnalysis.Clean(line.Text);
            if (label.Length == 0 || OcrAnalysis.Describe(step.Kind, label) is not string description) continue;
            step.Description = description;
            step.DescriptionSource = "Ocr";
            var bounds = Rectangle.Inflate(new Rectangle(line.Bounds.X + area.X, line.Bounds.Y + area.Y, line.Bounds.Width, line.Bounds.Height), 6, 4);
            if (step.Annotations.FirstOrDefault(a => a.Kind == AnnotationKind.Box) is Annotation box)
            { box.X = bounds.X; box.Y = bounds.Y; box.Width = bounds.Width; box.Height = bounds.Height; }
            step.Annotations = Annotation.Normalize(step.Annotations, media.Width, media.Height);
            updated++;
        }
        return updated;
    }
}

/// <summary>画面の機密らしい文字を読み、マスクの候補を作る（R9）。候補は無効の状態で返し、利用者の承認で有効にする。</summary>
public static class MaskSuggester
{
    public const double Interval = 2;

    public static async Task<List<MaskRegion>> SuggestAsync(FfmpegService service, MediaInfo media, double start, double end, IEnumerable<string> keywords,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var words = keywords.ToArray();
        var times = new List<double>();
        // FrameTimeは末尾で止まるため、回数で進めて同じ時刻を繰り返さない。
        for (int k = 0; start + k * Interval < end - 1e-9 && start + k * Interval <= media.LastFrameTime + 1e-9; k++) times.Add(media.FrameTime(start + k * Interval));
        if (times.Count == 0) times.Add(media.FrameTime(start));
        var found = new List<(double Time, Rectangle Rect, string Kind)>();
        for (int i = 0; i < times.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"マスク候補を探しています… {i + 1} / {times.Count}");
            using Bitmap frame = await service.ReadFrameAsync(media, times[i], ct).ConfigureAwait(false);
            foreach (var line in await OcrService.RecognizeAsync(frame, ct).ConfigureAwait(false))
                if (OcrAnalysis.Sensitive(line.Text, words) is string kind) found.Add((times[i], Rectangle.Inflate(line.Bounds, 4, 4), kind));
        }
        // 続けて見つかった同じ種類・ほぼ同じ位置の候補は、1つのマスクにまとめて時間を延ばす。
        var merged = new List<(double Start, double End, Rectangle Rect, string Kind)>();
        foreach (var hit in found.OrderBy(f => f.Time))
        {
            int index = merged.FindIndex(m => m.Kind == hit.Kind && Overlap(m.Rect, hit.Rect) > .5 && hit.Time - m.End <= Interval + 1e-6);
            if (index >= 0) merged[index] = (merged[index].Start, hit.Time, Rectangle.Union(merged[index].Rect, hit.Rect), hit.Kind);
            else merged.Add((hit.Time, hit.Time, hit.Rect, hit.Kind));
        }
        // 読み取りの間隔の分だけ前後に広げる（隠しすぎは安全側）。
        var masks = merged.Select(m => new MaskRegion
        {
            Rect = new(m.Rect.X, m.Rect.Y, m.Rect.Width, m.Rect.Height), Start = Math.Max(start, m.Start - Interval), End = Math.Min(end, m.End + Interval),
            Mode = MaskMode.Fill, Enabled = false, Source = "Suggested", Note = "候補: " + m.Kind
        });
        return SettingsStore.NormalizeMasks(masks, media);
    }

    private static double Overlap(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        if (i.IsEmpty) return 0;
        return i.Width * (double)i.Height / Math.Min(a.Width * (double)a.Height, b.Width * (double)b.Height);
    }
}
