// Screenshots of the studio, one per tab, taken from the running app with a demo mod.
import type { ImageMetadata } from 'astro';
import overview from '../assets/screenshots/overview.png';
import files from '../assets/screenshots/files.png';
import prototypes from '../assets/screenshots/prototypes.png';
import blocks from '../assets/screenshots/blocks.png';
import resources from '../assets/screenshots/resources.png';
import localization from '../assets/screenshots/localization.png';
import problems from '../assets/screenshots/problems.png';
import build from '../assets/screenshots/build.png';

export const screenshots: Record<string, ImageMetadata> = {
  overview,
  files,
  prototypes,
  blocks,
  resources,
  localization,
  problems,
  build,
};
