using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public enum DependencyKind
{
    Required,
    Optional,        // "? name"
    HiddenOptional,  // "(?) name"
    Incompatible,    // "! name"
    NoLoadOrder      // "~ name"
}

/// <summary>One entry of the "dependencies" array in info.json, e.g. <c>? flib &gt;= 0.15.0</c>.</summary>
public sealed record Dependency(DependencyKind Kind, string Name, string? Operator, ModVersion? Version)
{
    private static readonly Regex Pattern = new(
        @"^\s*(?<prefix>\(\?\)|[!?~])?\s*(?<name>[^<>=\s][^<>=]*?)\s*(?:(?<op><=|>=|<|>|=)\s*(?<ver>\d+\.\d+\.\d+))?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_\- ]+$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out Dependency dependency)
    {
        dependency = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = Pattern.Match(text);
        if (!m.Success) return false;

        var name = m.Groups["name"].Value.Trim();
        if (!NamePattern.IsMatch(name)) return false;

        ModVersion? version = null;
        string? op = null;
        if (m.Groups["op"].Success)
        {
            if (!ModVersion.TryParse(m.Groups["ver"].Value, out var parsed)) return false;
            version = parsed;
            op = m.Groups["op"].Value;
        }

        var kind = m.Groups["prefix"].Value switch
        {
            "!" => DependencyKind.Incompatible,
            "?" => DependencyKind.Optional,
            "(?)" => DependencyKind.HiddenOptional,
            "~" => DependencyKind.NoLoadOrder,
            _ => DependencyKind.Required
        };

        dependency = new Dependency(kind, name, op, version);
        return true;
    }

    /// <summary>True if <paramref name="candidate"/> satisfies this dependency's version constraint (or there is none).</summary>
    public bool IsSatisfiedBy(ModVersion candidate)
    {
        if (Operator is null || Version is null) return true;
        var required = Version.Value;
        return Operator switch
        {
            "=" => candidate == required,
            ">=" => candidate >= required,
            ">" => candidate > required,
            "<=" => candidate <= required,
            "<" => candidate < required,
            _ => true
        };
    }

    public override string ToString()
    {
        var prefix = Kind switch
        {
            DependencyKind.Incompatible => "! ",
            DependencyKind.Optional => "? ",
            DependencyKind.HiddenOptional => "(?) ",
            DependencyKind.NoLoadOrder => "~ ",
            _ => ""
        };
        return Operator is null ? $"{prefix}{Name}" : $"{prefix}{Name} {Operator} {Version}";
    }
}
