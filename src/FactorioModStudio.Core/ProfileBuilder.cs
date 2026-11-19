using System.Text.Json;

namespace FactorioModStudio.Core;

public sealed record PreparedProfile(
    string Name,
    string Directory,
    string ModsDirectory,
    IReadOnlyList<string> EnabledMods,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds an isolated mods directory for a profile under <c>.fmstudio/profiles/NAME/mods</c>:
/// a copy of the project mod, its resolved dependencies and a matching <c>mod-list.json</c>.
/// Factorio is then started with <c>--mod-directory</c> pointing there, so the user's normal mods never interfere.
/// </summary>
public static class ProfileBuilder
{
    private static readonly HashSet<string> Dlc = new() { "elevated-rails", "quality", "space-age" };
    private const string PreservedFile = "mod-settings.dat";

    public static PreparedProfile Prepare(ProjectContext context, string profileName)
    {
        var profile = context.GetProfile(profileName);

        var modDir = context.ModDirectory;
        var infoPath = Path.Combine(modDir, "info.json");
        if (!File.Exists(infoPath)) throw new FmsException($"info.json not found in \"{modDir}\".");

        ModInfo info;
        try { info = ModInfo.Load(infoPath); }
        catch (JsonException ex) { throw new FmsException($"info.json is not valid JSON: {ex.Message}", ex); }

        if (info.Name is null || !ModVersion.TryParse(info.Version, out var version))
            throw new FmsException("info.json needs a valid \"name\" and \"version\" (run `fmstudio validate`).");

        var warnings = new List<string>();
        var sourceDir = context.ModsSourceDirectory;
        var available = sourceDir is not null ? ModSource.Scan(sourceDir) : new List<AvailableMod>();

        var resolution = DependencyResolver.Resolve(info, available, profile.Mods);
        if (resolution.Error is not null) throw new FmsException(resolution.Error);
        var dlcRequested = resolution.Bundled.ToList();
        var toCopy = resolution.Mods;
        var enabled = new List<string> { "base", info.Name };
        enabled.AddRange(toCopy.Select(m => m.Name));
        enabled.AddRange(dlcRequested);
        foreach (var dep in info.ParsedDependencies.Concat(toCopy.SelectMany(m => m.Dependencies))
                     .Where(d => d.Kind == DependencyKind.Incompatible && DependencyResolver.IsBundled(d.Name) && enabled.Contains(d.Name)))
            warnings.Add($"The profile enables {dep.Name}, but declares incompatibility '{dep}'. Check the installed game version.");

        var profileDir = Path.Combine(context.ProfilesDirectory, profileName);
        var modsDir = Path.Combine(profileDir, "mods");
        Directory.CreateDirectory(modsDir);
        CleanModsDirectory(modsDir);

        CopyProjectMod(modDir, Path.Combine(modsDir, $"{info.Name}_{version}"), context.StateDirectory);
        foreach (var mod in toCopy) CopyAvailableMod(mod, modsDir);

        WriteModList(Path.Combine(modsDir, "mod-list.json"), enabled, info.FactorioVersion, dlcRequested);

        return new PreparedProfile(profileName, profileDir, modsDir, enabled, warnings);
    }

    private static void CleanModsDirectory(string modsDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(modsDir))
        {
            if (string.Equals(Path.GetFileName(entry), PreservedFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    private static void CopyProjectMod(string modDir, string destination, string stateDirectory)
    {
        foreach (var relative in ModFiles.Enumerate(modDir, stateDirectory))
        {
            var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(modDir, relative.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
        }
    }

    private static void CopyAvailableMod(AvailableMod mod, string modsDir)
    {
        var name = Path.GetFileName(mod.Path);
        var target = Path.Combine(modsDir, name);
        if (mod.IsDirectory) CopyTree(mod.Path, target);
        else File.Copy(mod.Path, target, overwrite: true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void WriteModList(string path, IReadOnlyList<string> enabled, string? factorioVersion, IReadOnlyList<string> dlcRequested)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteStartArray("mods");

        foreach (var name in enabled) WriteEntry(writer, name, true);

        // Factorio 2.x and later list the bundled DLC mods explicitly; disable the ones the profile did not ask for.
        if (factorioVersion is not null && int.TryParse(factorioVersion.Split('.')[0], out var major) && major >= 2)
            foreach (var dlc in Dlc.OrderBy(n => n, StringComparer.Ordinal).Where(n => !dlcRequested.Contains(n)))
                WriteEntry(writer, dlc, false);

        writer.WriteEndArray();
        writer.WriteEndObject();

        static void WriteEntry(Utf8JsonWriter w, string name, bool enabled)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteBoolean("enabled", enabled);
            w.WriteEndObject();
        }
    }
}
