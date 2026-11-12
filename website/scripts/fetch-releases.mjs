// Refreshes src/data/releases.json from the GitHub Releases API before a build.
//
// It never fails the build: without a network, on a rate limit or on an answer it does not understand, the saved
// snapshot stays as it is and the site is built from it. The request is anonymous; in CI a GITHUB_TOKEN from the
// environment raises the rate limit. The token is used here only and never reaches the pages.
//
//   node scripts/fetch-releases.mjs            refresh the snapshot
//   FMS_SITE_OFFLINE=1 npm run build           build from the snapshot without asking GitHub

import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const REPOSITORY = '4a11/factorio-mod-studio';
const API = `https://api.github.com/repos/${REPOSITORY}/releases?per_page=100`;
const DOWNLOADS = `https://github.com/${REPOSITORY}/releases/`;
const SNAPSHOT = fileURLToPath(new URL('../src/data/releases.json', import.meta.url));
const TIMEOUT_MS = 15_000;

const keep = (reason) => console.warn(`[releases] ${reason}; the saved snapshot is used.`);

/** One release of the API in the shape the site reads (see src/data/releases.ts), or null when it is unusable. */
function toRelease(item) {
  if (!item || item.draft) return null;
  const tag = item.tag_name;
  const date = item.published_at ?? item.created_at;
  const url = item.html_url;
  if (typeof tag !== 'string' || typeof date !== 'string' || typeof url !== 'string') return null;
  if (!url.startsWith(DOWNLOADS)) return null;

  const assets = (Array.isArray(item.assets) ? item.assets : [])
    .filter(
      (asset) =>
        typeof asset?.name === 'string' &&
        Number.isFinite(asset.size) &&
        typeof asset.browser_download_url === 'string' &&
        // Only files of this repository's releases are ever offered as downloads.
        asset.browser_download_url.startsWith(`${DOWNLOADS}download/`),
    )
    .map((asset) => ({ name: asset.name, size: asset.size, url: asset.browser_download_url }))
    .sort((a, b) => a.name.localeCompare(b.name));

  return {
    tag,
    name: typeof item.name === 'string' && item.name ? item.name : tag,
    date,
    prerelease: item.prerelease === true,
    url,
    body: typeof item.body === 'string' ? item.body.replace(/\r\n?/g, '\n') : '',
    assets,
  };
}

async function main() {
  if (process.env.FMS_SITE_OFFLINE) return keep('FMS_SITE_OFFLINE is set');

  const headers = {
    Accept: 'application/vnd.github+json',
    'X-GitHub-Api-Version': '2022-11-28',
    'User-Agent': 'fmstudio-website-build',
  };
  const token = process.env.GITHUB_TOKEN;
  if (token) headers.Authorization = `Bearer ${token}`;

  let items;
  try {
    const response = await fetch(API, { headers, signal: AbortSignal.timeout(TIMEOUT_MS) });
    if (!response.ok) return keep(`GitHub answered ${response.status}`);
    items = await response.json();
  } catch (error) {
    return keep(`GitHub could not be reached (${error?.name ?? 'error'})`);
  }

  if (!Array.isArray(items)) return keep('GitHub sent something that is not a list');
  const releases = items
    .map(toRelease)
    .filter(Boolean)
    .sort((a, b) => b.date.localeCompare(a.date));
  // An empty list would wipe the Download and Changelog pages: more likely a mistake than the truth.
  if (releases.length === 0) return keep('GitHub listed no releases');

  const next = `${JSON.stringify(releases, null, 2)}\n`;
  const previous = await readFile(SNAPSHOT, 'utf8').catch(() => '');
  if (next === previous) return console.log(`[releases] ${releases.length} releases, nothing new.`);

  await writeFile(SNAPSHOT, next, 'utf8');
  console.log(`[releases] snapshot updated: ${releases.length} releases, newest ${releases[0].tag}.`);
}

await main().catch((error) => keep(`unexpected error (${error?.message ?? error})`));
