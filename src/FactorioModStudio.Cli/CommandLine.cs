namespace FactorioModStudio.Cli;

/// <summary>Tiny argument parser: positional values plus <c>--name value</c> options and <c>--flag</c> switches.</summary>
internal sealed class CommandLine
{
    // Options that take a value; everything else starting with "--" is a boolean switch.
    private static readonly HashSet<string> ValuedOptions = new(StringComparer.Ordinal)
    {
        "--project", "-p", "--out", "-o", "--factorio", "--ticks", "--timeout", "--seed", "--port", "--mods", "--workspace",
        "--template", "--title", "--author", "--description", "--factorio-version"
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = new();

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var parsed = new CommandLine();
        using var enumerator = args.GetEnumerator();

        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (!arg.StartsWith('-') || arg == "-")
            {
                parsed.Positional.Add(arg);
            }
            else if (ValuedOptions.Contains(arg))
            {
                if (!enumerator.MoveNext())
                    throw new Core.FmsException($"Option {arg} needs a value.");
                parsed._options[arg] = enumerator.Current;
            }
            else
            {
                parsed._options[arg] = null;
            }
        }

        return parsed;
    }

    public bool Has(params string[] names) => names.Any(_options.ContainsKey);

    public string? Get(params string[] names) =>
        names.Select(n => _options.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => v is not null);

    public int? GetInt(string name, params string[] aliases)
    {
        var text = Get(new[] { name }.Concat(aliases).ToArray());
        if (text is null) return null;
        return int.TryParse(text, out var value) && value > 0
            ? value
            : throw new Core.FmsException($"{name} expects a positive whole number, got \"{text}\".");
    }
}
