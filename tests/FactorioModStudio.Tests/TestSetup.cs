using System.Net;
using System.Runtime.CompilerServices;

namespace FactorioModStudio.Tests;

internal static class TestSetup
{
    /// <summary>
    /// The tests talk to a local server on 127.0.0.1. A system-wide proxy (e.g. a VPN client) would otherwise receive
    /// those requests too, and most proxies never answer for the loopback address, so every HTTP test would hang.
    /// </summary>
    [ModuleInitializer]
    internal static void BypassSystemProxy() => HttpClient.DefaultProxy = new WebProxy();

    /// <summary>
    /// The server listens on 127.0.0.1, but its URL says "localhost". On Windows a new connection to "localhost" tries ::1
    /// first and takes about two seconds to give up on it, which added minutes to the test run. Without IPv6 in the test
    /// process "localhost" goes straight to 127.0.0.1.
    /// </summary>
    [ModuleInitializer]
    internal static void PreferIPv4() => AppContext.SetSwitch("System.Net.DisableIPv6", true);

    /// <summary>
    /// The studio's per-user data folder (installed Factorio versions, the saved account, update settings) is a fresh empty
    /// one for the test run. Otherwise a developer who installed Factorio with the studio got about 20 failures (the tests
    /// found that Factorio instead of their fake one), and tests could write into the real folder.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateStudioDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fmstudio-tests-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(FactorioModStudio.Core.FactorioInstallations.DataDirectoryVariable, directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the temp folder gets cleaned anyway */ }
        };
    }
}
