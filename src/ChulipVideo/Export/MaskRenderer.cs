using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ChulipVideo.Core;

namespace ChulipVideo.Export;

/// <summary>元動画の座標で指定したマスク（モザイク・ぼかし・塗りつぶし）を映像へ描く。</summary>
public static class MaskRenderer
{
    public static bool HasActive(IReadOnlyList<MaskRegion> masks, double seconds) => masks.Any(m => m.IsActive(seconds));

    /// <summary>
    /// 有効なマスクを描いた新しい画像を返す。元の画像は変更しない。
    /// scaleX/scaleYは元動画に対する画像の倍率（縮小プレビューでは1未満）。
    /// </summary>
    public static Bitmap Apply(Bitmap frame, IReadOnlyList<MaskRegion> masks, double seconds, float scaleX = 1, float scaleY = 1)
    {
        var result = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(frame, 0, 0);
            foreach (var mask in masks.Where(m => m.IsActive(seconds)))
            {
                var area = Rectangle.Intersect(new Rectangle(0, 0, frame.Width, frame.Height), Rectangle.FromLTRB(
                    (int)Math.Floor(mask.Rect.X * scaleX), (int)Math.Floor(mask.Rect.Y * scaleY),
                    (int)Math.Ceiling((mask.Rect.X + mask.Rect.Width) * scaleX), (int)Math.Ceiling((mask.Rect.Y + mask.Rect.Height) * scaleY)));
                if (area.Width <= 0 || area.Height <= 0) continue;
                int strength = Math.Max(2, (int)Math.Round(mask.Strength * Math.Min(scaleX, scaleY)));
                switch (mask.Mode)
                {
                    case MaskMode.Fill:
                        using (var graphics = Graphics.FromImage(result))
                        using (var brush = new SolidBrush(ColorText.Parse(mask.Color, System.Drawing.Color.Black)))
                            graphics.FillRectangle(brush, area);
                        break;
                    case MaskMode.Blur: Process(result, area, pixels => Blur(pixels, area.Width, area.Height, strength)); break;
                    default: Process(result, area, pixels => Mosaic(pixels, area.Width, area.Height, strength)); break;
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void Process(Bitmap bitmap, Rectangle area, Action<byte[]> transform)
    {
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int row = area.Width * 4;
            var pixels = new byte[row * area.Height];
            for (int y = 0; y < area.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * row, row);
            transform(pixels);
            for (int y = 0; y < area.Height; y++) Marshal.Copy(pixels, y * row, data.Scan0 + y * data.Stride, row);
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>ブロックごとの平均色で塗る。最低でも矩形の短辺の1/8の大きさにし、細部を読めなくする。</summary>
    private static void Mosaic(byte[] pixels, int width, int height, int strength)
    {
        int block = Math.Max(strength, Math.Max(2, Math.Min(width, height) / 8));
        for (int by = 0; by < height; by += block)
            for (int bx = 0; bx < width; bx += block)
            {
                int w = Math.Min(block, width - bx), h = Math.Min(block, height - by);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < by + h; y++)
                    for (int x = bx; x < bx + w; x++) { int i = (y * width + x) * 4; b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
                int count = w * h;
                for (int y = by; y < by + h; y++)
                    for (int x = bx; x < bx + w; x++)
                    {
                        int i = (y * width + x) * 4;
                        pixels[i] = (byte)(b / count); pixels[i + 1] = (byte)(g / count); pixels[i + 2] = (byte)(r / count); pixels[i + 3] = 255;
                    }
            }
    }

    /// <summary>横・縦のボックスぼかしを3回ずつ重ね、ガウスぼかしに近づける。</summary>
    private static void Blur(byte[] pixels, int width, int height, int radius)
    {
        var buffer = new byte[pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxPass(pixels, buffer, width, height, radius, horizontal: true);
            BoxPass(buffer, pixels, width, height, radius, horizontal: false);
        }
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
    }

    private static void BoxPass(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
            for (int channel = 0; channel < 3; channel++)
            {
                int Index(int position) => (horizontal ? line * width + position : position * width + line) * 4 + channel;
                int sum = 0;
                // 端は端の画素を延長して扱う。
                for (int k = -radius; k <= radius; k++) sum += source[Index(Math.Clamp(k, 0, length - 1))];
                for (int position = 0; position < length; position++)
                {
                    target[Index(position)] = (byte)(sum / (2 * radius + 1));
                    sum += source[Index(Math.Min(length - 1, position + radius + 1))] - source[Index(Math.Max(0, position - radius))];
                }
            }
    }
}
