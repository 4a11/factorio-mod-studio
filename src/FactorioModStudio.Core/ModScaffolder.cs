using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <param name="SupportedVersions">Factorio versions (MAJOR.MINOR) the generated mod is valid for.</param>
public sealed record TemplateInfo(string Id, string Title, string Description, IReadOnlyList<string> SupportedVersions);

/// <param name="Template">Id of one of <see cref="ModScaffolder.Templates"/>.</param>
/// <param name="FactorioVersion">Target game version, MAJOR.MINOR (default "2.0"); must be one of the template's supported versions.</param>
/// <param name="Title">Optional; defaults to the humanized name. Same for Author (FMS_AUTHOR or "Your Name") and Description.</param>
/// <param name="Content">Ids of <see cref="ModContent.Blocks"/> added to the new mod, each named "&lt;mod name&gt;-&lt;its default name&gt;".</param>
/// <param name="GameDataDirectory">Factorio's data folder; the blocks' icon files are recoloured copies of the originals in it.</param>
public sealed record ScaffoldRequest(string Name, string Template = "empty", string? Title = null, string? Author = null, string? Description = null, string FactorioVersion = ModScaffolder.DefaultFactorioVersion, IReadOnlyList<string>? Content = null, string? GameDataDirectory = null);

/// <param name="Folder">Full path of the new mod folder.</param>
/// <param name="Files">Relative paths with '/' separators, sorted ordinally.</param>
public sealed record ScaffoldResult(string Name, string Version, string Folder, IReadOnlyList<string> Files);

public enum ScaffoldError { InvalidName, UnknownTemplate, InvalidField, Exists, UnsupportedVersion }

/// <summary>A new mod could not be created. <see cref="Kind"/> says why; hosts map it to an exit code or HTTP status.</summary>
public sealed class ScaffoldException : FmsException
{
    public ScaffoldException(ScaffoldError kind, string message, string? existingPath = null) : base(message)
    {
        Kind = kind;
        ExistingPath = existingPath;
    }

    public ScaffoldError Kind { get; }

    /// <summary>For <see cref="ScaffoldError.Exists"/>: full path of the entry that is already there.</summary>
    public string? ExistingPath { get; }
}

/// <summary>
/// Creates a new mod from a built-in template. The mod is built in a hidden staging folder inside the parent directory
/// (<c>.fmstudio/new/&lt;guid&gt;</c>) and moved into place with one rename, so a failure leaves nothing behind and an existing
/// folder is never overwritten or merged.
/// </summary>
public static class ModScaffolder
{
    public const string InitialVersion = "0.1.0";
    public const string DefaultFactorioVersion = "2.0";
    public const string DefaultAuthor = "Your Name";
    public const string DefaultDescription = "A Factorio mod created with Factorio Mod Studio.";
    public const string AuthorEnvironmentVariable = "FMS_AUTHOR";
    public const int MaxTitleLength = 100;
    public const int MaxAuthorLength = 100;
    public const int MaxDescriptionLength = 500;

    private static readonly object Gate = new();
    private static readonly Regex Token = new(@"@@(?:(?<fmt>json|cfg):)?(?<key>name|title|author|description|date|version|factorio|store|storecomment)@@", RegexOptions.Compiled);

    public static IReadOnlyList<TemplateInfo> Templates { get; } = ScaffoldTemplates.All.Select(t => t.Info).ToList();

    /// <summary>Templates that are built from a building block on Factorio 2.0 and later.</summary>
    private static readonly Dictionary<string, string> BlockTemplates = new(StringComparer.Ordinal) { ["item-recipe"] = "item", ["entity"] = "chest" };

    public static ScaffoldResult Create(ScaffoldRequest request, string parentDirectory, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);

        var name = request.Name;
        if (!ModImporter.IsValidModName(name))
            throw new ScaffoldException(ScaffoldError.InvalidName,
                "A mod name has 1-100 letters, digits, \"_\" or \"-\" (no spaces or dots) and must not be a reserved system name.");

        var template = ScaffoldTemplates.All.FirstOrDefault(t => string.Equals(t.Info.Id, request.Template, StringComparison.Ordinal))
                       ?? throw new ScaffoldException(ScaffoldError.UnknownTemplate,
                           $"Unknown template \"{request.Template}\". Available: {string.Join(", ", Templates.Select(t => t.Id))}.");

        var targetVersion = (request.FactorioVersion ?? "").Trim();
        if (!Regex.IsMatch(targetVersion, @"^[0-9]{1,4}\.[0-9]{1,4}$", RegexOptions.CultureInvariant))
            throw new ScaffoldException(ScaffoldError.InvalidField, "The Factorio version must look like MAJOR.MINOR, for example 2.0.");
        targetVersion = Normalise(targetVersion);
        if (!template.Info.SupportedVersions.Contains(targetVersion, StringComparer.Ordinal))
            throw new ScaffoldException(ScaffoldError.UnsupportedVersion,
                $"The template \"{template.Info.Id}\" does not support Factorio {targetVersion} (supported: {string.Join(", ", template.Info.SupportedVersions)}).");
        var modern = int.Parse(targetVersion.Split('.')[0], CultureInfo.InvariantCulture) >= 2;

        // From Factorio 2.0 on, the item and chest templates are an empty mod plus a building block: a recoloured copy of the
        // iron gear wheel or the iron chest (see ModContent), named like before: <name>-item and <name>-chest.
        var requested = (request.Content ?? Array.Empty<string>()).ToList();
        if (modern && BlockTemplates.TryGetValue(template.Info.Id, out var blockId))
        {
            requested.Insert(0, blockId);
            template = ScaffoldTemplates.All.First(t => t.Info.Id == "empty");
        }

        var content = new List<ContentBlockInfo>();
        foreach (var id in requested.Distinct(StringComparer.Ordinal))
        {
            var block = ModContent.Blocks.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal))
                        ?? throw new ScaffoldException(ScaffoldError.UnknownTemplate,
                            $"Unknown block \"{id}\". Available: {string.Join(", ", ModContent.Blocks.Select(b => b.Id))}.");
            if (!block.SupportedVersions.Contains(targetVersion, StringComparer.Ordinal))
                throw new ScaffoldException(ScaffoldError.UnsupportedVersion,
                    $"The block \"{block.Id}\" is written for Factorio {string.Join(" and ", block.SupportedVersions)}, not {targetVersion}.");
            content.Add(block);
        }

        var title = CleanField(request.Title, "Title", MaxTitleLength) ?? Humanize(name);
        var author = CleanField(request.Author, "Author", MaxAuthorLength) ?? AuthorFromEnvironment() ?? DefaultAuthor;
        var description = CleanField(request.Description, "Description", MaxDescriptionLength) ?? DefaultDescription;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["title"] = title,
            ["author"] = author,
            ["description"] = description,
            ["date"] = (today ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["version"] = InitialVersion,
            ["factorio"] = targetVersion,
            // Factorio 2.0 renamed the persistent table `global` to `storage`.
            ["store"] = modern ? "storage" : "global",
            ["storecomment"] = modern
                ? "-- Factorio 2.0 and later keep persistent mod data in `storage`. Before 2.0 this table was called `global`."
                : "-- Factorio 1.x keeps persistent mod data in `global`. Factorio 2.0 renamed this table to `storage`."
        };

        // Render everything in memory first: nothing touches the disk if a template were broken.
        var rendered = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in template.Files)
        {
            var path = Render(file.Path, values);
            rendered[path] = file.Binary is { } generate
                ? generate(name)
                // LF always: the templates are C# string literals, whose line endings follow the checkout the tool was built from.
                : new UTF8Encoding(false).GetBytes(Render(file.Text!, values).Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        var parent = Path.GetFullPath(parentDirectory);
        var target = Path.Combine(parent, name);

        List<string> files;
        lock (Gate)
        {
            if (FindExisting(parent, name) is { } existing)
                throw Exists(name, existing);

            var createdParent = !Directory.Exists(parent);
            var staging = Path.Combine(parent, ".fmstudio", "new", Guid.NewGuid().ToString("N"));
            var moved = false;
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var (path, bytes) in rendered)
                {
                    var destination = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, bytes);
                }

                // Blocks go into the staging copy too, so the mod appears complete or not at all.
                foreach (var block in content)
                {
                    try { ModContent.Apply(staging, new ContentRequest(block.Id, $"{name}-{block.DefaultName}", ContentTitle(title, block.DefaultName)), request.GameDataDirectory); }
                    catch (ContentException ex) { throw new ScaffoldException(ScaffoldError.InvalidField, ex.Message); }
                }
                files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(staging, f).Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();

                try
                {
                    Directory.Move(staging, target);
                    moved = true;
                }
                catch (IOException) when (FindExisting(parent, name) is { } raced)
                {
                    throw Exists(name, raced);
                }
            }
            finally
            {
                if (!moved) TryDelete(staging);
                Tidy(parent, createdParent && !moved);
            }
        }

        return new ScaffoldResult(name, InitialVersion, target, files);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>"02.00" becomes "2.0".</summary>
    private static string Normalise(string version)
    {
        var parts = version.Split('.');
        return $"{int.Parse(parts[0], CultureInfo.InvariantCulture)}.{int.Parse(parts[1], CultureInfo.InvariantCulture)}";
    }

    private static ScaffoldException Exists(string name, string existing) =>
        new(ScaffoldError.Exists, $"\"{name}\" already exists in the target folder; nothing was changed.", existing);

    private static string? FindExisting(string parent, string name)
    {
        if (!Directory.Exists(parent)) return null;
        foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Removes the hidden folders again when they are empty (and the parent if we created it and the create failed).</summary>
    private static void Tidy(string parent, bool removeParentIfEmpty)
    {
        try
        {
            foreach (var dir in new[] { Path.Combine(parent, ".fmstudio", "new"), Path.Combine(parent, ".fmstudio") })
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (removeParentIfEmpty && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>"My Mod" and "solar-panel" give "My Mod solar panel"; shortened when that would be too long.</summary>
    private static string ContentTitle(string modTitle, string defaultName)
    {
        var title = $"{modTitle} {defaultName.Replace('-', ' ')}";
        return title.Length <= ModContent.MaxTitleLength ? title : Humanize(defaultName);
    }

    /// <summary>"hello-world" becomes "Hello World".</summary>
    internal static string Humanize(string name)
    {
        var words = name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var title = string.Join(' ', words);
        return title.Length == 0 ? name : title;
    }

    private static string? AuthorFromEnvironment()
    {
        try
        {
            return CleanField(Environment.GetEnvironmentVariable(AuthorEnvironmentVariable), "Author", MaxAuthorLength);
        }
        catch (ScaffoldException)
        {
            return null; // an unusable FMS_AUTHOR must not break creating mods
        }
    }

    /// <summary>Trims; null when nothing is left (use the default). Rejects control characters, lone surrogates and overlong text.</summary>
    private static string? CleanField(string? value, string label, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < ' ' || c == '\u007f')
                throw new ScaffoldException(ScaffoldError.InvalidField, $"{label} must not contain control characters (such as line breaks or tabs).");
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(c))
                throw new ScaffoldException(ScaffoldError.InvalidField, $"{label} contains invalid Unicode text.");
        }

        if (text.Length > maxLength)
            throw new ScaffoldException(ScaffoldError.InvalidField, $"{label} is longer than {maxLength} characters.");
        return text;
    }

    /// <summary>Single pass: substituted values are never scanned again, so a "@@x@@" typed by the user stays as typed.</summary>
    private static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Token.Replace(template, m =>
        {
            var value = values[m.Groups["key"].Value];
            return m.Groups["fmt"].Value switch
            {
                "json" => JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value,
                _ => value // "cfg": single-line values need no escaping (control characters are rejected up front)
            };
        });
}
