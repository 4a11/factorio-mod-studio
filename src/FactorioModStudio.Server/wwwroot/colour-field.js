// The colour of a building block: a hue slider with a swatch and the block's icons recoloured as they would be written
// (GET /api/content/blocks/{block}/icon?suffix=&color=). The recolouring keeps each pixel's brightness and shading and takes
// only the hue, so one slider covers every colour a block can get; the colour written into the Lua is that hue at the
// saturation the blocks come in. Used by the "Add to mod" dialog and the Blocks tab editor.

import { t } from '/i18n.js';
import { hueOf, colourOfHue, sameHue, iconUrl } from '/colour-logic.js';

const PREVIEW_DELAY = 60;

/**
 * @param {{ el: Function, id: string, testid?: string, onChange?: (colour: string) => void }} options
 * @returns {{ element: HTMLElement, set: (state: { block: string|null, icons: string[], colour: string, original?: string, previews?: boolean, disabled?: boolean }) => void, value: () => string }}
 */
export function createColourField({ el, id, testid = 'colour', onChange }) {
  let state = { block: null, icons: [], colour: '#ffffff', original: null, previews: true, disabled: false };
  let timer = 0;

  const label = el('label', { for: id, class: 'cf-label' });
  const slider = el('input', { id, type: 'range', min: '0', max: '359', step: '1', class: 'cf-slider', 'data-testid': `${testid}-slider` });
  const swatch = el('span', { class: 'cf-swatch', 'aria-hidden': 'true' });
  const value = el('span', { class: 'mono small muted cf-value', 'data-testid': `${testid}-value` });
  const reset = el('button', { type: 'button', class: 'linklike small cf-reset', 'data-testid': `${testid}-reset` });
  const icons = el('span', { class: 'cf-icons', 'data-testid': `${testid}-icons` });
  const note = el('span', { class: 'small muted cf-note' });
  const element = el('div', { class: 'cf', 'data-testid': testid },
    label,
    el('div', { class: 'cf-row' }, swatch, slider, value),
    el('div', { class: 'cf-row cf-preview' }, icons, note, reset));

  function paint() {
    const { colour } = state;
    label.textContent = t('colour.label');
    slider.value = String(hueOf(colour));
    slider.disabled = !!state.disabled;
    slider.setAttribute('aria-valuetext', colour);
    swatch.style.setProperty('background', colour);
    value.textContent = colour;
    reset.textContent = t('colour.reset');
    reset.hidden = !state.original || sameHue(state.original, colour) || !!state.disabled;
    note.textContent = state.block && !state.previews ? t('colour.noGame') : '';
    schedulePreviews();
  }

  // The previews follow the slider with a short delay; the old picture stays until the new one is there.
  function schedulePreviews() {
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!state.block || !state.previews || !state.icons.length) { icons.replaceChildren(); return; }
      const imgs = [...icons.querySelectorAll('img')];
      if (imgs.length !== state.icons.length) {
        icons.replaceChildren(...state.icons.map(() => el('img', { class: 'cf-icon', alt: '', width: '40', height: '40' })));
      }
      [...icons.querySelectorAll('img')].forEach((img, i) => {
        const url = iconUrl(state.block, state.icons[i], state.colour);
        if (img.dataset.url === url) return;
        const next = new Image();
        next.onload = () => { img.src = url; img.dataset.url = url; img.hidden = false; };
        next.onerror = () => {
          img.hidden = true;
          // No game to take the pictures from: the colour still goes into the Lua and the game tints the icons.
          if ([...icons.querySelectorAll('img')].every((x) => x.hidden)) note.textContent = t('colour.noGame');
        };
        next.src = url;
      });
    }, PREVIEW_DELAY);
  }

  slider.addEventListener('input', () => {
    state = { ...state, colour: colourOfHue(slider.value) };
    paint();
    onChange?.(state.colour);
  });
  reset.addEventListener('click', () => {
    state = { ...state, colour: state.original };
    paint();
    onChange?.(state.colour);
    slider.focus();
  });

  return {
    element,
    set(next) { state = { ...state, ...next }; paint(); },
    value: () => state.colour,
  };
}
