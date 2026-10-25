namespace ChulipVideo.Core;

/// <summary>設定の色を #RRGGBB に限定して扱う。不正な値で描画や読込を止めない。</summary>
public static class ColorText
{
    public static bool IsValid(string? value, bool optional = false)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0) return optional;
        return text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
    }

    /// <summary>空欄は transparentWhenEmpty なら透明、それ以外と不正値は fallback を返す。</summary>
    public static Color Parse(string? value, Color fallback, bool transparentWhenEmpty = false)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0) return transparentWhenEmpty ? Color.Empty : fallback;
        if (!IsValid(text)) return fallback;
        return Color.FromArgb(255, Convert.ToByte(text[1..3], 16), Convert.ToByte(text[3..5], 16), Convert.ToByte(text[5..7], 16));
    }

    /// <summary>保存値を検証し、不正なら既定値へ戻す。</summary>
    public static string Normalize(string? value, string fallback, bool optional = false) =>
        IsValid(value, optional) ? value?.Trim() ?? "" : fallback;
}
