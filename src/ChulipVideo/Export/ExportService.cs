using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChulipVideo.Core;
using ChulipVideo.Media;

namespace ChulipVideo.Export;

public sealed class ExportService(FfmpegService media)
{
    public static void SavePng(Bitmap image, string path, int compression) => PngEncoder.Save(image, path, compression);

    public Task<ExportResult> ExportAsync(ExportRequest request, EventLog events, IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(() => ExportCoreAsync(request, events, progress, ct), ct);

    private async Task<ExportResult> ExportCoreAsync(ExportRequest request, EventLog events, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(request.Start) || !double.IsFinite(request.End) || request.Start < 0 || request.Start >= request.Media.Duration || request.End <= request.Start || request.End > request.Media.Duration + .01)
            throw new ArgumentException("書き出し範囲は動画内の開始時刻から終了時刻までを指定してください。");
        // プレビューと同じフレームを起点とし、区間の終了は次の格子まで広げる。
        // 元要求を検証してから揃えることで、空区間や動画外の開始を最後のフレームへ変換しない。
        double effectiveStart = request.Media.FrameTime(request.Start);
        double frameCount = Math.Max(1, Math.Ceiling((request.End - effectiveStart) * request.Media.Fps - 1e-7));
        double effectiveEnd = Math.Min(request.Media.Duration, effectiveStart + frameCount / request.Media.Fps);
        request = request with { Start = effectiveStart, End = effectiveEnd };
        if (request.Crop.Width is < 1 or > 16384 || request.Crop.Height is < 1 or > 16384)
            throw new ArgumentException("出力サイズが無効です。");
        string target = Path.GetFullPath(request.OutputPath);
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("保存先が無効です。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("保存先フォルダーがありません: " + parent);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("保存先は既に存在します: " + target);
        var sidecars = new List<string>();
        if (events.Count > 0)
        {
            if (request.Settings.ExportTsv) sidecars.Add(".tsv");
            if (request.Settings.ExportSrt) sidecars.Add(".srt");
            if (request.Settings.ExportVtt) sidecars.Add(".vtt");
        }
        if (request.Kind != ExportKind.PngSequence)
            foreach (string suffix in sidecars)
            {
                string path = Path.ChangeExtension(target, suffix);
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("字幕の保存先は既に存在します: " + path);
            }
        string stage = Path.Combine(parent, ".chulip-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var moved = new List<string>();
        try
        {
            int total = Math.Max(1, checked((int)Math.Ceiling((request.End - request.Start) * request.Media.Fps - 1e-7)));
            string stagedTarget = Path.Combine(stage, Path.GetFileName(target));
            int count;
            // 自動ズームは動画（MP4・GIF）だけに使う。PNG連番は切り出し範囲のまま。
            var camera = request.Kind != ExportKind.PngSequence && request.Settings.Zoom.Enabled
                ? CameraTrack.FromEvents(events, request.Crop, request.Settings.Zoom) : CameraTrack.Identity(request.Crop);
            if (request.Kind == ExportKind.PngSequence)
            {
                Directory.CreateDirectory(stagedTarget);
                count = await ExportPngAsync(request, events, stagedTarget, total, progress, ct).ConfigureAwait(false);
            }
            else count = await ExportMovieAsync(request, events, camera, stage, stagedTarget, total, progress, ct).ConfigureAwait(false);

            string sidecarBase = request.Kind == ExportKind.PngSequence
                ? Path.Combine(stagedTarget, "operations") : Path.Combine(stage, Path.GetFileNameWithoutExtension(target));
            foreach (string suffix in sidecars)
            {
                string content = suffix switch
                {
                    ".tsv" => events.ToTsv(request.Start, request.End, request.Media.Fps, request.Crop, camera.IsIdentity ? null : camera.ViewAt),
                    ".srt" => events.ToSubtitles(request.Start, request.End, request.Settings.Overlay, false),
                    _ => events.ToSubtitles(request.Start, request.End, request.Settings.Overlay, true)
                };
                await File.WriteAllTextAsync(sidecarBase + suffix, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (request.Kind == ExportKind.PngSequence) Directory.Move(stagedTarget, target);
            else
            {
                foreach (string suffix in sidecars)
                {
                    string destination = Path.ChangeExtension(target, suffix);
                    File.Move(sidecarBase + suffix, destination, false);
                    moved.Add(destination);
                }
                File.Move(stagedTarget, target, false);
            }
            progress?.Report(new(total, total, $"完了: {count} フレーム"));
            return new(target, count);
        }
        catch
        {
            // 最後の動画移動に失敗した場合、今回新規作成した字幕だけを取り消す。
            foreach (string path in moved) if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task<int> ExportPngAsync(ExportRequest request, EventLog events, string output, int total, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        await using var reader = media.OpenReader(request.Media, request.Start, request.End);
        FrameData? current = null, next = null;
        byte[]? previous = null, lastSaved = null;
        int index = 0, saved = 0;
        try
        {
            int read = 0;
            current = await ReadAsync(reader, request, read++, ct).ConfigureAwait(false);
            if (current is null) throw new IOException("指定区間からフレームを読み取れませんでした。");
            next = await ReadAsync(reader, request, read++, ct).ConfigureAwait(false);
            while (current is not null)
            {
                ct.ThrowIfCancellationRequested();
                var tolerance = request.Settings.Stability;
                bool stable = !request.Settings.StableOnly ||
                    ((previous is null || FrameComparer.IsSame(previous, current.Pixels, tolerance)) && (next is null || FrameComparer.IsSame(next.Pixels, current.Pixels, tolerance)));
                bool duplicate = request.Settings.SkipDuplicates && lastSaved is not null && FrameComparer.IsSame(lastSaved, current.Pixels, tolerance);
                if (stable && !duplicate)
                {
                    double seconds = request.Media.FrameTime(request.Start + index / request.Media.Fps);
                    using Bitmap rendered = FrameComposer.Compose(current.Frame, new(request.Crop, seconds, events, request.Settings.Overlay) { Masks = request.Masks });
                    string name = FormattableString.Invariant($"frame_{index:D8}_{seconds:F3}s.png");
                    SavePng(rendered, Path.Combine(output, name), request.Settings.PngCompression);
                    lastSaved = current.Pixels;
                    saved++;
                }
                previous = current.Pixels;
                current.Dispose(); current = next; next = null;
                index++;
                progress?.Report(new(Math.Min(index, total), total, $"PNG確認 {index} / {total}、保存 {saved} 枚"));
                if (current is not null) next = await ReadAsync(reader, request, read++, ct).ConfigureAwait(false);
            }
            return saved;
        }
        finally { current?.Dispose(); next?.Dispose(); }
    }

    private async Task<int> ExportMovieAsync(ExportRequest request, EventLog events, CameraTrack camera, string stage, string output, int total, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        bool gif = request.Kind == ExportKind.Gif;
        string encoded = gif ? Path.Combine(stage, "rendered.nut") : output;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{request.Crop.Width}x{request.Crop.Height}", "-framerate", request.Media.Fps.ToString("R", CultureInfo.InvariantCulture),
            "-i", "pipe:0", "-an"
        };
        if (gif) args.AddRange(["-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra"]);
        else args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt",
            request.Crop.Width % 2 == 0 && request.Crop.Height % 2 == 0 ? "yuv420p" : "yuv444p", "-movflags", "+faststart"]);
        args.Add(encoded);
        int count = 0;
        using var encoder = StartProcess(args, true);
        Task<string> errors = encoder.StandardError.ReadToEndAsync();
        using var registration = ct.Register(() => Kill(encoder));
        try
        {
            await using (var reader = media.OpenReader(request.Media, request.Start, request.End))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    using Bitmap? frame = await reader.ReadAsync(ct).ConfigureAwait(false);
                    if (frame is null) break;
                    using Bitmap rendered = FrameComposer.Compose(frame, new(request.Crop, request.Media.FrameTime(request.Start + count / request.Media.Fps), events,
                        request.Settings.Overlay) { DrawOverlay = request.Settings.BurnOverlay, Masks = request.Masks,
                        View = camera.IsIdentity ? null : camera.ViewAt(request.Media.FrameTime(request.Start + count / request.Media.Fps)) });
                    byte[] pixels = BitmapPixels.Bgra(rendered);
                    await encoder.StandardInput.BaseStream.WriteAsync(pixels, ct).ConfigureAwait(false);
                    count++;
                    progress?.Report(new(Math.Min(count, total), total, $"{(gif ? "GIF準備" : "MP4保存")} {count} / {total}"));
                }
            }
            encoder.StandardInput.Close();
            await encoder.WaitForExitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (encoder.ExitCode != 0) throw new IOException("FFmpegの書き出しに失敗しました。\n" + await errors.ConfigureAwait(false));
            if (count == 0) throw new IOException("指定区間からフレームを読み取れませんでした。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Kill(encoder);
            await encoder.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string detail = await errors.ConfigureAwait(false);
            throw new IOException("動画の書き出しに失敗しました。\n" + detail, ex);
        }
        finally
        {
            Kill(encoder);
            await encoder.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        if (gif)
        {
            // パレット生成と適用を分け、大きな動画全体をRAMにためない。
            string palette = Path.Combine(stage, "palette.png");
            progress?.Report(new(total, total, "GIFのパレットを作成しています…"));
            await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", encoded, "-vf", "palettegen", "-frames:v", "1", palette], ct).ConfigureAwait(false);
            progress?.Report(new(total, total, "GIFに変換しています…"));
            await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", encoded, "-i", palette,
                "-lavfi", "paletteuse=dither=sierra2_4a", "-loop", "0", output], ct).ConfigureAwait(false);
        }
        return count;
    }

    private Process StartProcess(IEnumerable<string> arguments, bool input)
    {
        var info = new ProcessStartInfo(media.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("FFmpegを起動できませんでした。");
    }

    private async Task RunAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = StartProcess(arguments, false);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var registration = ct.Register(() => Kill(process));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("FFmpegの変換に失敗しました。\n" + await errors.ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // 静止・重複の比較は、マスク後・演出前の見えている画像で行う。
    private static async Task<FrameData?> ReadAsync(FrameReader reader, ExportRequest request, int index, CancellationToken ct)
    {
        Bitmap? frame = await reader.ReadAsync(ct).ConfigureAwait(false);
        if (frame is null) return null;
        try
        {
            double seconds = request.Media.FrameTime(request.Start + index / request.Media.Fps);
            using Bitmap raw = FrameComposer.Compose(frame, new(request.Crop, seconds, EventLog.Empty, new()) { DrawOverlay = false, Masks = request.Masks });
            return new(frame, BitmapPixels.Bgra(raw));
        }
        catch { frame.Dispose(); throw; }
    }

    private sealed record FrameData(Bitmap Frame, byte[] Pixels) : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }
}
