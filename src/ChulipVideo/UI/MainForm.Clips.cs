using System.Text;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;

namespace ChulipVideo.UI;

/// <summary>複数の区間・マーカーと一括書き出し（R11）。区間とマーカーは動画別設定に属し、上部の「保存」で記録する。</summary>
public sealed partial class MainForm
{
    private List<ClipRegion> clips = [];
    private List<Marker> markers = [];

    private void SyncClips()
    {
        timeline.Markers = markers.Select(m => m.Time).ToArray();
        timeline.ClipRanges = clips.Where(c => c.Enabled).Select(c => (c.Start, c.End)).ToArray();
        timeline.Invalidate();
    }

    private void AddMarker()
    {
        if (current is null) return;
        double time = current.FrameTime(currentTime);
        if (markers.Any(m => Math.Abs(m.Time - time) < 1e-9)) { Status("この位置にはマーカーがあります。"); return; }
        markers.Add(new Marker { Time = time, Label = $"マーカー{markers.Count + 1}" });
        markers = markers.OrderBy(m => m.Time).ToList();
        SyncClips();
        Status($"マーカーを追加しました（{TimecodeUpDown.FormatTimecode((decimal)time)}）。上部の「保存」で動画別設定へ記録します。");
    }

    private void JumpMarker(int direction)
    {
        if (current is null) return;
        double time = current.FrameTime(currentTime);
        var target = direction < 0 ? markers.LastOrDefault(m => m.Time < time - 1e-9) : markers.FirstOrDefault(m => m.Time > time + 1e-9);
        if (target is null) { Status(direction < 0 ? "前のマーカーはありません。" : "次のマーカーはありません。"); return; }
        Seek(target.Time);
        Status($"{target.Label}（{TimecodeUpDown.FormatTimecode((decimal)target.Time)}）");
    }

    private void OpenClipList()
    {
        if (current is null) { Status("先に動画を選択してください。"); return; }
        StopPlaying();
        using var form = new ClipListForm(clips, markers, current, rangeStart, rangeEnd, canvas.Crop, currentTime, Path.GetDirectoryName(current.Path) ?? "");
        if (form.ShowDialog(this) != DialogResult.OK) return;
        clips = form.Clips; markers = form.Markers;
        SyncClips();
        if (form.Apply is ClipRegion apply)
        {
            if (apply.Crop is CropRect crop) SetCrop(crop);
            SetRange(apply.Start, apply.End);
            Status($"区間「{apply.Name}」をトリミング範囲へ反映しました。");
        }
        if (form.Batch is ClipBatchRequest batch) BeginBatch(batch);
    }

    private void BeginBatch(ClipBatchRequest batch)
    {
        if (current is null || media is null || exporting || loading) return;
        var targets = clips.Where(c => c.Enabled).Select(c => c with { }).ToArray();
        if (targets.Length == 0) { Status("有効な区間がありません。"); return; }
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        exportCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); exporting = true; SetBusy(true); cancel.Enabled = true; progress.Value = 0;
        exportTask = BatchAsync(targets, batch, current, media, events, canvas.Crop, snapshot, MaskSnapshot(), exportCancel.Token);
    }

    /// <summary>1件の失敗で止めず、残りの区間・形式を続けてから結果をまとめて示す。</summary>
    private async Task BatchAsync(ClipRegion[] targets, ClipBatchRequest batch, MediaInfo source, FfmpegService service, EventLog log, CropRect defaultCrop,
        AppSettings snapshot, MaskRegion[] maskSnapshot, CancellationToken token)
    {
        IReadOnlyList<ClipBatchResult> results;
        try
        {
            results = await ClipBatch.RunAsync(service, source, log, targets, batch, defaultCrop, snapshot, maskSnapshot,
                new Progress<string>(message => { if (!closing) Status(message); }), token);
        }
        catch (Exception ex) { if (!closing) ShowError(ex); return; }
        finally
        {
            exporting = false; exportCancel?.Dispose(); exportCancel = null;
            if (!closing) { cancel.Enabled = false; SetBusy(false); }
        }
        if (closing) return;
        progress.Value = 100;
        int failed = results.Count(r => !r.Succeeded);
        Status($"一括書き出し: 成功 {results.Count - failed} 件・失敗 {failed} 件");
        var text = new StringBuilder($"一括書き出しが終わりました（成功 {results.Count - failed} 件・失敗 {failed} 件）。\n\n");
        foreach (var r in results) text.Append(r.Succeeded ? "○ " : "× ").Append(r.Clip).Append(" / ").Append(r.Kind).Append("\n　").Append(r.Message).Append('\n');
        MessageBox.Show(this, text.ToString(), WindowTitle.Create("一括書き出し"), MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}

[Flags]
public enum ClipBatchKind { None = 0, PngSequence = 1, Mp4 = 2, Gif = 4, Steps = 8 }

public sealed record ClipBatchRequest(ClipBatchKind Kinds, string Folder);
public sealed record ClipBatchResult(string Clip, string Kind, bool Succeeded, string Message);

/// <summary>区間ごとに選んだ形式を順に書き出す。出力名は「動画名_区間名」。</summary>
public static class ClipBatch
{
    public static async Task<IReadOnlyList<ClipBatchResult>> RunAsync(FfmpegService service, MediaInfo source, EventLog log, IReadOnlyList<ClipRegion> targets,
        ClipBatchRequest batch, CropRect defaultCrop, AppSettings settings, IReadOnlyList<MaskRegion> masks, IProgress<string>? progress, CancellationToken token)
    {
        var results = new List<ClipBatchResult>();
        var export = new ExportService(service);
        var kinds = new[] { (ClipBatchKind.PngSequence, "PNG連番"), (ClipBatchKind.Mp4, "MP4"), (ClipBatchKind.Gif, "GIF"), (ClipBatchKind.Steps, "手順書") }
            .Where(k => batch.Kinds.HasFlag(k.Item1)).ToArray();
        string video = Path.GetFileNameWithoutExtension(source.Path);
        for (int i = 0; i < targets.Count; i++)
        {
            var clip = targets[i];
            var crop = clip.Crop ?? defaultCrop;
            string name = StepDocumentWriter.SafeName($"{video}_{clip.Name}");
            foreach (var (kind, label) in kinds)
            {
                if (token.IsCancellationRequested) { results.Add(new(clip.Name, label, false, "中断しました。")); continue; }
                progress?.Report($"一括書き出し {i + 1} / {targets.Count}: {clip.Name}（{label}）");
                try
                {
                    string output;
                    if (kind == ClipBatchKind.Steps)
                    {
                        var document = await StepDetector.DetectAsync(service, source, log, clip.Start, clip.End, crop, settings, null, token);
                        if (document.Steps.Count == 0) { results.Add(new(clip.Name, label, false, "区間内に手順となる操作がありません。")); continue; }
                        var formats = StepDocumentFormat.Markdown | StepDocumentFormat.Html | StepDocumentFormat.Word | (EdgePdf.FindEdge() is null ? 0 : StepDocumentFormat.Pdf);
                        var result = await StepDocumentWriter.ExportAsync(service, source, log, document, settings, masks, new(clip.Name, formats, batch.Folder, name + "_手順書"), null, token);
                        output = result.Directory + (result.PdfWarning is null ? "" : "（" + result.PdfWarning + "）");
                    }
                    else
                    {
                        var exportKind = kind switch { ClipBatchKind.Mp4 => ExportKind.Mp4, ClipBatchKind.Gif => ExportKind.Gif, _ => ExportKind.PngSequence };
                        string path = Path.Combine(batch.Folder, kind switch { ClipBatchKind.Mp4 => name + ".mp4", ClipBatchKind.Gif => name + ".gif", _ => name + "_連番" });
                        var done = await export.ExportAsync(new ExportRequest(source, crop, clip.Start, clip.End, path, exportKind, settings) { Masks = masks }, log, null, token);
                        output = $"{done.OutputPath}（{done.FrameCount} フレーム）";
                    }
                    results.Add(new(clip.Name, label, true, output));
                }
                catch (OperationCanceledException) { results.Add(new(clip.Name, label, false, "中断しました。")); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { results.Add(new(clip.Name, label, false, ex.Message)); }
            }
        }
        return results;
    }
}
