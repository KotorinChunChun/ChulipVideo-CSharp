using ChulipVideo.Core;

namespace ChulipVideo.Export;

/// <summary>1フレームの合成に必要な指定。時刻は元動画の秒、座標は元動画のピクセル。</summary>
public sealed record RenderContext(CropRect Crop, double Time, EventLog Events, OverlayOptions Options)
{
    /// <summary>falseならポインター・クリック・字幕を描かない（静止判定用の素の画像）。</summary>
    public bool DrawOverlay { get; init; } = true;
}

/// <summary>
/// プレビュー・コピー・PNG・連番・MP4・GIFが共通に通る合成処理。
/// 段階: ①マスク → ②表示範囲（クロップ・ズーム） → ③ポインター・クリック → ④字幕 → ⑤注釈。
/// 出力側はOverlayRendererを直接呼ばず、必ずここを通す（マスクの適用漏れを防ぐため）。
/// </summary>
public static class FrameComposer
{
    /// <summary>出力用。クロップの幅・高さの画像を作る。</summary>
    public static Bitmap Compose(Bitmap frame, RenderContext context) =>
        OverlayRenderer.Render(frame, context.Crop, context.Time, context.Events, context.Options, context.DrawOverlay);

    /// <summary>プレビュー用。縮小フレームの全体へ、元動画との比率で演出を重ねる。</summary>
    public static Bitmap ComposePreview(Bitmap frame, MediaInfo media, RenderContext context) =>
        OverlayRenderer.RenderPreview(frame, media, context.Time, context.Events, context.Options, context.Crop);
}
