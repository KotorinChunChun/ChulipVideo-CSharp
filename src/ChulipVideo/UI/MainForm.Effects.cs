using ChulipVideo.Core;
using ChulipVideo.Export;

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
            SyncEffects();
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
}
