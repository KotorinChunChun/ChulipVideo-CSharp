namespace ChulipVideo.Export;

/// <summary>
/// 入力ログの座標から、描くポインターの位置を時刻で求める（R8）。
/// Offは直前の行の座標、Weakは前後の行の直線補間、Strongは小さな揺れを除いてセントリペタルCatmull-Rom曲線で補間する。
/// 押下・解放の前後<see cref="PinWindow"/>秒は記録どおりの座標に固定し、クリック位置をずらさない。
/// CapchunWindowは範囲外のカーソルを(-1,-1)で記録するため、その前後は補間しない。
/// </summary>
public sealed class PointerPath
{
    public const double PinWindow = .1;
    public const float JitterPixels = 2;
    private readonly double[] times;
    private readonly PointF[] points;
    private readonly double[] smoothTimes;
    private readonly PointF[] smoothPoints;
    private readonly double[] edges;

    public PointerPath(IReadOnlyList<InputEvent> events)
    {
        times = events.Select(e => e.Timestamp).ToArray();
        points = events.Select(e => new PointF(e.X, e.Y)).ToArray();
        var edgeList = new List<double>();
        var previous = new HashSet<string>();
        foreach (var entry in events)
        {
            var buttons = EventLog.MouseButtons(entry.Click).Where(b => b.EndsWith("-Click", StringComparison.Ordinal)).ToHashSet();
            if (!buttons.SetEquals(previous)) edgeList.Add(entry.Timestamp);
            previous = buttons;
        }
        edges = edgeList.ToArray();
        var keptTimes = new List<double>();
        var kept = new List<PointF>();
        for (int i = 0; i < points.Length; i++)
        {
            if (!Valid(points[i])) continue;
            bool last = i == points.Length - 1;
            if (kept.Count == 0 || last || Distance(kept[^1], points[i]) >= JitterPixels) { keptTimes.Add(times[i]); kept.Add(points[i]); }
        }
        smoothTimes = keptTimes.ToArray();
        smoothPoints = kept.ToArray();
    }

    public PointF? At(double seconds, string smoothing)
    {
        int i = Floor(times, seconds);
        if (i < 0) return null;
        var raw = points[i];
        if (smoothing is not ("Weak" or "Strong") || i == points.Length - 1 || !Valid(raw) || !Valid(points[i + 1]) || NearEdge(seconds)) return raw;
        if (smoothing == "Weak")
        {
            double span = times[i + 1] - times[i];
            float u = span <= 0 ? 0 : (float)Math.Clamp((seconds - times[i]) / span, 0, 1);
            return new(raw.X + (points[i + 1].X - raw.X) * u, raw.Y + (points[i + 1].Y - raw.Y) * u);
        }
        int j = Floor(smoothTimes, seconds);
        if (j < 0 || j >= smoothPoints.Length - 1) return raw;
        double length = smoothTimes[j + 1] - smoothTimes[j];
        double fraction = length <= 0 ? 0 : Math.Clamp((seconds - smoothTimes[j]) / length, 0, 1);
        var p0 = j > 0 ? smoothPoints[j - 1] : smoothPoints[j];
        var p3 = j + 2 < smoothPoints.Length ? smoothPoints[j + 2] : smoothPoints[j + 1];
        return CatmullRom(p0, smoothPoints[j], smoothPoints[j + 1], p3, fraction);
    }

    private bool NearEdge(double seconds)
    {
        int index = Array.BinarySearch(edges, seconds);
        if (index >= 0) return true;
        index = ~index;
        return index < edges.Length && edges[index] - seconds <= PinWindow || index > 0 && seconds - edges[index - 1] <= PinWindow;
    }

    private static int Floor(double[] values, double seconds)
    {
        int lo = 0, hi = values.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (values[mid] <= seconds + 1e-8) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    private static bool Valid(PointF p) => p.X >= 0 && p.Y >= 0;
    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Barry-Goldman法によるセントリペタル（α=0.5）Catmull-Rom。fractionはp1→p2の区間内の割合。</summary>
    public static PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, double fraction)
    {
        static double Knot(double t, PointF a, PointF b) => t + Math.Max(1e-4, Math.Sqrt(Distance(a, b)));
        double t0 = 0, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
        double t = t1 + (t2 - t1) * fraction;
        static PointF Mix(PointF a, PointF b, double ta, double tb, double t)
        {
            double wa = (tb - t) / (tb - ta), wb = (t - ta) / (tb - ta);
            return new((float)(a.X * wa + b.X * wb), (float)(a.Y * wa + b.Y * wb));
        }
        var a1 = Mix(p0, p1, t0, t1, t); var a2 = Mix(p1, p2, t1, t2, t); var a3 = Mix(p2, p3, t2, t3, t);
        var b1 = Mix(a1, a2, t0, t2, t); var b2 = Mix(a2, a3, t1, t3, t);
        return Mix(b1, b2, t1, t2, t);
    }
}
