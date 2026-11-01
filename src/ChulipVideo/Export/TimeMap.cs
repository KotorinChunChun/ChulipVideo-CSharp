using ChulipVideo.Core;
using ChulipVideo.Media;

namespace ChulipVideo.Export;

/// <summary>
/// 元動画の時刻sから出力の時刻o（区間の開始を0秒）への、単調な区分線形の写像（R7）。
/// カットの区間は傾き0、早送りの区間は傾き1/倍率、それ以外は傾き1。
/// </summary>
public sealed class TimeMap
{
    /// <summary>元動画の[Start, End)を、出力1秒あたり元動画Rate秒の逆数で進める区間。Rate=0はカット。</summary>
    public sealed record Segment(double Start, double End, double Rate);
    private readonly double start, end;
    private readonly List<Segment> segments = [];
    private readonly double[] outputStarts;

    public IReadOnlyList<Segment> Changed => segments;
    public bool IsIdentity => segments.Count == 0;
    public double OutputDuration => ToOutput(end);

    public static TimeMap Identity(double start, double end) => new(start, end, [], new IdleSettings());

    /// <summary>idleは元動画の時刻の無操作区間。前後にMarginを残した内側をカット・早送りにする。</summary>
    public TimeMap(double start, double end, IEnumerable<(double Start, double End)> idle, IdleSettings settings)
    {
        this.start = start; this.end = end;
        if (settings.Enabled)
            foreach (var (a, b) in idle.OrderBy(i => i.Start))
            {
                double s = Math.Max(start, a + settings.Margin), e = Math.Min(end, b - settings.Margin);
                if (e - s <= 1e-6 || segments.Count > 0 && s < segments[^1].End) continue;
                segments.Add(new(s, e, settings.Mode == "Cut" ? 0 : 1 / settings.Speed));
            }
        outputStarts = new double[segments.Count];
        double output = 0, cursor = start;
        for (int i = 0; i < segments.Count; i++)
        {
            output += segments[i].Start - cursor;
            outputStarts[i] = output;
            output += (segments[i].End - segments[i].Start) * segments[i].Rate;
            cursor = segments[i].End;
        }
    }

    public double ToOutput(double source)
    {
        double s = Math.Clamp(source, start, end), shift = 0, cursor = start;
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (s < segment.Start) break;
            if (s < segment.End) return outputStarts[i] + (s - segment.Start) * segment.Rate;
            shift = outputStarts[i] + (segment.End - segment.Start) * segment.Rate;
            cursor = segment.End;
        }
        return shift + (s - cursor);
    }
}

/// <summary>入力の変化も画面の変化もない状態が続く区間を探す（R7）。</summary>
public static class IdleDetector
{
    /// <summary>ポインターがこれ以上動いたら操作中とみなす。</summary>
    public const float MovePixels = 3;

    public static IReadOnlyList<(double Start, double End)> Detect(EventLog events, MotionProfile profile, IdleSettings settings)
    {
        var activity = ActivityTimes(events);
        var result = new List<(double, double)>();
        double? idleFrom = null;
        int a = 0;
        for (int i = 0; i < profile.Changes.Count; i++)
        {
            double time = profile.TimeOf(i), previous = i == 0 ? double.NegativeInfinity : profile.TimeOf(i - 1);
            bool input = false;
            while (a < activity.Count && activity[a] <= time + 1e-9) { if (activity[a] > previous + 1e-9) input = true; a++; }
            bool active = input || !profile.IsSteady(i);
            if (!active) { idleFrom ??= time; continue; }
            if (idleFrom is double from && time - from >= settings.MinSeconds) result.Add((from, time));
            idleFrom = null;
        }
        if (idleFrom is double last)
        {
            double tail = profile.TimeOf(profile.Changes.Count - 1) + 1 / profile.Media.Fps;
            if (tail - last >= settings.MinSeconds) result.Add((last, tail));
        }
        return result;
    }

    /// <summary>ボタン・キーの押下状態が変わった時刻と、ポインターが動いた時刻。</summary>
    public static List<double> ActivityTimes(EventLog events)
    {
        var times = new List<double>();
        InputEvent? previous = null;
        foreach (var e in events.Events)
        {
            if (previous is not null && (e.Click != previous.Click || e.Keys != previous.Keys
                || Math.Abs(e.X - previous.X) + Math.Abs(e.Y - previous.Y) > MovePixels)) times.Add(e.Timestamp);
            previous = e;
        }
        return times;
    }
}
