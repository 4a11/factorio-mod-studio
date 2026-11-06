using System.Runtime.InteropServices;

namespace FactorioModStudio.Cli;

internal static class ConsoleWindow
{
    /// <summary>
    /// True when the program got a console window of its own, i.e. it was started by a double click in Explorer and not
    /// from a terminal. Such a window closes as soon as the program exits. Always false outside Windows.
    /// </summary>
    public static bool IsOwn()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
