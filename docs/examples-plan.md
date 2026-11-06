# Examples plan (roadmap 0.5)

A set of small, complete mods that show how to add a common kind of thing to Factorio. Each example is offered under
**New mod...** and `fmstudio new --template <id>`, passes `fmstudio validate`, and has been loaded in the game, not only
written from the documentation.

The prototype types below follow the Factorio 2.0 prototype API; check them against
<https://lua-api.factorio.com/latest/prototypes.html> before building an example. Types marked *SA* need the Space Age
expansion (a `space-age` dependency).

## The first set

17 examples, each one a working mod on its own.

| # | Example | Prototype types |
| --- | --- | --- |
| 1 | Item and recipe (exists today as `item-recipe`) | `item`, `recipe`, `technology` |
| 2 | Chest | `container` |
| 3 | Logistic chest | `logistic-container` |
| 4 | Fluid tank | `storage-tank`, `fluid` |
| 5 | Belt tier: belt, underground belt and splitter of one speed | `transport-belt`, `underground-belt`, `splitter` |
| 6 | Inserter | `inserter` |
| 7 | Pipe and underground pipe | `pipe`, `pipe-to-ground` |
| 8 | Electric pole | `electric-pole` |
| 9 | Machine with its own recipes | `assembling-machine`, `recipe-category` |
| 10 | Furnace | `furnace` |
| 11 | New ore and a drill for it | `resource`, `autoplace-control`, `mining-drill` |
| 12 | Solar panel and accumulator | `solar-panel`, `accumulator` |
| 13 | Weapon: gun, ammo, grenade | `gun`, `ammo`, `capsule`, `projectile` |
| 14 | Defense: turret and wall | `ammo-turret`, `wall`, `gate` |
| 15 | Vehicles: car, locomotive and wagon | `car`, `locomotive`, `cargo-wagon` |
| 16 | Armor with equipment | `armor`, `equipment-grid`, `energy-shield-equipment` |
| 17 | Script tool: shortcut, hotkey and area selection | `shortcut`, `custom-input`, `selection-tool` |

## Every type that could get an example later

★ marks the types used in the first set.

### Items and data (not placed on the map)
- ★ `item`, ★ `recipe`, ★ `technology`, ★ `fluid`, ★ `recipe-category`
- `tool` (science pack), `module`, `item-group` / `item-subgroup`, `quality`
- ★ `selection-tool`, `item-with-entity-data`, `rail-planner`, `repair-pack`

### Storage
- ★ `container`, ★ `logistic-container`, ★ `storage-tank`
- `linked-container`, `infinity-container`

### Logistics
- ★ `transport-belt`, ★ `underground-belt`, ★ `splitter`, `lane-splitter`, `loader`, `loader-1x1`, `linked-belt`
- ★ `inserter`
- ★ `pipe`, ★ `pipe-to-ground`, `pump`, `offshore-pump`, `valve`
- ★ `electric-pole`, `power-switch`
- `roboport`, `logistic-robot`, `construction-robot`

### Production and energy
- ★ `assembling-machine`, ★ `furnace`, ★ `mining-drill`, `lab`, `beacon`, `rocket-silo`
- ★ `solar-panel`, ★ `accumulator`, `boiler`, `generator`, `burner-generator`
- `reactor`, `heat-pipe`, `electric-energy-interface`
- *SA*: `agricultural-tower`, `asteroid-collector`, `fusion-reactor`, `fusion-generator`, `lightning-attractor`

### Weapons and defense
- ★ `gun`, ★ `ammo`, ★ `capsule`, ★ `projectile`, `stream`, `beam`
- ★ `ammo-turret`, `electric-turret`, `fluid-turret`, `artillery-turret`
- ★ `wall`, ★ `gate`, `land-mine`, `radar`
- ★ `armor`, `sticker`, `fire`
- `unit`, `unit-spawner`, `turret` (enemies, nests, worms)

### Equipment (armor and vehicle grids)
- ★ `equipment-grid`, ★ `energy-shield-equipment`
- `battery-equipment`, `solar-panel-equipment`, `generator-equipment`, `active-defense-equipment`,
  `movement-bonus-equipment`, `night-vision-equipment`, `roboport-equipment`, `belt-immunity-equipment`,
  `inventory-bonus-equipment`

### Transport
- ★ `car`, `spider-vehicle`
- ★ `locomotive`, ★ `cargo-wagon`, `fluid-wagon`, `artillery-wagon`
- `train-stop`, `rail-signal`, `rail-chain-signal`
- rails (`straight-rail`, `curved-rail-a`, `curved-rail-b`, `half-diagonal-rail`, the `elevated-*` rails, `rail-ramp`,
  `rail-support`): complex, not a good first example

### Circuits and signals
- `constant-combinator`, `arithmetic-combinator`, `decider-combinator`, `selector-combinator`
- `lamp`, `programmable-speaker`, `display-panel`, `virtual-signal`

### World
- ★ `resource`, ★ `autoplace-control`, `tile`, `tree`, `simple-entity`, `fish`, `cliff`, `optional-decorative`,
  `noise-expression`
- *SA*: `planet`, `space-location`, `space-connection`, `asteroid`, `asteroid-chunk`

### Interface and other
- ★ `shortcut`, ★ `custom-input`, `sprite`, `sound`, `achievement`, `tips-and-tricks-item`, `damage-type`
