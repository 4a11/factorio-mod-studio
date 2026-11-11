using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>
/// "Quick try": the mod in a fresh test map, one click away. A small helper mod (<see cref="ModName"/>) is put into a prepared
/// profile; its scenario makes a flat lab-tile map that is always day, without enemies, turns on cheat mode, enables the recipes
/// the mod adds and puts a stack of every item the mod adds into the player's inventory. Factorio is started with
/// <c>--load-scenario fms-quick-try/quick-try</c>. Factorio 2.0 and later (the scenario reads <c>prototypes.get_history</c>).
/// </summary>
public static class QuickTry
{
    public const string ModName = "fms-quick-try";
    public const string ScenarioName = "quick-try";
    public const string HelperVersion = "1.0.0";

    /// <summary>What to pass to Factorio after <c>--mod-directory</c>.</summary>
    public static IReadOnlyList<string> Arguments { get; } = new[] { "--load-scenario", $"{ModName}/{ScenarioName}" };

    private static readonly Regex Version = new(@"^(\d+)\.(\d+)");

    /// <summary>Why the mod cannot be tried this way (its <c>factorio_version</c> is older than 2.0), or null when it can.</summary>
    public static string? Problem(string? factorioVersion)
    {
        var m = Version.Match(factorioVersion ?? "");
        if (!m.Success) return "info.json has no factorio_version.";
        return int.Parse(m.Groups[1].Value) >= 2 ? null : $"A quick try needs Factorio 2.0 or later; this mod is for {factorioVersion}. Use Run instead.";
    }

    /// <summary>
    /// Writes the helper mod into <paramref name="profile"/>'s mods folder and enables it in its mod-list.json. The profile was just
    /// prepared (<see cref="ProfileBuilder.Prepare"/>), which empties that folder first, so nothing of an earlier try is left.
    /// </summary>
    public static void Install(PreparedProfile profile, string modName, string factorioVersion)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Problem(factorioVersion) is { } problem) throw new FmsException(problem);
        var m = Version.Match(factorioVersion);
        var shortVersion = $"{m.Groups[1].Value}.{m.Groups[2].Value}";

        var root = Path.Combine(profile.ModsDirectory, $"{ModName}_{HelperVersion}");
        var scenario = Path.Combine(root, "scenarios", ScenarioName);
        Directory.CreateDirectory(scenario);
        var info = new JsonObject
        {
            ["name"] = ModName,
            ["version"] = HelperVersion,
            ["title"] = "Quick try (Factorio Mod Studio)",
            ["author"] = "Factorio Mod Studio",
            ["description"] = $"A test map for {modName}, added by Factorio Mod Studio to its own mods folder only.",
            ["factorio_version"] = shortVersion,
            ["dependencies"] = new JsonArray("base", modName),
        };
        File.WriteAllText(Path.Combine(root, "info.json"), info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(scenario, "control.lua"), Script(modName));
        File.WriteAllText(Path.Combine(scenario, "description.json"),
            """{ "order": "z", "multiplayer-compatible": false, "is-main-game": false }""");

        var listPath = Path.Combine(profile.ModsDirectory, "mod-list.json");
        var list = File.Exists(listPath) ? JsonNode.Parse(File.ReadAllText(listPath)) as JsonObject : null;
        list ??= new JsonObject { ["mods"] = new JsonArray() };
        var mods = list["mods"] as JsonArray ?? new JsonArray();
        list["mods"] = mods;
        if (!mods.Any(n => n?["name"]?.GetValue<string>() == ModName))
            mods.Add(new JsonObject { ["name"] = ModName, ["enabled"] = true });
        File.WriteAllText(listPath, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The scenario's control.lua for <paramref name="modName"/>.</summary>
    public static string Script(string modName) => ScriptTemplate.Replace("@@mod@@", LuaString(modName), StringComparison.Ordinal);

    private static string LuaString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private const string ScriptTemplate = """
        -- Quick try, written by Factorio Mod Studio: a flat test map with the items of one mod in the inventory.
        local MOD = @@mod@@

        local function made_by_mod(kind, name)
          local ok, history = pcall(prototypes.get_history, kind, name)
          return ok and history and history.created == MOD
        end

        local function own_items()
          local names = {}
          for name, item in pairs(prototypes.item) do
            -- The history is kept under the real type (gun, ammo, armor...), not under "item".
            if made_by_mod(item.type, name) and not item.hidden then names[#names + 1] = name end
          end
          table.sort(names)
          return names
        end

        script.on_init(function()
          local surface = game.surfaces[1]
          surface.generate_with_lab_tiles = true
          surface.always_day = true
          surface.peaceful_mode = true
          surface.clear(true)
          for _, entity in pairs(surface.find_entities_filtered({ force = "enemy" })) do entity.destroy() end
          game.map_settings.enemy_expansion.enabled = false
          game.map_settings.pollution.enabled = false
          local force = game.forces.player
          for name, recipe in pairs(force.recipes) do
            if made_by_mod("recipe", name) then recipe.enabled = true end
          end
        end)

        script.on_event(defines.events.on_player_created, function(event)
          local player = game.get_player(event.player_index)
          if not player then return end
          player.cheat_mode = true
          local given = {}
          for _, name in pairs(own_items()) do
            local count = player.insert({ name = name, count = prototypes.item[name].stack_size })
            if count > 0 then given[#given + 1] = "[item=" .. name .. "]" end
          end
          -- Power for machines the mod adds: two energy interfaces and poles.
          for name, count in pairs({ ["electric-energy-interface"] = 2, ["medium-electric-pole"] = 20 }) do
            if prototypes.item[name] then player.insert({ name = name, count = count }) end
          end
          if #given > 0 then
            player.print("[Factorio Mod Studio] Quick try of " .. MOD .. ": " .. table.concat(given, " ") .. " are in your inventory; cheat mode is on.")
          else
            player.print("[Factorio Mod Studio] Quick try of " .. MOD .. ": it adds no items to put into the inventory.")
          end
        end)

        """;
}
