using System.Drawing.Drawing2D;
using ChulipVideo.Core;

namespace ChulipVideo.Export;

/// <summary>手順書の注釈を描く。元動画の座標pを、画像上の (p + offset) × scale へ写す。</summary>
public static class AnnotationRenderer
{
    public static void Draw(Graphics g, IEnumerable<Annotation> annotations, PointF offset, float scale, int stepNumber, Annotation? selected = null)
    {
        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // 強調（周囲を暗くする）は他の注釈の下に描く。
            var ordered = annotations.OrderBy(a => a.Kind == AnnotationKind.Spotlight ? 0 : 1).ToArray();
            foreach (var a in ordered) DrawOne(g, a, offset, scale, stepNumber);
            if (selected is not null && ordered.Contains(selected))
            {
                using var pen = new Pen(Color.FromArgb(0, 150, 255), 1) { DashStyle = DashStyle.Dash };
                var bounds = Bounds(selected, offset, scale, g);
                g.DrawRectangle(pen, bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6);
            }
        }
        finally { g.Restore(state); }
    }

    private static PointF Map(float x, float y, PointF offset, float scale) => new((x + offset.X) * scale, (y + offset.Y) * scale);

    private static void DrawOne(Graphics g, Annotation a, PointF offset, float scale, int stepNumber)
    {
        var color = ColorText.Parse(a.Color, Color.Red);
        float width = Math.Max(1, a.LineWidth * scale);
        var p = Map(a.X, a.Y, offset, scale);
        switch (a.Kind)
        {
            case AnnotationKind.Box:
            {
                using var pen = new Pen(color, width);
                g.DrawRectangle(pen, p.X, p.Y, a.Width * scale, a.Height * scale);
                break;
            }
            case AnnotationKind.Spotlight:
            {
                using var region = new Region(g.VisibleClipBounds);
                region.Exclude(new RectangleF(p.X, p.Y, a.Width * scale, a.Height * scale));
                using var shade = new SolidBrush(Color.FromArgb(120, Color.Black));
                g.FillRegion(shade, region);
                break;
            }
            case AnnotationKind.Arrow:
            {
                using var pen = new Pen(color, width) { CustomEndCap = new AdjustableArrowCap(4, 4), StartCap = LineCap.Round };
                g.DrawLine(pen, p, Map(a.EndX, a.EndY, offset, scale));
                break;
            }
            case AnnotationKind.Badge:
            {
                float radius = Math.Max(6, a.FontSize * .9f * scale);
                using var fill = new SolidBrush(color);
                using var border = new Pen(Color.White, Math.Max(1, 2 * scale));
                g.FillEllipse(fill, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                g.DrawEllipse(border, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                using var font = new Font("Yu Gothic UI", Math.Max(6, a.FontSize * scale), FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(stepNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), font, Brushes.White, new RectangleF(p.X - radius, p.Y - radius, radius * 2, radius * 2), format);
                break;
            }
            case AnnotationKind.Text when a.Text.Length > 0:
            {
                using var font = new Font("Yu Gothic UI", Math.Max(6, a.FontSize * scale), FontStyle.Bold, GraphicsUnit.Pixel);
                var size = g.MeasureString(a.Text, font);
                using var background = new SolidBrush(Color.FromArgb(225, Color.White));
                using var text = new SolidBrush(color);
                g.FillRectangle(background, p.X - 4 * scale, p.Y - 2 * scale, size.Width + 8 * scale, size.Height + 4 * scale);
                g.DrawString(a.Text, font, text, p);
                break;
            }
        }
    }

    /// <summary>画像上での注釈の外接矩形（選択・当たり判定用）。</summary>
    public static RectangleF Bounds(Annotation a, PointF offset, float scale, Graphics? measure = null)
    {
        var p = Map(a.X, a.Y, offset, scale);
        switch (a.Kind)
        {
            case AnnotationKind.Box or AnnotationKind.Spotlight: return new(p.X, p.Y, a.Width * scale, a.Height * scale);
            case AnnotationKind.Arrow:
                var e = Map(a.EndX, a.EndY, offset, scale);
                return RectangleF.FromLTRB(Math.Min(p.X, e.X), Math.Min(p.Y, e.Y), Math.Max(p.X, e.X), Math.Max(p.Y, e.Y));
            case AnnotationKind.Badge:
                float radius = Math.Max(6, a.FontSize * .9f * scale);
                return new(p.X - radius, p.Y - radius, radius * 2, radius * 2);
            default:
                using (var font = new Font("Yu Gothic UI", Math.Max(6, a.FontSize * scale), FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var size = measure?.MeasureString(a.Text.Length > 0 ? a.Text : " ", font) ?? TextRenderer.MeasureText(a.Text.Length > 0 ? a.Text : " ", font);
                    return new(p.X, p.Y, size.Width, size.Height);
                }
        }
    }
}
