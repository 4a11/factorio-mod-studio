import { en, type Dict } from './en';
import { ru } from './ru';

export const languages = ['en', 'ru'] as const;
export type Lang = (typeof languages)[number];
export const defaultLang: Lang = 'en';

export const languageNames: Record<Lang, string> = { en: 'English', ru: 'Русский' };

const dictionaries: Record<Lang, Dict> = { en, ru };

export function useT(lang: Lang): Dict {
  return dictionaries[lang];
}

/** Paths for a page under `src/pages/[...lang]/`: English at the root, Russian under /ru/. */
export function langPaths() {
  return languages.map((lang) => ({
    params: { lang: lang === defaultLang ? undefined : lang },
    props: { lang },
  }));
}

export type { Dict };
