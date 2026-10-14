namespace ChulipVideo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BrandedForm { Text = WindowTitle.Create() });
    }
}
