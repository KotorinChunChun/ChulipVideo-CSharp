using System.Text.Json;
using ChulipVideo.Core;

namespace ChulipVideo.Steps;

/// <summary>手順書の1ステップ。時刻は元動画の秒、座標は元動画のピクセル。</summary>
public sealed record StepItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>InputActionKindの名前、または手動追加の"Manual"。</summary>
    public string Kind { get; set; } = "Manual";
    /// <summary>操作の時刻。</summary>
    public double Time { get; set; }
    /// <summary>手順書に使う画像の時刻。</summary>
    public double FrameTime { get; set; }
    public double? AfterFrameTime { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int? EndX { get; set; }
    public int? EndY { get; set; }
    /// <summary>操作の表示（例: Ctrl + S、左クリック）。</summary>
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>説明文の出どころ: Template / Ocr / Ai / Manual。</summary>
    public string DescriptionSource { get; set; } = "Template";
    public bool Enabled { get; set; } = true;
    public List<Annotation> Annotations { get; set; } = [];
}

public sealed record StepDocument
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string VideoFile { get; set; } = "";
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double Duration { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public CropRect Crop { get; set; }
    public List<StepItem> Steps { get; set; } = [];
}

/// <summary>手順を動画と同じフォルダーの&lt;動画名&gt;.chulip.steps.jsonへ保存・復元する。</summary>
public static class StepStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string PathFor(string video) => Path.ChangeExtension(video, ".chulip.steps.json");

    public static StepDocument? Load(MediaInfo media, out string? warning)
    {
        warning = null;
        string path = PathFor(media.Path);
        if (!File.Exists(path)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<StepDocument>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("手順ファイルが空です。");
            if (document.SchemaVersion != StepDocument.CurrentSchema)
            {
                warning = $"対応していない手順ファイルの形式です（形式 {document.SchemaVersion}）。";
                return null;
            }
            Normalize(document, media);
            if (document.VideoWidth != media.Width || document.VideoHeight != media.Height || Math.Abs(document.Duration - media.Duration) > .5)
                warning = "保存した手順と動画の解像度・長さが異なります。動画が差し替えられた可能性があります。";
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            warning = "手順ファイルを読み込めませんでした: " + ex.Message;
            return null;
        }
    }

    public static void Save(MediaInfo media, StepDocument document)
    {
        document.SchemaVersion = StepDocument.CurrentSchema;
        document.VideoFile = Path.GetFileName(media.Path);
        document.VideoWidth = media.Width; document.VideoHeight = media.Height; document.Duration = media.Duration;
        AtomicFile.WriteAllText(PathFor(media.Path), JsonSerializer.Serialize(document, Json));
    }

    private static void Normalize(StepDocument document, MediaInfo media)
    {
        document.Steps = (document.Steps ?? []).Where(s => s is not null).ToList();
        document.Start = Clamp(document.Start, 0, media.LastFrameTime);
        document.End = Clamp(document.End, document.Start, media.Duration);
        document.Crop = document.Crop.Width > 0 && document.Crop.Height > 0 ? document.Crop.Clamp(media.Width, media.Height, true) : CropRect.Full(media);
        foreach (var step in document.Steps)
        {
            step.Id = string.IsNullOrWhiteSpace(step.Id) ? Guid.NewGuid().ToString("N") : step.Id;
            step.Kind ??= "Manual"; step.Label ??= ""; step.Description ??= ""; step.DescriptionSource ??= "Manual";
            step.Time = Clamp(step.Time, 0, media.Duration);
            step.FrameTime = media.FrameTime(Clamp(step.FrameTime, 0, media.LastFrameTime));
            step.AfterFrameTime = step.AfterFrameTime is double after ? media.FrameTime(Clamp(after, 0, media.LastFrameTime)) : null;
            step.X = Math.Clamp(step.X, 0, Math.Max(0, media.Width - 1)); step.Y = Math.Clamp(step.Y, 0, Math.Max(0, media.Height - 1));
            if (step.EndX is int endX) step.EndX = Math.Clamp(endX, 0, Math.Max(0, media.Width - 1));
            if (step.EndY is int endY) step.EndY = Math.Clamp(endY, 0, Math.Max(0, media.Height - 1));
            step.Annotations = Annotation.Normalize(step.Annotations, media.Width, media.Height);
        }
    }

    private static double Clamp(double value, double min, double max) => Math.Clamp(double.IsFinite(value) ? value : min, min, Math.Max(min, max));
}
