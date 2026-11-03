using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>A Factorio mod version: three numbers, each in 0..65535.</summary>
public readonly record struct ModVersion(int Major, int Minor, int Patch) : IComparable<ModVersion>
{
    private static readonly Regex Pattern = new(@"^(\d{1,5})\.(\d{1,5})\.(\d{1,5})$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out ModVersion version)
    {
        version = default;
        if (text is null) return false;

        var m = Pattern.Match(text.Trim());
        if (!m.Success) return false;

        int major = int.Parse(m.Groups[1].Value);
        int minor = int.Parse(m.Groups[2].Value);
        int patch = int.Parse(m.Groups[3].Value);
        if (major > ushort.MaxValue || minor > ushort.MaxValue || patch > ushort.MaxValue) return false;

        version = new ModVersion(major, minor, patch);
        return true;
    }

    public static ModVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid mod version (expected X.Y.Z).");

    public int CompareTo(ModVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
