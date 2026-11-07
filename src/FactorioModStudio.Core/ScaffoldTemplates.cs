namespace FactorioModStudio.Core;

/// <summary>One file of a template: either text with <c>@@placeholders@@</c> or a generator for binary content (argument: mod name).</summary>
internal sealed record TemplateFile(string Path, string? Text = null, Func<string, byte[]>? Binary = null);

internal sealed record TemplateDefinition(TemplateInfo Info, IReadOnlyList<TemplateFile> Files);

/// <summary>
/// The built-in mod templates. Text files use <c>@@key@@</c> placeholders (name, title, author, description, date, version),
/// optionally with an escaping prefix: <c>@@json:key@@</c> (JSON string contents) or <c>@@cfg:key@@</c> (locale value).
/// User-supplied values are never lexed as Lua: the Lua files only contain the mod name, which has a restricted charset.
/// </summary>
internal static class ScaffoldTemplates
{
    private const string Unverified =
        "-- Note: these prototypes were written from the documentation of the Factorio version you picked and have not\n" +
        "-- been verified in the game; if Factorio rejects something, check the log (fmstudio log) and the official prototype docs.\n";

    // Entity prototypes (container / sprite / technology fields) were not checked against Factorio 1.1: offered for 2.x only.
    private static readonly string[] AllVersions = { "1.1", "2.0", "2.1" };
    private static readonly string[] ModernVersions = { "2.0", "2.1" };

    private static readonly TemplateFile Info = new("info.json", """
        {
          "name": "@@name@@",
          "version": "@@version@@",
          "title": "@@json:title@@",
          "author": "@@json:author@@",
          "description": "@@json:description@@",
          "factorio_version": "@@factorio@@",
          "dependencies": ["base >= @@factorio@@.0"]
        }

        """);

    private static readonly TemplateFile Changelog = new("changelog.txt",
        new string('-', 99) + "\n" + """
        Version: @@version@@
        Date: @@date@@
          Features:
            - Initial release.

        """);

    private static readonly TemplateFile Thumbnail = new("thumbnail.png", Binary: ScaffoldArt.Thumbnail);

    private static TemplateFile Locale(string extra) => new("locale/en/@@name@@.cfg",
        "[mod-name]\n@@name@@=@@cfg:title@@\n\n[mod-description]\n@@name@@=@@cfg:description@@\n" + extra);

    private static IEnumerable<TemplateFile> Common(string localeExtra) => new[] { Info, Changelog, Thumbnail, Locale(localeExtra) };

    public static readonly IReadOnlyList<TemplateDefinition> All = new[]
    {
        new TemplateDefinition(
            new TemplateInfo("empty", "Empty mod", "Only info.json, changelog, thumbnail and locale, plus empty data.lua and control.lua.", AllVersions),
            Common("").Concat(new[]
            {
                new TemplateFile("data.lua", """
                    -- Data stage: define prototypes (items, recipes, entities, technologies ...) here.
                    -- Example:
                    --   data:extend({ { type = "item", name = "my-item", ... } })
                    -- Docs: https://lua-api.factorio.com/latest/prototypes.html
                    """ + "\n"),
                new TemplateFile("control.lua", """
                    -- Control stage: runtime scripting (events, commands, GUIs).
                    -- Example:
                    --   script.on_event(defines.events.on_player_created, function(event) ... end)
                    -- Docs: https://lua-api.factorio.com/latest/
                    """ + "\n"),
            }).ToList()),

        new TemplateDefinition(
            new TemplateInfo("item-recipe", "Item and recipe", "A recoloured copy of the iron gear wheel with a cheaper recipe (Factorio 2.0+); on 1.1 a new item with a generated icon.", AllVersions),
            Common("\n[item-name]\n@@name@@-item=@@cfg:title@@ item\n\n[item-description]\n@@name@@-item=An item added by @@cfg:title@@.\n").Concat(new[]
            {
                new TemplateFile("data.lua", Unverified + """

                    data:extend({
                      {
                        type = "item",
                        name = "@@name@@-item",
                        icon = "__@@name@@__/graphics/icons/@@name@@-item.png",
                        icon_size = 64,
                        subgroup = "intermediate-product",
                        order = "z[@@name@@]",
                        stack_size = 100
                      },
                      {
                        type = "recipe",
                        name = "@@name@@-item",
                        enabled = true,
                        energy_required = 0.5,
                        ingredients = {{type = "item", name = "iron-plate", amount = 2}},
                        results = {{type = "item", name = "@@name@@-item", amount = 1}}
                      }
                    })
                    """ + "\n"),
                new TemplateFile("graphics/icons/@@name@@-item.png", Binary: ScaffoldArt.Ingot),
            }).ToList()),

        new TemplateDefinition(
            new TemplateInfo("entity", "Placeable chest", "A recoloured copy of the iron chest, icon and picture included, with 48 slots instead of 32.", ModernVersions),
            Common("\n[entity-name]\n@@name@@-chest=@@cfg:title@@ chest\n\n[entity-description]\n@@name@@-chest=A storage chest added by @@cfg:title@@.\n\n" +
                   "[item-name]\n@@name@@-chest=@@cfg:title@@ chest\n\n[technology-name]\n@@name@@-chest=@@cfg:title@@ chest\n\n" +
                   "[technology-description]\n@@name@@-chest=Unlocks the @@cfg:title@@ chest.\n").Concat(new[]
            {
                new TemplateFile("data.lua", Unverified + """

                    data:extend({
                      {
                        type = "container",
                        name = "@@name@@-chest",
                        icon = "__@@name@@__/graphics/icons/@@name@@-chest.png",
                        icon_size = 64,
                        flags = {"placeable-neutral", "player-creation"},
                        minable = {mining_time = 0.2, result = "@@name@@-chest"},
                        max_health = 150,
                        collision_box = {{-0.35, -0.35}, {0.35, 0.35}},
                        selection_box = {{-0.5, -0.5}, {0.5, 0.5}},
                        inventory_size = 16,
                        picture = {
                          filename = "__@@name@@__/graphics/entity/@@name@@-chest.png",
                          width = 64,
                          height = 64,
                          scale = 0.5
                        }
                      },
                      {
                        type = "item",
                        name = "@@name@@-chest",
                        icon = "__@@name@@__/graphics/icons/@@name@@-chest.png",
                        icon_size = 64,
                        subgroup = "storage",
                        order = "a[items]-z[@@name@@-chest]",
                        place_result = "@@name@@-chest",
                        stack_size = 50
                      },
                      {
                        type = "recipe",
                        name = "@@name@@-chest",
                        enabled = false,
                        energy_required = 0.5,
                        ingredients = {{type = "item", name = "iron-plate", amount = 8}},
                        results = {{type = "item", name = "@@name@@-chest", amount = 1}}
                      },
                      {
                        type = "technology",
                        name = "@@name@@-chest",
                        icon = "__@@name@@__/graphics/technology/@@name@@-chest.png",
                        icon_size = 256,
                        effects = {{type = "unlock-recipe", recipe = "@@name@@-chest"}},
                        prerequisites = {},
                        unit = {
                          count = 10,
                          ingredients = {{"automation-science-pack", 1}},
                          time = 15
                        },
                        order = "a-b-c"
                      }
                    })
                    """ + "\n"),
                new TemplateFile("graphics/icons/@@name@@-chest.png", Binary: ScaffoldArt.ChestIcon),
                new TemplateFile("graphics/entity/@@name@@-chest.png", Binary: ScaffoldArt.ChestPicture),
                new TemplateFile("graphics/technology/@@name@@-chest.png", Binary: ScaffoldArt.TechnologyIcon),
            }).ToList()),

        new TemplateDefinition(
            new TemplateInfo("runtime-script", "Runtime script and settings", "Mod settings plus a control script with events, a chat command and a periodic tick handler.", AllVersions),
            Common("""

                [mod-setting-name]
                @@name@@-enable-greeting=Greet new players
                @@name@@-interval-seconds=Reminder interval (seconds)
                @@name@@-greeting-style=Greeting style

                [mod-setting-description]
                @@name@@-enable-greeting=Needs a restart of the map to take effect (startup setting).
                @@name@@-interval-seconds=How often the periodic reminder is printed. Changes apply immediately.
                @@name@@-greeting-style=Each player chooses their own style.

                [string-mod-setting]
                @@name@@-greeting-style-friendly=Friendly
                @@name@@-greeting-style-formal=Formal
                @@name@@-greeting-style-silent=Silent

                [command-help]
                @@name@@-hello=Prints a greeting from the mod.

                [@@name@@-messages]
                welcome-friendly=Hi __1__, welcome to the factory!
                welcome-formal=Good day, __1__.
                hello-command=Hello from the mod, __1__!
                reminder=Reminder: you have been playing for a while. Stretch your legs.

                """).Concat(new[]
            {
                new TemplateFile("settings.lua", """
                    -- Settings stage: defines the mod settings shown in the "Mod settings" menu.
                    data:extend({
                      {
                        type = "bool-setting",
                        name = "@@name@@-enable-greeting",
                        setting_type = "startup",
                        default_value = true,
                        order = "a"
                      },
                      {
                        type = "int-setting",
                        name = "@@name@@-interval-seconds",
                        setting_type = "runtime-global",
                        default_value = 300,
                        minimum_value = 10,
                        maximum_value = 3600,
                        order = "b"
                      },
                      {
                        type = "string-setting",
                        name = "@@name@@-greeting-style",
                        setting_type = "runtime-per-user",
                        default_value = "friendly",
                        allowed_values = {"friendly", "formal", "silent"},
                        order = "c"
                      }
                    })
                    """ + "\n"),
                new TemplateFile("control.lua", Unverified + """

                    script.on_init(function()
                      @@storecomment@@
                      @@store@@.seconds = 0
                    end)

                    -- Greet every new player, using the startup setting and the player's own style setting.
                    script.on_event(defines.events.on_player_created, function(event)
                      local player = game.get_player(event.player_index)
                      if not player then return end
                      if not settings.startup["@@name@@-enable-greeting"].value then return end

                      local style = settings.get_player_settings(player)["@@name@@-greeting-style"].value
                      if style == "friendly" then
                        player.print({"@@name@@-messages.welcome-friendly", player.name})
                      elseif style == "formal" then
                        player.print({"@@name@@-messages.welcome-formal", player.name})
                      end
                    end)

                    -- A chat command: type /@@name@@-hello in the game console.
                    commands.add_command("@@name@@-hello", {"command-help.@@name@@-hello"}, function(command)
                      local player = command.player_index and game.get_player(command.player_index)
                      if player then
                        player.print({"@@name@@-messages.hello-command", player.name})
                      end
                    end)

                    -- Runs once per second; prints a reminder every N seconds (runtime-global setting).
                    script.on_nth_tick(60, function()
                      @@store@@.seconds = (@@store@@.seconds or 0) + 1
                      if @@store@@.seconds >= settings.global["@@name@@-interval-seconds"].value then
                        @@store@@.seconds = 0
                        game.print({"@@name@@-messages.reminder"})
                      end
                    end)
                    """ + "\n"),
            }).ToList()),
    };
}
