using ChulipVideo.Core;

namespace ChulipVideo.Media;

public sealed class PreviewReader : IAsyncDisposable
{
    private readonly FfmpegService _service;
    private readonly MediaInfo _info;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private FrameReader? _reader;
    private Bitmap? _lastFrame;
    private long _frameIndex = -1;
    private bool _terminalFrame;
    public int Width { get; }
    public int Height { get; }

    public PreviewReader(FfmpegService service, MediaInfo info)
    {
        _service = service; _info = info;
        double scale = Math.Min(1, Math.Min(1280d / info.Width, 1280d / info.Height));
        Width = Math.Max(1, (int)Math.Round(info.Width * scale));
        Height = Math.Max(1, (int)Math.Round(info.Height * scale));
    }

    public async Task<Bitmap> ReadAsync(double seconds, CancellationToken ct = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            double time = FfmpegService.NormalizeTime(_info, seconds);
            bool terminal = time >= _info.LastFrameTime - 1e-9;
            if (terminal)
            {
                if (!_terminalFrame || _lastFrame is null)
                {
                    if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
                    _reader = _service.OpenReaderCore(_info, time, _info.Duration, Width, Height, 1);
                    Bitmap frame = await _reader.ReadAsync(linked.Token).ConfigureAwait(false)
                        ?? throw new IOException("動画の最終フレームを取得できませんでした。");
                    _lastFrame?.Dispose(); _lastFrame = frame;
                    _terminalFrame = true;
                }
                return (Bitmap)_lastFrame.Clone();
            }
            long target = (long)Math.Floor(time * _info.Fps + 1e-7);
            if (_reader is null || _terminalFrame || target < _frameIndex || target - _frameIndex > Math.Max(1, _info.Fps / 2))
            {
                if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
                _reader = _service.OpenReaderCore(_info, target / _info.Fps, _info.Duration, Width, Height);
                _frameIndex = target - 1;
                _terminalFrame = false;
                _lastFrame?.Dispose(); _lastFrame = null;
            }
            while (_frameIndex < target)
            {
                Bitmap? frame = await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    if (_lastFrame is null) throw new IOException("指定した位置にプレビューフレームがありません。");
                    _frameIndex = target;
                    break;
                }
                _lastFrame?.Dispose(); _lastFrame = frame; _frameIndex++;
            }
            return (Bitmap)(_lastFrame ?? throw new IOException("プレビューフレームがありません。")).Clone();
        }
        catch
        {
            if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
            _terminalFrame = false;
            throw;
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null; _lastFrame?.Dispose(); _lastFrame = null;
        }
        finally { _gate.Release(); }
    }
}
