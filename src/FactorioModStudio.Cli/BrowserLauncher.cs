using System.Diagnostics;

namespace FactorioModStudio.Cli;

internal static class BrowserLauncher
{
    /// <summary>Opens <paramref name="url"/> in the default browser. Failures are ignored; the URL is printed anyway.</summary>
    public static void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No browser available (e.g. a headless server). Nothing to do.
        }
    }
}
