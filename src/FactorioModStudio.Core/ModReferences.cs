using FactorioModStudio.Core.Lua;

namespace FactorioModStudio.Core;

/// <param name="Unused">Per file path: true / false for graphics, sound and script files; absent for everything else.</param>
/// <param name="RuntimeOnly">
/// Lua files reached through literal <c>require</c>s from <c>control.lua</c> but from no data or settings stage file. Their tables
/// such as <c>{ type = "item", name = ... }</c> are runtime data (GUI, remote interfaces, presets for other mods), not prototypes.
/// </param>
public sealed record ModReferenceReport(IReadOnlyDictionary<string, bool> Unused, IReadOnlyList<ValidationIssue> Issues,
    IReadOnlySet<string> RuntimeOnly);

/// <summary>
/// Static cross-references between the files of a mod: <c>__mod__/path</c> asset literals and <c>require</c> calls in Lua.
/// Finds broken references (FMS030 missing asset, FMS031 asset letter-case mismatch, FMS032 missing require; reported through
/// <see cref="ModValidator"/>) and graphics, sounds and scripts that nothing refers to. Heuristic and token based: no Lua is executed, so a path built at run time is only seen through its literal prefix
/// (<c>"__mod__/graphics/" .. name</c> marks everything under <c>graphics/</c> as possibly used).
/// </summary>
public static class ModReferences
{
    private const long MaxLuaFileBytes = 3 * 1024 * 1024;

    /// <summary>Files Factorio loads by itself, or that are not referenced from Lua at all.</summary>
    private static readonly HashSet<string> EntryFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "data.lua", "data-updates.lua", "data-final-fixes.lua", "control.lua",
        "settings.lua", "settings-updates.lua", "settings-final-fixes.lua",
        "thumbnail.png", "info.json", "changelog.txt"
    };

    /// <summary>Where the prototype (and settings) stages start; whatever these reach can define prototypes.</summary>
    private static readonly string[] DataStageRoots =
        { "data.lua", "data-updates.lua", "data-final-fixes.lua", "settings.lua", "settings-updates.lua", "settings-final-fixes.lua" };

    private static readonly string[] EntryFolders = { "locale/", "migrations/", "scenarios/", "campaigns/", "tutorials/" };

    /// <summary>Modules of Factorio's core/lualib that mods require by bare name (<c>require("util")</c>); never reported missing.</summary>
    private static readonly HashSet<string> CoreLualib = new(StringComparer.Ordinal)
    {
        "util", "mod-gui", "math2d", "math3d", "event_handler", "collision-mask-util", "noise", "story", "silo-script",
        "production-score", "bonus-gui-ordering", "dataloader", "inspect", "camera", "kill-score", "meld", "flying-text",
        "circuit-connector-sprites", "circuit-connector-generated-definitions", "autoplace_utils", "resource-autoplace",
        "sound-util", "item-sounds", "item_sounds", "pipecovers", "builtin-silo-script", "surface-render-parameter-effects",
        "crash-site", "space-finish-script"
    };

    private static readonly HashSet<string> Tracked = new(StringComparer.Ordinal) { "graphics", "sound", "script" };

    /// <param name="root">The mod directory.</param>
    /// <param name="modName">info.json "name"; without it asset literals cannot be recognised and only requires are checked.</param>
    /// <param name="files">The mod's files as returned by <see cref="ModFiles.Enumerate"/> ('/'-separated, relative).</param>
    public static ModReferenceReport Analyze(string root, string? modName, IReadOnlyList<string> files)
    {
        var issues = new List<ValidationIssue>();
        var exact = new HashSet<string>(files, StringComparer.Ordinal);
        var insensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) insensitive.TryAdd(f, f);

        var referenced = new HashSet<string>(StringComparer.Ordinal); // asset paths and required scripts
        var requires = new Dictionary<string, List<string>>(StringComparer.Ordinal); // script -> the mod scripts it requires
        var prefixes = new List<string>(); // dynamic asset prefixes
        var assetPrefix = modName is null ? null : $"__{modName}__/";

        foreach (var file in files.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            List<Token> tokens;
            try
            {
                var full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(full).Length > MaxLuaFileBytes) continue;
                tokens = LuaLexer.Tokenize(File.ReadAllText(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var requireArguments = new HashSet<int>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (RequireArgument(tokens, i) is not { } arg) continue;
                requireArguments.Add(arg);
                var target = ResolveRequire(tokens[arg].Text, file, modName, exact, insensitive, out var check);
                if (target is not null)
                {
                    referenced.Add(target);
                    if (!requires.TryGetValue(file, out var targets)) requires[file] = targets = new List<string>();
                    targets.Add(target);
                }
                else if (check)
                    issues.Add(new ValidationIssue(Severity.Error, "FMS032",
                        $"require(\"{tokens[arg].Text}\") does not match a .lua file in the mod.", file, tokens[arg].Line));
            }

            if (assetPrefix is null) continue;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind != TokenKind.String || requireArguments.Contains(i) ||
                    !token.Text.StartsWith(assetPrefix, StringComparison.Ordinal)) continue;

                var path = token.Text[assetPrefix.Length..];
                // string.format template ("graphics/icons/ore/%d/%d.png"): only the part before the first % is a real path.
                if (path.IndexOf('%') is var percent and >= 0)
                {
                    prefixes.Add(path[..percent]);
                    continue;
                }
                var concatenated = i + 1 < tokens.Count && tokens[i + 1] is { Kind: TokenKind.Symbol, Text: ".." };
                // No extension: a prefix completed later through a variable ("tier-badges/tier-badge-", then iconPath .. tier .. ".png").
                var noExtension = Path.GetExtension(path).Length == 0;
                if (concatenated || noExtension || path.Length == 0 || path.EndsWith('/') || Directory.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
                {
                    prefixes.Add(path);
                    continue;
                }

                if (exact.Contains(path)) { referenced.Add(path); continue; }

                if (insensitive.TryGetValue(path, out var actual))
                {
                    referenced.Add(actual);
                    issues.Add(new ValidationIssue(Severity.Error, "FMS031",
                        $"Referenced \"{path}\" differs in letter case from the real file \"{actual}\"; this works on Windows but fails inside the zip and on Linux.",
                        file, token.Line));
                }
                else
                {
                    issues.Add(new ValidationIssue(Severity.Error, "FMS030", $"Referenced file \"{path}\" does not exist in the mod.", file, token.Line));
                }
            }
        }

        var categories = files.ToDictionary(f => f, ModInspector.Categorize, StringComparer.Ordinal);
        var unused = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!Tracked.Contains(categories[file])) continue;
            var used = IsEntry(file) || referenced.Contains(file) || prefixes.Any(p => file.StartsWith(p, StringComparison.Ordinal));
            unused[file] = !used;
        }

        return new ModReferenceReport(unused, issues, RuntimeOnly(files, requires));
    }

    /// <summary>Scripts reachable from control.lua minus those reachable from a data or settings stage root.</summary>
    private static HashSet<string> RuntimeOnly(IReadOnlyList<string> files, Dictionary<string, List<string>> requires)
    {
        HashSet<string> Reach(IEnumerable<string> roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(roots.Select(r => files.FirstOrDefault(f => f.Equals(r, StringComparison.OrdinalIgnoreCase))).OfType<string>());
            while (queue.Count > 0)
            {
                var file = queue.Dequeue();
                if (!seen.Add(file)) continue;
                if (requires.TryGetValue(file, out var targets))
                    foreach (var target in targets) queue.Enqueue(target);
            }
            return seen;
        }

        var runtime = Reach(new[] { "control.lua" });
        runtime.ExceptWith(Reach(DataStageRoots));
        return runtime;
    }

    /// <summary>True for files that count as used without any reference (entry points, locale, migrations, scenarios, ...).</summary>
    public static bool IsEntry(string relativePath) =>
        EntryFiles.Contains(relativePath) || EntryFolders.Any(f => relativePath.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Index of the string literal in <c>require("x")</c>, <c>require "x"</c> or <c>require('x')</c> at <paramref name="i"/>.</summary>
    private static int? RequireArgument(List<Token> tokens, int i)
    {
        if (tokens[i] is not { Kind: TokenKind.Name, Text: "require" }) return null;
        if (i > 0 && tokens[i - 1] is { Kind: TokenKind.Symbol, Text: "." or ":" }) return null; // some.require(...)

        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.String) return i + 1;
        if (i + 3 < tokens.Count && tokens[i + 1] is { Kind: TokenKind.Symbol, Text: "(" } &&
            tokens[i + 2].Kind == TokenKind.String && tokens[i + 3] is { Kind: TokenKind.Symbol, Text: ")" })
            return i + 2;
        return null; // not a literal argument: ignored
    }

    /// <summary>
    /// The mod file a require name points to, or null. <paramref name="check"/> is false when the name belongs to another mod or to
    /// Factorio's own Lua library, i.e. when "not found" must not be reported.
    /// </summary>
    private static string? ResolveRequire(string name, string requiringFile, string? modName,
        HashSet<string> exact, Dictionary<string, string> insensitive, out bool check)
    {
        check = true;
        var fromRootOnly = false;
        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            var end = name.IndexOf("__", 2, StringComparison.Ordinal);
            if (end < 0) return null;
            var owner = name[2..end];
            if (modName is null || owner != modName) { check = false; return null; }
            name = name[(end + 2)..].TrimStart('.', '/');
            fromRootOnly = true;
        }
        else if (CoreLualib.Contains(name))
        {
            check = false;
        }

        if (name.Length == 0) return null;
        var relative = name.Replace('.', '/') + ".lua";

        var candidates = new List<string>();
        var directory = requiringFile.Contains('/') ? requiringFile[..(requiringFile.LastIndexOf('/') + 1)] : "";
        if (!fromRootOnly && directory.Length > 0) candidates.Add(directory + relative);
        candidates.Add(relative);

        foreach (var candidate in candidates)
            if (exact.Contains(candidate)) { check = false; return candidate; }

        // A case-only difference still loads on Windows; count the file as used and let the asset rules stay about assets.
        foreach (var candidate in candidates)
            if (insensitive.TryGetValue(candidate, out var actual)) { check = false; return actual; }

        return null;
    }
}
