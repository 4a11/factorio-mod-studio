# Factorio Mod Studio

A command-line toolkit that takes the repetitive work out of developing [Factorio](https://factorio.com) mods:
validate a mod, package a release, run the game with an isolated set of mods, understand error logs and
smoke-test a mod headlessly (locally or in CI).

> **Status: early MVP (0.1).** The logic is covered by unit tests, but the interaction with a real Factorio
> installation (`run`, `smoke`) has not been verified against the game yet. Feedback and bug reports are very welcome.
>
> Unofficial community project. Not affiliated with or endorsed by Wube Software. "Factorio" is a trademark of Wube Software Ltd.

## Why

Existing tools already cover editing Lua, debugging and unit testing (for example the *Factorio Modding Tool Kit*
extension for VS Code and the *FactorioTest* framework). What is still tedious is the glue: building a clean release,
launching the game with a reproducible set of mods, and finding the actual cause in a wall of log output.
Factorio Mod Studio focuses on that glue and is meant to work next to your favourite editor, not replace it.

## Install

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg
fmstudio help
```

Or run it without installing: `dotnet run --project src/FactorioModStudio.Cli -- <command>`.

## Commands

| Command | What it does |
| --- | --- |
| `fmstudio init` | Creates `fmstudio.json` (optional configuration) in the project folder. |
| `fmstudio new <name>` | Creates a new mod from a template (see "Creating a mod"). |
| `fmstudio validate [mod-dir]` | Static checks of `info.json`, `changelog.txt`, the thumbnail and file references in Lua. |
| `fmstudio pack [--out dir] [--force]` | Validates, then builds `NAME_VERSION.zip` with the correct top-level folder. Output goes to `dist/`. |
| `fmstudio prepare [profile]` | Builds an isolated mods folder for a profile (your mod, its dependencies and `mod-list.json`). |
| `fmstudio run [profile]` | Prepares the profile and starts Factorio with `--mod-directory` pointing at it. |
| `fmstudio log <file>` | Summarizes errors and warnings of a Factorio log: location, merged duplicates, hints. |
| `fmstudio smoke [profile]` | Headless test: generates a map (or loads the profile's save), simulates N ticks, fails on any log error. |
| `fmstudio factorio [--json]` | Lists the Factorio installations found (id, version, where each came from) and the one chosen for the project's mod. |
| `fmstudio ui` | Opens the graphical mod browser in your web browser (see below). |

Common options: `--project <dir>` (default: current directory), `--factorio <path>`, `--json` (for `validate`, `log`, `smoke`, `factorio`).
Exit codes: `0` ok, `1` problems found, `2` usage or configuration error — so every command can gate a CI job.

### Creating a mod

`fmstudio new <name>` scaffolds a ready-to-validate mod in the workspace folder (`mods/` by default; override with `--out <dir>`).
The same templates are available in the browser UI under **New mod…**. Every template passes `fmstudio validate` and `fmstudio pack`
without any issue, includes a generated 144x144 thumbnail, a `changelog.txt` and an English locale file.

| Template | Contents |
| --- | --- |
| `empty` (default) | `data.lua` and `control.lua` with explanatory comments only. |
| `item-recipe` | One item with a generated icon and a recipe (2 iron plates), available from the start. |
| `entity` | A placeable container (`<name>-chest`) with item, recipe, technology and generated sprites. |
| `runtime-script` | `settings.lua` (startup, runtime-global and per-player settings) and a `control.lua` with `storage` (`global` for 1.x), an event handler, a console command and `on_nth_tick`. |

`--factorio-version <MAJOR.MINOR>` (default `2.0`; the UI has a *Factorio version* selector) sets `factorio_version` and the
`base >= <version>.0` dependency of the new mod. Each template lists the versions it is valid for (`new --list`): `empty`,
`item-recipe` and `runtime-script` support 1.1, 2.0 and 2.1; `entity` is offered for 2.0 and 2.1 only (its prototypes were not
checked against 1.1). Asking for an unsupported combination is an error; in the UI such templates are greyed out with the reason.
The generated code is written from the documentation and has not been verified in the game.

### Several Factorio versions

A mod targets one game version (`factorio_version` in `info.json`, e.g. `1.1`, `2.0`, `2.1`); keep one branch per version. `run`,
`smoke` and the UI's Build tab choose the installed Factorio whose `MAJOR.MINOR` equals the mod's, the newest patch release if there
are several. Candidates (merged by executable path) come from:

1. installations added in the UI (stored in `.fmstudio/settings.json`; the CLI reads them too),
2. the `FACTORIO_PATH` variable,
3. `factorioPath` in `fmstudio.json`,
4. automatic discovery: the usual install folders, their sibling folders named `factorio*` (`Factorio-1.1`, `factorio_2.0.76`, ...;
   also next to the installations above) and the sub-folders of `<user data dir>/FactorioModStudio/factorio/`
   (`FMS_DATA_DIR` replaces `<user data dir>/FactorioModStudio`). Only direct children are inspected, links are not followed.

The version is read from `data/base/info.json`; the executable is never started to find it out. `--factorio <path>` is used exactly as
given (a mismatch with the mod only prints a warning). Without a matching installation the command fails and names the versions it
found. `fmstudio factorio` shows the candidates and the pick; the UI's version selector can override the pick for one mod.

```sh
fmstudio new --list
fmstudio new my-mod                                   # empty template, in ./mods
fmstudio new my-chest --template entity --title "My Chest" --author "Jane Doe" --description "Adds a chest."
fmstudio new my-mod --out ../other-folder --json      # machine-readable result
```

Names use letters, digits, `-` and `_` (up to 100 characters). Title and author are optional: the title defaults to the humanized
name (`hello-world` becomes "Hello World"), the author to the `FMS_AUTHOR` environment variable, else "Your Name". An existing mod folder is
never overwritten (exit code 2). Set `FMS_AUTHOR` once in your shell profile to stop typing `--author`.

> The Factorio 2.0 prototype formats in the templates (and `storage` instead of `global`) are written from the documentation and have **not
> been verified in the game**. If Factorio complains, check `fmstudio log` and the official prototype documentation.

### Configuration

`fmstudio.json` is optional. Comments and trailing commas are allowed.

```jsonc
{
  "modPath": ".",                       // folder with info.json, relative to this file
  "workspace": "mods",                  // folder with mods you work on; the UI discovers and edits them
  "factorioPath": null,                 // executable or install folder (or set FACTORIO_PATH)
  "modsSource": "path/to/factorio/mods",// where dependencies are taken from
  "profiles": {
    "default": {},
    "with-flib": {
      "mods": ["flib >= 0.15.0"],       // extra mods on top of your mod's required dependencies
      "args": [],                       // extra Factorio command-line arguments
      "save": null                      // load this save instead of generating a map
    }
  }
}
```

Profiles live in `.fmstudio/profiles/<name>/` (add `.fmstudio/` and `dist/` to your `.gitignore`).
Required dependencies listed in `info.json` are resolved transitively from `modsSource`, using the highest available version.
Your normal Factorio mods folder is never touched. `mod-settings.dat` inside a profile is preserved between runs.

### Smoke test

```sh
fmstudio smoke --ticks 600
```

Runs `factorio --create` and `factorio --benchmark <save> --benchmark-ticks N` with the profile's mods and parses the output.
It catches mods that fail to load and errors thrown from `control.lua` in the first ticks. It does **not** replace real tests
of your mod's behaviour; use a framework such as FactorioTest for that.

### Graphical mod browser

```sh
fmstudio ui                                # browse the mod in the current folder + the ./mods workspace
fmstudio ui --workspace <folder>           # use another workspace folder
fmstudio ui --mods <mods-folder>           # ...plus installed mods (read-only; zips or unpacked folders)
```

Opens a local page where you pick a mod and look inside it.

**Workspace.** Put your mods (one unpacked folder each, with an `info.json`) into the workspace folder, `mods` by default
(set `"workspace"` in `fmstudio.json` or pass `--workspace`). They show up under *Workspace* in the sidebar and are watched
live: new mods, new/changed/deleted files and edits made in your code editor appear in the browser within a moment (the
"Live" indicator at the bottom left). Files of workspace mods (and of the project mod) can be edited in the built-in editor:
`.lua`, `.cfg`, `.json`, `.txt` and `.md`, Ctrl/Cmd+S to save, *New file…* on the Files tab. If the file changes on disk
while you have unsaved edits, you get a choice between loading the disk version and overwriting it. Installed mods, zips and
the game's data are always read-only.

**Lua highlighting and checks.** In the editor, `.lua` files are syntax-highlighted (keywords, strings, numbers, comments and the
common Lua/Factorio globals; files over 300 KB are shown as plain text). About 400 ms after you stop typing the buffer is sent to
`POST /api/lint`, which runs a quick *structural* check. Problems get a red wavy underline, a marker in the line-number gutter and
a counter in the title bar (click it to jump from problem to problem); the message is shown in the status line when the caret is on
that line. This is not a full Lua parser: it does not check expressions, types, undefined variables or Factorio's API, and it can
miss errors that Lua itself reports. Factorio remains the source of truth.

| Code | Meaning |
| --- | --- |
| `LUA001` | Unterminated string (reported at the opening quote). |
| `LUA002` | Unterminated long string or comment (`[[ ... ]]`, `--[==[ ... ]==]`). |
| `LUA003` | Unmatched, mismatched or unclosed bracket `( ) [ ] { }`. |
| `LUA004` | Unexpected `end`, `until`, `else` or `elseif` with no matching open block. |
| `LUA005` | Block still open: `function`, `if`, `for`, `while`, `do` (missing `end`) or `repeat` (missing `until`). |

**Adding mods.** Drag a mod `.zip` or a mod folder anywhere onto the page, or use *Add mod…* / *Add folder…* in the sidebar.
The mod (at the top level or inside one folder, with an `info.json` that has a valid `name` and `version`) is copied to
`<workspace>/<name>` and opened. An existing folder is never overwritten (you get an "already exists" message with a link to
it). Only text, image and audio files (`.lua .cfg .json .txt .md .csv .png .jpg .jpeg .ogg .wav .mp3 .flac`) are imported;
other and hidden files are skipped. Limits: 200 MB per zip, 50 MB per uploaded file, 500 MB unpacked, 20000 files.

**Layout.** With a mod open, a bar at the top of the page stays visible on every tab: the mod's title, version and target Factorio
version, the buttons *Validate*, *Pack*, *Smoke test* and *Run in Factorio* (plus *Cancel* while a job of this mod runs), a badge
with the Factorio installation chosen for the mod (click: the Build tab), a chip with the state of the latest job (click: its
output) and the problem counter ("N errors · M warnings", click: the Problems list). The buttons use the same jobs and the same
rules as the Build tab, so a job started in one place is shown in the other. Below the page there is a collapsible **bottom
panel** (Ctrl/Cmd+J, closed by default; drag its top edge or use the arrow keys to resize it, double-click resets) with three
tabs: *Problems* (errors first; Enter or a click opens the file at the line), *Output* (live log of the latest job of the mod; it
opens by itself only when a job is started from the bar or the palette) and *Search*. The sidebar width can be changed the same way
(drag its right edge, ←/→, double-click resets). The *Theme* selector next to the language selector switches between *Auto*
(follows the system, also while the page is open), *Light* and *Dark*. The page remembers the theme, the sidebar width, the bottom
panel, the last mod and tab and the open editor tabs of each mod in the browser (`localStorage`, key `fms.ui.v1`); the last mod,
tab and editor tabs are restored only when the address has no `#/...` route.

**Editor tabs.** Every file you open (from the Files tab, a problem, a search result, quick open or a link) gets its own tab in
the viewer with its own text, unsaved changes, caret, scroll position, undo history, problems and conflict banner; a dot marks
unsaved changes and a tab of a file that was deleted on disk is struck through. At most 20 tabs are open: opening another one
closes the least recently used tab without unsaved changes (or refuses when all have some).

**Search in a mod.** Ctrl/Cmd+Shift+F (or *Find in mod* in the palette) opens the *Search* tab of the bottom panel: plain text or
a regular expression (the *.\** toggle), case-insensitive unless *Aa* is on, results grouped by file; Enter or a click opens the
file at the match and selects it. Typing searches after 250 ms; a note says when files changed since the last search.

**Quick open and commands.** Ctrl/Cmd+P opens the palette with the files of the open mod (fuzzy match, recently opened files
first, `path:line` or `path:line:col` jumps to a line; without an open mod it lists the mods). Ctrl/Cmd+K, F1 and
Ctrl/Cmd+Shift+P start it in command mode (`>`): go to a tab, open a mod, validate / pack / smoke test / run, new mod, add a mod,
toggle the panel, find in mod, save, close tabs, theme and language. Commands that do not apply right now are left out. The
sidebar button *Go to…* opens it too.

| Shortcut | Action |
| --- | --- |
| Ctrl/Cmd+P | Quick open a file |
| Ctrl/Cmd+K, F1, Ctrl/Cmd+Shift+P | Command palette |
| Ctrl/Cmd+J | Show / hide the bottom panel |
| Ctrl/Cmd+Shift+F | Search in the mod |
| Ctrl/Cmd+S | Save the active file |
| Ctrl+W | Close the active tab |
| Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+PageDown, Ctrl+PageUp | Next / previous tab |
| Esc | Close the active tab (the palette and dialogs keep Esc for themselves) |

What the tabs show:

* **Overview** — a release-readiness checklist (thumbnail, changelog entry for the version, localised mod name, errors, packed zip) with a
  progress bar and a button for each open item (open the file, go to Problems / Localization / Files / Build); clickable cards with counts
  that lead to the matching tab; entry points (`data.lua`, `control.lua`, `settings.lua`, migrations); and which existing prototypes the mod
  touches via `data.raw`. Hovering a dependency in the header explains its prefix (`!`, `?`, `(?)`, `~`).
* **Files** — a folder tree (or the files grouped by type), with a built-in viewer (Lua and text with line numbers, images, audio) and file management, see below.
* **Prototypes** — every prototype the mod defines, with display names from the locale files and icon previews; click to jump to the source line.
* **Recipes** — ingredients and results, for both the Factorio 2.x format and the older `result` / `normal` style.
* **Resources** — image gallery, sound players and locale files.
* **Localization** — a translation editor, see below.
* **Problems** — the same findings as `fmstudio validate`.
* **Build** — validate, pack, smoke-test and run the mod without leaving the browser, see below.

If Factorio is found (`--factorio`, `FACTORIO_PATH`, `factorioPath`, an installation added in the UI or a usual folder; the newest one), the game's own `base` and `core` data is listed too, which is handy as a reference.

The Lua analysis is **static**: nothing is executed. Prototypes written out literally (the usual `data:extend({ ... })` style) are found;
ones generated by loops or helper functions are not, and the Overview says how many entries could not be resolved.

Security notes: the server listens on `localhost` only, rejects requests with a foreign `Host` header, serves files only from inside the
selected mod (by mod id plus relative path) and sends a strict Content-Security-Policy. Writing is limited to text files inside editable
mods: every write needs a random per-session token that only the served page receives (plus a same-origin check), uses `If-Match`
checks so it can never silently overwrite a newer file, refuses hidden files/folders, symbolic links and anything outside the mod,
limits files to 2 MB, and goes through a temp file and rename so a half-written file is never visible. Line endings (CRLF) and a
UTF-8 BOM of an existing file are kept. The command needs the ASP.NET Core runtime, which is included with the .NET SDK.

### Build tab: validate, pack, smoke test and run

The *Build* tab of a mod runs the same operations as the CLI and streams the output while they work:

| Button | Same as | Notes |
| --- | --- | --- |
| *Validate* | `fmstudio validate` | Works for every mod, also read-only ones and zips. Lists the findings; click one to open the file. |
| *Pack* | `fmstudio pack` | Writes `NAME_VERSION.zip` to `<project>/dist` and offers it as a download. *Pack even with errors* is `--force`. |
| *Smoke test* | `fmstudio smoke` | Validates first (errors stop it before Factorio starts), then loads the mod headless for *Ticks* ticks with the given *Seed*. Shows the verdict and the diagnostics from the Factorio log. |
| *Run in Factorio* | `fmstudio run` | Prepares the profile and starts the game; the job stays *running* until Factorio exits. |

Pack, smoke test and run need an editable mod (the project mod or an unpacked mod in the workspace); Smoke test and Run also need
a Factorio installation. The *Factorio to use* selector is *Automatic* (the installation matching the mod's `factorio_version`) or one
installation you pick for this mod (remembered in the browser); the line above it says which one is used and why, and a hint
appears when none fits. *Manage installations...* lists every installation found (version, label, path, where it came from), lets you add
and remove your own and rescan. The first log line of a smoke test or run is `Using Factorio <version> (<path>) [auto|manual]`.
*Cancel* stops the running job and kills Factorio together with its child processes. The jobs of the last runs are listed under
*Recent jobs*; click one to see its log and result again. A job that is still running is picked up again after a page reload.

Behaviour worth knowing:

* Mods run from the browser use their own profile `ui-<mod name>-<MAJOR.MINOR>` (the mod's `factorio_version`) under
  `.fmstudio/profiles/`, using `mods`, `args` and `save` of your `default` profile, so a run from the browser never touches the profiles
  you use on the command line.
* One job at a time per mod, and only one smoke test or run at a time overall (a second start answers `409`). Validate and pack of
  different mods can run in parallel.
* The server keeps the 20 most recent jobs in memory (lost on restart) and at most 5000 log lines of 4000 characters per job.
  Stopping `fmstudio ui` stops the Factorio processes it started.
* The browser never chooses the arguments, and the executable only among the installations the server itself found or validated
  (next section); the arguments are built by the server. Every start and cancel needs the
  session token and the same-origin check like any other write, job ids are random, and `/api/downloads/<file>` serves only
  `*.zip` files that lie directly in `<project>/dist`.

#### Managing Factorio installations in the browser

*Manage installations...* opens a dialog with the list. *Add installation...* shows a text field (paste an absolute path; quotes around it
are removed), an optional label and a *Browse...* button with a built-in folder browser (folders, plus files named `factorio`).
Added installations are saved per project in `.fmstudio/settings.json` (`{ "factorioInstallations": [ { "path", "label" } ] }`,
not in `fmstudio.json`, because paths are personal; at most 50). A path saved by the previous single-path setting
(`"factorioPath"`) is picked up as one entry and dropped when the file is next written. Entries whose executable vanished stay in the
list marked as not found, so they can be removed. *Rescan* drops the 5-second cache of the discovery.

Because the saved program gets executed, the server is strict:

* Only an absolute path that resolves to a file named `factorio` / `factorio.exe` (an install folder is resolved to `bin/x64/factorio`)
  is accepted, never a symbolic link; anything else is answered with `400`. The stored value is checked again before every use,
  and the file is never started while saving.
* The folder listing returns only the names of non-hidden sub-folders and of files named `factorio` / `factorio.exe`: no other
  files, no contents, sizes or dates. It needs the session token even though it is a GET, rejects foreign `Host` headers like
  every request, never reads through symbolic links or junctions (`403`), and shows at most 2000 entries.

API (JSON, `X-FMS-Token` required for POST, DELETE and the folder listing):

| Request | Result |
| --- | --- |
| `GET /api/environment?modId=ID` | `{ "factorio": { "found", "path", "error", "source", "version" }, "resolution": { "modVersion", "mode": "auto\|manual\|none", "chosenId", "reason" } }` for the installation chosen for that mod (`source`: first of `ui`, `env`, `config`, `auto`; `none` when nothing fits). Without `modId`: the newest installation. An extra `installation=<id>` describes a manual choice. `404` unknown mod |
| `GET /api/factorio/installations` | `{ "installations": [ { "id", "path", "version", "label", "sources", "valid", "removable" } ], "invalid": [ { "id", "path", "label", "error" } ] }` (recomputed at most every 5 s) |
| `POST /api/factorio/installations` `{ "path": "<absolute path of the executable or install folder>", "label"?: string }` | `201` (or `200` when the same executable was already added; the label is updated) with the list above; `400` not an absolute path to a factorio executable, bad label or more than 50 entries, `413` body over 4 KB |
| `DELETE /api/factorio/installations/{id}` | the list; `404` unknown or not added in the UI |
| `POST /api/factorio/installations/rescan` | the list, freshly discovered |
| `GET /api/fs/list?path=<absolute folder>` | `{ "path", "parent", "separator", "entries": [ { "name", "type": "dir\|file", "factorio" } ], "truncated" }`; no `path` lists the drives / `/` and the home folder; `400` bad path, `403` unreadable or a link, `404` missing |
| `POST /api/mods/{id}/jobs` `{ "kind": "validate\|pack\|smoke\|run", "ticks"?, "seed"?, "timeoutMinutes"?, "force"?, "installation"?: "<id>" }` | `202` with the job (`factorio`: `{ id, path, version, mode, reason }` for smoke and run); a smoke / run without a usable installation fails with the reason as `error`; `400` bad request, `403` read-only mod, `404` unknown mod, `409` busy (`jobId` of the active job), `413` body over 4 KB |
| `GET /api/jobs/{jobId}?after=N` | the job with the log lines numbered above `N` (`state`: `queued`, `running`, `succeeded`, `failed`, `cancelled`) |
| `GET /api/jobs?modId=ID` | recent jobs, newest first, without log lines |
| `POST /api/jobs/{jobId}/cancel` | the cancelled job; `409` if it had already finished |
| `GET /api/downloads/{file}` | a packed archive from `dist` |

### Files tab

The *Files* tab shows the mod as a folder tree (*Tree*) or grouped into scripts, locale, graphics, sounds and data (*By type*; the
choice is remembered in the browser). *Filter by name* keeps the files whose name contains the text; *Unused only* keeps graphics,
sounds and scripts that nothing in the mod refers to (the button shows how many there are; it is hidden when there are none). *Expand
all* / *Collapse all* opens or closes every folder, and the line on the right counts the files shown. Each row has a type icon and
badges for problems, unsaved changes in open tabs, entry points and
*unused* files. The "unused" check is static: `__mod__/path` strings and `require` calls in Lua are followed, entry points
(`data.lua`, `control.lua`, `settings*.lua`, ...), `locale/`, `migrations/` and scenario folders always count as used, and a path built
at run time is only seen through its literal prefix (`"__mod__/graphics/" .. name` marks everything under `graphics/` as used).

In editable mods the toolbar says where new files go (the selected folder, the folder of the selected file, or the mod root; click
empty space or *Use the mod root* to go back to the root). The toolbar and the context menu (right-click, the row's *⋯* button, the
Menu key or Shift+F10) offer *New file…*, *New folder…*, *Upload
files…*, *Rename…* (F2), *Duplicate…*, *Delete* (Del) and *Show in file manager* (also for read-only mods). Drag rows onto a folder to
move them, or drop files from your computer onto a folder or onto the drop zone under the tree (the mod root) to upload them (text files as in the editor, images and sounds as binary assets; existing
files are never overwritten, dropped folders are skipped). Names are checked before anything is sent: no `/` `\`, no leading dot, no
`..`, no Windows device names (`CON`, `NUL`, `COM1`, ...), no `<>:"|?*`, no trailing dot or space, at most 255 characters. Open editor
tabs follow a renamed or moved file with their unsaved edits; deleting closes them (after a warning when they have unsaved changes).

*Delete* moves the file or folder into a trash outside the mod (`<workspace>/.fmstudio/trash/<mod>/`) and shows a notice with *Undo*.
The trash keeps the 20 newest entries of each mod for at most 7 days. Undo fails with a message when something else now exists at
the original path.

API (same token, same-origin and `Host` rules as the other writes; only editable mods, `403` for read-only ones, `404` unknown mod; paths
are relative to the mod, hidden files, links and anything outside the mod are refused with `400`; errors as `{ "error" }`):

| Request | Result |
| --- | --- |
| `POST /api/mods/{id}/fs/mkdir?path=<folder>` | `{ "path" }`; `409` the name exists |
| `POST /api/mods/{id}/fs/move` `{ "from", "to" }` | Moves or renames a file or folder (a case-only rename works on Windows and macOS too); `{ "from", "to" }`; `400` into itself or a folder with links that would have to be copied across drives, `404` missing source, `409` target exists, `413` body over 16 KB |
| `POST /api/mods/{id}/fs/copy` `{ "from", "to" }` | Copies one file (not folders); same answers as move |
| `DELETE /api/mods/{id}/file?path=<file or folder>` | Moves it into the trash; `{ "trashId" }` |
| `GET /api/mods/{id}/trash` | `[ { "trashId", "path", "kind": "file\|folder", "deletedUtc" } ]`, newest first (no token needed) |
| `POST /api/mods/{id}/trash/{trashId}/restore` | `{ "path" }`; `404` unknown entry, `409` the original path is taken |
| `PUT /api/mods/{id}/asset?path=<file>` | Raw body: a `.png`, `.jpg`/`.jpeg`, `.ogg` or `.wav` file whose leading bytes match the extension, at most 32 MB. Needs `If-None-Match: *` to create or `If-Match: <etag>` to overwrite (`428` without, `409` `{ error, etag }` when the file changed); returns `{ "etag", "size" }`; `400` content does not match, `413` too large, `415` other extension |
| `POST /api/mods/{id}/reveal?path=<file or folder>` | Opens Explorer / Finder / the default file manager at the file (no `path`: the mod folder; for a zip mod: the archive); `204`. Works for read-only mods too |

### Search API

`GET /api/mods/{id}/search?q=<text>&regex=0|1&case=0|1` searches the text files of a mod (`.lua .cfg .json .txt .md .csv .ini`; hidden
folders such as `.git`, symbolic links and junctions are never followed). It is read-only, needs no token (like reading a file) and is
bounded: the query has 1 to 200 characters, files over 2 MB and files that are not valid UTF-8 or contain NUL bytes are skipped
(counted in `skippedFiles`), at most 2000 files are scanned, 500 matches and 100 files with matches are returned, regular
expressions (.NET syntax) have a 1 s match timeout and the whole search a 3 s budget; hitting a limit sets `truncated`.

```json
{ "query": "stack_size", "files": [ { "path": "data.lua", "matches": [ { "line": 12, "column": 5, "length": 10, "text": "<the line, at most 300 characters>" } ] } ],
  "totalMatches": 1, "truncated": false, "skippedFiles": 0 }
```

`line` and `column` are 1-based, `column` and `length` count UTF-16 code units of the full line. Files are ordered by path, matches by
position. Errors: `400` empty or too long query or an invalid regular expression (`{ "error" }`), `404` unknown mod.

### Localization editor

The *Localization* tab of a mod turns `locale/<lang>/*.cfg` into a translation table: one row per key of the source language
(`en` if the mod has `locale/en`, otherwise the first language folder), the original text next to the translation of the language
chosen in the selector.

* **Progress and filters.** A progress bar and counters show translated, untranslated, identical-to-original and extra keys.
  Filter by *All strings*, *Untranslated*, *Identical to original*, *Extra keys* or *Placeholder problems*, or search section, key and text.
  Long tables are shown in chunks of 200 rows (*Show more*).
* **Placeholder checks.** The tokens of the original (`__1__`, `__ITEM__iron-plate__`, `__CONTROL__build__`, plural blocks
  `__plural_for_parameter_1_{...}__`, rich-text tags such as `[color=red]` / `[/color]` / `[item=iron-plate]`, and the literal `\n`)
  are shown as chips under the translation; a click inserts the token at the cursor. While you type, missing and unexpected tokens
  are reported live (the same rules run on the server in `LocalePlaceholders` and in the browser in `locale-placeholders.js`; the
  contents of plural blocks are not compared).
* **Suggestions.** For an untranslated key the editor offers a translation it already knows for the same original text, taken from
  the translated rows of this language in this mod and in the other editable mods of the workspace (a local translation memory;
  nothing is sent anywhere). A suggestion is only offered when its placeholders match; it is never applied automatically.
* **Saving.** *Save* sends only the changed rows in one request. A value is a single line: a line break typed into a translation is
  stored as the two characters `\n`. Only the edited lines of the `.cfg` files change; comments, blank lines, order, indentation,
  BOM and line endings are kept. New keys go into the file that mirrors the original's file (`locale/en/main.cfg` →
  `locale/de/main.cfg`), after the last key of their section. If the files change on disk while you hold unsaved edits (or the
  save is refused because they changed), a banner offers *Reload* (which discards your edits); without unsaved edits the table
  just refreshes. Extra keys (not present in the source language) have a *Remove* button.
* **New language.** *New language…* creates `locale/<code>/` with an empty stub file (one comment line) for every file of the source
  language. Installed mods, zips and the game's data are shown read-only.

API (same token, same-origin and `Host` rules as the file endpoints; JSON, errors as `{ "error": "..." }`):

| Request | Result |
| --- | --- |
| `GET /api/mods/{id}/locale` | `{ sourceLanguage, editable, languages: [{ code, total, translated, untranslated, identical, extra, placeholderProblems }] }` |
| `GET /api/mods/{id}/locale/{lang}` | The translation table (`rows`, `summary`, `revision`) with `ETag: "<revision>"` |
| `PUT /api/mods/{id}/locale/{lang}` | Body `{ "changes": [{ "section", "key", "value" }] }` (`value: null` removes the key); needs `If-Match: "<revision>"` (428 without, 409 `{ error, revision }` if the files changed); returns `{ revision, added, updated, removed, touchedFiles }` |
| `POST /api/mods/{id}/locale` | Body `{ "code": "de" }`; 201, or 409 if the language exists |

Limits: 5000 changes and 2 MB per request, 65536 characters per value. Not included: machine translation, editing comments or renaming keys.

### Validation rules

| Code | Level | Meaning |
| --- | --- | --- |
| FMS001 / FMS002 | error | `info.json` missing / not valid JSON |
| FMS003 | error | required field (`name`, `version`, `title`, `author`) missing |
| FMS004 / FMS005 | error | invalid mod name / version |
| FMS006 / FMS007 | warning / error | `factorio_version` missing / malformed |
| FMS008 | error | malformed dependency string |
| FMS009 | warning | no dependency on `base` |
| FMS010 / FMS011 | warning | thumbnail missing / not 144x144 |
| FMS012 | info | no `changelog.txt` |
| FMS013 / FMS014 | warning | changelog does not match `info.json` version / malformed changelog |
| FMS015 | error | depends on a DLC mod (`space-age`, `quality`, `elevated-rails`) while `factorio_version` is below 2.0 |
| FMS016 | info | `factorio_version` is well-formed but not one of 1.0, 1.1, 2.0, 2.1 |
| FMS030 | error | Lua references `__your-mod__/path` that does not exist |
| FMS031 | error | referenced file exists only with different letter case (works on Windows, breaks in the zip and on Linux) |
| FMS032 | error | `require("a.b")` / `require "a/b"` matches no `.lua` file of the mod (looked up next to the requiring file, then at the mod root; other mods and Factorio's lualib modules such as `util` are ignored) |
| FMS040 | error | malformed `locale/**/*.cfg` line (key before any section, line without `=`, bad section header, empty key) |
| FMS041 | warning | duplicate key in the same language and section (reported at the later one) |
| FMS042 | warning | prototype of this mod has no localised name in `locale/en` |
| FMS043 | warning | `{"section.key"}` in Lua uses a section the mod's `en` locale defines, but the key is missing |
| FMS044 / FMS045 | warning / info | a language lacks keys the default language has / has keys the default does not (one issue per language) |
| FMS046 | warning | numbered placeholders (`__1__`..`__9__`) differ from the default language's value |
| FMS047 | info | key in a non-standard section that no Lua file references (at most 30 listed, then a summary) |
| FMS048 | warning | other languages exist but there is no `locale/en` with a `.cfg` file |

Warnings and info issues never change the exit code of `fmstudio validate`; only errors (including FMS040) make it exit with 1.

The locale checks are static heuristics, written from knowledge of Factorio's behaviour and not verified against the game. The default
language is `en`. FMS042 only looks at prototypes found statically in data-stage Lua files (not `control.lua`, `settings*.lua` or
migrations), skips tables that set `localised_name`, accepts `item-name.<x>` for entities (Factorio falls back to the placing item) and
ignores unknown prototype types. FMS043 and FMS047 only see literal strings: dynamically built keys (`"section." .. name`) are not
resolved (a section used like that is excluded from FMS047), keys of the base game or other mods are not known (so FMS043 can warn about
a base-game key if the mod also defines the same section), and `__ENTITY__x__`-style references are neither checked nor compared. The
Resources tab of the UI shows per-language key counts with missing and extra keys.

## Translating the interface

The web UI is available in English (default), Russian, German, Simplified Chinese, Ukrainian, Polish, French and Spanish.
Pick a language with the selector in the bottom left of the sidebar; the choice is remembered in the browser. Only the
interface itself is translated: messages produced by the validator, API errors, template descriptions and the content of
mods are shown as they are (in English).

To add a language:

1. Copy `src/FactorioModStudio.Server/wwwroot/i18n/en.json` to `<code>.json` (a BCP 47 tag such as `it` or `pt-BR`) and translate the values.
   Keep every key and every `{placeholder}`; plural keys (`something.one`, `.other`, ...) need exactly the categories
   `new Intl.PluralRules('<code>').resolvedOptions().pluralCategories` reports for that language. Leave `Factorio`, `Lua`,
   file names and codes such as `FMS001` untranslated.
2. Add `{ code: '<code>', name: '<native name>' }` to `LANGUAGES` in `src/FactorioModStudio.Server/wwwroot/i18n.js`.
3. Add the code to the `UiLanguages` allowlist in `src/FactorioModStudio.Server/UiServer.cs`.

The translations are machine-assisted and may contain mistakes or unnatural wording; corrections are welcome via pull request.

## CI example

```yaml
- run: dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg
- run: fmstudio validate
- run: fmstudio pack
- run: fmstudio smoke --factorio /path/to/headless/factorio --ticks 600
```

## Development

```sh
dotnet build
dotnet test
node --test tests/js/highlight.test.mjs   # editor syntax highlighter (Node 18+, no dependencies)
node --test tests/js/palette-logic.test.mjs   # fuzzy matching of the quick-open palette
```

* `src/FactorioModStudio.Core` — library: validation, packaging, profiles, log parser, smoke test, mod inspector (Lua reading, locale, zip extraction). No console or HTTP code.
* `src/FactorioModStudio.Server` — local web host: a JSON API over Core (read, plus guarded writes), live change events (server-sent events) and the embedded UI (`wwwroot`, plain HTML/CSS/JS, no build step). The page is a set of ES modules: `app.js` (routing and wiring), `editor-tabs.js`, `action-bar.js`, `bottom-panel.js`, `search-panel.js`, `palette.js` with the pure `palette-logic.js`, `build-view.js`, `locale-view.js`, `theme.js` / `theme-init.js` and `ui-state.js`. A new module has to be added to `StaticScripts` in `UiServer.cs`.
* `src/FactorioModStudio.Cli` — the `fmstudio` command-line tool; a thin layer over Core and Server.
* `tests/FactorioModStudio.Tests` — xUnit tests; Factorio itself is replaced by a fake process runner.
* `examples/hello-mod` — a minimal mod that passes all checks.

## Architecture

```
clients:   CLI  |  web UI (browser)  |  future: editor extension, desktop shell
hosts:     CLI (in-process)  |  Server (JSON API)
core:      FactorioModStudio.Core
```

Core holds all behaviour and knows nothing about the console or HTTP; hosts only translate. New features go into Core first, with tests,
and become available to every client. The shape of the `--json` output and of the `/api` responses is the public contract.

## Roadmap

Ideas, roughly in order of intended complexity. Nothing here is a promise; priorities will follow real feedback.

* Verify `run` / `smoke` against real Factorio on Windows, Linux and macOS; capture `factorio-current.log` as well as stdout.
* Locale checks (keys used in Lua vs. `locale/*.cfg`).
* `fmstudio new` — project templates.
* UI: rename/move files, profile selection and saves in the Build tab, dependency graph, ingredient icons from other mods.
* Inspecting the final prototypes via `--dump-data` ("which mod changed this item?").
* Integration with existing test frameworks instead of reimplementing them.
* Dependency / conflict analysis; a GUI or editor extension on top of the same library.

## License

[MIT](LICENSE)
