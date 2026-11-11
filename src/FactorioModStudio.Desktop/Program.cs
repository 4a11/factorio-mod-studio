using Avalonia;

namespace FactorioModStudio.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // "Restart now" after an update: the window started this copy with --apply-update <its process id> and closed. Wait for
        // it, swap in the downloaded version and start that one; the same happens on any start while an update is waiting.
        int? waitFor = null;
        if (args is ["--apply-update", var pid, ..] && int.TryParse(pid, out var id))
        {
            waitFor = id;
            args = args[2..];
        }
        if (FactorioModStudio.Core.StudioUpdates.ApplyPendingAtStart("FactorioModStudio", args, waitForExit: false, waitFor) is { } code) return code;

        // What the last update left behind (the old program files) can go now that they no longer run.
        FactorioModStudio.Core.StudioUpdates.CleanUp();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}
