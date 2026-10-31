using ChulipVideo.Core;

namespace ChulipVideo.Media;

/// <summary>
/// 区間の各フレームが直前からどれだけ変化したかを、縮小した映像で一度に測る。
/// ステップ抽出・無操作区間の検出・マスク候補の時間範囲で共通に使う。
/// </summary>
public sealed class MotionProfile
{
    public const int MeasureLongEdge = 320;
    public MediaInfo Media { get; }
    public double Start { get; }
    /// <summary>Changes[i]は区間のi番目のフレームと直前のフレームの変化画素率。先頭は0。</summary>
    public IReadOnlyList<float> Changes { get; }
    public StabilitySettings Stability { get; }

    public MotionProfile(MediaInfo media, double start, IReadOnlyList<float> changes, StabilitySettings stability)
    {
        Media = media; Start = media.FrameTime(start); Changes = changes; Stability = stability with { };
    }

    public double TimeOf(int index) => Media.FrameTime(Start + index / Media.Fps);
    public int IndexAt(double seconds) => Math.Clamp((int)Math.Floor((Media.FrameTime(seconds) - Start) * Media.Fps + 1e-7), 0, Math.Max(0, Changes.Count - 1));
    public bool IsSteady(int index) => index <= 0 || Changes[index] <= Stability.MaxChangedRatio;

    /// <summary>[from, to]の中で最も遅い、直前と変わらないフレームの時刻。なければnull。</summary>
    public double? LatestSteady(double from, double to)
    {
        if (Changes.Count == 0 || to < from) return null;
        for (int i = IndexAt(to); i >= IndexAt(from); i--)
            if (IsSteady(i) && TimeOf(i) >= from - 1e-9 && TimeOf(i) <= to + 1e-9) return TimeOf(i);
        return null;
    }

    /// <summary>[from, to]の中で最も早い、直前と変わらないフレームの時刻。なければnull。</summary>
    public double? EarliestSteady(double from, double to)
    {
        if (Changes.Count == 0 || to < from) return null;
        for (int i = IndexAt(from); i <= IndexAt(to); i++)
            if (IsSteady(i) && TimeOf(i) >= from - 1e-9 && TimeOf(i) <= to + 1e-9) return TimeOf(i);
        return null;
    }

    public static async Task<MotionProfile> MeasureAsync(FfmpegService service, MediaInfo media, double start, double end, CropRect crop,
        StabilitySettings stability, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        double scale = Math.Min(1, MeasureLongEdge / (double)Math.Max(media.Width, media.Height));
        int width = Math.Max(1, (int)Math.Round(media.Width * scale)), height = Math.Max(1, (int)Math.Round(media.Height * scale));
        Rectangle region = Region(crop, scale, width, height);
        var changes = new List<float>();
        await using FrameReader reader = service.OpenReaderCore(media, start, end, width, height);
        long total = Math.Max(1L, (long)Math.Ceiling((Math.Min(end, media.Duration) - start) * media.Fps - 1e-7));
        byte[]? previous = null;
        while (await reader.ReadPixelsAsync(ct).ConfigureAwait(false) is { } pixels)
        {
            byte[] current = Extract(pixels, width, region);
            // 縮小で圧縮ノイズは平均されるため、色差しきい値は元画像と同じ値を使う。
            changes.Add(previous is null ? 0 : (float)FrameComparer.ChangedRatio(previous, current, stability.ChannelThreshold));
            previous = current;
            progress?.Report(Math.Min(1, changes.Count / (double)total));
        }
        return new MotionProfile(media, start, changes, stability);
    }

    private static Rectangle Region(CropRect crop, double scale, int width, int height)
    {
        var scaled = Rectangle.FromLTRB((int)Math.Floor(crop.X * scale), (int)Math.Floor(crop.Y * scale),
            (int)Math.Ceiling((crop.X + crop.Width) * scale), (int)Math.Ceiling((crop.Y + crop.Height) * scale));
        var region = Rectangle.Intersect(scaled, new Rectangle(0, 0, width, height));
        return region.Width > 0 && region.Height > 0 ? region : new Rectangle(0, 0, width, height);
    }

    private static byte[] Extract(byte[] pixels, int width, Rectangle region)
    {
        var result = new byte[region.Width * region.Height * 4];
        for (int y = 0; y < region.Height; y++)
            Buffer.BlockCopy(pixels, ((region.Y + y) * width + region.X) * 4, result, y * region.Width * 4, region.Width * 4);
        return result;
    }
}
