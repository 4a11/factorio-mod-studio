namespace FactorioModStudio.Core;

/// <param name="Category">"combat", "logistics", "production" or "vehicles".</param>
/// <param name="DefaultName">Suggested prototype name; hosts usually put the mod's name in front of it.</param>
/// <param name="SupportedVersions">Factorio versions (MAJOR.MINOR) the generated Lua is written for.</param>
public sealed record ContentBlockInfo(string Id, string Category, string DefaultName, IReadOnlyList<string> SupportedVersions);

/// <summary>One locale line the block needs; Key and Value may contain the @@id@@ and @@title@@ tokens.</summary>
internal sealed record BlockLocale(string Section, string Key, string Value);

internal sealed record ContentBlock(ContentBlockInfo Info, string Lua, IReadOnlyList<BlockLocale> Locale);

/// <summary>
/// The building blocks that can be added to a mod: each one is a Lua file that copies a vanilla prototype
/// (<c>table.deepcopy</c>) and gives it its own name, numbers and tinted icon, so it looks like the game right away and
/// keeps working between game versions. Shared code lives in <see cref="ModContent.HelperPath"/>.
/// Written for Factorio 2.0 and 2.1 (base game only; Space Age is not needed).
/// </summary>
internal static class ContentBlocks
{
    private static readonly string[] Modern = { "2.0", "2.1" };

    private static ContentBlock Block(string id, string category, string defaultName, string lua, params BlockLocale[] locale) =>
        new(new ContentBlockInfo(id, category, defaultName, Modern), lua, locale);

    private static BlockLocale L(string section, string key, string value) => new(section, key, value);

    private const string Head = """
        -- @@title@@ (@@id@@), added by Factorio Mod Studio.
        -- A copy of a vanilla prototype with its own name and numbers: change the values below to make it your own.
        local copy = require("prototypes.fms-copy-v1")


        """;

    public static IReadOnlyList<ContentBlock> All { get; } = new[]
    {
        // ------------------------------------------------------------------ combat
        Block("armor", "combat", "armor", Head + """
            local name = "@@id@@"
            local tint = { r = 0.45, g = 0.75, b = 1.0 }

            -- Modular armor, with better resistances. Each resistance takes off `decrease` first, then `percent` of the rest.
            local armor = copy.item("modular-armor", name, tint)
            armor.durability = 8000
            armor.resistances = {
              { type = "physical", decrease = 8, percent = 40 },
              { type = "explosion", decrease = 15, percent = 45 },
              { type = "acid", decrease = 5, percent = 40 },
              { type = "fire", decrease = 5, percent = 35 },
            }
            armor.equipment_grid = "small-equipment-grid" -- try "medium-equipment-grid" for more room

            data:extend({ armor, copy.recipe("modular-armor", name) })
            copy.unlock("modular-armor", name)
            """,
            L("item-name", "@@id@@", "@@title@@"),
            L("item-description", "@@id@@", "Armor with a small equipment grid and better resistances.")),

        Block("gun", "combat", "rifle", Head + """
            local name = "@@id@@"
            local ammo_name = name .. "-ammo"
            local tint = { r = 1.0, g = 0.55, b = 0.3 }

            -- The submachine gun, firing faster.
            local gun = copy.item("submachine-gun", name, tint)
            gun.attack_parameters.cooldown = 4 -- ticks between shots (60 ticks = 1 second); the submachine gun has 6

            -- Magazines for it (any gun that takes bullets can use them), with 60 % more damage.
            local ammo = copy.item("firearm-magazine", ammo_name, tint)
            copy.scale_damage(ammo.ammo_type, 1.6)

            data:extend({ gun, ammo, copy.recipe("submachine-gun", name), copy.recipe("firearm-magazine", ammo_name) })
            copy.unlock("military", name)
            copy.unlock("military", ammo_name)
            """,
            L("item-name", "@@id@@", "@@title@@"),
            L("item-name", "@@id@@-ammo", "@@title@@ ammo"),
            L("item-description", "@@id@@", "A faster submachine gun.")),

        Block("grenade", "combat", "grenade", Head + """
            local name = "@@id@@"
            local tint = { r = 0.4, g = 1.0, b = 0.4 }

            -- The thrown projectile, with twice the damage of a grenade.
            local projectile = table.deepcopy(data.raw.projectile["grenade"])
            projectile.name = name
            copy.scale_damage(projectile, 2)

            -- The capsule you throw: it now creates our projectile instead of the vanilla one.
            local capsule = copy.item("grenade", name, tint)
            copy.walk(capsule.capsule_action, function(node)
              if node.projectile == "grenade" then node.projectile = name end
            end)

            data:extend({ projectile, capsule, copy.recipe("grenade", name) })
            copy.unlock("military-2", name)
            """,
            L("item-name", "@@id@@", "@@title@@"),
            L("item-description", "@@id@@", "A grenade with twice the damage.")),

        Block("turret", "combat", "turret", Head + """
            local name = "@@id@@"
            local tint = { r = 1.0, g = 0.35, b = 0.35 }

            -- The gun turret, tougher and with a longer range.
            local turret = copy.entity("ammo-turret", "gun-turret", name)
            turret.max_health = 600
            turret.attack_parameters.range = 22

            data:extend({ turret, copy.item("gun-turret", name, tint, name), copy.recipe("gun-turret", name) })
            copy.unlock("gun-turret", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A gun turret with more health and a longer range.")),

        Block("wall", "combat", "wall", Head + """
            local name = "@@id@@"
            local tint = { r = 0.6, g = 0.6, b = 0.9 }

            -- The stone wall, with twice the health.
            local wall = copy.entity("wall", "stone-wall", name)
            wall.max_health = 700

            data:extend({ wall, copy.item("stone-wall", name, tint, name), copy.recipe("stone-wall", name) })
            copy.unlock("stone-wall", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A wall with twice the health of stone walls.")),

        Block("enemy", "combat", "biter", Head + """
            local name = "@@id@@"
            local tint = { r = 0.2, g = 0.9, b = 0.5, a = 1 }

            -- A small biter in another colour: tougher, faster and with a harder bite.
            local biter = table.deepcopy(data.raw.unit["small-biter"])
            biter.name = name
            biter.max_health = 75
            biter.movement_speed = 0.25
            copy.retint(biter.run_animation, tint)
            copy.retint(biter.attack_parameters.animation, tint)
            copy.scale_damage(biter.attack_parameters, 1.5)
            data:extend({ biter })

            -- Biter nests spawn it: { evolution, weight } pairs, so it is common early and gone later.
            table.insert(data.raw["unit-spawner"]["biter-spawner"].result_units,
              { name, { { 0.0, 0.1 }, { 0.4, 0.3 }, { 0.8, 0.0 } } })
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A tougher, faster small biter.")),

        Block("shield", "combat", "shield", Head + """
            local name = "@@id@@"
            local tint = { r = 0.5, g = 0.6, b = 1.0 }

            -- Armor equipment: the energy shield, holding more.
            local shield = table.deepcopy(data.raw["energy-shield-equipment"]["energy-shield-equipment"])
            shield.name = name
            shield.take_result = name
            shield.max_shield_value = 100
            copy.retint(shield.sprite, tint)
            shield.sprite.tint = tint

            local item = copy.item("energy-shield-equipment", name, tint)
            item.place_as_equipment_result = name

            data:extend({ shield, item, copy.recipe("energy-shield-equipment", name) })
            copy.unlock("energy-shield-equipment", name)
            """,
            L("item-name", "@@id@@", "@@title@@"),
            L("equipment-name", "@@id@@", "@@title@@"),
            L("item-description", "@@id@@", "An energy shield for armor that holds more.")),

        // ------------------------------------------------------------------ logistics
        Block("belt", "logistics", "belt", Head + """
            local belt_name = "@@id@@"
            local underground_name = belt_name .. "-underground-belt"
            local splitter_name = belt_name .. "-splitter"
            local speed = 0.125 -- tiles per tick; the express belt has 0.09375 (45 items per second)
            local tint = { r = 0.7, g = 0.35, b = 1.0 }

            -- A belt tier: belt, underground belt and splitter, copied from the express tier.
            local belt = copy.entity("transport-belt", "express-transport-belt", belt_name)
            belt.speed = speed
            belt.related_underground_belt = underground_name

            local underground = copy.entity("underground-belt", "express-underground-belt", underground_name)
            underground.speed = speed
            underground.max_distance = 11 -- tiles between the two ends

            local splitter = copy.entity("splitter", "express-splitter", splitter_name)
            splitter.speed = speed

            data:extend({
              belt, underground, splitter,
              copy.item("express-transport-belt", belt_name, tint, belt_name),
              copy.item("express-underground-belt", underground_name, tint, underground_name),
              copy.item("express-splitter", splitter_name, tint, splitter_name),
              copy.recipe("express-transport-belt", belt_name),
              copy.recipe("express-underground-belt", underground_name),
              copy.recipe("express-splitter", splitter_name),
            })
            for _, recipe in pairs({ belt_name, underground_name, splitter_name }) do
              copy.unlock("logistics-3", recipe)
            end
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-name", "@@id@@-underground-belt", "@@title@@ underground"),
            L("entity-name", "@@id@@-splitter", "@@title@@ splitter")),

        Block("inserter", "logistics", "inserter", Head + """
            local name = "@@id@@"
            local tint = { r = 1.0, g = 0.8, b = 0.2 }

            -- The fast inserter, 50 % faster.
            local inserter = copy.entity("inserter", "fast-inserter", name)
            inserter.rotation_speed = inserter.rotation_speed * 1.5
            inserter.extension_speed = inserter.extension_speed * 1.5

            data:extend({ inserter, copy.item("fast-inserter", name, tint, name), copy.recipe("fast-inserter", name) })
            copy.unlock("fast-inserter", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "An inserter 50 % faster than the fast inserter.")),

        Block("pipe", "logistics", "pipe", Head + """
            local pipe_name = "@@id@@"
            local underground_name = pipe_name .. "-to-ground"
            local tint = { r = 0.3, g = 0.9, b = 0.9 }

            -- A pipe that holds twice as much, and an underground pipe that reaches twice as far.
            local pipe = copy.entity("pipe", "pipe", pipe_name)
            pipe.fluid_box.volume = pipe.fluid_box.volume * 2

            local underground = copy.entity("pipe-to-ground", "pipe-to-ground", underground_name)
            copy.walk(underground.fluid_box, function(node)
              if node.max_underground_distance then node.max_underground_distance = 20 end
            end)

            data:extend({
              pipe, underground,
              copy.item("pipe", pipe_name, tint, pipe_name),
              copy.item("pipe-to-ground", underground_name, tint, underground_name),
              copy.recipe("pipe", pipe_name),
              copy.recipe("pipe-to-ground", underground_name),
            })
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-name", "@@id@@-to-ground", "@@title@@ to ground")),

        Block("pole", "logistics", "pole", Head + """
            local name = "@@id@@"
            local tint = { r = 0.9, g = 0.5, b = 0.2 }

            -- The medium electric pole, reaching further and powering a larger area.
            local pole = copy.entity("electric-pole", "medium-electric-pole", name)
            pole.maximum_wire_distance = 12
            pole.supply_area_distance = 4.5

            data:extend({ pole, copy.item("medium-electric-pole", name, tint, name), copy.recipe("medium-electric-pole", name) })
            copy.unlock("electric-energy-distribution-1", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "An electric pole with a longer reach and a larger supply area.")),

        // ------------------------------------------------------------------ production
        Block("assembler", "production", "assembler", Head + """
            local name = "@@id@@"
            local category = name .. "-crafting"
            local tint = { r = 0.5, g = 1.0, b = 0.6 }

            -- A recipe category of its own: recipes in it can only be made in this machine.
            local machine = copy.entity("assembling-machine", "assembling-machine-2", name)
            machine.crafting_categories = { category, "crafting" }
            machine.crafting_speed = 1
            machine.fixed_recipe = nil

            -- An example recipe for the new category: a cheaper way to make gears.
            local example = {
              type = "recipe",
              name = name .. "-gears",
              enabled = false,
              energy_required = 1,
              ingredients = { { type = "item", name = "iron-plate", amount = 3 } },
              results = { { type = "item", name = "iron-gear-wheel", amount = 2 } },
            }
            copy.set_category(example, category)

            data:extend({
              { type = "recipe-category", name = category },
              machine,
              copy.item("assembling-machine-2", name, tint, name),
              copy.recipe("assembling-machine-2", name),
              example,
            })
            copy.unlock("automation-2", name)
            copy.unlock("automation-2", example.name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "An assembling machine with recipes of its own.")),

        Block("furnace", "production", "furnace", Head + """
            local name = "@@id@@"
            local tint = { r = 1.0, g = 0.45, b = 0.2 }

            -- The steel furnace, smelting faster.
            local furnace = copy.entity("furnace", "steel-furnace", name)
            furnace.crafting_speed = 3

            data:extend({ furnace, copy.item("steel-furnace", name, tint, name), copy.recipe("steel-furnace", name) })
            copy.unlock("advanced-material-processing", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A furnace that smelts 50 % faster than the steel furnace.")),

        Block("ore", "production", "ore", Head + """
            local resource_autoplace = require("resource-autoplace")

            local name = "@@id@@"
            local tint = { r = 0.35, g = 0.85, b = 0.95 }

            -- A new ore on Nauvis: a copy of iron ore in another colour, placed in its own patches.
            resource_autoplace.initialize_patch_set(name, true)
            local ore = table.deepcopy(data.raw.resource["iron-ore"])
            ore.name = name
            ore.minable.result = name
            ore.minable.results = nil
            ore.map_color = tint
            ore.mining_visualisation_tint = tint
            copy.retint(ore.stages, tint)
            ore.stages.sheet.tint = tint
            ore.autoplace = resource_autoplace.resource_autoplace_settings({
              name = name,
              order = "z",
              base_density = 6,
              has_starting_area_placement = true,
              regular_rq_factor_multiplier = 1.0,
              starting_rq_factor_multiplier = 1.1,
            })

            -- The ore item, and a recipe that smelts it into iron plates.
            local item = copy.item("iron-ore", name, tint)
            local smelting = {
              type = "recipe",
              name = name .. "-smelting",
              energy_required = 3.2,
              ingredients = { { type = "item", name = name, amount = 1 } },
              results = { { type = "item", name = "iron-plate", amount = 1 } },
            }
            copy.set_category(smelting, "smelting")

            data:extend({
              { type = "autoplace-control", name = name, richness = true, order = "b-z", category = "resource" },
              ore, item, smelting,
            })

            -- New maps on Nauvis place it (it also shows up in the map generator settings).
            local settings = data.raw.planet.nauvis.map_gen_settings
            settings.autoplace_controls[name] = {}
            settings.autoplace_settings.entity.settings[name] = {}
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("item-name", "@@id@@", "@@title@@"),
            L("autoplace-control-names", "@@id@@", "@@title@@")),

        Block("drill", "production", "drill", Head + """
            local name = "@@id@@"
            local tint = { r = 0.95, g = 0.8, b = 0.3 }

            -- The electric mining drill, twice as fast and mining a 7x7 area.
            local drill = copy.entity("mining-drill", "electric-mining-drill", name)
            drill.mining_speed = 1
            drill.resource_searching_radius = 3.49

            data:extend({ drill, copy.item("electric-mining-drill", name, tint, name), copy.recipe("electric-mining-drill", name) })
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A mining drill that is twice as fast and covers a 7x7 area.")),

        Block("solar", "production", "solar-panel", Head + """
            local panel_name = "@@id@@"
            local accumulator_name = panel_name .. "-accumulator"
            local tint = { r = 0.4, g = 0.7, b = 1.0 }

            -- A stronger solar panel and an accumulator that stores twice as much.
            local panel = copy.entity("solar-panel", "solar-panel", panel_name)
            panel.production = "100kW"

            local accumulator = copy.entity("accumulator", "accumulator", accumulator_name)
            accumulator.energy_source.buffer_capacity = "10MJ"

            data:extend({
              panel, accumulator,
              copy.item("solar-panel", panel_name, tint, panel_name),
              copy.item("accumulator", accumulator_name, tint, accumulator_name),
              copy.recipe("solar-panel", panel_name),
              copy.recipe("accumulator", accumulator_name),
            })
            copy.unlock("solar-energy", panel_name)
            copy.unlock("electric-energy-accumulators", accumulator_name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-name", "@@id@@-accumulator", "@@title@@ accumulator")),

        // ------------------------------------------------------------------ vehicles
        Block("car", "vehicles", "car", Head + """
            local name = "@@id@@"
            local tint = { r = 1.0, g = 0.3, b = 0.6 }

            -- The car, tougher, stronger and with a bigger trunk.
            local car = copy.entity("car", "car", name)
            car.max_health = 600
            car.consumption = "250kW"
            car.inventory_size = 120

            data:extend({ car, copy.item("car", name, tint, name), copy.recipe("car", name) })
            copy.unlock("automobilism", name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-description", "@@id@@", "A tougher car with a bigger trunk.")),

        Block("train", "vehicles", "locomotive", Head + """
            local locomotive_name = "@@id@@"
            local wagon_name = locomotive_name .. "-wagon"
            local tint = { r = 0.9, g = 0.9, b = 0.3 }

            -- A faster locomotive and a cargo wagon that holds more.
            local locomotive = copy.entity("locomotive", "locomotive", locomotive_name)
            locomotive.max_speed = 1.6 -- tiles per tick; the locomotive has 1.2
            locomotive.max_power = "800kW"

            local wagon = copy.entity("cargo-wagon", "cargo-wagon", wagon_name)
            wagon.inventory_size = 60

            data:extend({
              locomotive, wagon,
              copy.item("locomotive", locomotive_name, tint, locomotive_name),
              copy.item("cargo-wagon", wagon_name, tint, wagon_name),
              copy.recipe("locomotive", locomotive_name),
              copy.recipe("cargo-wagon", wagon_name),
            })
            copy.unlock("railway", locomotive_name)
            copy.unlock("railway", wagon_name)
            """,
            L("entity-name", "@@id@@", "@@title@@"),
            L("entity-name", "@@id@@-wagon", "@@title@@ wagon")),
    };

    /// <summary>The shared helpers every block requires; created once per mod.</summary>
    public const string Helper = """
        -- Helpers for copying vanilla prototypes, used by the blocks Factorio Mod Studio added to this mod.
        -- A copy keeps the graphics, sounds and behaviour of the original; the block then gives it its own name and numbers.
        -- This file is shared by all blocks: change it with care, or copy a function into a block to change it there only.
        local copy = {}

        local item_types = { "item", "item-with-entity-data", "armor", "gun", "ammo", "capsule", "rail-planner" }

        --- The item prototype called `name`, whatever its type.
        function copy.find_item(name)
          for _, type in pairs(item_types) do
            local item = data.raw[type] and data.raw[type][name]
            if item then return item end
          end
          error("There is no item called " .. name)
        end

        --- The icons of `prototype` as a list of layers, each tinted with `tint`.
        function copy.tinted_icons(prototype, tint)
          local icons = prototype.icons and table.deepcopy(prototype.icons)
            or { { icon = prototype.icon, icon_size = prototype.icon_size } }
          for _, layer in pairs(icons) do layer.tint = tint end
          return icons
        end

        --- A copy of the item `vanilla` called `name`, with a tinted icon; it places `place_result` when that is given.
        function copy.item(vanilla, name, tint, place_result)
          local item = table.deepcopy(copy.find_item(vanilla))
          item.name = name
          item.icons = copy.tinted_icons(item, tint)
          item.icon = nil
          item.order = (item.order or "z") .. "-" .. name
          if place_result then item.place_result = place_result end
          return item
        end

        --- A copy of the recipe `vanilla` called `name`, making `result` (default: `name`) where it made `vanilla`.
        function copy.recipe(vanilla, name, result)
          local recipe = table.deepcopy(data.raw.recipe[vanilla])
          recipe.name = name
          for _, product in pairs(recipe.results or {}) do
            if product.name == vanilla then product.name = result or name end
          end
          recipe.main_product = nil
          return recipe
        end

        --- A copy of the entity `vanilla` of type `type` called `name`, mined into the item `item` (default: `name`).
        function copy.entity(type, vanilla, name, item)
          local entity = table.deepcopy(data.raw[type][vanilla])
          entity.name = name
          if entity.minable then
            entity.minable.result = item or name
            entity.minable.results = nil
          end
          entity.next_upgrade = nil -- vanilla tiers upgrade into each other; the copy stands on its own
          return entity
        end

        --- Unlocks `recipe` with `technology`. A recipe the original had from the start stays available from the start;
        --- without that technology (another game version, a mod that removes it) it is available from the start too.
        function copy.unlock(technology, recipe)
          local prototype = data.raw.recipe[recipe]
          if prototype.enabled ~= false then return end
          local tech = data.raw.technology[technology]
          if tech then
            tech.effects = tech.effects or {}
            table.insert(tech.effects, { type = "unlock-recipe", recipe = recipe })
          else
            prototype.enabled = true
          end
        end

        --- The version of the base game as { major, minor }, e.g. { 2, 1 }.
        function copy.game_version()
          local major, minor = string.match(mods["base"] or "", "^(%d+)%.(%d+)")
          return { tonumber(major) or 0, tonumber(minor) or 0 }
        end

        --- Puts `recipe` into the recipe category `category`. Factorio 2.1 replaced the field `category` of recipes with the
        --- list `categories`; this writes whichever the running game expects.
        function copy.set_category(recipe, category)
          local v = copy.game_version()
          if v[1] > 2 or (v[1] == 2 and v[2] >= 1) then
            recipe.categories = { category }
            recipe.category = nil
          else
            recipe.category = category
          end
        end

        --- Calls `change` on `t` and on every table inside it, once each.
        function copy.walk(t, change, seen)
          seen = seen or {}
          if type(t) ~= "table" or seen[t] then return end
          seen[t] = true
          change(t)
          for _, value in pairs(t) do copy.walk(value, change, seen) end
        end

        --- Multiplies every damage amount inside `t` (ammo, projectiles, attacks) by `factor`.
        function copy.scale_damage(t, factor)
          copy.walk(t, function(node)
            if node.type == "damage" and node.damage and node.damage.amount then
              node.damage.amount = node.damage.amount * factor
            end
          end)
        end

        --- Replaces every tint inside `t` (the coloured mask layers of animations) with `tint`.
        function copy.retint(t, tint)
          copy.walk(t, function(node)
            if node.tint then node.tint = tint end
          end)
        end

        return copy
        """;
}
