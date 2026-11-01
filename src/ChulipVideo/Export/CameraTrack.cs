using ChulipVideo.Core;

namespace ChulipVideo.Export;

/// <summary>
/// 自動ズーム（R6）の表示範囲を、時刻だけから決める（プレビュー・書き出し・TSVで同じ結果になる）。
/// 近いクリックを1つの「場面」にまとめ、場面の間は寄ったまま中心だけを移し、場面の外は切り出し範囲の全体を表示する。
/// </summary>
public sealed class CameraTrack
{
    private sealed record Scene(double Start, double End, IReadOnlyList<(double Time, PointF Point)> Clicks);
    private readonly CropRect crop;
    private readonly ZoomSettings settings;
    private readonly List<Scene> scenes = [];

    public static CameraTrack Identity(CropRect crop) => new(crop, new ZoomSettings { Enabled = false }, []);

    /// <summary>clicksは元動画の座標。切り出し範囲の外のクリックは寄せない。</summary>
    public CameraTrack(CropRect crop, ZoomSettings settings, IEnumerable<(double Time, PointF Point)> clicks)
    {
        this.crop = crop;
        this.settings = settings with { };
        if (!settings.Enabled || settings.Scale <= 1.0001) return;
        var inside = clicks.Where(c => c.Point.X >= crop.X && c.Point.Y >= crop.Y && c.Point.X < crop.X + crop.Width && c.Point.Y < crop.Y + crop.Height)
            .OrderBy(c => c.Time).ToList();
        var current = new List<(double, PointF)>();
        foreach (var click in inside)
        {
            // 次の寄り始めが前の戻り終わりより前なら、戻らずに同じ場面として続ける。
            if (current.Count > 0 && click.Time - current[^1].Item1 > settings.MergeGap
                && click.Time - settings.Lead >= current[^1].Item1 + settings.Hold + settings.ZoomOut)
            { scenes.Add(Build(current)); current = []; }
            current.Add(click);
        }
        if (current.Count > 0) scenes.Add(Build(current));
    }

    public static CameraTrack FromEvents(EventLog events, CropRect crop, ZoomSettings settings) =>
        new(crop, settings, InputActions.Extract(events)
            .Where(a => a.Kind is InputActionKind.Click or InputActionKind.DoubleClick or InputActionKind.RightClick or InputActionKind.MiddleClick or InputActionKind.Drag)
            .Select(a => (a.Time, new PointF(a.Point.X, a.Point.Y))));

    private Scene Build(List<(double Time, PointF Point)> clicks) =>
        new(clicks[0].Time - settings.Lead, clicks[^1].Time + settings.Hold, clicks.ToArray());

    public bool IsIdentity => scenes.Count == 0;

    /// <summary>時刻tに表示する範囲（元動画の座標）。常に切り出し範囲の中に収まり、縦横比は切り出し範囲と同じ。</summary>
    public RectangleF ViewAt(double t)
    {
        var full = new RectangleF(crop.X, crop.Y, crop.Width, crop.Height);
        foreach (var scene in scenes)
        {
            if (t < scene.Start || t >= scene.End + settings.ZoomOut) continue;
            double zoom = t < scene.Start + settings.ZoomIn
                ? 1 + (settings.Scale - 1) * Ease((t - scene.Start) / settings.ZoomIn)
                : t >= scene.End ? 1 + (settings.Scale - 1) * (1 - Ease((t - scene.End) / settings.ZoomOut)) : settings.Scale;
            return View(Center(scene, t), zoom);
        }
        return full;
    }

    // 次のクリックのPan秒前から中心を移し始め、クリックの時刻に移し終える。
    private PointF Center(Scene scene, double t)
    {
        var clicks = scene.Clicks;
        var center = clicks[0].Point;
        for (int k = 1; k < clicks.Count; k++)
        {
            double begin = clicks[k].Time - settings.Pan;
            if (t < begin) break;
            float u = (float)Ease(Math.Clamp((t - begin) / settings.Pan, 0, 1));
            center = new(clicks[k - 1].Point.X + (clicks[k].Point.X - clicks[k - 1].Point.X) * u, clicks[k - 1].Point.Y + (clicks[k].Point.Y - clicks[k - 1].Point.Y) * u);
        }
        return center;
    }

    private RectangleF View(PointF center, double zoom)
    {
        float width = (float)(crop.Width / zoom), height = (float)(crop.Height / zoom);
        float x = Math.Clamp(center.X - width / 2, crop.X, crop.X + crop.Width - width);
        float y = Math.Clamp(center.Y - height / 2, crop.Y, crop.Y + crop.Height - height);
        return new(x, y, width, height);
    }

    public static double Ease(double u)
    {
        u = Math.Clamp(u, 0, 1);
        return u < .5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
    }
}
