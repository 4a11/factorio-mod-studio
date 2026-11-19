namespace FactorioModStudio.Core;

public sealed record DependencyResolution(IReadOnlyList<AvailableMod> Mods, IReadOnlyList<string> Bundled, string? Error);
public sealed record DependencyStatus(string Specification, string Status, string? Version, string Detail);

/// <summary>Resolves a complete profile before any files are changed. Optional mods are not enabled implicitly.</summary>
public static class DependencyResolver
{
    public static bool IsBundled(string name) => name is "base" or "core" or "space-age" or "quality" or "elevated-rails";
    private static bool Required(Dependency d) => d.Kind is DependencyKind.Required or DependencyKind.NoLoadOrder;

    public static DependencyResolution Resolve(ModInfo info, IReadOnlyList<AvailableMod> available, IEnumerable<string> extras)
    {
        if (info.RawDependencies.Any(s => !Dependency.TryParse(s, out _))) return new([], [], "Invalid dependency in info.json.");
        var initial = info.ParsedDependencies.Select(d => (Dep: d, By: info.Name ?? "project")).ToList();
        foreach (var spec in extras)
        {
            if (!Dependency.TryParse(spec, out var dep)) return new([], [], $"Invalid profile dependency: {spec}.");
            initial.Add((dep with { Kind = DependencyKind.Required }, "profile"));
        }
        string? error = null;
        var attempts = 0;
        Dictionary<string, AvailableMod>? Search(Dictionary<string, AvailableMod> chosen)
        {
            if (++attempts > 10000) { error = "Dependency resolution exceeded its search limit. Narrow the version requirements."; return null; }
            var requirements = initial.Concat(chosen.Values.SelectMany(m => m.Dependencies.Select(d => (Dep: d, By: m.Name)))).ToList();
            foreach (var (dep, by) in requirements.Where(r => !IsBundled(r.Dep.Name)))
            {
                ModVersion? version = chosen.TryGetValue(dep.Name, out var selected) ? selected.Version :
                    dep.Name == info.Name && ModVersion.TryParse(info.Version, out var own) ? own : null;
                if (version is null) continue;
                var valid = dep.IsSatisfiedBy(version.Value);
                if (dep.Kind == DependencyKind.Incompatible ? valid : !valid)
                {
                    error = $"{by} requires '{dep}', but {dep.Name} {version} is selected. No compatible dependency set was found.";
                    return null;
                }
            }
            var next = requirements.FirstOrDefault(r => Required(r.Dep) && !IsBundled(r.Dep.Name) && r.Dep.Name != info.Name && !chosen.ContainsKey(r.Dep.Name));
            if (next.Dep is null) return chosen;
            var candidates = available.Where(m => m.Name == next.Dep.Name).OrderByDescending(m => m.Version).ToList();
            var constraints = requirements.Where(r => r.Dep.Name == next.Dep.Name).ToList();
            foreach (var candidate in candidates.Where(m => constraints.All(r => r.Dep.Kind == DependencyKind.Incompatible ? !r.Dep.IsSatisfiedBy(m.Version) : r.Dep.IsSatisfiedBy(m.Version))))
            {
                var branch = new Dictionary<string, AvailableMod>(chosen) { [candidate.Name] = candidate };
                var result = Search(branch);
                if (result is not null) return result;
                if (attempts > 10000) return null;
            }
            error ??= $"Cannot resolve {next.Dep.Name}: {string.Join("; ", constraints.Select(r => r.By + " requires '" + r.Dep + "'"))}. Available versions: {(candidates.Count == 0 ? "none (check modsSource)" : string.Join(", ", candidates.Select(m => m.Version)))}.";
            return null;
        }
        var solution = Search(new());
        if (solution is null) return new([], [], error);
        var bundled = initial.Select(r => r.Dep).Concat(solution.Values.SelectMany(m => m.Dependencies))
            .Where(d => Required(d) && IsBundled(d.Name) && d.Name is not "base" and not "core").Select(d => d.Name).Distinct().ToList();
        return new(solution.Values.ToList(), bundled, null);
    }

    public static IReadOnlyList<DependencyStatus> Statuses(ModInfo info, DependencyResolution resolution)
    {
        return info.RawDependencies.Select(spec =>
        {
            if (!Dependency.TryParse(spec, out var dep)) return new DependencyStatus(spec, "invalid", null, "Invalid dependency specification.");
            if (IsBundled(dep.Name)) return new DependencyStatus(spec, "game", null, "Bundled game mod; its installed version is not checked in this overview.");
            var selected = resolution.Mods.FirstOrDefault(m => m.Name == dep.Name);
            if (resolution.Error is not null) return new DependencyStatus(spec, "unresolved", null, resolution.Error);
            if (selected is null) return new DependencyStatus(spec, "inactive", null, "Not enabled in the default profile.");
            return new DependencyStatus(spec, "ready", selected.Version.ToString(), $"Default profile: {dep.Name} {selected.Version}.");
        }).ToList();
    }
}
