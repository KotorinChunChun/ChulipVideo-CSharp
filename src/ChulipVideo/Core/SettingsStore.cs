using System.Text.Json;

namespace ChulipVideo.Core;

public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Path { get; } = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotorinToys", "ChulipVideo", "settings.json");
    public string? Warning { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? throw new InvalidDataException("設定が空です。");
            settings.Speed = double.IsFinite(settings.Speed) ? Math.Clamp(settings.Speed,-9.9,9.9) : 1;
            settings.PngCompression = Math.Clamp(settings.PngCompression,0,9);
            settings.WindowWidth = Math.Clamp(settings.WindowWidth,800,3000);
            settings.WindowHeight = Math.Clamp(settings.WindowHeight,650,2000);
            settings.Presets = (settings.Presets ?? []).Where(p=>p is not null && p.Width is >0 and <=16384 && p.Height is >0 and <=16384).ToList();
            settings.FfmpegPath ??= "";
            settings.LastVideoPath ??= "";
            settings.CanvasColor = ColorText.Normalize(settings.CanvasColor, "#787878");
            settings.CropColor = ColorText.Normalize(settings.CropColor, "#FF8C00");
            settings.Overlay ??= new();
            NormalizeOverlay(settings.Overlay);
            settings.Stability ??= new();
            settings.Stability.ChannelThreshold = Math.Clamp(settings.Stability.ChannelThreshold, 0, 255);
            settings.Stability.MaxChangedRatio = FiniteClamp(settings.Stability.MaxChangedRatio, 0, .2);
            settings.Steps ??= new();
            settings.Steps.PreSeconds = FiniteClamp(settings.Steps.PreSeconds, 0, 5);
            settings.Steps.PostSeconds = FiniteClamp(settings.Steps.PostSeconds, 0, 5);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            Warning = $"設定を読み込めませんでした。初期値で開始します: {ex.Message}";
            return new();
        }
    }

    private static void NormalizeOverlay(OverlayOptions o)
    {
        o.FontSize=Math.Clamp(o.FontSize,8,160);o.MaxStack=Math.Clamp(o.MaxStack,1,12);
        o.PointerSize=Math.Clamp(o.PointerSize,1,200);o.RippleSize=Math.Clamp(o.RippleSize,1,300);
        o.PointerWidth=Math.Clamp(o.PointerWidth,1,32);o.ClickWidth=Math.Clamp(o.ClickWidth,1,32);
        o.RippleDuration=FiniteClamp(o.RippleDuration,0.05,10);o.FadeDuration=FiniteClamp(o.FadeDuration,0.1,30);
        o.OffsetX=Math.Clamp(o.OffsetX,-8192,8192);o.OffsetY=Math.Clamp(o.OffsetY,-8192,8192);
        o.TextOffsetY=Math.Clamp(o.TextOffsetY,-100,100);o.PaddingY=Math.Clamp(o.PaddingY,0,100);
        o.PointerShape??="cursor";o.ClickShape??="circle";
        o.FontFamily??="Yu Gothic UI";o.Horizontal??="center";o.Vertical??="bottom";
        // 設定画面と同じ#RRGGBBだけを受け付ける。手編集等の不正値は既定色へ戻す。
        o.PointerColor=ColorText.Normalize(o.PointerColor,"#202020");o.PointerFill=ColorText.Normalize(o.PointerFill,"",true);
        o.LeftColor=ColorText.Normalize(o.LeftColor,"#FF4444");o.RightColor=ColorText.Normalize(o.RightColor,"#4488FF");o.MiddleColor=ColorText.Normalize(o.MiddleColor,"#44CC66");
        o.TextColor=ColorText.Normalize(o.TextColor,"#FFFFFF");o.BackgroundColor=ColorText.Normalize(o.BackgroundColor,"#202020",true);
        o.OutlineColor=ColorText.Normalize(o.OutlineColor,"",true);
    }

    public void Save(AppSettings settings)
    {
        if(Warning is not null && File.Exists(Path))
        {
            File.Copy(Path,Path+".corrupt-"+DateTime.Now.ToString("yyyyMMddHHmmssfff")+".json",false);
            Warning=null;
        }
        WriteAtomic(Path, settings);
    }
    public static string VideoPath(string video) => System.IO.Path.ChangeExtension(video, ".chulip.settings.json");

    public static VideoSettings LoadVideo(MediaInfo media, out string? warning)
    {
        warning = null;
        var settings = new VideoSettings { Crop = CropRect.Full(media), Start = 0, End = media.Duration };
        var target = VideoPath(media.Path);
        if (!File.Exists(target)) target = System.IO.Path.ChangeExtension(media.Path, ".settings.json");
        if (!File.Exists(target)) return settings;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(target));
            var root = doc.RootElement;
            if (root.TryGetProperty("crop_rect", out var crop))
            {
                var x = crop.GetProperty("x1").GetInt32(); var y = crop.GetProperty("y1").GetInt32();
                settings.Crop = new(x,y,crop.GetProperty("x2").GetInt32()-x,crop.GetProperty("y2").GetInt32()-y);
                if(root.TryGetProperty("start_time",out var start)) settings.Start = start.GetDouble();
                if(root.TryGetProperty("end_time",out var end)) settings.End = end.GetDouble();
                if(root.TryGetProperty("current_time",out var pos)) settings.Position = pos.GetDouble();
                if(root.TryGetProperty("allow_oversize",out var outside)) settings.AllowOutside = outside.GetBoolean();
            }
            else settings = root.Deserialize<VideoSettings>(Json) ?? throw new InvalidDataException("動画設定が空です。");
            settings.Crop = settings.Crop.Clamp(media.Width,media.Height,settings.AllowOutside);
            settings.Start = FiniteClamp(settings.Start,0,media.LastFrameTime);
            settings.End = FiniteClamp(settings.End,Math.Min(media.Duration,settings.Start+1/media.Fps),media.Duration);
            settings.Position = FiniteClamp(settings.Position,0,media.LastFrameTime);
            return settings;
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            warning = $"動画設定を読み込めませんでした: {ex.Message}";
            return new() { Crop = CropRect.Full(media), End = media.Duration };
        }
    }

    private static double FiniteClamp(double x,double min,double max) => Math.Clamp(double.IsFinite(x)?x:min,min,Math.Max(min,max));
    public static void SaveVideo(MediaInfo info, VideoSettings settings) => WriteAtomic(VideoPath(info.Path),settings);
    private static void WriteAtomic<T>(string path,T value) => AtomicFile.WriteAllText(path,JsonSerializer.Serialize(value,Json));
}
