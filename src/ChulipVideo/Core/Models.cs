namespace ChulipVideo.Core;

public sealed record MediaInfo(string Path, int Width, int Height, double Duration, double Fps)
{
    public double? FinalFrameTime { get; init; }
    public string? TimingWarning { get; init; }
    public double LastFrameTime => FinalFrameTime ?? Math.Max(0, (Math.Max(1, Math.Round(Duration * Fps)) - 1) / Fps);
    public double FrameTime(double seconds)
    {
        double clamped=Math.Clamp(double.IsFinite(seconds)?seconds:0,0,LastFrameTime);
        return clamped>=LastFrameTime-1e-9?LastFrameTime:Math.Floor(clamped*Fps+1e-7)/Fps;
    }
}

public readonly record struct CropRect(int X, int Y, int Width, int Height)
{
    public Rectangle Rectangle => new(X, Y, Width, Height);
    public static CropRect Full(MediaInfo media) => new(0, 0, media.Width, media.Height);
    public CropRect Clamp(int width, int height, bool allowOutside)
    {
        int w = Math.Clamp(Width, 1, 16384), h = Math.Clamp(Height, 1, 16384);
        if (allowOutside) return new(Math.Clamp(X, -16384, width + 16384), Math.Clamp(Y, -16384, height + 16384), w, h);
        w = Math.Min(w, width); h = Math.Min(h, height);
        return new(Math.Clamp(X, 0, width - w), Math.Clamp(Y, 0, height - h), w, h);
    }
}

public sealed record VideoSettings
{
    public CropRect Crop { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Position { get; set; }
    public bool AllowOutside { get; set; }
    public List<MaskRegion> Masks { get; set; } = [];
    public List<ClipRegion> Clips { get; set; } = [];
    public List<Marker> Markers { get; set; } = [];
}

/// <summary>一括書き出しの単位となる区間（R11）。Cropがnullなら書き出し時の切り出し範囲を使う。</summary>
public sealed record ClipRegion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Start { get; set; }
    public double End { get; set; }
    public CropRect? Crop { get; set; }
}

/// <summary>タイムライン上の目印（R11）。</summary>
public sealed record Marker
{
    public double Time { get; set; }
    public string Label { get; set; } = "";
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<MaskMode>))]
public enum MaskMode { Mosaic, Blur, Fill }

/// <summary>範囲と時間帯を指定して映像を隠すマスク。座標は元動画のピクセル、時間は開始を含み終了を含まない。</summary>
public sealed record MaskRegion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public CropRect Rect { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public MaskMode Mode { get; set; } = MaskMode.Mosaic;
    /// <summary>モザイクのブロック、ぼかしの半径（元動画のピクセル）。</summary>
    public int Strength { get; set; } = 16;
    /// <summary>塗りつぶしの色（#RRGGBB）。</summary>
    public string Color { get; set; } = "#000000";
    public string Note { get; set; } = "";
    /// <summary>Manual、またはOCRの候補を表すSuggested（承認されるまで無効）。</summary>
    public string Source { get; set; } = "Manual";
    public bool IsActive(double seconds) => Enabled && seconds >= Start && seconds < End && Rect.Width > 0 && Rect.Height > 0;
}

public sealed record ResolutionPreset(string Name, int Width, int Height)
{
    public override string ToString() => $"{Name} ({Width}×{Height})";
}

public sealed record OverlayOptions
{
    public bool ShowPointer { get; set; } = true;
    public bool ShowClicks { get; set; } = true;
    public bool ShowKeys { get; set; } = true;
    public string PointerColor { get; set; } = "#202020";
    public string PointerShape { get; set; } = "cursor";
    public string PointerFill { get; set; } = "";
    public int PointerWidth { get; set; } = 2;
    public string ClickShape { get; set; } = "circle";
    public int ClickWidth { get; set; } = 3;
    public string LeftColor { get; set; } = "#FF4444";
    public string RightColor { get; set; } = "#4488FF";
    public string MiddleColor { get; set; } = "#44CC66";
    public int PointerSize { get; set; } = 10;
    public int RippleSize { get; set; } = 28;
    public double RippleDuration { get; set; } = 0.5;
    public double FadeDuration { get; set; } = 1.0;
    public int FontSize { get; set; } = 24;
    public int MaxStack { get; set; } = 3;
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public string TextColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#202020";
    public string OutlineColor { get; set; } = "";
    public int TextOffsetY { get; set; } = -2;
    public int PaddingY { get; set; } = 5;
    public string Horizontal { get; set; } = "center";
    public string Vertical { get; set; } = "bottom";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    /// <summary>描くポインターの動き: Off（記録どおり）、Weak（直線で補間）、Strong（揺れを除いて曲線で補間）。</summary>
    public string PointerSmoothing { get; set; } = "Off";
}

/// <summary>動画書き出しの自動ズーム（R6）。秒はクリックの時刻を基準にする。</summary>
public sealed record ZoomSettings
{
    public bool Enabled { get; set; }
    public double Scale { get; set; } = 1.6;
    /// <summary>クリックの何秒前から寄り始めるか。</summary>
    public double Lead { get; set; } = .6;
    /// <summary>最後のクリックの後、何秒寄ったままにするか。</summary>
    public double Hold { get; set; } = 1.2;
    public double ZoomIn { get; set; } = .5;
    public double ZoomOut { get; set; } = .6;
    /// <summary>次のクリックまでがこの秒数以内なら、寄ったまま中心だけを移す。</summary>
    public double MergeGap { get; set; } = 1.5;
    public double Pan { get; set; } = .6;
}

/// <summary>無操作区間のカット・早送り（R7）。Modeは Off / Cut / Speed。</summary>
public sealed record IdleSettings
{
    public string Mode { get; set; } = "Off";
    /// <summary>入力も画面の変化もない状態が、この秒数以上続いたら無操作区間とする。</summary>
    public double MinSeconds { get; set; } = 3;
    /// <summary>無操作区間の前後に残す秒数。</summary>
    public double Margin { get; set; } = .5;
    /// <summary>早送りの倍率。</summary>
    public double Speed { get; set; } = 4;
    [System.Text.Json.Serialization.JsonIgnore] public bool Enabled => Mode is "Cut" or "Speed";
}

/// <summary>静止判定・重複除外の許容差。0・0は完全一致。</summary>
public sealed record StabilitySettings
{
    public static StabilitySettings Exact => new() { ChannelThreshold = 0, MaxChangedRatio = 0 };
    public static StabilitySettings Standard => new();
    public static StabilitySettings Loose => new() { ChannelThreshold = 24, MaxChangedRatio = .005 };
    public int ChannelThreshold { get; set; } = 8;
    public double MaxChangedRatio { get; set; } = .0005;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsExact => ChannelThreshold == 0 && MaxChangedRatio == 0;
}

/// <summary>ステップ抽出の設定。秒は操作の時刻からの相対値。</summary>
public sealed record StepSettings
{
    public double PreSeconds { get; set; } = .2;
    public double PostSeconds { get; set; } = .5;
    public bool IncludeWheel { get; set; }
    public bool SaveAfter { get; set; }
    /// <summary>trueなら手順書の画像を操作位置の周り（切り出し範囲の縦横1/2）に絞る。</summary>
    public bool FocusImage { get; set; }
}

public sealed record AppSettings
{
    public string FfmpegPath { get; set; } = "";
    public string LastVideoPath { get; set; } = "";
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 850;
    public double Speed { get; set; } = 1;
    public bool RangeOnly { get; set; }
    public bool Loop { get; set; }
    public bool PingPong { get; set; }
    public bool StableOnly { get; set; } = true;
    public bool SkipDuplicates { get; set; } = true;
    public StabilitySettings Stability { get; set; } = new();
    public StepSettings Steps { get; set; } = new();
    public ZoomSettings Zoom { get; set; } = new();
    public IdleSettings Idle { get; set; } = new();
    public int PngCompression { get; set; } = 3;
    public bool BurnOverlay { get; set; }
    public bool ExportTsv { get; set; } = true;
    public bool ExportSrt { get; set; }
    public bool ExportVtt { get; set; }
    public string CanvasColor { get; set; } = "#787878";
    public string CropColor { get; set; } = "#FF8C00";
    public OverlayOptions Overlay { get; set; } = new();
    public List<ResolutionPreset> Presets { get; set; } = [new("VGA",640,480),new("SVGA",800,600),new("HD",1280,720),new("FHD",1920,1080),new("正方形",1080,1080),new("縦型",1080,1920),new("アイコン",32,32)];
}

public enum ExportKind { PngSequence, Mp4, Gif }
public sealed record ExportRequest(MediaInfo Media, CropRect Crop, double Start, double End, string OutputPath, ExportKind Kind, AppSettings Settings)
{
    public IReadOnlyList<MaskRegion> Masks { get; init; } = [];
}
public sealed record ExportProgress(int Completed, int Total, string Message);
public sealed record ExportResult(string OutputPath, int FrameCount);
