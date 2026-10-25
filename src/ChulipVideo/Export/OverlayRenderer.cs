using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ChulipVideo.Core;

namespace ChulipVideo.Export;

public static class OverlayRenderer
{
    public static Bitmap Render(Bitmap frame, CropRect crop, double seconds, EventLog events, OverlayOptions options, bool overlay = true)
    {
        if (crop.Width <= 0 || crop.Height <= 0 || crop.Width > 16384 || crop.Height > 16384)
            throw new ArgumentOutOfRangeException(nameof(crop), "クロップ幅・高さは1～16384ピクセルで指定してください。");
        var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Black);
            Rectangle source = Rectangle.Intersect(crop.Rectangle, new(0, 0, frame.Width, frame.Height));
            if (source.Width > 0 && source.Height > 0)
                graphics.DrawImage(frame, new Rectangle(source.X - crop.X, source.Y - crop.Y, source.Width, source.Height), source, GraphicsUnit.Pixel);
            if (overlay) Draw(graphics, seconds, events, options, 1, 1, -crop.X, -crop.Y, new(0, 0, crop.Width, crop.Height));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static Bitmap RenderPreview(Bitmap frame, MediaInfo info, double seconds, EventLog events, OverlayOptions options, CropRect? crop = null)
    {
        var result = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImageUnscaled(frame, 0, 0);
            float sx = (float)frame.Width / info.Width, sy = (float)frame.Height / info.Height;
            CropRect rect = crop ?? CropRect.Full(info);
            Draw(graphics, seconds, events, options, sx, sy, 0, 0, new(rect.X * sx, rect.Y * sy, rect.Width * sx, rect.Height * sy));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void Draw(Graphics graphics, double seconds, EventLog events, OverlayOptions options,
        float sx, float sy, float offsetX, float offsetY, RectangleF captionArea)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = (sx + sy) / 2;
        InputEvent? current = events.AtOrBefore(seconds);
        if (current is not null)
        {
            float x = (current.X + offsetX) * sx, y = (current.Y + offsetY) * sy;
            if (options.ShowClicks)
            {
                foreach (string button in EventLog.MouseButtons(current.Click))
                    if (button.EndsWith("-Click", StringComparison.Ordinal))
                        Shape(graphics, options.ClickShape, x, y, 12 * scale, ClickColor(options, button), Color.Empty, Math.Max(.5f, options.ClickWidth * scale), 0);
                double duration = Math.Max(.01, options.RippleDuration);
                foreach (var ripple in events.ActiveRipples(seconds, duration))
                {
                    double fraction = (seconds - ripple.Timestamp) / duration;
                    Color color = Color.FromArgb((int)(255 * (1 - fraction)), ClickColor(options, ripple.Button));
                    Shape(graphics, options.ClickShape, (ripple.X + offsetX) * sx, (ripple.Y + offsetY) * sy,
                        (12 + (float)fraction * options.RippleSize) * scale, color, Color.Empty, Math.Max(.5f, options.ClickWidth * scale), (float)(fraction * 180));
                }
            }
            if (options.ShowPointer)
                Shape(graphics, options.PointerShape, x, y, Math.Max(1, options.PointerSize) * scale,
                    ParseColor(options.PointerColor, Color.Black, true), ParseColor(options.PointerFill, Color.Empty, true),
                    Math.Max(.5f, options.PointerWidth * scale), 0);
        }
        if (options.ShowKeys) DrawCaptions(graphics, seconds, events, options, scale, captionArea);
    }

    private static void DrawCaptions(Graphics graphics, double seconds, EventLog events, OverlayOptions options, float scale, RectangleF area)
    {
        var captions = events.ActiveCaptions(seconds, options.FadeDuration, options.MaxStack);
        if (captions.Count == 0 || area.Width < 1 || area.Height < 1) return;
        using var font = CreateFont(options.FontFamily, Math.Max(1, options.FontSize * scale));
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        float py = Math.Max(0, options.PaddingY) * scale, px = 8 * scale, gap = 8 * scale;
        float lineHeight = font.GetHeight(graphics) + 2 * py;
        float totalHeight = captions.Count * lineHeight + (captions.Count - 1) * gap;
        float y = options.Vertical switch
        {
            "top" => area.Top + 20 * scale + totalHeight - lineHeight,
            "center" => area.Top + (area.Height - totalHeight) / 2,
            _ => area.Bottom - 20 * scale - totalHeight
        };
        y += options.OffsetY * scale;
        var saved = graphics.Save();
        graphics.SetClip(area, CombineMode.Intersect);
        try
        {
            foreach (var caption in captions)
            {
                double age = Math.Max(0, seconds - caption.HeldUntil);
                double fade = Math.Max(.01, options.FadeDuration);
                double fadeWidth = Math.Min(.5, fade);
                double opacity = Math.Clamp((fade - age) / fadeWidth, 0, 1);
                float width = graphics.MeasureString(caption.Text, font, int.MaxValue, format).Width + 2 * px;
                float x = options.Horizontal switch
                {
                    "left" => area.Left + 20 * scale,
                    "right" => area.Right - 20 * scale - width,
                    _ => area.Left + (area.Width - width) / 2
                };
                x += options.OffsetX * scale;
                var box = new RectangleF(x, y, width, lineHeight);
                Color background = ParseColor(options.BackgroundColor, Color.Empty, true);
                if (!background.IsEmpty)
                {
                    using var brush = new SolidBrush(Color.FromArgb((int)(200 * opacity), background));
                    graphics.FillRectangle(brush, box);
                }
                Color outline = ParseColor(options.OutlineColor, Color.Empty, true);
                if (caption.Text.Contains("左", StringComparison.Ordinal) && caption.Text.Contains("クリック", StringComparison.Ordinal)) outline = ClickColor(options, "L-Click");
                else if (caption.Text.Contains("右", StringComparison.Ordinal) && caption.Text.Contains("クリック", StringComparison.Ordinal)) outline = ClickColor(options, "R-Click");
                else if (caption.Text.Contains("中", StringComparison.Ordinal) && caption.Text.Contains("クリック", StringComparison.Ordinal)) outline = ClickColor(options, "M-Click");
                if (!outline.IsEmpty)
                {
                    using var pen = new Pen(Color.FromArgb((int)(255 * opacity), outline), Math.Max(.5f, scale));
                    graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                }
                Color color = ParseColor(options.TextColor, Color.White, true);
                if (!color.IsEmpty)
                {
                    using var brush = new SolidBrush(Color.FromArgb((int)(255 * opacity), color));
                    graphics.DrawString(caption.Text, font, brush, x + px, y + py + options.TextOffsetY * scale, format);
                }
                y += options.Vertical == "top" ? -(lineHeight + gap) : lineHeight + gap;
            }
        }
        finally { graphics.Restore(saved); }
    }

    private static Font CreateFont(string name, float size)
    {
        string normalized = name.ToLowerInvariant() switch { "msgothic.ttc" => "MS Gothic", "meiryo.ttc" => "Meiryo", "arialbd.ttf" => "Arial", _ => name };
        try { return new Font(normalized, size, FontStyle.Bold, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Pixel); }
    }

    private static Color ClickColor(OverlayOptions options, string button) => ParseColor(button switch
    {
        "L-Click" => options.LeftColor, "R-Click" => options.RightColor, "M-Click" => options.MiddleColor, _ => "#CC44CC"
    }, Color.Red);

    private static Color ParseColor(string value, Color fallback, bool transparent = false) => ColorText.Parse(value, fallback, transparent);

    private static void Shape(Graphics graphics, string shape, float x, float y, float radius, Color outline, Color fill, float width, float rotation)
    {
        using var path = new GraphicsPath();
        if (shape == "cursor")
        {
            float s = radius / 10;
            path.AddPolygon(new PointF[] { new(x,y), new(x,y+17*s), new(x+4*s,y+13*s), new(x+9*s,y+22*s), new(x+12*s,y+20*s), new(x+7*s,y+11*s), new(x+12*s,y+12*s) });
            if (fill.IsEmpty) fill = Color.White;
        }
        else if (shape == "square") path.AddRectangle(new RectangleF(x - radius, y - radius, radius * 2, radius * 2));
        else if (shape is "star" or "rote_star")
        {
            var points = new PointF[10];
            for (int i = 0; i < points.Length; i++)
            {
                double angle = (i * 36 - 90 + (shape == "rote_star" ? 36 + rotation : 0)) * Math.PI / 180;
                float distance = i % 2 == 0 ? radius : radius * .45f;
                points[i] = new(x + distance * (float)Math.Cos(angle), y + distance * (float)Math.Sin(angle));
            }
            path.AddPolygon(points);
        }
        else path.AddEllipse(x - radius, y - radius, radius * 2, radius * 2);
        if (!fill.IsEmpty) { using var brush = new SolidBrush(fill); graphics.FillPath(brush, path); }
        if (!outline.IsEmpty) { using var pen = new Pen(outline, width); graphics.DrawPath(pen, path); }
    }
}
