using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;

namespace ChulipVideo.UI;

/// <summary>演出（R6自動ズーム）のプレビュー。書き出しで写る範囲をキャンバスに枠で示す。</summary>
public sealed partial class MainForm
{
    private readonly CheckBox showEffects = new() { Text = "演出を表示", AutoSize = true, AccessibleName = "自動ズームで写る範囲の表示" };
    private CameraTrack? camera;
    private (EventLog Events, CropRect Crop, ZoomSettings Zoom)? cameraKey;

    private void InitializeEffects()
    {
        showEffects.CheckedChanged += (_, _) =>
        {
            if (showEffects.Checked && !settings.Zoom.Enabled) Status("自動ズームは無効です。「設定」の「演出」で有効にすると、写る範囲を枠で表示します。");
            SyncEffects(); SyncIdle();
        };
        tips.SetToolTip(showEffects, "MP4・GIFの自動ズームで写る範囲を、プレビューに枠で表示");
    }

    /// <summary>入力ログ・切り出し範囲・ズーム設定が変わったときだけ作り直す。</summary>
    private CameraTrack CurrentCamera()
    {
        var key = (events, canvas.Crop, settings.Zoom with { });
        if (camera is null || cameraKey != key)
        {
            camera = settings.Zoom.Enabled ? CameraTrack.FromEvents(events, canvas.Crop, settings.Zoom) : CameraTrack.Identity(canvas.Crop);
            cameraKey = key;
        }
        return camera;
    }

    private void SyncEffects()
    {
        if (current is null || !showEffects.Checked || !settings.Zoom.Enabled) { canvas.ViewRect = null; return; }
        canvas.ViewRect = CurrentCamera().ViewAt(currentTime);
    }

    private CancellationTokenSource? idleScan;
    private (string Path, CropRect Crop, StabilitySettings Stability)? idleScanKey;
    private MotionProfile? idleProfile;
    private (string Path, CropRect Crop, StabilitySettings Stability)? idleProfileKey;

    /// <summary>
    /// カット・早送りされる区間をタイムラインに示す（R7）。動き量は縮小映像で動画全体を一度だけ裏で測り、
    /// 動画・切り出し範囲・判定の許容差が同じ間は使い回す。再生中の時刻同期からは呼ばない。
    /// </summary>
    private void SyncIdle()
    {
        if (current is null || media is null || !showEffects.Checked || !settings.Idle.Enabled)
        {
            idleScan?.Cancel();
            timeline.IdleRanges = []; timeline.Invalidate();
            return;
        }
        var key = (current.Path, canvas.Crop, settings.Stability with { });
        if (idleProfile is not null && idleProfileKey == key) { ApplyIdle(idleProfile); return; }
        if (idleScan is not null && idleScanKey == key) return;
        idleScan?.Cancel();
        var scan = CancellationTokenSource.CreateLinkedTokenSource(mediaLifetime.Token);
        idleScan = scan; idleScanKey = key;
        var (source, service, crop, stability) = (current, media, canvas.Crop, settings.Stability with { });
        RunUiAsync(async () =>
        {
            try
            {
                var progress = new Progress<double>(p => { if (idleScan == scan) Status($"無操作区間を計測しています… {p * 100:0}%"); });
                var profile = await MotionProfile.MeasureAsync(service, source, 0, source.Duration, crop, stability, progress, scan.Token);
                if (idleScan != scan || closing) return;
                idleProfile = profile; idleProfileKey = key;
                ApplyIdle(profile);
                Status($"無操作区間: {timeline.IdleRanges.Count} 区間（書き出しでは区間ごとに計測し直します）");
            }
            catch (OperationCanceledException) when (idleScan != scan || scan.IsCancellationRequested) { }
            finally { if (idleScan == scan) { idleScan = null; idleScanKey = null; } scan.Dispose(); }
        });
    }

    private void ApplyIdle(MotionProfile profile)
    {
        if (current is null) return;
        var idle = IdleDetector.Detect(events, profile, settings.Idle);
        timeline.IdleRanges = new TimeMap(0, current.Duration, idle, settings.Idle).Changed.Select(s => (s.Start, s.End)).ToArray();
        timeline.Invalidate();
    }
}
