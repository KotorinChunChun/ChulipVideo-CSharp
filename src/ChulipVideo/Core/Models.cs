namespace ChulipVideo.Core;

public sealed record MediaInfo(string Path, int Width, int Height, double Duration, double Fps)
{
    public double LastFrameTime => Math.Max(0, Duration - 1 / Fps);
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
    public string Horizontal { get; set; } = "center";
    public string Vertical { get; set; } = "bottom";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
}

public sealed record AppSettings
{
    public string FfmpegPath { get; set; } = "";
    public string LastVideoPath { get; set; } = "";
    public double Speed { get; set; } = 1;
    public bool RangeOnly { get; set; }
    public bool Loop { get; set; }
    public bool PingPong { get; set; }
    public bool StableOnly { get; set; } = true;
    public bool SkipDuplicates { get; set; } = true;
    public int PngCompression { get; set; } = 3;
    public bool BurnOverlay { get; set; }
    public bool ExportTsv { get; set; } = true;
    public bool ExportSrt { get; set; }
    public bool ExportVtt { get; set; }
    public string CanvasColor { get; set; } = "#20242B";
    public string CropColor { get; set; } = "#FF5566";
    public OverlayOptions Overlay { get; set; } = new();
    public List<ResolutionPreset> Presets { get; set; } = [new("VGA",640,480),new("SVGA",800,600),new("HD",1280,720),new("FHD",1920,1080),new("正方形",1080,1080),new("縦型",1080,1920),new("アイコン",32,32)];
}

public enum ExportKind { PngSequence, Mp4, Gif }
public sealed record ExportRequest(MediaInfo Media, CropRect Crop, double Start, double End, string OutputPath, ExportKind Kind, AppSettings Settings);
public sealed record ExportProgress(int Completed, int Total, string Message);
public sealed record ExportResult(string OutputPath, int FrameCount);
