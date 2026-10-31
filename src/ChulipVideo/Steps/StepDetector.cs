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
            var step = new StepItem
            {
                Kind = action.Kind.ToString(), Time = action.Time, FrameTime = frame,
                AfterFrameTime = settings.SaveAfter && action.Kind != InputActionKind.Text ? After(action.EndTime + settings.PostSeconds, action.EndTime + settings.PostSeconds, profile, media) : null,
                X = action.Point.X, Y = action.Point.Y, EndX = action.EndPoint?.X, EndY = action.EndPoint?.Y,
                Label = action.Label, Description = Describe(action), DescriptionSource = "Template"
            };
            step.Annotations = DefaultAnnotations(step, media);
            yield return step;
        }
    }

    public const int BoxWidth = 120, BoxHeight = 40, BadgeGap = 22, BadgeMargin = 16;

    /// <summary>
    /// 自動で配置する注釈。マウス操作は番号バッジと強調枠（ドラッグは矢印）、キー操作は注釈なし。
    /// バッジは枠（ドラッグは始点）の右、入らなければ左へ、操作位置と同じ高さに置き、枠や対象を隠さない。
    /// </summary>
    public static List<Annotation> DefaultAnnotations(StepItem step, MediaInfo media)
    {
        if (step.Kind is not ("Click" or "DoubleClick" or "RightClick" or "MiddleClick" or "Drag")) return [];
        var result = new List<Annotation>();
        int left = step.X, right = step.X;
        if (step.Kind == "Drag" && step.EndX is int endX && step.EndY is int endY)
            result.Add(new Annotation { Kind = AnnotationKind.Arrow, X = step.X, Y = step.Y, EndX = endX, EndY = endY });
        else
        {
            int x = Math.Clamp(step.X - BoxWidth / 2, 0, Math.Max(0, media.Width - BoxWidth));
            int y = Math.Clamp(step.Y - BoxHeight / 2, 0, Math.Max(0, media.Height - BoxHeight));
            var box = new Annotation { Kind = AnnotationKind.Box, X = x, Y = y, Width = Math.Min(BoxWidth, media.Width), Height = Math.Min(BoxHeight, media.Height) };
            result.Add(box);
            left = box.X; right = box.X + box.Width;
        }
        int badgeX = right + BadgeGap <= media.Width - BadgeMargin ? right + BadgeGap : left - BadgeGap;
        int badgeY = Math.Clamp(step.Y, BadgeMargin, Math.Max(BadgeMargin, media.Height - BadgeMargin));
        result.Add(new Annotation { Kind = AnnotationKind.Badge, X = Math.Max(BadgeMargin, badgeX), Y = badgeY });
        return Annotation.Normalize(result, media.Width, media.Height);
    }

    /// <summary>注目範囲: 操作位置を中心に、切り出し範囲の縦横1/2の矩形（切り出し範囲の中に収める）。</summary>
    public static CropRect FocusArea(StepItem step, CropRect crop)
    {
        int width = Math.Max(1, crop.Width / 2), height = Math.Max(1, crop.Height / 2);
        int x = Math.Clamp(step.X - width / 2, crop.X, crop.X + crop.Width - width);
        int y = Math.Clamp(step.Y - height / 2, crop.Y, crop.Y + crop.Height - height);
        return new(x, y, width, height);
    }

    /// <summary>手順書に載せる画像。マスク・注釈を重ね、設定に応じて注目範囲へ絞る。</summary>
    public static async Task<Bitmap> RenderStepAsync(FfmpegService service, MediaInfo media, EventLog events, StepItem step, int number, CropRect crop,
        AppSettings settings, IReadOnlyList<MaskRegion> masks, CancellationToken ct = default)
    {
        var area = settings.Steps.FocusImage ? FocusArea(step, crop) : crop;
        using Bitmap frame = await service.ReadFrameAsync(media, step.FrameTime, ct).ConfigureAwait(false);
        return FrameComposer.Compose(frame, new(area, step.FrameTime, events, settings.Overlay)
            { DrawOverlay = false, Masks = masks, Annotations = step.Annotations, StepNumber = number });
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
    public static async Task<Bitmap> RenderImageAsync(FfmpegService service, MediaInfo media, EventLog events, CropRect crop, AppSettings settings, double time,
        IReadOnlyList<MaskRegion>? masks = null, CancellationToken ct = default)
    {
        using Bitmap frame = await service.ReadFrameAsync(media, time, ct).ConfigureAwait(false);
        return FrameComposer.Compose(frame, new(crop, time, events, settings.Overlay) { DrawOverlay = false, Masks = masks ?? [] });
    }
}
