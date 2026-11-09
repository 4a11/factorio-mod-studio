# Security policy

## Supported versions

Factorio Mod Studio is in early development. Security fixes go into the **latest release** only; please update before reporting.

| Version | Supported |
| --- | --- |
| latest release (0.x) | ✅ |
| older releases | ❌ |

## What counts as a security problem

The studio runs locally and works with files on your computer, so the important boundaries are:

* the local server must only be reachable from `localhost` and must reject requests with a foreign `Host` header;
* it must only read and write files inside the selected mod and the studio's own folders;
* opening or importing a mod (a folder or a `.zip`) must never write outside its target folder or run code from that mod
  outside the Factorio process the user started;
* `fmstudio update` must only install releases from this repository.

A way around any of these, or anything else that lets a web page, a mod or an archive do something the user did not ask for, is a security problem.

## Reporting a vulnerability

**Please do not open a public issue.** Report it privately through
[GitHub's private vulnerability reporting](https://github.com/4a11/factorio-mod-studio/security/advisories/new)
(the **Report a vulnerability** button on the Security tab).

Include what you found, the steps or a sample mod to reproduce it, the version and your OS. You can expect a first answer within a week.
Once a fix is released the advisory is published, and you are credited unless you prefer otherwise.
