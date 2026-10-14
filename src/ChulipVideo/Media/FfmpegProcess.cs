using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ChulipVideo.Media;

internal sealed class FfmpegProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _errors = new();
    private readonly Task _errorReader;
    private readonly int _maxErrorChars;
    private bool _disposed;
    public string Errors { get { lock (_errors) return _errors.ToString(); } }
    public Stream Output => _process.StandardOutput.BaseStream;

    private FfmpegProcess(Process process, int maxErrorChars)
    {
        _process = process;
        _maxErrorChars = maxErrorChars;
        _errorReader = ReadErrorsAsync();
    }

    public static FfmpegProcess Start(string executable, IEnumerable<string> arguments, int maxErrorChars = 65536)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("FFmpeg を起動できませんでした。");
            return new FfmpegProcess(process, maxErrorChars);
        }
        catch { process.Dispose(); throw; }
    }

    public async Task WaitForSuccessAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using CancellationTokenRegistration registration = timeout.Token.Register(Kill);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await _errorReader.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (_process.ExitCode != 0)
                throw new IOException($"動画処理に失敗しました（FFmpeg 終了コード {_process.ExitCode}）。{Environment.NewLine}{Errors}");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("動画処理が 30 秒以内に応答しませんでした。", ex); }
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException) { }
    }

    private async Task ReadErrorsAsync()
    {
        char[] chars = new char[4096];
        try
        {
            while (true)
            {
                int count = await _process.StandardError.ReadAsync(chars).ConfigureAwait(false);
                if (count == 0) return;
                lock (_errors)
                {
                    _errors.Append(chars, 0, count);
                    if (_errors.Length > _maxErrorChars) _errors.Remove(0, _errors.Length - _maxErrorChars);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _errorReader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException) { }
        finally { _process.Dispose(); }
    }
}
