using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ChulipVideo.Media;

public sealed class FrameReader : IAsyncDisposable
{
    private readonly FfmpegProcess _process;
    private readonly byte[] _pixels;
    private readonly long _frameLimit;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private long _readCount;
    private bool _ended;
    public int Width { get; }
    public int Height { get; }

    internal FrameReader(FfmpegProcess process, int width, int height, long frameLimit)
    {
        _process = process;
        Width = width; Height = height; _frameLimit = frameLimit;
        _pixels = new byte[checked(width * height * 4)];
    }

    public async Task<Bitmap?> ReadAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using CancellationTokenRegistration cancellation = linked.Token.Register(_process.Kill);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_ended) return null;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using CancellationTokenRegistration registration = timeout.Token.Register(_process.Kill);
            int offset = 0;
            try
            {
                while (offset < _pixels.Length)
                {
                    int count = await _process.Output.ReadAsync(_pixels.AsMemory(offset), timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    if (count == 0)
                    {
                        await _process.WaitForSuccessAsync(timeout.Token).ConfigureAwait(false);
                        _ended = true;
                        if (offset != 0) throw new IOException("動画のフレームが途中で途切れました。");
                        return null;
                    }
                    offset += count;
                }
                _readCount++;
                if (_readCount >= _frameLimit)
                {
                    await _process.WaitForSuccessAsync(timeout.Token).ConfigureAwait(false);
                    _ended = true;
                }
                return CreateBitmap(_pixels, Width, Height);
            }
            catch (OperationCanceledException ex) when (!linked.IsCancellationRequested)
            { throw new TimeoutException("動画フレームの取得が 30 秒以内に完了しませんでした。", ex); }
            catch { _process.Kill(); throw; }
        }
        finally { _gate.Release(); }
    }

    private static Bitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowLength = width * 4;
                for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * rowLength, data.Scan0 + y * data.Stride, rowLength);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _process.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
