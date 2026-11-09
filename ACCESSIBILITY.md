# Accessibility

Factorio Mod Studio should be usable by every mod author, including people who use a keyboard instead of a mouse,
a screen reader, magnification or high-contrast settings. This page says what is in place today, what is known to be
missing, and how to tell us about a barrier.

## Where it runs

* **Desktop app** on Windows (the system web view); the Linux and macOS builds have not been tried yet.
* **Browser UI** (`fmstudio ui`) in a current Chromium-based browser or Firefox.
* **Command line** (`fmstudio`): every check, pack and test run is available there with plain-text and `--json` output,
  so the main workflows do not depend on the graphical interface at all.

## What is in place

* Light and dark themes, plus **Auto**, which follows the system setting.
* Animations are reduced when the system asks for reduced motion.
* The interface is translated into English, Russian, German, Simplified Chinese, Ukrainian, Polish, French and Spanish.
* Interactive parts of the web UI carry ARIA roles and labels, and the localization editor can be driven from the keyboard
  (see [Localization editor](README.md#localization-editor)).
* Page zoom (Ctrl + / Ctrl −) scales the whole interface.

## Known limitations

* The UI has **not yet been tested with a screen reader** (NVDA, JAWS, VoiceOver or Orca), so some views may read badly or not at all.
* Not every tab has been checked for complete keyboard navigation and visible focus.
* Font sizes are set in pixels, so the browser's own *text size* setting has no effect; use page zoom instead.
* Colour contrast has not been measured against WCAG 2.2 AA in both themes.
* Image previews of a mod's graphics have no text alternative beyond their file names.

The aim is WCAG 2.2 level AA for the web UI. Fixing the gaps above is part of the roadmap.

## Reporting a barrier

If something gets in your way, please [open an issue](https://github.com/4a11/factorio-mod-studio/issues/new?labels=accessibility&template=bug_report.yml)
and add the **accessibility** label. It helps to mention the assistive technology and its version, the browser or the desktop app,
and the tab or command where you got stuck. Accessibility problems are treated as bugs, not as feature requests.
