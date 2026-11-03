using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public sealed class ProfileConfig
{
    /// <summary>Extra mods to enable besides the project mod and its required dependencies, e.g. "flib >= 0.15.0".</summary>
    [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new();

    /// <summary>Extra command-line arguments passed to Factorio by <c>run</c> and <c>smoke</c>.</summary>
    [JsonPropertyName("args")] public List<string> Args { get; set; } = new();

    /// <summary>Optional save file to load instead of generating a fresh map (path relative to the project root).</summary>
    [JsonPropertyName("save")] public string? Save { get; set; }
}

/// <summary>Contents of <c>fmstudio.json</c>. Comments and trailing commas are allowed.</summary>
public sealed class ProjectConfig
{
    public const string FileName = "fmstudio.json";

    /// <summary>Folder containing the mod's info.json, relative to the project root.</summary>
    [JsonPropertyName("modPath")] public string ModPath { get; set; } = ".";

    /// <summary>Path to the Factorio executable or installation directory. Overridden by --factorio and FACTORIO_PATH.</summary>
    [JsonPropertyName("factorioPath")] public string? FactorioPath { get; set; }

    /// <summary>Directory holding other mods (usually Factorio's own "mods" folder) used to satisfy dependencies.</summary>
    [JsonPropertyName("modsSource")] public string? ModsSource { get; set; }

    /// <summary>
    /// Folder whose sub-folders are mods you work on (each with its own info.json), relative to the project root.
    /// The UI discovers them, watches them for changes and lets you edit their files. Defaults to "mods".
    /// </summary>
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }

    [JsonPropertyName("profiles")] public Dictionary<string, ProfileConfig> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ProjectConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectConfig>(json, ReadOptions) ?? new ProjectConfig();
        }
        catch (JsonException ex)
        {
            throw new FmsException($"{FileName} is invalid: {ex.Message}", ex);
        }
    }

    public const string Template = """
        {
          // Folder with the mod's info.json, relative to this file.
          "modPath": ".",

          // Factorio executable or installation folder. Alternatively set the FACTORIO_PATH environment variable.
          "factorioPath": null,

          // Folder with other mods (e.g. Factorio's mods folder). Dependencies of your mod are copied from here.
          "modsSource": null,

          // Folder whose sub-folders are mods you work on. `fmstudio ui` discovers them and lets you edit their files live.
          "workspace": "mods",

          "profiles": {
            "default": {
              "mods": [],
              "args": [],
              "save": null
            }
          }
        }
        """;
}

/// <summary>A project on disk: its root folder plus the parsed configuration.</summary>
public sealed class ProjectContext
{
    private static readonly Regex ProfileNamePattern = new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    public ProjectContext(string root, ProjectConfig config, bool configFileExists)
    {
        Root = Path.GetFullPath(root);
        Config = config;
        ConfigFileExists = configFileExists;
    }

    public string Root { get; }
    public ProjectConfig Config { get; }
    public bool ConfigFileExists { get; }

    public string ConfigPath => Path.Combine(Root, ProjectConfig.FileName);
    public string ModDirectory => Path.GetFullPath(Path.Combine(Root, Config.ModPath));
    public string StateDirectory => Path.Combine(Root, ".fmstudio");
    public string ProfilesDirectory => Path.Combine(StateDirectory, "profiles");

    public string? ModsSourceDirectory => Config.ModsSource is null ? null : ResolvePath(Config.ModsSource);

    /// <summary>The workspace folder (configured, or "mods" next to fmstudio.json). It may not exist yet.</summary>
    public string WorkspaceDirectory => ResolvePath(string.IsNullOrWhiteSpace(Config.Workspace) ? "mods" : Config.Workspace);

    public string ResolvePath(string path) => Path.GetFullPath(Path.Combine(Root, path));

    public static ProjectContext Load(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), ProjectConfig.FileName);
        if (!File.Exists(path)) return new ProjectContext(root, new ProjectConfig(), false);
        return new ProjectContext(root, ProjectConfig.Parse(File.ReadAllText(path)), true);
    }

    public ProfileConfig GetProfile(string name)
    {
        if (!ProfileNamePattern.IsMatch(name))
            throw new FmsException($"Invalid profile name \"{name}\" (letters, digits, '.', '_' and '-' only).");

        if (Config.Profiles.TryGetValue(name, out var profile)) return profile;
        if (name == "default") return new ProfileConfig();

        var known = Config.Profiles.Count == 0 ? "none defined" : string.Join(", ", Config.Profiles.Keys);
        throw new FmsException($"Profile \"{name}\" not found in {ProjectConfig.FileName} (available: {known}).");
    }
}
