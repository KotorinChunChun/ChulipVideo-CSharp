using ChulipVideo.Core;

namespace ChulipVideo.Export;

/// <summary>1フレームの合成に必要な指定。時刻は元動画の秒、座標は元動画のピクセル。</summary>
public sealed record RenderContext(CropRect Crop, double Time, EventLog Events, OverlayOptions Options)
{
    /// <summary>falseならポインター・クリック・字幕を描かない（静止判定用の素の画像）。</summary>
    public bool DrawOverlay { get; init; } = true;
    /// <summary>①で描くマスク。DrawOverlayに関係なく常に適用する。</summary>
    public IReadOnlyList<MaskRegion> Masks { get; init; } = [];
    /// <summary>⑤で描く手順書の注釈（出力用の合成のみ）。</summary>
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];
    /// <summary>注釈の番号バッジに描く手順番号。</summary>
    public int StepNumber { get; init; }
}

/// <summary>
/// プレビュー・コピー・PNG・連番・MP4・GIF・手順書画像が共通に通る合成処理。
/// 段階: ①マスク → ②表示範囲（クロップ・ズーム） → ③ポインター・クリック → ④字幕 → ⑤注釈。
/// 出力側はOverlayRendererを直接呼ばず、必ずここを通す（マスクの適用漏れを防ぐため）。
/// </summary>
public static class FrameComposer
{
    /// <summary>出力用。クロップの幅・高さの画像を作る。</summary>
    public static Bitmap Compose(Bitmap frame, RenderContext context)
    {
        using Bitmap? masked = Masked(frame, context, 1, 1);
        var result = OverlayRenderer.Render(masked ?? frame, context.Crop, context.Time, context.Events, context.Options, context.DrawOverlay);
        if (context.Annotations.Count == 0) return result;
        try
        {
            using var graphics = Graphics.FromImage(result);
            AnnotationRenderer.Draw(graphics, context.Annotations, new PointF(-context.Crop.X, -context.Crop.Y), 1, context.StepNumber);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>プレビュー用。縮小フレームの全体へ、元動画との比率で演出を重ねる。</summary>
    public static Bitmap ComposePreview(Bitmap frame, MediaInfo media, RenderContext context)
    {
        using Bitmap? masked = Masked(frame, context, (float)frame.Width / media.Width, (float)frame.Height / media.Height);
        return OverlayRenderer.RenderPreview(masked ?? frame, media, context.Time, context.Events, context.Options, context.Crop);
    }

    // 元フレームはプレビューで使い回すため書き換えず、マスクがあるときだけ複製して描く。
    private static Bitmap? Masked(Bitmap frame, RenderContext context, float scaleX, float scaleY) =>
        MaskRenderer.HasActive(context.Masks, context.Time) ? MaskRenderer.Apply(frame, context.Masks, context.Time, scaleX, scaleY) : null;
}
