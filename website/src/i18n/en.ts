// English texts: the source language. `ru.ts` has to have the same shape (the Dict type checks it).
export const en = {
  meta: {
    description:
      'Factorio Mod Studio is a dedicated development environment for creating, inspecting, testing and packaging Factorio mods.',
    ogAlt: 'Factorio Mod Studio: Build. Inspect. Test. Ship. Next to it, the studio’s window with the Blocks tab.',
  },
  nav: {
    label: 'Main',
    features: 'Features',
    download: 'Download',
    changelog: 'Changelog',
    community: 'Community',
    github: 'GitHub',
    menu: 'Menu',
    skip: 'Skip to content',
    language: 'Language',
    home: 'Factorio Mod Studio: home',
  },
  footer: {
    tagline: 'A dedicated workspace for building, inspecting, testing and packaging Factorio mods.',
    product: 'Product',
    project: 'Project',
    repository: 'Source code',
    releases: 'Releases',
    issues: 'Issues',
    discussions: 'Discussions',
    license: 'MIT License',
    disclaimer:
      'Unofficial community project, not affiliated with Wube Software. “Factorio” is a trademark of Wube Software Ltd.',
  },
  // The studio's tabs. `id` is also the screenshot's file name in src/assets/screenshots.
  features: [
    {
      id: 'overview',
      name: 'Overview',
      text: 'Release readiness, counts of scripts, prototypes and resources, entry points and the game prototypes the mod changes.',
      benefit: 'See what is left before a release.',
      shot: 'The Overview tab: a release readiness checklist and counts of the mod’s content.',
    },
    {
      id: 'files',
      name: 'Files',
      text: 'The file tree with entry points and unused files, and a Lua editor with live checks.',
      benefit: 'Fix a problem where you found it.',
      shot: 'The Files tab: the file tree next to the Lua editor, with the validation output below.',
    },
    {
      id: 'prototypes',
      name: 'Prototypes',
      text: 'Everything the mod defines or changes, by type, with icons and the place it is defined. With the game’s own data dump, also the values the game ends up with.',
      benefit: 'Answer “which mod changed this, and to what?”',
      shot: 'The Prototypes tab: the mod’s items, recipes and entities grouped by type.',
    },
    {
      id: 'blocks',
      name: 'Blocks',
      text: '20 building blocks (machines, logistics, combat, vehicles) as recoloured copies of the game’s own: a colour slider, icons of your own and a properties panel showing original → yours.',
      benefit: 'Add content without writing prototypes from scratch.',
      shot: 'The Blocks tab: a furnace block with a colour slider, its icon and a properties table.',
    },
    {
      id: 'resources',
      name: 'Resources',
      text: 'Graphics and sounds by folder, with previews and the prototypes that use them.',
      benefit: 'Find what nothing uses before it ships.',
      shot: 'The Resources tab: the mod’s graphics with previews.',
    },
    {
      id: 'localization',
      name: 'Localization',
      text: 'A side-by-side editor with progress per language, placeholder checks and suggestions from your earlier translations.',
      benefit: 'Keep translations from drifting apart.',
      shot: 'The Localization tab: progress per language and the side-by-side translation editor.',
    },
    {
      id: 'problems',
      name: 'Problems',
      text: 'info.json, changelog, thumbnail, broken paths and requires, missing or broken translations: each with a rule code, file and line.',
      benefit: 'Catch mistakes before the game does.',
      shot: 'The Problems tab: one error and two warnings grouped by rule, each with its file.',
    },
    {
      id: 'build',
      name: 'Build',
      text: 'Validate, pack, a headless smoke test, a run in the game and Quick try on a test map, each with an isolated mod set and the right Factorio version.',
      benefit: 'Test without touching your game.',
      shot: 'The Build tab: the chosen Factorio version, the actions and the result of a validation job.',
    },
  ],
  // The long form of each tab for the Features page, by the same ids. Backticks mark code.
  featureDetails: {
    overview: {
      purpose: 'The first screen of a mod: how close it is to a release and what it consists of.',
      points: [
        'A release checklist: thumbnail, changelog entry for the version, localised mod name, no errors, packed zip. Each open item has a button that leads to the fix.',
        'Counts of scripts, prototypes, resources and languages; each card opens its tab.',
        'Entry points (`data.lua`, `control.lua`, `settings.lua`, migrations) and the game prototypes the mod changes through `data.raw`.',
      ],
      scenario: 'You come back to a mod after a month and want to know what is missing before you publish an update.',
    },
    files: {
      purpose: 'The mod as a folder tree, with an editor for quick fixes.',
      points: [
        'Entry points, files with problems and files nothing refers to are marked in the tree; *Unused only* lists the leftovers.',
        'Create, rename, move, duplicate and upload files. Delete goes to a trash, with Undo.',
        'Lua highlighting and a structural check as you type: unclosed strings, brackets and blocks. It is a quick check, not a full Lua analysis.',
        'Edits made in your own code editor show up within a moment.',
      ],
      scenario: 'A problem points at a line in `data.lua`: you open it, fix the path and save, without switching windows.',
    },
    prototypes: {
      purpose: 'What the mod adds to the game and what it changes there.',
      points: [
        'Items, recipes, technologies, entities and the rest, by type or by name, with icons and display names from the locale files.',
        'Recipes show ingredients → results; typing `iron-plate` finds every recipe that uses or makes it.',
        'A click jumps to the line where the prototype is defined.',
        '*In the game* mode: the studio starts Factorio without a window, with and without the mod, and lists everything the mod adds, changes or removes, with every value. Needs an installed Factorio.',
      ],
      scenario: 'A recipe behaves differently in the game than in your file: the *In the game* mode shows the final value and what it was before.',
    },
    blocks: {
      purpose: 'Ready-made content: copies of the game’s own prototypes that you rename, recolour and tune.',
      points: [
        '20 blocks: armor, a gun with ammo, a turret, belt tiers, inserters, machines, an ore, a car, a train and more. For Factorio 2.0 and 2.1.',
        'A colour slider with the icons previewed as they will be written, or PNG icons of your own.',
        'Each number the block sets (health, speed, inventory size, damage) as original / yours / in the game, saved back into the block’s Lua.',
        'Rename or remove a block safely: its file, the `require`, the locale keys in every language and the icons change together, with Undo.',
      ],
      scenario: 'You want a faster belt tier in your mod’s colour: add the block, move the slider, raise the speed and try it on a test map.',
    },
    resources: {
      purpose: 'Every picture and sound of the mod, and what uses it.',
      points: [
        'Graphics and sounds in a section per folder, with a name filter and three preview sizes.',
        'Each image shows its pixel size, its file size and the prototypes that use it as an icon; sounds play in a built-in player.',
        '*Unused only* keeps the files nothing in the mod refers to.',
        'One button copies the file’s Lua path (`__mod-name__/graphics/...`).',
      ],
      scenario: 'Before a release you check which sprites are left over from an old version and only make the zip bigger.',
    },
    localization: {
      purpose: 'A translation table instead of hand-edited `.cfg` files.',
      points: [
        'Every language with its progress; translated, identical-to-original and untranslated strings are counted separately.',
        'The original next to the translation, grouped by locale section, with filters and search.',
        'Placeholder checks: a translation that loses `__1__` or a plural block is flagged.',
        'Suggestions from your own earlier translations, and shortcuts that jump to the next untranslated string.',
      ],
      scenario: 'You added new items: the tab shows which strings each language is now missing and jumps from one to the next.',
    },
    problems: {
      purpose: 'Everything the checks found, in one list.',
      points: [
        'Checks of `info.json`, the changelog, the thumbnail, file paths and `require`s in Lua, and the locale files.',
        'Each problem has a rule code, a file and a line; a click opens the file there.',
        'Filter by text, code or file, group by rule or by file, copy the list for a bug report.',
        'After a smoke test or a run, errors from the Factorio log appear here too.',
      ],
      scenario: 'You renamed a graphics folder: the tab lists every Lua path that now points nowhere.',
    },
    build: {
      purpose: 'Check, pack and test the mod, with the output shown while it works.',
      points: [
        '*Validate* and *Pack*: `NAME_VERSION.zip` with the right folder layout, built only when the mod validates.',
        '*Smoke test*: the mod is loaded without a game window for a number of ticks and fails on any error in the log.',
        '*Run in Factorio* and *Quick try*: the game starts with only this mod and its dependencies; Quick try puts you on a test map with the mod’s items in the inventory (Factorio 2.0 and later).',
        'The installed Factorio that matches the mod’s `factorio_version` is chosen automatically; your normal mods folder is never touched.',
      ],
      scenario: 'You changed a recipe and want to see it in the game: one button, and you stand on a test map with the new item in the inventory.',
    },
  },
  lightbox: {
    label: 'Screenshot viewer',
    close: 'Close',
    previous: 'Previous screenshot',
    next: 'Next screenshot',
    enlarge: 'Enlarge',
  },
  pages: {
    home: {
      title: 'Factorio Mod Studio: build, inspect, test and ship Factorio mods',
      eyebrow: 'Development environment for Factorio mods',
      heading: 'Build. Inspect. Test. Ship.',
      lead: 'Everything around your mod’s code in one dedicated workspace: checks, prototypes, translations, test runs and a clean release.',
      download: 'Download',
      explore: 'Explore features',
      source: 'Source on GitHub',
      facts: ['Windows · Linux · macOS', 'Runs on your machine', 'Open source, MIT'],
      heroShot: 'The Blocks tab of Factorio Mod Studio: a furnace block with a colour slider, its icon and a properties table.',
      problem: {
        eyebrow: 'Why it exists',
        heading: 'The code is the easy part to tool',
        lead: 'Editing Lua, debugging and unit tests are well covered by existing editors and extensions. What stays manual is everything around them.',
        beforeTitle: 'Without a dedicated tool',
        before: [
          'A release zip assembled by hand, with the folder layout the game insists on.',
          'Test runs in your everyday mods folder, next to mods that have nothing to do with it.',
          'One real error somewhere in a long Factorio log.',
          'Translations that quietly fall behind the English file.',
          'Files and graphics nothing references any more, shipped anyway.',
        ],
        afterTitle: 'With Factorio Mod Studio',
        after: [
          'Pack builds NAME_VERSION.zip with the right layout, and only when the mod validates.',
          'Every run gets an isolated set of mods; your own mods folder is never touched.',
          'A log is condensed to its errors and warnings, with file, line and a hint.',
          'Progress per language, with missing keys listed and checked placeholders.',
          'Unused files and resources are marked before you release.',
        ],
        note: 'It works next to your code editor and does not replace it.',
      },
      featuresSection: {
        eyebrow: 'Features',
        heading: 'Eight tabs, one mod',
        lead: 'Each tab looks at the same mod from a different side.',
        all: 'All features in detail',
      },
      workflow: {
        eyebrow: 'Workflow',
        heading: 'From an empty folder to a release',
        lead: 'The tools hand work to each other: a problem opens the file at its line, a new block appears in the prototypes and in every language file, a test run reports back into Problems.',
        steps: [
          {
            name: 'Develop',
            text: 'Start from a template, add building blocks and edit Lua with live checks.',
            tools: ['Templates', 'Blocks', 'Files'],
          },
          {
            name: 'Inspect',
            text: 'See what the mod defines and changes, which resources it uses and what is wrong, with file and line.',
            tools: ['Prototypes', 'Resources', 'Problems', 'Localization'],
          },
          {
            name: 'Test',
            text: 'Load the mod headless, run the game with an isolated mod set or jump onto a test map with one click.',
            tools: ['Smoke test', 'Run', 'Quick try'],
          },
          {
            name: 'Build',
            text: 'Pack a clean release zip once the mod validates. The same commands run in CI.',
            tools: ['Pack', 'CLI'],
          },
        ],
      },
      preview: {
        eyebrow: 'Product preview',
        heading: 'The studio, as it is',
        lead: 'Real screenshots of version 0.3 with a demo mod. Select one to enlarge it.',
      },
      cta: {
        eyebrow: 'Download',
        heading: 'Ready to run, nothing to install',
        lead: 'Unpack the archive for your system and start the studio. It is free and open source.',
        latest: 'Latest release',
        released: 'released',
        all: 'All downloads',
        fallback: 'The current version is listed on GitHub Releases.',
      },
      community: {
        eyebrow: 'Community',
        heading: 'Developed in the open',
        lead: 'The project is early and feedback shapes what comes next.',
        cards: [
          { key: 'repo', title: 'Repository', text: 'Source code, documentation and the roadmap.' },
          { key: 'issues', title: 'Issues', text: 'Report a bug or ask for a feature.' },
          { key: 'releases', title: 'Releases', text: 'Every version with its notes and files.' },
        ],
        more: 'How to take part',
      },
    },
    features: {
      title: 'Features',
      description: 'What Factorio Mod Studio does: files, prototypes, building blocks, localization, checks, test runs and packaging.',
      eyebrow: 'Features',
      heading: 'The tools inside the studio',
      lead: 'Every tab works on the same mod, so a finding in one of them leads straight to the place in another.',
      jump: 'Tabs',
      tab: 'Tab',
      scenario: 'When it helps',
      // The same three stages as the workflow on the home page.
      groups: [
        {
          id: 'develop',
          name: 'Develop',
          heading: 'Write the mod and fill it with content',
          lead: 'Edit files with checks as you type, and add machines, belts or weapons as copies of the game’s own.',
        },
        {
          id: 'inspect',
          name: 'Inspect',
          heading: 'See what the mod is made of',
          lead: 'Prototypes, resources, translations and problems: each tab answers one question about the mod.',
        },
        {
          id: 'ship',
          name: 'Test and build',
          heading: 'Try it in the game and pack a release',
          lead: 'From a quick check to a run in Factorio, without touching your own mods folder.',
        },
      ],
      extras: {
        eyebrow: 'Around the tabs',
        heading: 'Also in the studio',
        lead: 'Tools that do not have a tab of their own.',
        items: [
          {
            title: 'Templates',
            text: 'Four starting points: empty, item + recipe, entity and runtime script, for Factorio 2.0 and 2.1 (all but the entity one also for 1.1). A new mod passes every check out of the box.',
          },
          {
            title: 'Adding mods',
            text: 'Drop a zip or a folder onto the window, even Factorio’s whole `mods` folder: everything is checked before anything is copied.',
          },
          {
            title: 'Log reader',
            text: '`fmstudio log` condenses a Factorio log to its errors and warnings, merges duplicates and adds the file, the line and a hint.',
          },
          {
            title: 'Factorio versions',
            text: 'Several installed versions are found automatically, and each mod runs on the one it targets. On Linux the studio can download the free headless server for smoke tests.',
          },
          {
            title: 'Quick open and search',
            text: 'Quick open by file name, a command palette, and search across the mod with plain text or regular expressions.',
          },
          {
            title: 'Interface in 8 languages',
            text: 'English, Russian, German, Simplified Chinese, Ukrainian, Polish, French and Spanish, with light and dark themes.',
          },
        ],
      },
      cli: {
        eyebrow: 'Command line',
        heading: 'The same tools without the window',
        lead: 'Everything behind the Build tab is also a command. Exit codes and `--json` output make each of them usable as a step in CI.',
        comments: {
          validate: 'what is wrong, with file:line',
          pack: 'dist/my-mod_0.1.0.zip',
          smoke: 'load it headless for 600 ticks; fails on any error',
          run: 'start Factorio with only this mod and its dependencies',
        },
      },
      limits: {
        eyebrow: 'Honest limits',
        heading: 'What it does not do',
        items: [
          'It works next to your code editor and does not replace it: there is no Lua completion and no debugger.',
          'The file analysis is static, nothing is executed: prototypes built in loops or by helper functions are not found. The *In the game* mode shows them, because there the game itself loads the mod.',
          'A smoke test catches a mod that fails to load or throws an error in the first ticks. It does not test how your mod behaves.',
          'This is an early version. The desktop app has been tried on Windows; on Linux and macOS it is built but not tried yet.',
        ],
      },
      cta: {
        heading: 'Try it on your own mod',
        lead: 'Unpack the archive, put a mod into the `mods` folder next to the program and open the studio.',
        download: 'Download',
        source: 'Source on GitHub',
      },
    },
    download: {
      title: 'Download',
      description: 'Download Factorio Mod Studio for Windows, Linux and macOS. Ready-to-run builds, no installer.',
      eyebrow: 'Download',
      heading: 'Get Factorio Mod Studio',
      lead: 'Ready-to-run builds for Windows, Linux and macOS. Unpack the archive and start: nothing to install.',
      latest: 'Latest release',
      released: 'released',
      notes: 'What is new',
      onGithub: 'Open on GitHub',
      megabytes: 'MB',
      free: 'Free and open source, MIT License.',
      fallback: {
        text: 'The list of files could not be read. The current version and its archives are on GitHub Releases.',
        button: 'Open GitHub Releases',
      },
      platformsHeading: 'Choose your system',
      tried: 'Tried',
      untried: 'Built, not tried yet',
      missing: 'No archive for this system in this release.',
      // Steps may use `code` and *emphasis*.
      platforms: {
        windows: {
          name: 'Windows',
          arch: 'x64',
          steps: [
            'Unpack the archive anywhere.',
            'Double-click `FactorioModStudio.exe`. The studio opens in a window of its own, with a `mods` folder next to it for your mods.',
            'Windows may warn about an unknown publisher: *More info → Run anyway*.',
          ],
        },
        linux: {
          name: 'Linux',
          arch: 'x64',
          steps: [
            'Unpack the archive anywhere.',
            'Double-click `Factorio Mod Studio.desktop`, or run `./FactorioModStudio` in a terminal (GNOME Files opens the `.desktop` file as text).',
            'The window needs the WebKitGTK package (`webkit2gtk-4.1`). Without it the studio offers your browser instead, as does `./fmstudio ui`.',
          ],
        },
        macArm: {
          name: 'macOS',
          arch: 'Apple Silicon',
          steps: [
            'Unpack the archive anywhere.',
            'Clear the download quarantine: run `xattr -dr com.apple.quarantine .` in the unpacked folder.',
            'Double-click `Factorio Mod Studio.app`, or run `./FactorioModStudio`. `./fmstudio ui` opens the studio in the browser.',
          ],
        },
        macIntel: {
          name: 'macOS',
          arch: 'Intel',
          steps: [
            'Unpack the archive anywhere.',
            'Clear the download quarantine: run `xattr -dr com.apple.quarantine .` in the unpacked folder.',
            'Double-click `Factorio Mod Studio.app`, or run `./FactorioModStudio`. `./fmstudio ui` opens the studio in the browser.',
          ],
        },
      },
      status: {
        heading: 'What has been tried',
        text: 'The desktop app has been tried on Windows. For Linux and macOS it is built by the same release process but has not been tried yet, so reports from these systems are especially welcome.',
        report: 'Report how it went',
      },
      requirements: {
        heading: 'What you need',
        items: [
          'A 64-bit Windows, Linux or macOS (Apple Silicon or Intel).',
          'No .NET and no installer: everything is inside the archive.',
          'The window uses the system’s web engine: WebView2 on Windows, WebKit on macOS, WebKitGTK on Linux.',
          'Factorio is optional. Checks, files, translations and packing work without it; smoke tests, runs, Quick try and the *In the game* data need an installed game.',
        ],
      },
      factorio: {
        heading: 'Factorio versions',
        items: [
          'Smoke tests are checked in CI against the headless server of Factorio **1.1**, **2.0** and **2.1** on Linux.',
          'Starting the game from the studio has been tried with Factorio **2.1** on Windows.',
          'Building blocks and Quick try are for Factorio **2.0** and **2.1**. Templates exist for 1.1 too, except the entity one.',
          'An installed game is found automatically (Steam and the usual folders); you can add other installations in the Build tab.',
        ],
      },
      other: {
        eyebrow: 'Other ways',
        heading: 'For the command line and from source',
        lead: 'These need the .NET 10 SDK.',
        tool: {
          title: 'As a .NET tool',
          text: 'Installs the `fmstudio` command from NuGet; `fmstudio ui` opens the studio in your browser.',
        },
        source: {
          title: 'From source',
          text: 'Clone the repository and start the desktop app.',
        },
        nix: {
          title: 'With Nix',
          text: 'The repository is a flake.',
        },
      },
      preview: {
        label: 'Preview',
        text: 'A newer preview is out. Previews come between releases and get less testing.',
      },
      previous: {
        heading: 'All versions',
        version: 'Version',
        date: 'Date',
        kind: 'Kind',
        release: 'Release',
        preview: 'Preview',
        files: 'Notes and files',
        changelog: 'See what changed in each version',
      },
    },
    changelog: {
      title: 'Changelog',
      description: 'What changed between the releases of Factorio Mod Studio.',
      eyebrow: 'Changelog',
      heading: 'What changed, release by release',
      lead: 'Every version with its date, what was added, what was fixed and a link to the release on GitHub.',
      versions: 'Versions',
      release: 'Release',
      preview: 'Preview',
      latest: 'Latest',
      onGithub: 'Release on GitHub',
      // Shown where the page language differs from the language of the notes; empty in English.
      languageNote: '',
      empty: 'The release notes could not be read. They are on GitHub Releases.',
      all: 'All releases on GitHub',
    },
    community: {
      title: 'Community',
      description: 'Report a bug, ask for a feature or contribute to Factorio Mod Studio on GitHub.',
      eyebrow: 'Community',
      heading: 'Built in the open',
      lead: 'Bug reports, ideas, translations and code are all welcome. Everything happens on GitHub.',
      account: 'The links open GitHub. You need a GitHub account to write there; reading needs none.',
      feedback: {
        eyebrow: 'Feedback',
        heading: 'Tell us what happened',
        cards: [
          {
            key: 'bug',
            title: 'Report a bug',
            text: 'Something broke or behaves oddly. The form asks for what is needed to reproduce it.',
          },
          {
            key: 'feature',
            title: 'Suggest a feature',
            text: 'Something is missing or could be easier. Describe what you were trying to do.',
          },
          {
            key: 'discussions',
            title: 'Ask or discuss',
            text: 'Questions and open-ended ideas fit Discussions better than an issue.',
          },
        ],
      },
      report: {
        heading: 'What makes a bug report useful',
        items: [
          'The version of the studio: it is at the bottom of the sidebar, or `fmstudio --version`.',
          'Your operating system and the Factorio version.',
          'The tab you were in or the exact command.',
          'What happened and what you expected instead.',
          'A small mod that shows the problem, if you can share one.',
        ],
        search: 'Look through the open issues first: a detail added to an existing one helps more than a duplicate.',
        searchLink: 'Open issues',
      },
      security: {
        heading: 'Security problems',
        text: 'Please do not open a public issue for a vulnerability. Report it privately through GitHub; the security policy explains what counts and what to expect.',
        report: 'Report privately',
        policy: 'Security policy',
      },
      contribute: {
        eyebrow: 'Taking part',
        heading: 'Ways to help',
        cards: [
          {
            key: 'testing',
            title: 'Try it on Linux or macOS',
            text: 'The desktop app has only been tried on Windows. A report that it started, or how it failed, is real help.',
          },
          {
            key: 'translations',
            title: 'Fix a translation',
            text: 'The interface comes in 8 languages. Corrections and new languages are welcome.',
          },
          {
            key: 'code',
            title: 'Contribute code',
            text: 'The contributing guide covers the setup, how the code is organised and what a pull request needs.',
          },
          {
            key: 'roadmap',
            title: 'Read the roadmap',
            text: 'What is planned and in which order. The order follows feedback.',
          },
        ],
      },
      project: {
        heading: 'The project',
        repo: 'Source code',
        releases: 'Releases',
        license: 'MIT License',
        conduct: 'Code of Conduct',
        disclaimer:
          'Factorio Mod Studio is an unofficial community project and is not affiliated with Wube Software.',
      },
    },
    notFound: {
      title: 'Page not found',
      description: 'This page does not exist.',
      eyebrow: 'Error 404',
      heading: 'Nothing at this address',
      lead: 'The page was moved or never existed. The links below lead back to the site.',
      home: 'Go to the home page',
    },
  },
};

export type Dict = typeof en;
