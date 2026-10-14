namespace ChulipVideo;

public static class WindowTitle
{
    public static string Create(string? screen = null)
    {
        var v = typeof(WindowTitle).Assembly.GetName().Version!;
        var title = $"ChulipVideo - ver {v.Major}.{v.Minor}.{v.Build}";
        return string.IsNullOrWhiteSpace(screen) ? title : $"{screen} - {title}";
    }
}
