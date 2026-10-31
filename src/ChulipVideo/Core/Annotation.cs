using System.Text.Json.Serialization;

namespace ChulipVideo.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AnnotationKind>))]
public enum AnnotationKind { Badge, Box, Arrow, Text, Spotlight }

/// <summary>
/// 手順書の画像へ重ねる注釈。座標は元動画のピクセル。
/// Badgeは(X,Y)に手順番号、Box・Spotlightは(X,Y,Width,Height)、Arrowは(X,Y)→(EndX,EndY)、Textは(X,Y)に文字。
/// </summary>
public sealed record Annotation
{
    public AnnotationKind Kind { get; set; } = AnnotationKind.Box;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int EndX { get; set; }
    public int EndY { get; set; }
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#E53935";
    public int LineWidth { get; set; } = 3;
    public int FontSize { get; set; } = 18;

    public static List<Annotation> Normalize(IEnumerable<Annotation?>? annotations, int width, int height)
    {
        var result = new List<Annotation>();
        foreach (var a in annotations ?? [])
        {
            if (a is null || !Enum.IsDefined(a.Kind)) continue;
            a.X = Math.Clamp(a.X, 0, Math.Max(0, width - 1)); a.Y = Math.Clamp(a.Y, 0, Math.Max(0, height - 1));
            a.EndX = Math.Clamp(a.EndX, 0, Math.Max(0, width - 1)); a.EndY = Math.Clamp(a.EndY, 0, Math.Max(0, height - 1));
            a.Width = Math.Clamp(a.Width, 0, width - a.X); a.Height = Math.Clamp(a.Height, 0, height - a.Y);
            a.Text ??= ""; a.Color = ColorText.Normalize(a.Color, "#E53935");
            a.LineWidth = Math.Clamp(a.LineWidth, 1, 32); a.FontSize = Math.Clamp(a.FontSize, 8, 96);
            result.Add(a);
        }
        return result;
    }
}
