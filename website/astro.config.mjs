// @ts-check
import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';

// The site is served from a project path on GitHub Pages, not from the domain root:
// https://4a11.github.io/factorio-mod-studio/
// To move to a custom domain later, change `site` and set `base` to '/'.
export default defineConfig({
  site: 'https://4a11.github.io',
  base: '/factorio-mod-studio',
  trailingSlash: 'always',
  integrations: [
    sitemap({
      i18n: { defaultLocale: 'en', locales: { en: 'en', ru: 'ru' } },
    }),
  ],
});
