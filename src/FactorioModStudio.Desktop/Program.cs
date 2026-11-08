using Avalonia;

namespace FactorioModStudio.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // What the last update left behind (the old program files) can go now that they no longer run.
        FactorioModStudio.Core.StudioUpdates.CleanUp();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}
