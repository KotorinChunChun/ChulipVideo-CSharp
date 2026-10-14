using System.Globalization;
using System.Text.RegularExpressions;
using ChulipVideo.Core;

namespace ChulipVideo.Media;

public sealed class FfmpegService
{
    public string Executable { get; }

    public FfmpegService(string executable)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("FFmpeg が見つかりません。", executable);
        Executable = Path.GetFullPath(executable);
    }

    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        string? environmentPath = Environment.GetEnvironmentVariable("CHULIPVIDEO_FFMPEG");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath)) return Path.GetFullPath(environmentPath);
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.CurrentDirectory, ".local", "ffmpeg.exe")
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, ".local", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0) continue;
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory), "ffmpeg.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("動画が見つかりません。", path);
        // 自動回転後に SAR を正方形画素へ補正し、実際の出力ストリームから表示サイズを取得する。
        await using FfmpegProcess process = FfmpegProcess.Start(Executable,
        [
            "-hide_banner", "-nostdin", "-threads", "1", "-filter_threads", "1", "-i", Path.GetFullPath(path),
            "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "scale=round(iw*max(sar\\,1)):round(ih/max(min(sar\\,1)\\,0.000001)),setsar=1",
            "-frames:v", "1", "-f", "null", "NUL"
        ]);
        await process.WaitForSuccessAsync(ct).ConfigureAwait(false);
        string text = process.Errors;
        Match duration = Regex.Match(text, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        int outputIndex = text.IndexOf("Output #0", StringComparison.Ordinal);
        string inputText = outputIndex < 0 ? text : text[..outputIndex];
        string outputText = outputIndex < 0 ? "" : text[outputIndex..];
        Match inputVideo = Regex.Match(inputText, @"Stream[^\r\n]*Video:[^\r\n]*", RegexOptions.CultureInvariant);
        Match outputVideo = Regex.Match(outputText, @"Stream[^\r\n]*Video:[^\r\n]*", RegexOptions.CultureInvariant);
        Match dimensions = Regex.Match(outputVideo.Value, @"[, ](\d{1,6})x(\d{1,6})(?:[, \[])", RegexOptions.CultureInvariant);
        Match fps = Regex.Match(inputVideo.Value, @"[, ](\d+(?:\.\d+)?)(?:/(\d+(?:\.\d+)?))?\s+fps", RegexOptions.CultureInvariant);
        if (!fps.Success) fps = Regex.Match(inputVideo.Value, @"[, ](\d+(?:\.\d+)?)(?:/(\d+(?:\.\d+)?))?\s+tbr", RegexOptions.CultureInvariant);
        if (!duration.Success || !dimensions.Success || !fps.Success)
            throw new IOException("動画の長さ・表示サイズ・フレームレートを取得できませんでした。");
        double seconds = Parse(duration.Groups[1].Value) * 3600 + Parse(duration.Groups[2].Value) * 60 + Parse(duration.Groups[3].Value);
        double frameRate = Parse(fps.Groups[1].Value) / (fps.Groups[2].Success ? Parse(fps.Groups[2].Value) : 1);
        // FFmpeg の表示で丸められる NTSC レートを正確な有理数へ戻す。
        if (Math.Abs(frameRate - 23.98) < 0.0001) frameRate = 24000d / 1001;
        else if (Math.Abs(frameRate - 29.97) < 0.0001) frameRate = 30000d / 1001;
        else if (Math.Abs(frameRate - 59.94) < 0.0001) frameRate = 60000d / 1001;
        int width = int.Parse(dimensions.Groups[1].Value, CultureInfo.InvariantCulture);
        int height = int.Parse(dimensions.Groups[2].Value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(frameRate) || frameRate is <= 0 or > 1000
            || width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 67_108_864)
            throw new IOException("対応できる動画の長さ・表示サイズ・フレームレートではありません。");
        // 240 fps の1フレームなど、表示上 0.00 秒となっても先頭のデコードに成功していれば動画として扱う。
        MediaInfo info = new(Path.GetFullPath(path), width, height, Math.Max(seconds, 1 / frameRate), frameRate);
        return await VerifyEndAsync(info, ct).ConfigureAwait(false);
    }

    private async Task<MediaInfo> VerifyEndAsync(MediaInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Duration 表示は小数2桁なので、末尾1秒だけを読み、整数 PTS と time_base から終端を確定する。
        // 大きな GOP や壊れた索引で長時間待たないよう、5秒と3000フレームの両方で上限を設ける。
        const int maximumFrames = 3000;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using FfmpegProcess tail = FfmpegProcess.Start(Executable,
            [
                "-hide_banner", "-nostdin", "-threads", "1", "-filter_threads", "1",
                "-sseof", "-1", "-copyts", "-start_at_zero", "-i", info.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", "scale=2:2,showinfo=checksum=0", "-fps_mode", "passthrough", "-frames:v", maximumFrames.ToString(CultureInfo.InvariantCulture), "-f", "null", "NUL"
            ], 2_097_152);
            await tail.WaitForSuccessAsync(timeout.Token).ConfigureAwait(false);
            string errors = tail.Errors;
            Match timeBase = Regex.Match(errors, @"config in time_base:\s*(\d+)/(\d+)", RegexOptions.CultureInvariant);
            Match exactRate = Regex.Match(errors, @"config in time_base:[^\r\n]*frame_rate:\s*(\d+)/(\d+)", RegexOptions.CultureInvariant);
            if (exactRate.Success)
            {
                double rate = Parse(exactRate.Groups[1].Value) / Parse(exactRate.Groups[2].Value);
                if (double.IsFinite(rate) && rate is > 0 and <= 1000) info = info with { Fps = rate };
            }
            MatchCollection frames = Regex.Matches(errors, @"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:\S+\s+duration:\s*(\d+)", RegexOptions.CultureInvariant);
            if (timeBase.Success && frames.Count is > 0 and < maximumFrames)
            {
                double tick = Parse(timeBase.Groups[1].Value) / Parse(timeBase.Groups[2].Value);
                Match last = frames[^1];
                double timestamp = Parse(last.Groups[2].Value) * tick;
                double frameDuration = Parse(last.Groups[3].Value) * tick;
                if (frameDuration <= 0 || !double.IsFinite(frameDuration)) frameDuration = 1 / info.Fps;
                if (double.IsFinite(timestamp) && timestamp >= 0 && timestamp <= info.Duration + Math.Max(2, 2 / info.Fps))
                    return info with { Duration = timestamp + frameDuration, FinalFrameTime = timestamp };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        ct.ThrowIfCancellationRequested();
        double estimatedFrames = Math.Max(1, Math.Round(info.Duration * info.Fps));
        return info with
        {
            Duration = estimatedFrames / info.Fps,
            TimingWarning = "終端の PTS を検証できなかったため、一定フレームレートとして最終位置を推定しています。"
        };
    }

    public async Task<Bitmap> ReadFrameAsync(MediaInfo info, double seconds, CancellationToken ct = default)
    {
        seconds = NormalizeTime(info, seconds);
        await using FrameReader reader = OpenReaderCore(info, seconds, info.Duration, info.Width, info.Height, 1);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("指定した位置に動画フレームがありません。");
    }

    /// <summary>開始位置を含み終了位置を含まない範囲を、元の FPS と表示解像度で読み取る。</summary>
    public FrameReader OpenReader(MediaInfo info, double start, double end) =>
        OpenReaderCore(info, start, end, info.Width, info.Height);

    internal FrameReader OpenReaderCore(MediaInfo info, double start, double end, int width, int height, int? frameLimit = null)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || start >= info.Duration)
            throw new ArgumentOutOfRangeException(nameof(start), "読み取り範囲は動画内の正の時間幅にしてください。");
        end = Math.Min(end, info.Duration);
        long count = frameLimit ?? Math.Max(1L, (long)Math.Ceiling((end - start) * info.Fps - 1e-7));
        string frameFilter = frameLimit == 1 ? "" : $"fps={Number(info.Fps)},";
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-filter_threads", "1",
            "-ss", Number(Math.Max(0, NormalizeTime(info, start) - 0.000001)), "-i", info.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"{frameFilter}scale={width}:{height},setsar=1", "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-threads:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ];
        return new FrameReader(FfmpegProcess.Start(Executable, args), width, height, count);
    }

    internal static double NormalizeTime(MediaInfo info, double seconds) => info.FrameTime(seconds);
    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}
