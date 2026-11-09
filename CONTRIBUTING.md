# Contributing to Factorio Mod Studio

Thanks for taking the time to help. Bug reports, ideas, translations and code are all welcome.

## Reporting a bug or asking for a feature

* Search the [existing issues](https://github.com/4a11/factorio-mod-studio/issues) first; adding details to an open one helps more than a duplicate.
* Use the **Bug report** or **Feature request** form when you open a new issue.
* For a bug, the most useful things are: the `fmstudio` version (`fmstudio --version`), your OS, the Factorio version, the exact command or
  the tab you were in, and what happened compared with what you expected. A small mod that shows the problem is gold.
* Questions and open-ended ideas fit better in [Discussions](https://github.com/4a11/factorio-mod-studio/discussions).
* A security problem is **not** a public issue: see [SECURITY.md](SECURITY.md).

## Setting up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the browser-side tests, Node 18 or newer.

```sh
git clone https://github.com/4a11/factorio-mod-studio.git
cd factorio-mod-studio
dotnet build
dotnet test
node --test tests/js/*.test.mjs
```

Run the studio from source with `dotnet run --project src/FactorioModStudio.Cli -- ui` (the browser UI) or
`dotnet run --project src/FactorioModStudio.Desktop` (the desktop window).

If `fmstudio ui` is running it locks the CLI's build output; build the tests elsewhere with
`dotnet test --artifacts-path <folder>` and set `FMS_REPO_ROOT=<repository>` so the tests can find the repository.

## How the code is organised

The [Development and Architecture sections of the README](README.md#development) describe the projects. The short version:

* **Behaviour goes into `FactorioModStudio.Core` first, with tests.** The CLI, the server and the desktop app only translate.
* The shape of the `--json` output and of the `/api` responses is the public contract; changing it is a breaking change and needs a note in the changelog.
* The web UI is plain HTML/CSS/JS ES modules with no build step. A new module has to be added to `StaticScripts` in `UiServer.cs`.
* Tests never start the real game: Factorio is replaced by a fake process runner (`tests/FactorioModStudio.Tests/FakeFactorio.cs`).

## Making a pull request

1. For anything bigger than a small fix, open an issue or a discussion first so we can agree on the approach.
2. Keep a pull request to one topic. Small pull requests get reviewed sooner.
3. Add or update tests for what you change; `dotnet test` and the Node tests must pass (CI runs them on Linux and Windows).
4. Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user would notice.
5. If you change the introduction of the English README (what it does, roadmap, why, quick start), update the translated READMEs too
   or say in the pull request that they need it.
6. Write commit messages that say what changes for the user, in plain words.

By contributing you agree that your work is released under the project's [MIT License](LICENSE).

## Translations

The interface is translated into several languages and corrections are very welcome. How to fix a translation or add a language is
described in [Translating the interface](README.md#translating-the-interface).

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
