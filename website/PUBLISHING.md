# Publishing the website

The site in this folder is published to GitHub Pages at <https://4a11.github.io/factorio-mod-studio/> by the
*Website* workflow (`.github/workflows/website.yml`).

## Working on it

Node 22.22 or newer.

```sh
cd website
npm install
npm run dev        # http://localhost:4321/factorio-mod-studio/
npm run build      # refreshes the releases, checks the types, builds into dist/
npm run preview    # serves dist/
```

`npm run build` first asks the GitHub Releases API for the list of releases and saves it to
`src/data/releases.json`. The request is anonymous and read-only. Without a network, or when GitHub refuses, the
saved list is used and the build goes on. `FMS_SITE_OFFLINE=1 npm run build` skips the request.

The site's address is set in `astro.config.mjs` (`site` and `base`). With a domain of its own, `base` becomes `/`
and `site` the domain; `public/robots.txt` names the sitemap's full address and has to follow.

The picture shown when a link to the site is shared is `public/og.png`; `scripts/og.html` is its source and says
how to make it again.

## When it is republished

- A push to `main` that changes `website/` (or the workflow itself).
- A new release: *Website* runs after the *Release* workflow succeeds, so the Download and Changelog pages pick up
  the new version by themselves.
- By hand: Actions → Website → Run workflow. Do this after *Release notes* rewrites the notes of an old release.

GitHub Pages has to be set to Settings → Pages → *Build and deployment* → Source: **GitHub Actions**.

## Things to keep in step by hand

- `src/pages/[...lang]/download.astro` marks the systems the desktop app was tried on (`tried`). Change it there
  when Linux or macOS is confirmed.
- The screenshots in `src/assets/screenshots/` are taken from the running studio with a demo mod; retake them when
  the interface changes noticeably.

## Optional

- `ci.yml` and `game.yml` run on every push to `main`, also on pushes that change only `website/`. That costs CI
  minutes, nothing else. A `paths-ignore: ['website/**']` in their `push` trigger avoids it.
- The `github-pages` environment (Settings → Environments) can require an approval before each deployment.

## Taking it down

Settings → Pages → *Unpublish site*, and delete `.github/workflows/website.yml`. The folder stays in the history of
the repository; a search engine may keep its copy of the pages for a while.
