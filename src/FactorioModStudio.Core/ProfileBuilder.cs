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

        var enabled = new List<string> { "base", info.Name };
        var dlcRequested = new List<string>();
        var toCopy = new List<AvailableMod>();
        var incompatibilities = new List<(string By, Dependency Dep)>();

        var queue = new Queue<(Dependency Dep, string RequiredBy)>();
        foreach (var dep in info.ParsedDependencies)
        {
            if (dep.Kind == DependencyKind.Required) queue.Enqueue((dep, info.Name));
            else if (dep.Kind == DependencyKind.Incompatible) incompatibilities.Add((info.Name, dep));
        }

        foreach (var spec in profile.Mods)
        {
            if (!Dependency.TryParse(spec, out var dep))
                throw new FmsException($"Invalid mod \"{spec}\" in profile \"{profileName}\" (expected e.g. \"flib >= 0.15.0\").");
            queue.Enqueue((dep, $"profile \"{profileName}\""));
        }

        while (queue.Count > 0)
        {
            var (dep, requiredBy) = queue.Dequeue();
            var name = dep.Name;

            if (name is "base" or "core" || name == info.Name) continue;
            if (enabled.Contains(name)) continue;

            if (Dlc.Contains(name))
            {
                enabled.Add(name);
                dlcRequested.Add(name);
                continue;
            }

            var best = available.Where(m => m.Name == name).OrderByDescending(m => m.Version).FirstOrDefault();
            if (best is null)
            {
                var where = sourceDir is null
                    ? $"no \"modsSource\" is set in {ProjectConfig.FileName}"
                    : $"it was not found in \"{sourceDir}\"";
                throw new FmsException($"Mod \"{name}\" (needed by {requiredBy}) is unavailable: {where}.");
            }

            if (!dep.IsSatisfiedBy(best.Version))
                warnings.Add($"{name} {best.Version} does not satisfy \"{dep}\" required by {requiredBy}.");

            enabled.Add(name);
            toCopy.Add(best);

            foreach (var sub in best.Dependencies)
            {
                if (sub.Kind == DependencyKind.Required) queue.Enqueue((sub, name));
                else if (sub.Kind == DependencyKind.Incompatible) incompatibilities.Add((name, sub));
            }
        }

        foreach (var (by, dep) in incompatibilities.Where(i => enabled.Contains(i.Dep.Name)))
            warnings.Add($"{by} declares \"{dep}\" but {dep.Name} is enabled in this profile.");

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
