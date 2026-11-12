import { defaultLang, type Lang } from '../i18n';

// BASE_URL is '/factorio-mod-studio' or '/factorio-mod-studio/' depending on the Astro version: normalise it.
const base = import.meta.env.BASE_URL.replace(/\/+$/, '');

/** A page of the site. `path` is the English route with slashes on both sides: '/', '/features/'. */
export function pageUrl(lang: Lang, path: string): string {
  return `${base}${lang === defaultLang ? '' : `/${lang}`}${path}`;
}

/** A file from `public/`: '/favicon.svg', '/screenshots/files.png'. */
export function assetUrl(path: string): string {
  return `${base}${path}`;
}

/** The absolute address of a page, for canonical and Open Graph tags. */
export function absoluteUrl(siteUrl: URL | undefined, lang: Lang, path: string): string {
  return new URL(pageUrl(lang, path), siteUrl ?? 'http://localhost').href;
}
