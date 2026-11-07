// Building blocks: copies of vanilla prototypes (armor, a biter, a belt tier, a car...) added to a mod through
// POST /api/mods/{id}/content. The "Add to mod" dialog picks a block by category, takes a prototype name and a display name, and
// shows which files a dry run would create and change before anything is written. The New mod dialog reuses the block list.

import { t } from '/i18n.js';

export const CATEGORIES = ['combat', 'logistics', 'production', 'vehicles'];
const NAME = /^[A-Za-z0-9_-]{1,200}$/;
const PREVIEW_DELAY = 250;

let blocksPromise = null;

/** The block list from the server, fetched once. */
export function loadBlocks(api) {
  if (!blocksPromise) blocksPromise = api('/api/content/blocks').catch((error) => { blocksPromise = null; throw error; });
  return blocksPromise;
}

export const blockTitle = (block) => t(`content.block.${block.id}.title`);
export const blockDescription = (block) => t(`content.block.${block.id}.desc`);

/** True when the blocks can go into a mod for `factorioVersion` ("2.1", "2.1.3", ...). */
export function supportsVersion(block, factorioVersion) {
  const m = /^(\d+)\.(\d+)/.exec(String(factorioVersion || ''));
  return !!m && block.supportedVersions.includes(`${Number(m[1])}.${Number(m[2])}`);
}

const humanize = (name) => name.split(/[-_]+/).filter(Boolean).map((w) => w[0].toUpperCase() + w.slice(1)).join(' ');

/**
 * @param {{ el: Function, api: Function, onAdded: (mod: object, result: object) => void }} host
 */
export function createContentDialog({ el, api, onAdded }) {
  const s = { mod: null, blocks: [], category: CATEGORIES[0], block: null, nameTouched: false, preview: null, seq: 0, timer: 0, busy: false };

  const heading = el('h2', { id: 'cd-heading' });
  const hint = el('p', { class: 'small muted cd-hint' });
  const tabs = el('div', { class: 'cd-tabs', role: 'group' });
  const grid = el('div', { class: 'cd-grid', role: 'radiogroup', 'aria-labelledby': 'cd-heading' });
  const nameLabel = el('label', { for: 'cd-name' });
  const name = el('input', { id: 'cd-name', type: 'text', maxlength: '200', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', 'data-testid': 'content-name' });
  const titleLabel = el('label', { for: 'cd-title' });
  const title = el('input', { id: 'cd-title', type: 'text', maxlength: '100', autocomplete: 'off', 'data-testid': 'content-title' });
  const preview = el('div', { class: 'cd-preview small', role: 'status', 'aria-live': 'polite', 'data-testid': 'content-preview' });
  const cancel = el('button', { type: 'button', 'data-testid': 'content-cancel', onclick: () => dialog.close() });
  const add = el('button', { type: 'button', class: 'primary', 'data-testid': 'content-add', onclick: () => submit() });
  const dialog = el('dialog', { class: 'content-dialog', 'aria-labelledby': 'cd-heading', 'data-testid': 'content-dialog' },
    heading, hint, tabs, grid,
    el('div', { class: 'cd-fields' },
      el('div', { class: 'nm-field' }, nameLabel, name),
      el('div', { class: 'nm-field' }, titleLabel, title)),
    preview,
    el('div', { class: 'nm-actions' }, cancel, add));
  document.body.append(dialog);

  name.addEventListener('input', () => { s.nameTouched = true; schedulePreview(); });
  title.addEventListener('input', schedulePreview);
  dialog.addEventListener('close', () => { clearTimeout(s.timer); s.seq++; });

  const defaultName = (block) => `${s.mod.name}-${block.defaultName}`;
  const unsupported = () => !!s.mod && !!s.blocks.length && !supportsVersion(s.blocks[0], s.mod.factorioVersion);

  function render() {
    if (!s.mod) return;
    heading.textContent = t('content.heading', { mod: s.mod.title || s.mod.name });
    hint.textContent = unsupported()
      ? t('content.unsupported', { versions: s.blocks[0].supportedVersions.join(', '), version: s.mod.factorioVersion || '?' })
      : t('content.hint');
    hint.classList.toggle('bad', unsupported());
    nameLabel.textContent = t('content.name');
    titleLabel.textContent = t('content.title');
    cancel.textContent = t('dialog.cancel');

    tabs.replaceChildren(...CATEGORIES.map((category) => el('button', {
      type: 'button', class: 'cd-tab', 'aria-pressed': category === s.category ? 'true' : 'false', 'data-category': category,
      text: t(`content.category.${category}`),
      onclick: () => { s.category = category; render(); },
    })));
    grid.replaceChildren(...s.blocks.filter((b) => b.category === s.category).map((block) => el('button', {
      type: 'button', role: 'radio', class: 'cd-card', 'aria-checked': block === s.block ? 'true' : 'false', 'data-block': block.id,
      disabled: unsupported(), onclick: () => choose(block),
    }, el('strong', { text: blockTitle(block) }), el('span', { class: 'small muted', text: blockDescription(block) }))));

    title.placeholder = NAME.test(name.value.trim()) ? humanize(name.value.trim()) : '';
    name.disabled = title.disabled = unsupported();
    renderPreview();
  }

  function choose(block) {
    s.block = block;
    if (!s.nameTouched || !name.value.trim()) { name.value = defaultName(block); s.nameTouched = false; }
    render();
    grid.querySelector(`[data-block="${block.id}"]`)?.focus(); // the cards were rebuilt
    schedulePreview();
  }

  function renderPreview() {
    const p = s.preview;
    add.textContent = t('content.add');
    add.disabled = s.busy || !p || !p.ok;
    preview.classList.toggle('bad', !!p && !p.ok);
    if (!s.block) { preview.textContent = unsupported() ? '' : t('content.pick'); return; }
    if (!p) { preview.textContent = t('content.checking'); return; }
    if (!p.ok) { preview.textContent = p.error; return; }
    preview.replaceChildren(
      el('div', { text: t('content.creates', { files: p.result.create.join(', ') }) }),
      ...(p.result.change.length ? [el('div', { class: 'muted', text: t('content.changes', { files: p.result.change.join(', ') }) })] : []));
  }

  function request(dryRun) {
    const body = { block: s.block.id, name: name.value.trim(), title: title.value.trim() || null, dryRun };
    return api(`/api/mods/${encodeURIComponent(s.mod.id)}/content`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  }

  const errorOf = (error) => error?.body?.error || error?.message || t('content.failed');

  function schedulePreview() {
    clearTimeout(s.timer);
    s.preview = null;
    title.placeholder = NAME.test(name.value.trim()) ? humanize(name.value.trim()) : '';
    if (!s.block) { renderPreview(); return; }
    if (!NAME.test(name.value.trim())) { s.preview = { ok: false, error: t('content.badName') }; renderPreview(); return; }
    renderPreview();
    const seq = ++s.seq;
    s.timer = setTimeout(async () => {
      try {
        const result = await request(true);
        if (seq === s.seq) s.preview = { ok: true, result };
      } catch (error) {
        if (seq === s.seq) s.preview = { ok: false, error: errorOf(error) };
      }
      if (seq === s.seq) renderPreview();
    }, PREVIEW_DELAY);
  }

  async function submit() {
    if (s.busy || !s.preview?.ok) return;
    s.busy = true;
    renderPreview();
    try {
      const result = await request(false);
      dialog.close();
      onAdded(s.mod, result);
    } catch (error) {
      s.preview = { ok: false, error: errorOf(error) };
    } finally {
      s.busy = false;
      renderPreview();
    }
  }

  return {
    /** `mod`: { id, name, title, factorioVersion } of the open, editable mod. */
    async open(mod) {
      s.mod = mod;
      s.block = null;
      s.preview = null;
      s.nameTouched = false;
      name.value = '';
      title.value = '';
      s.blocks = [];
      render();
      dialog.showModal();
      try {
        s.blocks = await loadBlocks(api);
      } catch (error) {
        preview.textContent = errorOf(error);
        return;
      }
      if (!s.blocks.some((b) => b.category === s.category)) s.category = s.blocks[0]?.category || CATEGORIES[0];
      render();
      grid.querySelector('.cd-card:not(:disabled)')?.focus();
    },
    retranslate: render,
  };
}
