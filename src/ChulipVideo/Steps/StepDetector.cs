using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Steps;

/// <summary>入力ログの操作から、1操作1枚のステップと画像の時刻を決める。</summary>
public static class StepDetector
{
    /// <summary>操作直前の落ち着いたフレームを探す範囲（操作時刻からの秒）。</summary>
    public const double BeforeWindow = .6, BeforeGap = .05;
    /// <summary>操作後（文字入力の結果など）の落ち着いたフレームを探す長さ。</summary>
    public const double AfterWindow = 1.5, TextSettle = .3;

    public static async Task<StepDocument> DetectAsync(FfmpegService service, MediaInfo media, EventLog events, double start, double end, CropRect crop,
        AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var actions = InputActions.Extract(events, start, end);
        MotionProfile? profile = null;
        if (actions.Count > 0)
        {
            double from = Math.Max(0, start - BeforeWindow), to = Math.Min(media.Duration, end + AfterWindow + settings.Steps.PostSeconds);
            if (to > from) profile = await MotionProfile.MeasureAsync(service, media, from, to, crop, settings.Stability, progress, ct).ConfigureAwait(false);
        }
        return new StepDocument { Start = start, End = end, Crop = crop, Steps = FromActions(actions, profile, media, settings.Steps).ToList() };
    }

    /// <summary>動き量がなければ（null）既定の秒数だけ前後へずらした時刻を使う。</summary>
    public static IEnumerable<StepItem> FromActions(IReadOnlyList<InputAction> actions, MotionProfile? profile, MediaInfo media, StepSettings settings)
    {
        foreach (var action in actions)
        {
            if (action.Kind == InputActionKind.Wheel && !settings.IncludeWheel) continue;
            double frame = action.Kind == InputActionKind.Text
                ? After(action.EndTime + TextSettle, action.EndTime + settings.PostSeconds, profile, media)
                : Before(action.Time, settings.PreSeconds, profile, media);
            yield return new StepItem
            {
                Kind = action.Kind.ToString(), Time = action.Time, FrameTime = frame,
                AfterFrameTime = settings.SaveAfter && action.Kind != InputActionKind.Text ? After(action.EndTime + settings.PostSeconds, action.EndTime + settings.PostSeconds, profile, media) : null,
                X = action.Point.X, Y = action.Point.Y, EndX = action.EndPoint?.X, EndY = action.EndPoint?.Y,
                Label = action.Label, Description = Describe(action), DescriptionSource = "Template"
            };
        }
    }

    private static double Before(double time, double fallback, MotionProfile? profile, MediaInfo media)
    {
        double? steady = profile?.LatestSteady(time - BeforeWindow, time - BeforeGap);
        return media.FrameTime(Math.Max(0, steady ?? time - fallback));
    }

    private static double After(double from, double fallback, MotionProfile? profile, MediaInfo media)
    {
        double? steady = profile?.EarliestSteady(from, from + AfterWindow);
        return media.FrameTime(Math.Min(media.LastFrameTime, steady ?? fallback));
    }

    /// <summary>OCR・AIを使わないときの説明文。</summary>
    public static string Describe(InputAction action)
    {
        string holding = action.Kind is InputActionKind.Click or InputActionKind.DoubleClick or InputActionKind.RightClick or InputActionKind.MiddleClick or InputActionKind.Drag
            && action.Keys.Count > 0 ? string.Join(" + ", action.Keys) + " を押しながら、" : "";
        return action.Kind switch
        {
            InputActionKind.Click => holding + "この位置をクリックします。",
            InputActionKind.DoubleClick => holding + "この位置をダブルクリックします。",
            InputActionKind.RightClick => holding + "この位置を右クリックします。",
            InputActionKind.MiddleClick => holding + "この位置を中クリックします。",
            InputActionKind.Drag => holding + "ここから矢印の位置までドラッグします。",
            InputActionKind.Wheel => "この位置でマウスホイールを回します。",
            InputActionKind.Text => "文字を入力します。",
            _ => action.Label + " を押します。"
        };
    }

    /// <summary>ステップの画像を作る。注釈は手順書の書き出し時に重ねる。</summary>
    public static async Task<Bitmap> RenderImageAsync(FfmpegService service, MediaInfo media, EventLog events, CropRect crop, AppSettings settings, double time, CancellationToken ct = default)
    {
        using Bitmap frame = await service.ReadFrameAsync(media, time, ct).ConfigureAwait(false);
        return FrameComposer.Compose(frame, new(crop, time, events, settings.Overlay) { DrawOverlay = false });
    }
}
