namespace FactorioModStudio.Core;

public sealed record ModSummary(
    string? Name,
    string? Version,
    string? Title,
    string? Author,
    string? Description,
    string? FactorioVersion,
    IReadOnlyList<string> Dependencies);

/// <param name="Category">"script", "locale", "graphics", "sound", "data", "text" or "other".</param>
/// <param name="Role">For well-known Lua entry points, what the file is for (e.g. "Data stage: prototypes").</param>
public sealed record ModFileEntry(string Path, long Size, string Category, string? Role)
{
    /// <summary>For graphics, sound and script files: whether nothing in the mod refers to it (see <see cref="ModReferences"/>).
    /// Null for every other category, where the question does not apply.</summary>
    public bool? Unused { get; init; }
}

/// <param name="Icon">Path of the icon inside the mod, when the prototype's icon points into the same mod.</param>
/// <param name="Prototypes">Names of the prototypes the block's file defines.</param>
public sealed record BlockSummary(string File, string? Title, IReadOnlyList<string> Prototypes);

public sealed record PrototypeInfo(string Type, string Name, string? DisplayName, string? Icon, string File, int Line)
{
    /// <summary>For a copy of an existing prototype (table.deepcopy, the building blocks): the original's name. The copy has the
    /// original's values except what the file changes, so the recipe of a copied recipe is often not known here.</summary>
    public string? CopyOf { get; init; }
}

public sealed record RecipeItem(string Type, string Name, double? Amount);

public sealed record RecipeInfo(
    string Name,
    string? DisplayName,
    string? Category,
    double? EnergyRequired,
    IReadOnlyList<RecipeItem> Ingredients,
    IReadOnlyList<RecipeItem> Results,
    string File,
    int Line);

/// <summary>An existing prototype (from the base game or another mod) that this mod references through <c>data.raw</c>.</summary>
public sealed record TouchedPrototype(string Type, string Name, string File, int Line);

/// <summary>Everything the studio shows about one mod. All content is derived statically; no Lua is executed.</summary>
public sealed record ModOverview(
    ModSummary? Info,
    IReadOnlyList<ModFileEntry> Files,
    IReadOnlyList<PrototypeInfo> Prototypes,
    IReadOnlyList<RecipeInfo> Recipes,
    IReadOnlyList<TouchedPrototype> Touched,
    IReadOnlyList<string> Languages,
    int DynamicEntries,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<string> Notes)
{
    /// <summary>Per-language key counts, "en" first, then alphabetical. Missing/extra are relative to the default language.</summary>
    public IReadOnlyList<LocaleCoverage> LocaleCoverage { get; init; } = Array.Empty<LocaleCoverage>();

    /// <summary>The building blocks in the mod (Lua files written by <see cref="ModContent"/>), whose properties can be edited.</summary>
    public IReadOnlyList<BlockSummary> Blocks { get; init; } = Array.Empty<BlockSummary>();

    /// <summary>Release checklist (see <see cref="ReleaseReadiness"/>). Filled per request by the UI server, never cached.</summary>
    public IReadOnlyList<ReadinessCheck> Readiness { get; init; } = Array.Empty<ReadinessCheck>();
}
