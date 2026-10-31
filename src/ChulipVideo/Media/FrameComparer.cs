using ChulipVideo.Core;

namespace ChulipVideo.Media;

/// <summary>同じ寸法のBGRA画素を比べる。圧縮ノイズを許すため、色差しきい値を超えた画素の割合で判定する。</summary>
public static class FrameComparer
{
    /// <summary>RGBの差の最大値がしきい値を超えた画素の割合。αは比べない。</summary>
    public static double ChangedRatio(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int channelThreshold)
    {
        Validate(a, b);
        if (a.Length == 0 || a.SequenceEqual(b)) return 0;
        return CountChanged(a, b, channelThreshold, long.MaxValue) / (double)(a.Length / 4);
    }

    public static bool IsSame(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, StabilitySettings settings)
    {
        Validate(a, b);
        if (a.SequenceEqual(b)) return true;
        if (settings.IsExact) return false;
        long allowed = (long)Math.Floor(a.Length / 4 * settings.MaxChangedRatio);
        return CountChanged(a, b, settings.ChannelThreshold, allowed) <= allowed;
    }

    // 上限を超えた時点で数えるのをやめ、静止判定を全画素の比較より早く終える。
    private static long CountChanged(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int threshold, long stopAbove)
    {
        long changed = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int delta = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            if (delta > threshold && ++changed > stopAbove) return changed;
        }
        return changed;
    }

    private static void Validate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.Length % 4 != 0) throw new ArgumentException("比べる画像の寸法が一致しません。");
    }
}
