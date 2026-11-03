# Code graph with graphify and Obsidian (optional)

[graphify](https://github.com/safishamsi/graphify) parses the source code into a knowledge graph
(classes, functions, calls, imports, communities). This repository ships a small setup script that builds
that graph locally and exports it as an [Obsidian](https://obsidian.md) vault.

Why:

* **Context economy for AI agents.** An agent can ask `graphify query` / `explain` / `path` and get a
  compact answer instead of grepping and reading many files.
* **A visual map for humans.** Browse the architecture in Obsidian's graph view, on a canvas, or in
  `graph.html`.

Nothing here is needed to build, test or run Factorio Mod Studio. It is opt-in and local-only: no source
file under `src/` or `tests/` is touched and nothing generated is committed.

## Requirements

* Python 3.10 or newer (`python3`, `python`, or the `py -3` launcher on Windows)
* Internet access for the one-time `pip install` (PyPI package **`graphifyy`**, with a double *y*;
  other `graphify*` packages are not affiliated with the project)
* [Obsidian](https://obsidian.md) (optional, only to browse the vault)

## Quick start

Windows (PowerShell), from anywhere; paths with spaces are fine:

```powershell
powershell -ExecutionPolicy Bypass -File tools\graphify\setup.ps1
```

(`-ExecutionPolicy Bypass` applies to this one process only; it is needed if script execution is
disabled on your machine.)

Linux / macOS:

```sh
tools/graphify/setup.sh
```

The script creates a virtual environment in `.fmstudio/graphify-venv`, installs the pinned graphify
version there (nothing is installed globally), builds the graph (a few seconds, no LLM) and exports the
vault. Running it again is safe: an up-to-date installation is detected and skipped.

| Option (sh / PowerShell) | Effect |
| --- | --- |
| `--hooks` / `-Hooks` | also install the local Claude Code integration and git hooks (see below) |
| `--refresh` / `-Refresh` | skip installation, only rebuild the graph and re-export the vault |
| `--no-obsidian` / `-NoObsidian` | skip the vault export |
| `--uninstall` / `-Uninstall` | remove hooks and the virtual environment (keeps `graphify-out/`) |
| `--purge` / `-Purge` | with uninstall: also delete `graphify-out/` |
| `--help` / `-Help` | show usage |

## What gets created

| Path | Content | In git? |
| --- | --- | --- |
| `.fmstudio/graphify-venv/` | Python virtual environment with graphify | no (`.fmstudio/` is ignored) |
| `graphify-out/` | `graph.json`, `graph.html`, `GRAPH_REPORT.md`, cache, and the vault `obsidian/` | no |
| `CLAUDE.md`, `.claude/` | only with `--hooks`: local Claude Code rule and hooks | no |
| `.graphifyignore` | what is left out of the graph | yes |

`CLAUDE.md` and `.claude/` are ignored on purpose: the hooks contain the absolute path of the graphify
executable on your machine and must not be committed.

`.graphifyignore` (gitignore syntax) excludes `tests/`, `examples/` and the generated UI translation tables
(`src/FactorioModStudio.Server/wwwroot/i18n/`); `.gitignore` is respected too, so your `mods/` workspace is
skipped. To include the tests again, remove the `tests/` line from `.graphifyignore` and run `--refresh`.

## Using the vault in Obsidian

1. Start Obsidian, choose **Open folder as vault** and select `graphify-out/obsidian`.
2. Open the **graph view** to explore the code structure, or open `graph.canvas` for a canvas layout.
   Each node (class, function, file) is a note with links to what it calls and what calls it.
3. Read `graphify-out/GRAPH_REPORT.md` for a summary: the most connected nodes ("god nodes"),
   communities and surprising connections.

Re-exporting keeps notes you add yourself and the `.obsidian/` settings in that folder, so you can annotate
the vault.

## Keeping it fresh

The graph is a snapshot of the code at build time. Rebuild it after larger changes:

```sh
tools/graphify/setup.sh --refresh
```

With `--hooks`, graphify also installs git hooks (post-commit, post-checkout, post-merge, post-rewrite;
only in a git checkout) that rebuild the code graph automatically after those git operations.

## How agents use it

Prefer these commands over grep or reading whole files when asking structural questions:

```sh
.fmstudio/graphify-venv/bin/graphify query "how are mods validated?"
.fmstudio/graphify-venv/bin/graphify explain "ModCatalog"
.fmstudio/graphify-venv/bin/graphify path "UiServer" "ModCatalog"
```

On Windows the executable is `.fmstudio\graphify-venv\Scripts\graphify.exe`. They answer from
`graphify-out/graph.json` and work offline.

With `--hooks`, `graphify claude install` adds a rule section to the local `CLAUDE.md` telling Claude Code
to consult the graph (and `GRAPH_REPORT.md`) before searching files, and registers `PreToolUse` hooks in
`.claude/settings.json` that only add a short hint before search and read tools; they never block a tool.

## Privacy

* Code is parsed locally with tree-sitter. The setup uses only this AST path, needs no API key and sets
  `GRAPHIFY_BACKEND=none` as a safety net. Semantic (LLM) extraction exists in graphify for docs, PDFs and
  images; it is not used here.
* We found no telemetry in the package code. Query logging exists but is off by default; do not enable it
  if you do not want your questions stored.
* The only network use of the scripts is `pip install` from PyPI. Opening `graph.html` may load a script from
  a public CDN (unpkg), so your browser may contact that host.

## Limits

* Lua mod code yields few symbols; JSON, `.cfg` and CSS files are not indexed. The graph is strongest for
  the C# projects and the JavaScript UI.
* It is a snapshot; re-run `--refresh` to update it.
* Everything generated is derived data and safe to delete (`--uninstall --purge`, or just remove
  `graphify-out/`).
* `--uninstall` removes the rule and hooks that graphify added. `.claude/settings.json` (and a
  `settings.json.graphify-bak` backup) may remain as local, git-ignored files; check them if you use Claude
  Code settings of your own.
* To keep the tracked `.gitattributes` clean, the script removes the `graphify-out/graph.json merge=graphify`
  line that `graphify hook install` adds (the graph is not committed, so a merge driver is not needed).

## Upgrading the pinned version

The version is pinned in one variable at the top of each script (`GRAPHIFY_VERSION` in `setup.sh`,
`$GraphifyVersion` in `setup.ps1`) because look-alike packages exist and a supply-chain surprise on
`pip install` is the main risk of this tool. To upgrade: review the release on
[GitHub](https://github.com/safishamsi/graphify/releases) and PyPI, change the version in both scripts,
and run the script again; it reinstalls when the installed version differs. Check that `--refresh` still works.

## Uninstall

```sh
tools/graphify/setup.sh --uninstall           # add --purge to delete graphify-out/ as well
```

PowerShell: `powershell -ExecutionPolicy Bypass -File tools\graphify\setup.ps1 -Uninstall [-Purge]`.
