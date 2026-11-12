// Releases of the studio, read from the saved snapshot releases.json. `npm run build` refreshes the snapshot from the
// GitHub Releases API first (scripts/fetch-releases.mjs) and keeps the old one when GitHub cannot be reached, so the
// pages never ask GitHub from the browser and a build never depends on the network.
import snapshot from './releases.json';

export interface ReleaseAsset {
  name: string;
  size: number;
  url: string;
}

export interface Release {
  tag: string;
  name: string;
  date: string;
  prerelease: boolean;
  url: string;
  body: string;
  assets: ReleaseAsset[];
}

export const releases: Release[] = snapshot;

/** The newest release that is not a preview, or undefined when nothing is published. */
export const latestStable: Release | undefined = releases.find((r) => !r.prerelease);

export function formatDate(iso: string, lang: string): string {
  return new Intl.DateTimeFormat(lang === 'ru' ? 'ru-RU' : 'en-GB', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(new Date(iso));
}
