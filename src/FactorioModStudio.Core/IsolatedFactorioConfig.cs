using System.Text;

namespace FactorioModStudio.Core;

/// <summary>
/// A config.ini of the studio's own for runs that need no player settings (smoke tests, --dump-data): the game's data is read
/// from the installation, everything it writes (factorio-current.log, the .lock, crash dumps, script-output) goes to a folder of
/// the studio. The player's config, log and lock stay untouched, and such a run works while the player has the game open.
/// </summary>
public static class IsolatedFactorioConfig
{
    /// <summary>
    /// Writes <c>config.ini</c> into <paramref name="workDirectory"/> with write-data <c>&lt;workDirectory&gt;/write</c> and
    /// returns its path, or null when the installation's data folder is not found (then the run uses the game's own config).
    /// </summary>
    public static string? Write(string factorioExecutable, string workDirectory, string purpose)
    {
        var data = GameDataDump.DataDirectoryFor(factorioExecutable);
        if (data is null) return null;
        var write = Path.Combine(workDirectory, "write");
        Directory.CreateDirectory(write);
        var config = Path.Combine(workDirectory, "config.ini");
        File.WriteAllText(config, $"; Written by Factorio Mod Studio for {purpose}.\n[path]\nread-data={data}\nwrite-data={write}\n", new UTF8Encoding(false));
        return config;
    }

    /// <summary>The folder the game writes to under a config made by <see cref="Write"/>.</summary>
    public static string WriteDirectory(string workDirectory) => Path.Combine(workDirectory, "write");
}
