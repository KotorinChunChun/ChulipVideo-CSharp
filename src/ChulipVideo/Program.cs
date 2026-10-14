namespace ChulipVideo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? Option(string name) { var i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : null; }
        var store=new Core.SettingsStore(Option("--settings"));
        var settings=store.Load();
        if(Option("--ffmpeg") is { } ffmpeg) settings.FfmpegPath=Path.GetFullPath(ffmpeg);
        Application.Run(new UI.MainForm(store,settings,Option("--video")));
    }
}
