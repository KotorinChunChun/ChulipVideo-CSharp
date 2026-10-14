using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ChulipVideo.Export;

internal static class BitmapPixels
{
    public static byte[] Bgra(Bitmap image)
    {
        byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * image.Width * 4, image.Width * 4);
        }
        finally { image.UnlockBits(data); }
        return pixels;
    }
}

/// <summary>PNGの可逆圧縮レベル0～9をそのままzlibへ渡す。</summary>
internal static class PngEncoder
{
    public static void Save(Bitmap image, string path, int compression)
    {
        if (compression is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(compression));
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) throw new IOException("保存先は既に存在します: " + fullPath);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                Span<byte> header = stackalloc byte[13];
                BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
                BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
                header[8] = 8; header[9] = 6; // RGBA、8ビット
                header[10] = 0; header[11] = 0; header[12] = 0;
                Chunk(output, "IHDR", header);
                byte[] pixels = BitmapPixels.Bgra(image);
                using var compressed = new MemoryStream();
                using (var zlib = new ZLibStream(compressed, new ZLibCompressionOptions { CompressionLevel = compression }, true))
                {
                    byte[] row = new byte[1 + image.Width * 4];
                    for (int y = 0; y < image.Height; y++)
                    {
                        // Subフィルターで横方向の類似色を利用する。圧縮率に依らず画素は同一。
                        row[0] = 1;
                        for (int x = 0; x < image.Width; x++)
                        {
                            int input = (y * image.Width + x) * 4, left = input - 4, target = 1 + x * 4;
                            row[target] = unchecked((byte)(pixels[input + 2] - (x == 0 ? 0 : pixels[left + 2])));
                            row[target + 1] = unchecked((byte)(pixels[input + 1] - (x == 0 ? 0 : pixels[left + 1])));
                            row[target + 2] = unchecked((byte)(pixels[input] - (x == 0 ? 0 : pixels[left])));
                            row[target + 3] = unchecked((byte)(pixels[input + 3] - (x == 0 ? 0 : pixels[left + 3])));
                        }
                        zlib.Write(row);
                    }
                }
                Chunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
                Chunk(output, "IEND", []);
            }
            File.Move(temporary, fullPath, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Chunk(Stream stream, string kind, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] type = Encoding.ASCII.GetBytes(kind);
        stream.Write(type); stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in type) crc = Update(crc, b);
        foreach (byte b in data) crc = Update(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        return crc;
    }
}
