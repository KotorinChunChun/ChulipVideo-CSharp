namespace ChulipVideo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, @"Local\ChulipVideo", out var firstInstance);
        if (!firstInstance) { KotorinToys.Integration.ManagementEndpoint.ShowExisting("chulip-video"); return; }
        try
        {
        string? Option(string name) { var i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : null; }
        var settingsPath = Option("--settings") ?? (Option("--settings-dir") is { } directory ? Path.Combine(directory, "settings.json") : null);
        var store=new Core.SettingsStore(settingsPath);
        var settings=store.Load();
        if(Option("--ffmpeg") is { } ffmpeg) settings.FfmpegPath=Path.GetFullPath(ffmpeg);
        var window = new UI.MainForm(store,settings,Option("--video"));
        using var context = new KotorinToys.Integration.ManagedToolContext("chulip-video", window,
            args.Contains("--startup"), () => window.ManagementStopBlockReason);
        Application.Run(context);
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}
