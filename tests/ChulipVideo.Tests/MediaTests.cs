using System.Diagnostics;
using ChulipVideo.Core;
using ChulipVideo.Media;

namespace ChulipVideo.Tests;

public static class MediaTests
{
    public static async Task RunAsync(string ffmpeg, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        FfmpegService service = new(ffmpeg);
        await RejectRemotePlaylistAsync(service, outputDirectory);
        Check(FfmpegService.FindExecutable(ffmpeg) == Path.GetFullPath(ffmpeg), "指定 FFmpeg の検出");
        string video = Path.Combine(outputDirectory, "色変化 日本語.mp4");
        await GenerateAsync(ffmpeg,
        [
            "-f", "lavfi", "-i", "color=c=red:s=160x90:r=10:d=1[a];color=c=blue:s=160x90:r=10:d=1[b];[a][b]concat=n=2:v=1:a=0",
            "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", video
        ]);
        MediaInfo info = await service.ProbeAsync(video);
        Check(info.Width == 160 && info.Height == 90 && Math.Abs(info.Duration - 2) < .03 && Math.Abs(info.Fps - 10) < .01, "動画情報");
        using (Bitmap red = await service.ReadFrameAsync(info, 0)) Check(IsRed(red), "先頭フレームは赤");
        using (Bitmap blue = await service.ReadFrameAsync(info, 1.3)) Check(IsBlue(blue), "指定秒フレームは青");
        using (Bitmap tail = await service.ReadFrameAsync(info, info.Duration)) Check(IsBlue(tail), "動画末尾のクランプ");

        int frames = 0, redFrames = 0, blueFrames = 0;
        await using (FrameReader reader = service.OpenReader(info, .5, 1.5))
        {
            while (await reader.ReadAsync() is { } frame)
            {
                using (frame)
                {
                    Check(frame.Width == 160 && frame.Height == 90, "連続デコード解像度");
                    frames++;
                    if (IsRed(frame)) redFrames++;
                    if (IsBlue(frame)) blueFrames++;
                }
            }
        }
        Check(frames == 10 && redFrames == 5 && blueFrames == 5, "範囲読み取りの時間・フレーム数");
        await using (FrameReader tailReader = service.OpenReader(info, 1.98, 2))
        {
            using Bitmap? frame = await tailReader.ReadAsync();
            Check(frame is not null && IsBlue(frame), "最終フレーム内の短い範囲");
            Check(await tailReader.ReadAsync() is null, "短い範囲は1フレーム");
        }
        await using (PreviewReader preview = new(service, info))
        {
            using (Bitmap first = await preview.ReadAsync(0)) Check(IsRed(first), "プレビュー初期位置");
            using (Bitmap next = await preview.ReadAsync(.1)) Check(IsRed(next), "プレビュー連続位置");
            using (Bitmap jump = await preview.ReadAsync(1.2)) Check(IsBlue(jump), "プレビュー前方シーク");
            using (Bitmap back = await preview.ReadAsync(.2)) Check(IsRed(back), "プレビュー逆方向シーク");
            using (Bitmap repeat = await preview.ReadAsync(.2)) Check(IsRed(repeat), "プレビュー同位置再取得");
        }

        string rotated = Path.Combine(outputDirectory, "回転.mp4");
        await GenerateAsync(ffmpeg, ["-display_rotation:v:0", "90", "-i", video, "-c", "copy", rotated]);
        MediaInfo rotation = await service.ProbeAsync(rotated);
        Check(rotation.Width == 90 && rotation.Height == 160, "回転動画の表示解像度");
        using (Bitmap frame = await service.ReadFrameAsync(rotation, 0))
            Check(frame.Width == 90 && frame.Height == 160 && IsRed(frame), "自動回転デコード");

        string anamorphic = Path.Combine(outputDirectory, "SAR.mp4");
        await GenerateAsync(ffmpeg,
        ["-f", "lavfi", "-i", "color=c=red:s=120x80:r=10:d=1", "-vf", "setsar=2/1", "-c:v", "libx264", "-pix_fmt", "yuv420p", anamorphic]);
        MediaInfo sar = await service.ProbeAsync(anamorphic);
        Check(sar.Width == 240 && sar.Height == 80, "SAR 補正後の表示解像度");
        using (Bitmap frame = await service.ReadFrameAsync(sar, 0))
            Check(frame.Width == 240 && frame.Height == 80 && IsRed(frame), "SAR 補正デコード");
        string rotatedSar = Path.Combine(outputDirectory, "回転SAR.mp4");
        await GenerateAsync(ffmpeg, ["-display_rotation:v:0", "90", "-i", anamorphic, "-c", "copy", rotatedSar]);
        MediaInfo both = await service.ProbeAsync(rotatedSar);
        Check(both.Width == 80 && both.Height == 240, "SAR と回転の組合せ");
        using (Bitmap frame = await service.ReadFrameAsync(both, 0))
            Check(frame.Width == 80 && frame.Height == 240 && IsRed(frame), "SAR と回転を組み合わせたデコード");

        string ntsc = Path.Combine(outputDirectory, "NTSC.mp4");
        await GenerateAsync(ffmpeg,
        ["-f", "lavfi", "-i", "color=c=red:s=160x90:r=30000/1001:d=1.001,drawbox=color=blue:t=fill:enable='eq(n,29)'", "-frames:v", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", ntsc]);
        MediaInfo ntscInfo = await service.ProbeAsync(ntsc);
        Check(Math.Abs(ntscInfo.Fps - 30000d / 1001) < 1e-8, "NTSC フレームレートの丸め補正");
        Check(ntscInfo.FinalFrameTime.HasValue && ntscInfo.TimingWarning is null && Math.Abs(ntscInfo.LastFrameTime - 29 * 1001d / 30000) < 1e-8
            && Math.Abs(ntscInfo.Duration - 1.001) < 1e-8, "NTSC 終端の整数 PTS 取得");
        using (Bitmap frame = await service.ReadFrameAsync(ntscInfo, 28 / ntscInfo.Fps)) Check(IsRed(frame), "NTSC 最終直前フレーム");
        using (Bitmap frame = await service.ReadFrameAsync(ntscInfo, ntscInfo.Duration)) Check(IsBlue(frame), "NTSC 固有色の最終フレーム");
        await using (PreviewReader preview = new(service, ntscInfo))
        {
            using (Bitmap before = await preview.ReadAsync(28 / ntscInfo.Fps)) Check(IsRed(before), "NTSC プレビュー最終直前");
            using (Bitmap last = await preview.ReadAsync(ntscInfo.Duration)) Check(IsBlue(last), "NTSC プレビュー最終フレーム一致");
            using (Bitmap repeat = await preview.ReadAsync(ntscInfo.Duration)) Check(IsBlue(repeat), "NTSC 最終位置再取得");
            using (Bitmap back = await preview.ReadAsync(28 / ntscInfo.Fps)) Check(IsRed(back), "NTSC 最終位置から戻る");
        }

        foreach (int oneFrameRate in new[] { 30, 240 })
        {
            string oneFrame = Path.Combine(outputDirectory, $"1フレーム{oneFrameRate}fps.mp4");
            await GenerateAsync(ffmpeg,
            ["-f", "lavfi", "-i", $"color=c=blue:s=160x90:r={oneFrameRate}", "-frames:v", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", oneFrame]);
            MediaInfo one = await service.ProbeAsync(oneFrame);
            Check(one.FinalFrameTime == 0 && Math.Abs(one.Duration - 1d / oneFrameRate) < 1e-8, "1フレーム動画の丸められない長さ");
            using (Bitmap last = await service.ReadFrameAsync(one, one.Duration)) Check(IsBlue(last), "1フレーム動画の静止画");
            await using PreviewReader preview = new(service, one);
            using Bitmap previewLast = await preview.ReadAsync(one.Duration);
            Check(IsBlue(previewLast), "1フレーム動画のプレビュー");
        }

        string large = Path.Combine(outputDirectory, "大きいプレビュー.mp4");
        await GenerateAsync(ffmpeg,
        ["-f", "lavfi", "-i", "color=c=red:s=1920x1080:r=10:d=0.2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", large]);
        MediaInfo largeInfo = await service.ProbeAsync(large);
        await using (PreviewReader preview = new(service, largeInfo))
        {
            using Bitmap frame = await preview.ReadAsync(0);
            Check(largeInfo.Width == 1920 && frame.Width == 1280 && frame.Height == 720, "プレビューだけ縮小");
        }

        string invalid = Path.Combine(outputDirectory, "破損.mp4");
        await File.WriteAllTextAsync(invalid, "これは動画ではありません。");
        await ThrowsAsync<IOException>(() => service.ProbeAsync(invalid), "無効動画を検出");
        MediaInfo invalidInfo = info with { Path = invalid };
        await ThrowsAsync<IOException>(() => service.ReadFrameAsync(invalidInfo, 0), "デコーダー失敗の終了コード検出");
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => service.ProbeAsync(video, canceled.Token), "Probe キャンセル");
        using (CancellationTokenSource runningCancellation = new())
        {
            Task<MediaInfo> running = service.ProbeAsync(video, runningCancellation.Token);
            runningCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => running, "起動後の Probe キャンセル");
        }
        await using (FrameReader reader = service.OpenReader(info, 0, info.Duration))
            await ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(canceled.Token), "連続読み取りキャンセル");
        Console.WriteLine("メディア検証: 成功（情報、色、範囲、シーク、回転、SAR、縮小、破損、キャンセル）");
    }

    private static async Task RejectRemotePlaylistAsync(FfmpegService service, string directory)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        string path = Path.Combine(directory, "外部参照を含む動画.m3u8");
        await File.WriteAllTextAsync(path, $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:1,\nhttp://127.0.0.1:{port}/segment.ts\n#EXT-X-ENDLIST\n");
        bool denied = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await service.ProbeAsync(path, deadline.Token); }
        catch (IOException error) when (error.Message.Contains("whitelist", StringComparison.OrdinalIgnoreCase)) { denied = true; }
        Check(denied && !listener.Pending(), "動画内の外部参照をネットワーク接続前に拒否");
    }

    private static bool IsRed(Bitmap frame) { Color c = frame.GetPixel(frame.Width / 2, frame.Height / 2); return c.R > 200 && c.B < 40; }
    private static bool IsBlue(Bitmap frame) { Color c = frame.GetPixel(frame.Width / 2, frame.Height / 2); return c.B > 200 && c.R < 40; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception($"メディア検証失敗: {message}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"メディア検証失敗: {message} で {typeof(T).Name} が発生しませんでした。");
    }
    private static async Task GenerateAsync(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-filter_threads", "1" }.Concat(arguments)) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException("テスト用 FFmpeg を起動できませんでした。");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await output;
        if (process.ExitCode != 0) throw new IOException(await errors);
        await errors;
    }
}
