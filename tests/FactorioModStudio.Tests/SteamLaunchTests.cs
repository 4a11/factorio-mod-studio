using System.Diagnostics;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>The Steam copy of Factorio gets its app id, so it does not restart itself through Steam and exit at once.</summary>
public class SteamLaunchTests
{
    [Theory]
    [InlineData(@"D:\steam\steamapps\common\Factorio\bin\x64\factorio.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\SteamApps\common\Factorio\bin\x64\factorio.exe", true)]
    [InlineData("/home/me/.local/share/Steam/steamapps/common/Factorio/bin/x64/factorio", true)]
    [InlineData(@"C:\Games\Factorio\bin\x64\factorio.exe", false)]
    [InlineData(@"C:\Games\steamappsish\factorio.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSteamInstall_LooksForASteamLibrary(string? path, bool steam) => Assert.Equal(steam, SteamLaunch.IsSteamInstall(path));

    [Fact]
    public void Apply_SetsTheAppIdOnlyForSteamCopies()
    {
        var steam = new ProcessStartInfo(@"D:\steam\steamapps\common\Factorio\bin\x64\factorio.exe");
        SteamLaunch.Apply(steam);
        Assert.Equal("427520", steam.Environment["SteamAppId"]);
        Assert.Equal("427520", steam.Environment["SteamGameId"]);

        var standalone = new ProcessStartInfo(@"C:\Games\Factorio\bin\x64\factorio.exe");
        var before = standalone.Environment.ContainsKey("SteamAppId") ? standalone.Environment["SteamAppId"] : null;
        SteamLaunch.Apply(standalone);
        Assert.Equal(before, standalone.Environment.ContainsKey("SteamAppId") ? standalone.Environment["SteamAppId"] : null);
    }
}
