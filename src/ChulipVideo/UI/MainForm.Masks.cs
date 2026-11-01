using ChulipVideo.Core;
using ChulipVideo.Export;

namespace ChulipVideo.UI;

/// <summary>マスク（R4）の編集。マスクは動画別設定に属し、上部の「保存」で記録する。</summary>
public sealed partial class MainForm
{
    private readonly CheckBox maskEdit = new() { Text = "マスク", Appearance = Appearance.Button, AutoSize = true, MinimumSize = new Size(52, 30), TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "マスク編集の切替" };
    private readonly ContextMenuStrip maskMenu = new();
    private List<MaskRegion> masks = [];
    // キャンバスに表示中のマスク（現在時刻で有効なもの）。キャンバスの番号と対応する。
    private List<MaskRegion> visibleMasks = [];

    private void InitializeMasks()
    {
        maskEdit.CheckedChanged += (_, _) =>
        {
            canvas.MaskMode = maskEdit.Checked;
            canvas.ContextMenuStrip = maskEdit.Checked ? maskMenu : null;
            maskEdit.BackColor = maskEdit.Checked ? DetailTone : SystemColors.Control;
            if (maskEdit.Checked) Status("マスク編集: 左ドラッグで現在位置から区間の終わりまでのマスクを作成。右クリックで一覧・削除・時間の変更。");
            SyncMasks();
        };
        tips.SetToolTip(maskEdit, "マスク（モザイク・ぼかし・塗りつぶし）の編集に切り替える");
        canvas.MaskCommitted += (_, e) =>
        {
            if (current is null) return;
            if (e.Index < 0)
            {
                double start = current.FrameTime(currentTime);
                masks.Add(new MaskRegion { Rect = e.Rect, Start = start, End = rangeEnd > start ? rangeEnd : current.Duration });
                SyncMasks();
                canvas.SelectedMask = visibleMasks.Count - 1;
                Status($"マスクを追加しました（{TimecodeUpDown.FormatTimecode((decimal)start)}〜）。上部の「保存」で動画別設定へ記録します。");
            }
            else if (e.Index < visibleMasks.Count) visibleMasks[e.Index].Rect = e.Rect;
            Redraw();
        };
        canvas.MaskEdited += (_, e) => { if (e.Index < visibleMasks.Count) { visibleMasks[e.Index].Rect = e.Rect; Redraw(); } };
        maskMenu.Items.Add("マスク一覧…", null, (_, _) => OpenMaskList());
        maskMenu.Items.Add("マスク候補を検出（OCR）…", null, (_, _) => BeginMaskSuggestion());
        maskMenu.Items.Add("選択したマスクを削除 (Delete)", null, (_, _) => DeleteSelectedMask());
        maskMenu.Items.Add("現在位置を開始に", null, (_, _) => SetSelectedMaskTime(end: false));
        maskMenu.Items.Add("現在位置を終了に", null, (_, _) => SetSelectedMaskTime(end: true));
        foreach (var (mode, label) in new[] { (MaskMode.Mosaic, "方式: モザイク"), (MaskMode.Blur, "方式: ぼかし"), (MaskMode.Fill, "方式: 塗りつぶし（機密情報向け）") })
            maskMenu.Items.Add(label, null, (_, _) => { if (SelectedMask() is MaskRegion m) { m.Mode = mode; Redraw(); } });
    }

    private MaskRegion? SelectedMask() => canvas.SelectedMask >= 0 && canvas.SelectedMask < visibleMasks.Count ? visibleMasks[canvas.SelectedMask] : null;

    /// <summary>現在時刻で有効なマスクをキャンバスとタイムラインへ反映する。</summary>
    private void SyncMasks()
    {
        var selected = SelectedMask();
        visibleMasks = masks.Where(m => m.IsActive(currentTime)).ToList();
        canvas.MaskRects = visibleMasks.Select(m => m.Rect).ToArray();
        canvas.SelectedMask = selected is null ? -1 : visibleMasks.IndexOf(selected);
        timeline.MaskRanges = masks.Where(m => m.Enabled).Select(m => (m.Start, m.End)).ToArray();
        timeline.Invalidate();
    }

    private void DeleteSelectedMask()
    {
        if (SelectedMask() is not MaskRegion mask) { Status("削除するマスクを選んでください。"); return; }
        masks.Remove(mask);
        canvas.SelectedMask = -1;
        SyncMasks(); Redraw();
    }

    private void SetSelectedMaskTime(bool end)
    {
        if (SelectedMask() is not MaskRegion mask || current is null) { Status("マスクを選んでください。"); return; }
        double time = current.FrameTime(currentTime);
        if (end) mask.End = time; else mask.Start = time;
        if (mask.End <= mask.Start) { if (end) mask.Start = Math.Max(0, time - 1 / current.Fps); else mask.End = current.Duration; }
        SettingsStore.NormalizeMasks([mask], current);
        SyncMasks(); Redraw();
    }

    private void OpenMaskList()
    {
        if (current is null) return;
        StopPlaying();
        using var form = new MaskListForm(masks, current, current.FrameTime(currentTime), SelectedMask() is MaskRegion m ? masks.IndexOf(m) : -1);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        masks = form.Masks;
        canvas.SelectedMask = -1;
        SyncMasks(); Redraw();
    }

    /// <summary>トリミング範囲を2秒おきに読み取り、機密らしい文字を無効のマスク候補として加えてから、一覧で承認してもらう（R9）。</summary>
    private void BeginMaskSuggestion()
    {
        if (current is null || media is null || exporting || loading) return;
        if (Recognition.OcrService.Unavailable() is string reason) { MessageBox.Show(this, reason, WindowTitle.Create("マスク候補"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        StopPlaying();
        exportCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); exporting = true; SetBusy(true); cancel.Enabled = true;
        exportTask = SuggestMasksAsync(current, media, rangeStart, rangeEnd, [.. settings.Recognition.Keywords], exportCancel.Token);
    }

    private async Task SuggestMasksAsync(MediaInfo source, Media.FfmpegService service, double start, double end, string[] keywords, CancellationToken token)
    {
        List<MaskRegion>? found = null;
        try { found = await Recognition.MaskSuggester.SuggestAsync(service, source, start, end, keywords, new Progress<string>(m => { if (!closing) Status(m); }), token); }
        catch (OperationCanceledException) { if (!closing) Status("マスク候補の検出を中断しました。"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException) { if (!closing) ShowError(ex); }
        finally
        {
            exporting = false; exportCancel?.Dispose(); exportCancel = null;
            if (!closing) { cancel.Enabled = false; SetBusy(false); }
        }
        if (closing || found is null || current != source) return;
        // 既に同じ位置・時間を覆う有効なマスクがある候補は加えない。
        var added = found.Where(f => !masks.Any(m => m.Enabled && m.Start <= f.Start && m.End >= f.End && m.Rect.Rectangle.Contains(f.Rect.Rectangle))).ToList();
        masks.AddRange(added);
        SyncMasks(); Redraw();
        Status(added.Count == 0 ? "マスク候補は見つかりませんでした。" : $"マスク候補 {added.Count} 件を無効の状態で追加しました。一覧で確かめて有効にしてください。");
        if (added.Count > 0) OpenMaskList();
    }

    /// <summary>出力（コピー・PNG・書き出し・手順書）に渡すマスク。編集中の変更が出力へ混ざらないよう複製する。</summary>
    private MaskRegion[] MaskSnapshot() => masks.Select(m => m with { }).ToArray();

    /// <summary>プレビュー・コピー・PNG保存が共通に使う合成の指定。</summary>
    private RenderContext OutputContext(CropRect crop, double time) => new(crop, time, events, settings.Overlay) { Masks = MaskSnapshot() };
}
