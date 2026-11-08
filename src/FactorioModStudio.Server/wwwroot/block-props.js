// Properties of a building block, in a dialog: each number the block sets (health, speed, inventory, damage...) with the value
// of the original it copies and the value the game has, from the "In the game" data (game-data-view.js). Saving writes only the
// changed literals back into the block's Lua (POST /api/mods/{id}/blocks, which refuses a file that changed in between).

import { t, formatNumber } from '/i18n.js';

/** "attack_parameters.range" -> its translated label, or "Range" made from the last part of the path. */
export function fieldLabel(path) {
  const last = String(path).split('.').pop();
  const key = `blockprop.${last}`;
  const label = t(key);
  if (label !== key) return label;
  const words = last.replace(/_/g, ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** A value of the game data for display: numbers rounded to 6 significant digits, strings as they are. */
export function shownValue(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') return formatNumber(Number(value.toPrecision(6)), { maximumFractionDigits: 6 });
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

/** The text an input gets back when the property is reset: the original for a value, 1 for a factor; null when not known. */
export function resetText(property) {
  if (property.kind === 'multiplier' || property.kind === 'damage') return '1';
  const original = property.original;
  if (original === null || original === undefined || typeof original === 'object') return null;
  return typeof original === 'number' ? String(Number(original.toPrecision(12))) : String(original);
}

/**
 * @param {{ api: Function, el: Function, onSaved?: (file: string) => void }} host
 */
export function createBlockProps({ api, el, onSaved = () => {} }) {
  const enc = encodeURIComponent;

  async function open(modId, file) {
    let data;
    try { data = await api(`/api/mods/${enc(modId)}/blocks?file=${enc(file)}`); }
    catch (error) { alert(t('blockprops.loadFailed', { message: error.message })); return; }
    show(modId, data);
  }

  function show(modId, { block, gameData, editable }) {
    const inputs = new Map(); // key -> { input, property }
    const error = el('p', { class: 'files-dialog-error', role: 'alert', 'data-testid': 'blockprops-error' });
    const save = el('button', { type: 'submit', class: 'primary', disabled: true, 'data-testid': 'blockprops-save', text: t('blockprops.save') });
    const cancel = el('button', { type: 'button', 'data-testid': 'blockprops-cancel', text: t('files.dialog.cancel') });

    const changed = () => [...inputs.values()].filter(({ input, property }) => input.value.trim() !== property.value);
    const update = () => {
      for (const { input, property } of inputs.values()) input.closest('.bp-row')?.classList.toggle('changed', input.value.trim() !== property.value);
      save.disabled = !editable || changed().length === 0;
    };

    const row = (p) => {
      const factor = p.kind === 'multiplier' || p.kind === 'damage';
      const input = el('input', {
        type: 'text', inputmode: p.valueType === 'number' ? 'decimal' : 'text', class: 'mono bp-input', value: p.value,
        'aria-label': fieldLabel(p.path), disabled: !editable, 'data-testid': 'blockprops-input', oninput: update,
      });
      inputs.set(p.key, { input, property: p });
      const original = shownValue(p.original);
      const game = shownValue(p.game);
      const reset = resetText(p);

      // "original 0.04 · in game 0.06" (a factor), "original 450" (a value); nothing without game data.
      const known = [
        original !== null && t('blockprops.original', { value: original }),
        factor && game !== null && t('blockprops.inGame', { value: game }),
      ].filter(Boolean).join(' · ');

      return el('div', { class: 'bp-row', 'data-kind': p.kind },
        el('label', { class: 'bp-label' },
          el('span', { text: p.kind === 'damage' ? t('blockprops.damage') : fieldLabel(p.path) + (factor ? ' ×' : '') }),
          el('span', { class: 'muted small mono', text: p.kind === 'damage' ? t('blockprops.damageHint') : p.path }),
          p.comment && el('span', { class: 'muted small', text: p.comment }),
          p.alsoFor.length > 0 && el('span', { class: 'muted small', text: t('blockprops.alsoFor', { names: p.alsoFor.join(', ') }) })),
        el('span', { class: 'bp-value' },
          input,
          known && el('span', { class: 'muted small', 'data-testid': 'blockprops-original', text: known }),
          editable && reset !== null && el('button', {
            type: 'button', class: 'linklike small', 'data-testid': 'blockprops-reset', title: t('blockprops.resetTitle'),
            onclick: () => { input.value = reset; update(); input.focus(); }, text: t(factor ? 'blockprops.resetFactor' : 'blockprops.reset'),
          })));
    };

    // One group per prototype: the chest and its item, the belt and its underground...
    const groups = new Map();
    for (const p of block.properties) {
      const key = `${p.prototypeType}/${p.prototypeName}`;
      if (!groups.has(key)) groups.set(key, { type: p.prototypeType, name: p.prototypeName, copyOf: p.copyOf, items: [] });
      groups.get(key).items.push(p);
    }

    const body = block.properties.length
      ? [...groups.values()].map((g) => el('fieldset', { class: 'bp-group' },
        el('legend', {}, el('span', { class: 'mono', text: g.name }), el('span', { class: 'chip proto-type mono', text: g.type }),
          g.copyOf && el('span', { class: 'muted small', text: t('prototypes.copyOf', { name: g.copyOf }) })),
        g.items.map(row)))
      : [el('p', { class: 'muted', text: t('blockprops.none') })];

    const dialog = el('dialog', { class: 'files-dialog bp-dialog', 'data-testid': 'blockprops', 'aria-label': t('blockprops.title', { name: block.title || block.file }) },
      el('form', {},
        el('h2', { text: t('blockprops.title', { name: block.title || block.file }) }),
        el('p', { class: 'muted small mono', text: block.file }),
        !gameData && el('p', { class: 'bp-note small', text: t('blockprops.noGameData') }),
        !editable && el('p', { class: 'bp-note small', text: t('blockprops.readOnly') }),
        el('div', { class: 'bp-body' }, body),
        error,
        el('div', { class: 'nm-actions' }, cancel, editable && save)));

    dialog.querySelector('form').addEventListener('submit', async (e) => {
      e.preventDefault();
      const values = Object.fromEntries(changed().map(({ input, property }) => [property.key, input.value.trim()]));
      if (!Object.keys(values).length) { dialog.close(); return; }
      save.disabled = true;
      error.textContent = '';
      try {
        await api(`/api/mods/${enc(modId)}/blocks`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ file: block.file, version: block.version, values }) });
        dialog.close();
        onSaved(block.file);
      } catch (err) {
        error.textContent = err.status === 409 ? t('blockprops.conflict') : t('blockprops.saveFailed', { message: err.message });
        update();
      }
    });
    cancel.addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => dialog.remove());
    document.body.append(dialog);
    dialog.showModal();
    (dialog.querySelector('.bp-input') || cancel).focus();
  }

  return { open };
}
