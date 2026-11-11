using System.Diagnostics;

namespace FactorioModStudio.Core;

/// <summary>
/// Starting the Steam copy of Factorio directly. Started by anything but Steam, the game asks Steam to start it again ("Steam
/// requires game restart, restarting...") and exits at once with code 0: whoever started it loses the game (no log, no exit
/// code, no way to stop it). Steam itself passes the game's app id in the environment; with that set the game stays the process
/// that was started and still talks to a running Steam client.
/// </summary>
public static class SteamLaunch
{
    /// <summary>Factorio's app id on Steam.</summary>
    public const string FactorioAppId = "427520";

    /// <summary>True when <paramref name="executable"/> lies in a Steam library ("…/steamapps/common/…").</summary>
    public static bool IsSteamInstall(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return false;
        var parts = executable.Split('/', '\\');
        return parts.Any(p => p.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The environment variables to start <paramref name="executable"/> with (none for copies not from Steam).</summary>
    public static IReadOnlyDictionary<string, string> Environment(string? executable) =>
        IsSteamInstall(executable)
            ? new Dictionary<string, string> { ["SteamAppId"] = FactorioAppId, ["SteamGameId"] = FactorioAppId }
            : new Dictionary<string, string>();

    /// <summary>Adds <see cref="Environment"/> for <see cref="ProcessStartInfo.FileName"/> to <paramref name="startInfo"/>.</summary>
    public static void Apply(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var (name, value) in Environment(startInfo.FileName)) startInfo.Environment[name] = value;
    }
}
