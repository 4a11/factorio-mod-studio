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
}
