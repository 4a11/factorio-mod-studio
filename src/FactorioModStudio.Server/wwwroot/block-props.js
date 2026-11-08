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
  const toast = el('div', { class: 'files-toast', role: 'status', 'aria-live': 'polite', 'data-testid': 'blockprops-toast', hidden: true });
  document.body.append(toast);
  let toastTimer = 0;
  const hideToast = () => { clearTimeout(toastTimer); toast.hidden = true; toast.replaceChildren(); };
  function showToast(text, actions = [], bad = false) {
    clearTimeout(toastTimer);
    toast.classList.toggle('bad', bad);
    toast.replaceChildren(el('span', { text }), ...actions,
      el('button', { type: 'button', class: 'linklike', 'aria-label': t('files.toast.dismiss'), text: '×', onclick: hideToast }));
    toast.hidden = false;
    toastTimer = setTimeout(hideToast, 10000);
  }
  const failText = (error) => (error.body?.code === 'in-use' ? t('files.inUse') : error.message);

  // ------------------------------------------------------------ rename / remove / upgrade, each shown before it is done

  const post = (modId, operation, body) => api(`/api/mods/${enc(modId)}/blocks/${operation}`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  /** The files a planned operation writes and deletes, and what it cannot fix itself. */
  function planList(plan) {
    return el('div', { class: 'bp-plan', 'data-testid': 'blockops-plan' },
      el('ul', { class: 'bp-plan-files' }, plan.changes.map((c) => el('li', { class: 'mono small', 'data-delete': c.delete ? 'true' : 'false' },
        el('span', { class: 'bp-plan-kind', text: t(c.delete ? 'blockops.deletes' : 'blockops.writes') }), el('span', { text: c.path })))),
      plan.warnings.length > 0 && el('ul', { class: 'bp-plan-warnings' }, plan.warnings.map((w) => el('li', { class: 'small', text: w }))));
  }

  /**
   * A dialog for one operation: an optional form (the new name), the plan of the operation (asked from the server without
   * changing anything, again whenever the form changes) and a button that does it. Afterwards a notice offers Undo.
   */
  function operation(modId, block, kind, opened) {
    const nameInput = kind === 'rename' && el('input', { type: 'text', class: 'mono', value: opened.id, 'aria-label': t('blockops.newName'),
      'data-testid': 'blockops-name', spellcheck: 'false', autocomplete: 'off' });
    const migration = kind === 'rename' && el('input', { type: 'checkbox', checked: true, 'data-testid': 'blockops-migration' });
    const plan = el('div', { class: 'bp-plan-box' }, el('p', { class: 'muted small', text: t('gamedata.loadingItem') }));
    const error = el('p', { class: 'files-dialog-error', role: 'alert' });
    const go = el('button', { type: 'submit', class: kind === 'remove' ? 'primary danger' : 'primary', disabled: true, 'data-testid': 'blockops-ok',
      text: t(`blockops.${kind}Ok`) });
    const cancel = el('button', { type: 'button', text: t('files.dialog.cancel') });
    const body = () => ({ file: block.file, ...(kind === 'rename' ? { name: nameInput.value.trim(), migration: migration.checked } : {}) });

    let seq = 0;
    async function preview() {
      const mine = ++seq;
      go.disabled = true;
      error.textContent = '';
      try {
        const result = await post(modId, kind, { ...body(), dryRun: true });
        if (mine !== seq) return;
        plan.replaceChildren(planList(result));
        go.disabled = false;
      } catch (err) {
        if (mine !== seq) return;
        plan.replaceChildren();
        error.textContent = failText(err);
      }
    }

    const dialog = el('dialog', { class: 'files-dialog bp-dialog', 'data-testid': 'blockops', 'aria-label': t(`blockops.${kind}Title`, { name: block.title || block.file }) },
      el('form', {},
        el('h2', { text: t(`blockops.${kind}Title`, { name: block.title || block.file }) }),
        el('p', { class: 'muted small', text: t(`blockops.${kind}Explain`) }),
        nameInput && el('label', { class: 'bp-field' }, el('span', { text: t('blockops.newName') }), nameInput),
        migration && el('label', { class: 'bp-check' }, migration, el('span', { text: t('blockops.migration') })),
        plan, error,
        el('div', { class: 'nm-actions' }, cancel, go)));

    let timer = 0;
    nameInput?.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(preview, 250); });
    migration?.addEventListener('change', preview);
    cancel.addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => dialog.remove());
    dialog.querySelector('form').addEventListener('submit', async (e) => {
      e.preventDefault();
      go.disabled = true;
      try {
        const result = await post(modId, kind, body());
        dialog.close();
        const text = kind === 'rename'
          ? t('blockops.renamed', { from: opened.id, to: body().name })
          : t(kind === 'remove' ? 'blockops.removed' : 'blockops.upgraded', { name: block.title || block.file });
        showToast(text, [el('button', { type: 'button', 'data-testid': 'blockops-undo', text: t('files.undo'), onclick: () => undo(modId, result.undoId) })]);
        onSaved(block.file);
      } catch (err) {
        error.textContent = failText(err);
        go.disabled = false;
      }
    });
    document.body.append(dialog);
    dialog.showModal();
    (nameInput || cancel).focus();
    if (nameInput) nameInput.select();
    preview();
  }

  async function undo(modId, undoId) {
    hideToast();
    try {
      await api(`/api/mods/${enc(modId)}/blocks/undo/${enc(undoId)}`, { method: 'POST' });
      showToast(t('blockops.undone'));
    } catch (err) {
      showToast(t('blockops.undoFailed', { message: failText(err) }), [], true);
    }
  }

  async function open(modId, file) {
    let data;
    try { data = await api(`/api/mods/${enc(modId)}/blocks?file=${enc(file)}`); }
    catch (error) { alert(t('blockprops.loadFailed', { message: error.message })); return; }
    show(modId, data);
  }

  function show(modId, { block, gameData, editable, needsUpgrade }) {
    const opened = { id: block.file.split('/').pop().replace(/\.lua$/i, '') };
    // The other things to do with the block; unsaved numbers would be lost, so that is asked first.
    const act = (kind) => () => {
      if (changed().length && !confirm(t('blockops.discard'))) return;
      dialog.close();
      operation(modId, block, kind, opened);
    };
    const actions = editable && el('div', { class: 'bp-actions' },
      el('button', { type: 'button', class: 'linklike small', 'data-testid': 'blockops-rename', onclick: act('rename'), text: t('blockops.rename') }),
      needsUpgrade && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'blockops-upgrade', title: t('blockops.upgradeHint'), onclick: act('upgrade'), text: t('blockops.upgrade') }),
      el('button', { type: 'button', class: 'linklike small bp-danger', 'data-testid': 'blockops-remove', onclick: act('remove'), text: t('blockops.remove') }));
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
        actions,
        needsUpgrade && el('p', { class: 'bp-note small', text: t('blockops.oldBlock') }),
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
