namespace FactorioModStudio.Core.Lua;

/// <param name="CopyOf">For a copy of an existing prototype (<c>table.deepcopy</c>, the building-block helpers): the name of
/// the original. Its values are the original's except the fields the file sets, which <see cref="Table"/> holds.</param>
internal sealed record RawPrototype(string Type, string Name, int Line, LuaTable Table, string? CopyOf = null);

internal sealed record RawTouch(string Type, string Name, int Line);

internal sealed class ScanResult
{
    public List<RawPrototype> Prototypes { get; } = new();

    /// <summary>References like <c>data.raw.recipe["iron-plate"]</c>: existing prototypes the file reads or modifies.</summary>
    public List<RawTouch> Touched { get; } = new();

    /// <summary>Entries passed to <c>data:extend</c> that could not be resolved statically (variables, function calls).</summary>
    public int DynamicEntries { get; set; }

    /// <summary>The prototype each local variable holds at the end of the file (<c>local chest = copy.entity(...)</c>), by name.</summary>
    public Dictionary<string, RawPrototype> LocalPrototypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Finds prototype definitions in a Lua source file without running it.
/// Recognized: tables passed to <c>data:extend</c>, tables assigned to a variable or returned from a module, and copies:
/// <c>table.deepcopy(data.raw.type["name"])</c> and the helpers of the building blocks (<c>copy.item</c>, <c>copy.entity</c>,
/// <c>copy.recipe</c> from <c>prototypes/fms-copy-*.lua</c>) once they are passed to <c>data:extend</c>. Local strings
/// (<c>local name = "x"</c>, <c>name .. "-y"</c>) and the fields a file sets on a local table (<c>chest.inventory_size = 48</c>)
/// are followed in file order; branches and loops are not.
/// Anything else computed at load time is invisible; those are counted in <see cref="ScanResult.DynamicEntries"/>.
/// </summary>
internal static class PrototypeScanner
{
    // Fields that appear on recipe ingredient/result entries but never on a top-level prototype.
    private static readonly string[] EntryOnlyFields = { "amount", "amount_min", "amount_max", "probability" };

    /// <summary>The type of the vanilla items <c>copy.item</c> is used with that are not plain "item"s.</summary>
    private static readonly Dictionary<string, string> VanillaItemTypes = BuildItemTypes();

    public static ScanResult Scan(string source) => Scan(LuaLexer.Tokenize(source));

    public static ScanResult Scan(List<Token> tokens) => new FileScan(tokens).Run();

    /// <summary>The state of scanning one file: what its locals hold, which tables are copies, what was listed.</summary>
    private sealed class FileScan
    {
        private readonly List<Token> _tokens;
        private readonly ScanResult _result = new();
        private readonly LuaScope _scope;
        private readonly Dictionary<LuaTable, string> _copies = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<LuaTable> _listed = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _iconFiles = new(StringComparer.Ordinal);

        public FileScan(List<Token> tokens)
        {
            _tokens = tokens;
            _scope = new LuaScope
            {
                Call = Evaluate,
                RawRead = r => _result.Touched.Add(new RawTouch(r.Type, r.Name, r.Line))
            };
        }

        private LuaParser Parser(int start) => new(_tokens, start, _scope);

        public ScanResult Run()
        {
            var tokens = _tokens;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var statementStart = i == 0 || !(IsSymbol(tokens, i - 1, ".") || IsSymbol(tokens, i - 1, ":"));

                // data.raw.<type>.<name>  /  data.raw["type"]["name"]
                if (token is { Kind: TokenKind.Name, Text: "data" } && IsSymbol(tokens, i + 1, ".") && IsName(tokens, i + 2, "raw"))
                {
                    var next = ReadRawChain(tokens, i + 3, out var type, out var name);
                    if (type is not null && name is not null) _result.Touched.Add(new RawTouch(type, name, token.Line));
                    i = next - 1;
                    continue;
                }

                // data:extend({ ... })  /  data:extend{ ... }  /  data:extend(list)
                if (token is { Kind: TokenKind.Name, Text: "extend" } && i > 0 && (IsSymbol(tokens, i - 1, ":") || IsSymbol(tokens, i - 1, ".")))
                {
                    var j = i + 1;
                    if (IsSymbol(tokens, j, "(")) j++;

                    if (IsSymbol(tokens, j, "{"))
                    {
                        var parser = Parser(j);
                        if (parser.ParseExpression() is LuaTable list) AddFromList(list, countDynamic: true);
                        i = Math.Max(i, parser.Position - 1);
                    }
                    else if (j < tokens.Count && tokens[j].Kind == TokenKind.Name)
                    {
                        var parser = Parser(j);
                        if (parser.ParseExpression() is LuaTable list && list.Fields.Count == 0) AddFromList(list, countDynamic: true);
                        else _result.DynamicEntries++;
                        i = Math.Max(i, parser.Position - 1);
                    }
                    continue;
                }

                // local name = <expression>: remembered for the rest of the file
                if (token is { Kind: TokenKind.Name, Text: "local" } && i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Name
                    && IsSymbol(tokens, i + 2, "="))
                {
                    var parser = Parser(i + 3);
                    var value = parser.ParseExpression();
                    _scope.Locals[tokens[i + 1].Text] = value;
                    if (value is LuaTable table && !_copies.ContainsKey(table)) AddLiteral(table);
                    i = Understood(value) ? Math.Max(i, parser.Position - 1) : i + 2;
                    continue;
                }

                // chest.inventory_size = 48: a field of a local table
                if (statementStart && token.Kind == TokenKind.Name && IsSymbol(tokens, i + 1, ".") && i + 3 < tokens.Count
                    && tokens[i + 2].Kind == TokenKind.Name && IsSymbol(tokens, i + 3, "=")
                    && _scope.Locals.GetValueOrDefault(token.Text) is LuaTable target)
                {
                    var parser = Parser(i + 4);
                    var value = parser.ParseExpression();
                    target.Fields[tokens[i + 2].Text] = value;
                    if (value is LuaTable table && !_copies.ContainsKey(table)) AddLiteral(table);
                    i = Understood(value) ? Math.Max(i, parser.Position - 1) : i + 3;
                    continue;
                }

                // copy.set_icon(biter, name, tint) and the other calls of the building-block helpers. Their arguments are
                // scanned token by token afterwards too (a function passed to copy.walk may touch data.raw).
                if (statementStart && token.Kind == TokenKind.Name && IsSymbol(tokens, i + 1, ".") && IsHelper(token.Text))
                {
                    Parser(i).ParseExpression();
                    continue;
                }

                // x = { type = "...", name = "..." }   /   return { ... }
                var startsLiteral = (token is { Kind: TokenKind.Symbol, Text: "=" } || token is { Kind: TokenKind.Name, Text: "return" })
                                    && IsSymbol(tokens, i + 1, "{");
                if (startsLiteral)
                {
                    var parser = Parser(i + 1);
                    if (parser.ParseExpression() is LuaTable table) AddLiteral(table);
                    i = Math.Max(i, parser.Position - 1);
                }
            }

            foreach (var (local, value) in _scope.Locals)
                if (value is LuaTable table && TryPrototype(table, out var proto))
                    _result.LocalPrototypes[local] = _copies.TryGetValue(table, out var original) ? proto with { CopyOf = original } : proto;
            return _result;
        }

        // A value read completely can be stepped over. Anything else (a call the scanner does not know, a function, an
        // operator) is scanned token by token as before, so data.raw references and tables inside it are still found.
        private static bool Understood(LuaValue value) => value is not LuaUnknown;

        /// <summary>A table literal outside data:extend: a prototype, or a list of them.</summary>
        private void AddLiteral(LuaTable table)
        {
            if (TryPrototype(table, out _)) Add(table);
            else if (table.Fields.Count == 0 && table.Items.OfType<LuaTable>().Any(t => TryPrototype(t, out _)))
                AddFromList(table, countDynamic: false);
        }

        private void AddFromList(LuaTable list, bool countDynamic)
        {
            foreach (var item in list.Items)
            {
                if (item is LuaTable table)
                {
                    if (TryPrototype(table, out _)) Add(table);
                    else if (table.Fields.Count == 0 && table.Items.Count > 0) AddFromList(table, countDynamic); // nested list
                }
                else if (countDynamic)
                {
                    _result.DynamicEntries++;
                }
            }
        }

        /// <summary>Lists a prototype once, also when it is both a local literal and passed to data:extend by name.</summary>
        private void Add(LuaTable table)
        {
            if (!TryPrototype(table, out var proto) || !_listed.Add(table)) return;
            _result.Prototypes.Add(_copies.TryGetValue(table, out var original) ? proto with { CopyOf = original } : proto);
        }

        private bool IsHelper(string local) =>
            _scope.Locals.GetValueOrDefault(local) is LuaModule module && IsHelperModule(module.Path);

        // prototypes.fms-copy-v2 (and later versions): the helpers ContentBlocks writes into a mod.
        private static bool IsHelperModule(string path) =>
            path.Replace('/', '.').Split('.')[^1].StartsWith("fms-copy", StringComparison.Ordinal);

        private LuaValue? Evaluate(string callee, IReadOnlyList<LuaValue> args, int line)
        {
            string? Text(int index) => index < args.Count && args[index] is LuaString s ? s.Value : null;

            if (callee == "require")
                return Text(0) is { } path ? new LuaModule(path, line) : null;

            if (callee == "table.deepcopy")
                return args.Count > 0 && args[0] is LuaRawRef original ? Copy(original.Type, original.Name, original.Name, line) : null;

            var dot = callee.IndexOf('.');
            if (dot <= 0 || callee.IndexOf('.', dot + 1) >= 0 || !IsHelper(callee[..dot])) return null;

            switch (callee[(dot + 1)..])
            {
                case "item" when Text(0) is { } vanilla && Text(1) is { } name:
                {
                    var item = Copy(VanillaItemTypes.GetValueOrDefault(vanilla, "item"), name, vanilla, line);
                    SetIcon(item, name);
                    if (Text(3) is { } placed) item.Fields["place_result"] = new LuaString(placed, line);
                    return item;
                }
                case "entity" when Text(0) is { } type && Text(1) is { } vanilla && Text(2) is { } name:
                {
                    var entity = Copy(type, name, vanilla, line);
                    SetIcon(entity, name);
                    return entity;
                }
                case "recipe" when Text(0) is { } vanilla && Text(1) is { } name:
                    return Copy("recipe", name, vanilla, line);
                case "use_icons" when args.Count > 0 && args[0] is LuaTable files:
                    foreach (var (prototype, file) in files.Fields)
                        if (file is LuaString path) _iconFiles[prototype] = path.Value;
                    return null;
                case "set_icon" when args.Count > 1 && args[0] is LuaTable prototypeTable && Text(1) is { } iconName:
                    SetIcon(prototypeTable, iconName);
                    return null;
                case "set_category" when args.Count > 1 && args[0] is LuaTable recipe && Text(1) is { } category:
                    recipe.Fields["category"] = new LuaString(category, line);
                    return null;
                default:
                    return null;
            }
        }

        private LuaTable Copy(string type, string name, string original, int line)
        {
            var table = new LuaTable(line);
            table.Fields["type"] = new LuaString(type, line);
            table.Fields["name"] = new LuaString(name, line);
            _copies[table] = original;
            return table;
        }

        // The mod's own icon file for the prototype when the block registered one (copy.use_icons); otherwise the copy keeps
        // the original's icon, which is not a file of this mod.
        private void SetIcon(LuaTable prototype, string name)
        {
            if (_iconFiles.TryGetValue(name, out var file)) prototype.Fields["icon"] = new LuaString(file, prototype.Line);
        }
    }

    private static Dictionary<string, string> BuildItemTypes()
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string type, params string[] names) { foreach (var n in names) types[n] = type; }
        Put("armor", "light-armor", "heavy-armor", "modular-armor", "power-armor", "power-armor-mk2", "mech-armor");
        Put("gun", "pistol", "submachine-gun", "shotgun", "combat-shotgun", "rocket-launcher", "flamethrower", "railgun", "teslagun");
        Put("ammo", "firearm-magazine", "piercing-rounds-magazine", "uranium-rounds-magazine", "shotgun-shell", "piercing-shotgun-shell",
            "cannon-shell", "explosive-cannon-shell", "uranium-cannon-shell", "explosive-uranium-cannon-shell", "artillery-shell",
            "rocket", "explosive-rocket", "atomic-bomb", "flamethrower-ammo", "railgun-ammo", "tesla-ammo", "capture-robot-rocket");
        Put("capsule", "grenade", "cluster-grenade", "poison-capsule", "slowdown-capsule", "defender-capsule", "distractor-capsule",
            "destroyer-capsule", "raw-fish", "cliff-explosives");
        Put("item-with-entity-data", "car", "tank", "locomotive", "cargo-wagon", "fluid-wagon", "artillery-wagon", "spidertron");
        Put("rail-planner", "rail");
        return types;
    }

    internal static bool TryPrototype(LuaTable table, out RawPrototype prototype)
    {
        prototype = null!;
        var type = table.String("type");
        var name = table.String("name");
        if (type is null || name is null) return false;
        if (EntryOnlyFields.Any(table.Fields.ContainsKey)) return false;

        prototype = new RawPrototype(type, name, table.Line, table);
        return true;
    }

    private static int ReadRawChain(List<Token> tokens, int start, out string? type, out string? name)
    {
        var p = start;
        type = ReadAccessor(tokens, ref p);
        name = type is null ? null : ReadAccessor(tokens, ref p);
        return p;
    }

    private static string? ReadAccessor(List<Token> tokens, ref int p)
    {
        if (IsSymbol(tokens, p, ".") && p + 1 < tokens.Count && tokens[p + 1].Kind == TokenKind.Name)
        {
            var text = tokens[p + 1].Text;
            p += 2;
            return text;
        }

        if (IsSymbol(tokens, p, "[") && p + 2 < tokens.Count && tokens[p + 1].Kind == TokenKind.String && IsSymbol(tokens, p + 2, "]"))
        {
            var text = tokens[p + 1].Text;
            p += 3;
            return text;
        }

        return null;
    }

    private static bool IsSymbol(List<Token> tokens, int index, string text) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == TokenKind.Symbol && tokens[index].Text == text;

    private static bool IsName(List<Token> tokens, int index, string text) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == TokenKind.Name && tokens[index].Text == text;
}
