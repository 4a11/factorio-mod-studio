# Changelog

What changed between the releases of Factorio Mod Studio. The format follows [Keep a Changelog](https://keepachangelog.com);
versions are the tags on GitHub, and the same `fmstudio` versions are on [NuGet](https://www.nuget.org/packages/FactorioModStudio.Cli).

## [Unreleased]

### Changed
- **The Problems tab, redesigned** in the style of the Files tab: a filter (text, code or file), Errors / Warnings / Info
  toggles with counts, grouping by rule, by file or as one list (remembered), collapsible groups that show 20 rows and
  "Show N more", *Copy list* for bug reports, and a ⋯ / right-click menu on every problem: open the file, copy the message,
  show only this rule or this file. Click (or Enter) opens the file at the line; the arrow keys move between problems.
- **The Build tab, redesigned**: a *Factorio* card on top (the version that runs the mod, where it was found, why it was chosen,
  the picker and *Manage installations...*, with a green or red edge), the actions with icons, the options grouped under the
  action they belong to (*Smoke test*: ticks and seed, *Pack*: even with errors), then the recent jobs on the left and the
  chosen job on the right: its state, time, verdict, problems (the first 10, then *Show all*) and log. Before the first job an
  empty state explains what each action does.
- **The game version follows the mod's `base` dependency**: a mod that needs `base >= 2.0.20` is no longer given an installed
  Factorio 2.0.10 just because the branch matches. The Build tab, smoke tests, runs and `fmstudio factorio` pass over
  installations that do not satisfy it and say why; the install button offers a release that does (`base = 2.0.20` installs
  exactly that one). An installation chosen by hand is kept, with a note about the mismatch.

### Added
- **The full game from factorio.com**: besides the headless server, the studio installs Factorio with graphics (Windows x64 and
  Linux x64) into its own folder, where Run and the smoke tests find it. It needs a factorio.com account that owns the game:
  the installations dialog in the Build tab saves a username and token (on Windows the token is encrypted for your Windows user;
  it is only ever sent to factorio.com and never shown again) or uses the account Factorio itself is signed in to, read from its
  player-data.json each time. From the command line: `fmstudio factorio install 2.0 --game` with that account or with
  `FACTORIO_USERNAME` and `FACTORIO_TOKEN`; `fmstudio factorio remove <version> --game` deletes it.
- **Log in to factorio.com** from the installations dialog (username or e-mail and password, and the code factorio.com e-mails
  when it asks for one): the studio gets a token the way the game's launcher does and saves only the token, protected like a
  typed one; the password is used for that one request and kept nowhere. Five attempts a minute at most. Pasting a token still
  works, under *Paste a token instead*.
- **Downloads continue where they stopped**: an install that was cancelled, stalled or lost the network keeps what it got and
  asks the server for the rest next time. Before downloading, the studio checks the free disk space (archive plus unpacking)
  and says how much is needed; the progress shows megabytes ("41% (620 of 1500 MB)").
- **Run needs the full game**: when the Factorio that runs the mod is a headless server, the Build tab says that *Run in
  Factorio* needs the full game and offers to install it.
- **FMS049**: an error when the `base` dependency cannot be met by any release of the `factorio_version` the mod targets
  (`factorio_version` 1.1 with `base >= 2.0.0`): the game would refuse to load the mod.

## [0.2.1] - 2026-11-09

A preview on the way to 0.3 (see and edit content): the mod as the game loads it, and a tab for building blocks.

### Added
- **Updates from GitHub**: the studio says when a newer version is out, at the bottom of the sidebar, and a copy unpacked from
  a release archive installs it with one button (the window restarts itself). `fmstudio update` does the same from the command
  line, `fmstudio update --check` only looks. A .NET tool is shown its `dotnet tool update` command.
- **Two update channels**: *releases* (X.Y.0: 0.3.0, 0.4.0) and *previews*, which also get the versions between them
  (0.3.1, 0.3.2 ...). A preview starts on the preview channel, a release on the release channel; the choice is in the sidebar
  and `fmstudio update --channel stable|preview`.

- **A menu for each mod in the sidebar** (right click, the context-menu key, or "⋯" on hover): open, pin, show in the file
  manager and, for mods in the workspace folder, delete. A deleted mod goes to the studio's trash
  (`.fmstudio/trash` in the workspace, kept 7 days) and the notice offers Undo.

- **The mod as the game loads it**: the Prototypes tab has an "In the game" mode. Factorio starts twice without a window
  (`--dump-data`, with its own config so the player's settings and saves are not touched), without and with the mod, and
  the studio lists every prototype the mod adds, changes or removes with all its values: copies made with
  `table.deepcopy` and the building blocks show "original → this mod", changes made by code (technologies that unlock
  the new recipes, other mods reacting) "before → after". The dump without the mod is kept and reused, so loading again
  takes seconds; a notice says when the mod's files changed since. Dependencies are taken from the workspace folder
  when the project names no `modsSource`.
- **A Blocks tab** for the building blocks of a mod: the list on the left (icon, name, what it is: belt tier, armor...),
  the chosen block on the right as a table "original / yours / in the game" for each number it sets (health, speed,
  inventory, crafting speed, power, damage factor...), with a reset to the original per row and a save bar that appears
  when something changed. Saving changes only those numbers in the block's Lua; comments and anything else in the file
  stay, and a file that changed in between is not overwritten. The originals come from the game: the first time a block
  is opened without game data, the studio loads the mod in Factorio by itself.
- **Rename, remove and upgrade a block safely**: the name is edited in the block's header and checked as it is typed; a
  rename changes its prototypes, file, require, locale keys in every language, icon files and every Lua string of the mod
  that names them, and adds a migration so existing saves keep their entities and items. Removing (from the "⋯" menu)
  takes out the file, require, locale keys and icons (and the shared helpers with the last block) and says which files
  still name its prototypes. Every change is listed before it is made, and the notice afterwards offers Undo. Blocks
  added by older versions of the studio can be upgraded, one or all at once: they get recoloured icon files of their own.
- A clear message when a folder cannot be moved because another program has it open (a file manager, a terminal, an
  editor), instead of "Access to the path ... is denied".

### Changed
- Versions X.Y.Z with Z > 0 are published as GitHub pre-releases and not pushed to NuGet.

### Fixed
- **Building blocks in the Prototypes tab**: what a block adds (the copies made with `copy.item`, `copy.entity`, `copy.recipe`
  and `table.deepcopy(data.raw...)`) is listed now, with its icon and "copy of gun-turret"; before, only prototypes written
  out as tables showed up. Names built from `local name = ...` and `name .. "-ammo"` are followed too, and the
  localised-name check (FMS042) now covers these prototypes as well.

## [0.2.0] - 2026-11-08

Ready for people: a desktop app in every release archive, and the real game checked in CI.

### Added
- **Desktop app**: the studio in a window of its own (Avalonia 12 with the system's web view), in the release archives
  for Windows, Linux and macOS. Something to double-click on each system: `FactorioModStudio.exe`,
  `Factorio Mod Studio.app`, `Factorio Mod Studio.desktop`; the `mods` folder next to it is the workspace.
  A double click on `fmstudio` opens the window too. Without the system web engine (Linux without WebKitGTK) the
  window says so and offers the browser.
- **Building blocks**: `fmstudio add` and the studio's Add menu put armor, enemies, belt tiers, machines, vehicles and
  more (20 blocks) into a mod, as recoloured copies of the original pictures, checked in Factorio 2.1.
- **Errors from the game in Problems**: after Run in Factorio, `factorio-current.log` is read, and the Problems panel
  lists what the newest smoke test or run reported next to the mod's own checks, with `file:line` for the mod's files.
- **Import**: a folder is checked before it is added; a folder of mods opens a list to choose from; hidden files in
  zips are skipped; a short summary after importing many mods.
- **Sidebar**: compact mod rows with icons, collapsible groups, pinned mods, an Add menu; connection status and
  Go to... in the footer.
- `fmstudio smoke` prints Factorio's last lines when the game stops with an exit code but logs no error.
- CI: the *Game* workflow smoke-tests a mod from every template and one with every building block against the real
  headless server of Factorio 1.1, 2.0 and 2.1, downloaded with `fmstudio factorio install`.

### Changed
- The item and chest templates are copies of the originals, like the blocks.
- The Files tab editor can be resized.
- The Nix flake is built only when the version or the Nix files change.

### Fixed
- Smoke tests on the headless server: it rejects `--disable-audio`, so map generation failed on every version; the
  option now goes to the full game only.
- The Files tab and the Problems tab label could show the text "null".
- Building block names may start like mod names.

## [0.1.1] - 2026-11-07

### Changed
- Moved to .NET 10 (LTS).
- Releases publish to NuGet with Trusted Publishing (no stored API key), after the GitHub release, so a NuGet problem
  never holds the archives back.

### Added
- Nix flake: `nix run github:4a11/factorio-mod-studio`, using the prebuilt .NET 10 packages.

## [0.1.0] - 2026-11-06

The first release.

### Added
- **Command line**: `init`, `new` (templates: empty, item + recipe, entity, runtime script), `validate`, `pack`, `prepare` /
  `run`, `smoke`, `log`, `factorio` (`available`, `install`, `remove`: Factorio's headless server on Linux).
- **Studio** in the browser (`fmstudio ui`), in 8 interface languages:
  - *Overview*: release readiness with a button for every open item, cards per kind of content, entry points,
    the existing prototypes the mod touches.
  - *Files*: folder tree, create, rename, move and delete with a trash and undo, upload, unused-file detection.
  - *Prototypes*: what the mod defines or changes in `data.raw`, by family or by name; recipes with ingredients and
    results.
  - *Resources*: graphics and sounds by folder, previews, the prototypes that use each picture.
  - *Localization*: languages with their progress, side-by-side editor, placeholders, translation memory.
  - *Problems* and *Build*: validate, pack, smoke test and run from the studio, managing Factorio installations,
    installing a missing version.
- Ready-to-run release archives for Windows, Linux and macOS (no .NET needed) and the `FactorioModStudio.Cli` .NET
  tool on NuGet.
- README translations of the introduction in the 7 other interface languages.

[0.2.1]: https://github.com/4a11/factorio-mod-studio/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/4a11/factorio-mod-studio/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/4a11/factorio-mod-studio/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/4a11/factorio-mod-studio/releases/tag/v0.1.0
