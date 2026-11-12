// The Blocks tab: the building blocks of a mod in a list, the chosen one in an editor next to it. The editor shows each number the
// block sets as "original / yours / in the game" (GET /api/mods/{id}/blocks), saves changed numbers into the block's Lua (POST),
// renames the block in its header, changes its colour with a slider (icons previewed, POST /blocks/recolor) and its icons with
// pictures of your own (Replace... or a dropped PNG, POST /blocks/icon), and removes or
// upgrades it from its "⋯" menu (POST /blocks/{operation}, each with Undo).
// The originals come from the game: when the mod has no game data yet, a dump job (the Prototypes tab's "In the game") starts by
// itself the first time a block is opened, and the editor fills in when it is done.

import { t, formatNumber } from '/i18n.js';
import { fieldLabel, shownValue, resetText } from '/block-props.js';
import { createColourField } from '/colour-field.js';
import { sameHue, iconUrl } from '/colour-logic.js';

const POLL_MS = 1000;
const TOAST_MS = 10000;

/**
 * @param {{ api: Function, el: Function, openFile: (path: string, line?: number) => void, fileUrl: (path: string) => string,
 *   installation: (modId: string) => string | null }} host
 */
export function createBlocksView({ api, el, openFile, fileUrl, installation }) {
  const enc = encodeURIComponent;
  const root = el('div', { class: 'bv', 'data-testid': 'blocks' });
  /** Per mod: { selected, filter, details: Map(file -> data), edits: Map(key -> text), rename, dump: { id, line } | null, dumpTried, dumpError, overview } */
  const mods = new Map();
  let current = null; // { modId, editable, overview }
  let pollTimer = 0;

  const stateOf = (modId) => {
    if (!mods.has(modId)) mods.set(modId, { selected: null, filter: '', details: new Map(), loading: new Set(), edits: new Map(), rename: null, dump: null, dumpTried: false, dumpError: null, overview: null });
    return mods.get(modId);
  };

  // ------------------------------------------------------------ small shared pieces: toast, confirm, menu

  const toast = el('div', { class: 'files-toast', role: 'status', 'aria-live': 'polite', 'data-testid': 'blocks-toast', hidden: true });
  const menu = el('div', { class: 'files-menu', role: 'menu', 'data-testid': 'blocks-menu', hidden: true });
  document.body.append(toast, menu);
  let toastTimer = 0;
  const hideToast = () => { clearTimeout(toastTimer); toast.hidden = true; toast.replaceChildren(); };
  function showToast(text, actions = [], bad = false) {
    clearTimeout(toastTimer);
    toast.classList.toggle('bad', bad);
    toast.replaceChildren(el('span', { text }), ...actions,
      el('button', { type: 'button', class: 'linklike', 'aria-label': t('files.toast.dismiss'), text: '×', onclick: hideToast }));
    toast.hidden = false;
    toastTimer = setTimeout(hideToast, TOAST_MS);
  }
  const failText = (error) => (error?.body?.code === 'in-use' ? t('files.inUse') : error?.message ?? String(error));

  function confirmDialog({ title, body, okKey, danger = false }) {
    return new Promise((resolve) => {
      const ok = el('button', { type: 'submit', class: danger ? 'primary danger' : 'primary', 'data-testid': 'blocks-dialog-ok', text: t(okKey) });
      const cancel = el('button', { type: 'button', text: t('files.dialog.cancel') });
      const dialog = el('dialog', { class: 'files-dialog bp-dialog', 'data-testid': 'blocks-dialog', 'aria-label': title },
        el('form', {}, el('h2', { text: title }), ...body, el('div', { class: 'nm-actions' }, cancel, ok)));
      let result = false;
      dialog.querySelector('form').addEventListener('submit', (e) => { e.preventDefault(); result = true; dialog.close(); });
      cancel.addEventListener('click', () => dialog.close());
      dialog.addEventListener('close', () => { dialog.remove(); resolve(result); });
      document.body.append(dialog);
      dialog.showModal();
      cancel.focus();
    });
  }

  let menuReturn = null;
  const closeMenu = (focusBack = true) => { if (menu.hidden) return; menu.hidden = true; menu.replaceChildren(); if (focusBack) menuReturn?.focus(); menuReturn = null; };
  function openMenu(anchor, items) {
    menu.replaceChildren(...items.filter(Boolean).map(([key, run, danger, testid]) => el('button', {
      type: 'button', role: 'menuitem', tabindex: '-1', class: danger ? 'danger' : null, 'data-testid': `blocks-menu-${testid}`,
      text: t(key), onclick: () => { closeMenu(false); run(); } })));
    menuReturn = anchor;
    menu.hidden = false;
    const r = anchor.getBoundingClientRect();
    const box = menu.getBoundingClientRect();
    menu.style.setProperty('left', `${Math.round(Math.max(4, Math.min(r.right - box.width, innerWidth - box.width - 4)))}px`);
    menu.style.setProperty('top', `${Math.round(Math.min(r.bottom + 4, innerHeight - box.height - 4))}px`);
    menu.querySelector('[role="menuitem"]')?.focus();
  }
  menu.addEventListener('keydown', (e) => {
    const items = [...menu.querySelectorAll('[role="menuitem"]')];
    const i = items.indexOf(document.activeElement);
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); closeMenu(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus(); }
  });
  document.addEventListener('pointerdown', (e) => { if (!menu.hidden && !menu.contains(e.target)) closeMenu(false); });

  // ------------------------------------------------------------ colour: one field kept across renders, so a drag is not cut off

  const colourField = createColourField({ el, id: 'bv-colour', testid: 'blocks-colour', onChange: (colour) => {
    const s = stateOf(current.modId);
    const data = s.details.get(s.selected);
    s.colourEdit = data?.color && !sameHue(colour, data.color) ? colour : null;
    paintColourBar(s);
    previewTiles(data, s.colourEdit);
  } });
  const colourApply = el('button', { type: 'button', class: 'primary', 'data-testid': 'blocks-colour-apply', onclick: () => applyColour() });
  const colourCancel = el('button', { type: 'button', 'data-testid': 'blocks-colour-cancel', onclick: () => {
    const s = stateOf(current.modId);
    s.colourEdit = null;
    colourBound = null;
    render();
  } });
  const colourBar = el('span', { class: 'bv-colour-bar' }, colourCancel, colourApply);
  let colourBound = null; // "<file>|<colour>" the field was last set for

  function paintColourBar(s) {
    colourApply.textContent = t('colour.apply');
    colourCancel.textContent = t('files.dialog.cancel');
    colourBar.hidden = !s.colourEdit;
  }

  function colourSection(s, summary, data) {
    if (!data.color || !data.kind) return null;
    const key = `${summary.file}|${data.color}`;
    const editable = current.editable && data.editable && !data.needsUpgrade;
    if (colourBound !== key) {
      colourBound = key;
      colourField.set({ block: data.kind, icons: [], colour: s.colourEdit ?? data.color, original: data.color, previews: true, disabled: !editable });
    } else colourField.set({ disabled: !editable });
    paintColourBar(s);
    return el('div', { class: 'bv-colour' }, colourField.element,
      data.needsUpgrade && current.editable && el('span', { class: 'small muted', text: t('colour.oldBlock') }),
      colourBar,
      iconTiles(s, summary, data, editable));
  }

  // ------------------------------------------------------------ icons: one tile per prototype, Replace... or a dropped PNG

  const MAX_ICON_BYTES = 4 * 1024 * 1024;
  const iconPicker = el('input', { type: 'file', accept: 'image/png,.png', hidden: true, 'data-testid': 'blocks-icon-file' });
  document.body.append(iconPicker);
  let pickFor = null; // the suffix Replace... was pressed for

  iconPicker.addEventListener('change', () => {
    const picked = iconPicker.files?.[0];
    iconPicker.value = '';
    if (picked && pickFor !== null) replaceIcon(pickFor, picked);
  });

  function previewTiles(data, colour) {
    if (!data) return;
    for (const img of root.querySelectorAll('.bv-icon-tile img[data-suffix]')) {
      const f = (data.iconFiles || []).find((x) => x.suffix === img.dataset.suffix);
      // A picture of your own keeps its colours, so only the recoloured originals follow the slider.
      if (!f || (f.path && f.custom)) continue;
      const src = colour ? (data.icons ? iconUrl(data.kind, f.suffix, colour) : null) : (f.path ? iconSrc(f.path) : (data.icons ? iconUrl(data.kind, f.suffix, data.color) : null));
      if (src) img.src = src;
    }
  }

  function iconTiles(s, summary, data, editable) {
    const files = data.iconFiles || [];
    if (!files.length) return null;
    return el('div', { class: 'bv-icons', 'data-testid': 'blocks-icons' },
      el('span', { class: 'cf-label', text: t('icons.label') }),
      el('div', { class: 'bv-icon-tiles' }, files.map((f) => {
        const src = f.path ? iconSrc(f.path) : (data.icons ? iconUrl(data.kind, f.suffix, data.color) : null);
        const tile = el('div', { class: 'bv-icon-tile', 'data-testid': 'blocks-icon-tile', 'data-suffix': f.suffix, title: editable ? t('icons.dropHint') : null },
          src ? el('img', { alt: '', src, 'data-suffix': f.suffix }) : el('span', { class: 'bv-icon-none', 'aria-hidden': 'true', text: '⚙' }),
          el('span', { class: 'bv-icon-text' },
            el('span', { class: 'mono small', text: f.prototype }),
            el('span', { class: 'muted small', text: f.custom ? t('icons.own') : f.path ? t('icons.recoloured') : t('icons.tinted') })),
          editable && el('span', { class: 'bv-icon-actions' },
            el('button', { type: 'button', class: 'files-btn', 'data-testid': 'blocks-icon-replace', onclick: () => { pickFor = f.suffix; iconPicker.click(); } },
              el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: '⇪' }), el('span', { text: t('icons.replace') })),
            f.custom && data.icons && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'blocks-icon-restore', title: t('icons.restoreHint'),
              onclick: () => restoreIcon(f.suffix), text: t('icons.restore') })));
        if (editable) {
          tile.addEventListener('dragover', (e) => { if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); tile.classList.add('drop'); } });
          tile.addEventListener('dragleave', () => tile.classList.remove('drop'));
          tile.addEventListener('drop', (e) => {
            e.preventDefault();
            tile.classList.remove('drop');
            const dropped = e.dataTransfer?.files?.[0];
            if (dropped) replaceIcon(f.suffix, dropped);
          });
        }
        return tile;
      })),
      editable && el('p', { class: 'muted small bv-icon-tip', text: t('icons.tip') }));
  }

  const asBase64 = (blob) => new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });

  async function replaceIcon(suffix, picked) {
    if (!/\.png$/i.test(picked.name) && picked.type !== 'image/png') { showToast(t('icons.notPng'), [], true); return; }
    if (picked.size > MAX_ICON_BYTES) { showToast(t('icons.tooLarge'), [], true); return; }
    let png;
    try { png = await asBase64(picked); } catch (error) { showToast(failText(error), [], true); return; }
    await iconOperation(suffix, png, 'icons.replaced');
  }

  const restoreIcon = (suffix) => iconOperation(suffix, null, 'icons.restored');

  async function iconOperation(suffix, png, doneKey) {
    const { modId } = current;
    const s = stateOf(modId);
    const summary = blocksOf(current.overview).find((b) => b.file === s.selected);
    if (!summary) return;
    try {
      const done = await post(modId, 'icon', { file: summary.file, suffix, png });
      s.iconStamp = Date.now();
      colourBound = null;
      const warnings = done.warnings || [];
      showToast([t(doneKey, { name: idOf(summary) + suffix }), ...warnings].join(' '), [undoButton(modId, [done.undoId])]);
      await loadDetail(modId, summary.file);
    } catch (error) { showToast(failText(error), [], true); }
  }

  async function applyColour() {
    const { modId } = current;
    const s = stateOf(modId);
    const summary = blocksOf(current.overview).find((b) => b.file === s.selected);
    if (!summary || !s.colourEdit) return;
    try {
      const done = await post(modId, 'recolor', { file: summary.file, color: s.colourEdit });
      s.colourEdit = null;
      colourBound = null;
      s.iconStamp = Date.now();
      showToast([t('colour.applied', { name: summary.title || idOf(summary) }), ...(done.warnings || [])].join(' '),
        [undoButton(modId, [done.undoId])], (done.warnings || []).length > 0);
      await loadDetail(modId, summary.file);
    } catch (error) { showToast(failText(error), [], true); }
  }

  // ------------------------------------------------------------ data

  const blocksOf = (o) => [...(o.blocks || [])].sort((a, b) => (a.title || a.id || a.file).localeCompare(b.title || b.id || b.file));
  const idOf = (b) => b.id || b.file.split('/').pop().replace(/\.lua$/i, '');
  const iconOf = (o, b) => o.prototypes.find((p) => p.name === idOf(b) && p.icon)?.icon;
  // After a colour change the icon files keep their names: a stamp makes the browser read them again.
  const iconSrc = (path) => { const stamp = current && stateOf(current.modId).iconStamp; return stamp ? `${fileUrl(path)}&v=${stamp}` : fileUrl(path); };
  const kindTitle = (b) => {
    if (!b.kind) return null;
    const key = `content.block.${b.kind}.title`;
    const text = t(key);
    return text === key ? b.kind : text;
  };

  async function loadDetail(modId, file) {
    const s = stateOf(modId);
    if (s.loading.has(file)) return;
    s.loading.add(file);
    try { s.details.set(file, await api(`/api/mods/${enc(modId)}/blocks?file=${enc(file)}`)); }
    catch (error) { s.details.set(file, { error: failText(error) }); }
    finally { s.loading.delete(file); }
    if (current?.modId === modId) render();
    maybeDump(modId, file);
  }

  // The originals come from the game: the first block opened without game data starts a dump (once per mod and session).
  // `force`: load again although there is game data (it is from before the last change).
  async function maybeDump(modId, file, force = false) {
    const s = stateOf(modId);
    const data = s.details.get(file);
    if (!current?.editable || !data?.block || (data.gameData && !force) || s.dump || (s.dumpTried && !force)) return;
    s.dumpTried = true;
    try {
      const jobs = await api(`/api/jobs?modId=${enc(modId)}`);
      const running = (jobs || []).find((j) => j.kind === 'dump' && (j.state === 'running' || j.state === 'queued'));
      if (running) s.dump = { id: running.id, lastN: 0, line: '' };
      else {
        const body = { kind: 'dump' };
        const chosen = installation(modId);
        if (chosen) body.installation = chosen;
        const job = await api(`/api/mods/${enc(modId)}/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
        s.dump = { id: job.id, lastN: 0, line: '' };
      }
      s.dumpError = null;
      schedulePoll();
    } catch (error) {
      s.dumpError = error.status === 409 ? t('gamedata.busy') : failText(error);
    }
    if (current?.modId === modId) render();
  }

  function schedulePoll() { clearTimeout(pollTimer); pollTimer = setTimeout(poll, POLL_MS); }

  async function poll() {
    let pending = false;
    for (const [modId, s] of mods) {
      if (!s.dump) continue;
      try {
        const snap = await api(`/api/jobs/${enc(s.dump.id)}?after=${s.dump.lastN}`);
        for (const line of snap.lines || []) { s.dump.lastN = line.n; if (line.text.trim()) s.dump.line = line.text; }
        if (snap.state === 'running' || snap.state === 'queued') { pending = true; continue; }
        s.dump = null;
        if (snap.state === 'failed') s.dumpError = t('gamedata.failed', { error: snap.error || '' });
        s.details.clear(); // every block gets its originals now
        if (current?.modId === modId && s.selected) loadDetail(modId, s.selected);
      } catch {
        s.dump = null;
      }
    }
    if (current) render();
    if (pending) schedulePoll();
  }

  function refreshDump() {
    const s = stateOf(current.modId);
    s.dumpError = null;
    maybeDump(current.modId, s.selected, true);
  }

  async function retryDump() {
    const s = stateOf(current.modId);
    s.dumpTried = false;
    s.dumpError = null;
    await maybeDump(current.modId, s.selected);
  }

  // ------------------------------------------------------------ actions

  function select(file) {
    const s = stateOf(current.modId);
    if (file === s.selected) return;
    if (s.edits.size && !confirm(t('blockops.discard'))) return;
    s.selected = file;
    s.edits.clear();
    s.rename = null;
    s.colourEdit = null;
    render();
    if (!s.details.has(file)) loadDetail(current.modId, file);
  }

  async function save() {
    const { modId } = current;
    const s = stateOf(modId);
    const data = s.details.get(s.selected);
    if (!data?.block || !s.edits.size) return;
    try {
      await api(`/api/mods/${enc(modId)}/blocks`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ file: data.block.file, version: data.block.version, values: Object.fromEntries(s.edits) }) });
      const n = s.edits.size;
      s.edits.clear();
      showToast(t('blocks.saved', { n: formatNumber(n) }));
      await loadDetail(modId, s.selected);
    } catch (error) {
      showToast(error.status === 409 ? t('blockprops.conflict') : t('blockprops.saveFailed', { message: failText(error) }), [], true);
      if (error.status === 409) { s.edits.clear(); await loadDetail(modId, s.selected); }
    }
  }

  const post = (modId, operation, body) => api(`/api/mods/${enc(modId)}/blocks/${operation}`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  function undoButton(modId, undoIds) {
    return el('button', { type: 'button', 'data-testid': 'blocks-undo', text: t('files.undo'), onclick: async () => {
      hideToast();
      try {
        for (const id of [...undoIds].reverse()) await api(`/api/mods/${enc(modId)}/blocks/undo/${enc(id)}`, { method: 'POST' });
        stateOf(modId).details.clear();
        stateOf(modId).iconStamp = Date.now(); // the icon files may be back to what they were
        colourBound = null;
        showToast(t('blockops.undone'));
      } catch (error) { showToast(t('blockops.undoFailed', { message: failText(error) }), [], true); }
    } });
  }

  /** The files an operation writes and deletes, and what it cannot fix by itself. */
  function planList(plan) {
    return el('div', { class: 'bp-plan', 'data-testid': 'blockops-plan' },
      el('ul', { class: 'bp-plan-files' }, plan.changes.map((c) => el('li', { class: 'mono small', 'data-delete': c.delete ? 'true' : 'false' },
        el('span', { class: 'bp-plan-kind', text: t(c.delete ? 'blockops.deletes' : 'blockops.writes') }), el('span', { text: c.path })))),
      plan.warnings.length > 0 && el('ul', { class: 'bp-plan-warnings' }, plan.warnings.map((w) => el('li', { class: 'small', text: w }))));
  }

  async function removeBlock(block) {
    const { modId } = current;
    let plan;
    try { plan = await post(modId, 'remove', { file: block.file, dryRun: true }); }
    catch (error) { showToast(failText(error), [], true); return; }
    const ok = await confirmDialog({ title: t('blockops.removeTitle', { name: block.title || idOf(block) }), okKey: 'blockops.removeOk', danger: true,
      body: [el('p', { class: 'muted small', text: t('blockops.removeExplain') }), planList(plan)] });
    if (!ok) return;
    try {
      const done = await post(modId, 'remove', { file: block.file });
      const s = stateOf(modId);
      s.selected = null; s.edits.clear(); s.details.delete(block.file);
      showToast(t('blockops.removed', { name: block.title || idOf(block) }), [undoButton(modId, [done.undoId])]);
    } catch (error) { showToast(failText(error), [], true); }
  }

  async function upgrade(blocks) {
    const { modId } = current;
    const undoIds = [];
    const warnings = new Set();
    for (const block of blocks) {
      try {
        const done = await post(modId, 'upgrade', { file: block.file });
        undoIds.push(done.undoId);
        for (const w of done.warnings || []) warnings.add(w);
      } catch (error) { showToast(failText(error), undoIds.length ? [undoButton(modId, undoIds)] : [], true); return; }
    }
    stateOf(modId).details.clear();
    const text = blocks.length === 1 ? t('blockops.upgraded', { name: blocks[0].title || idOf(blocks[0]) }) : t('blocks.upgradedAll', { n: formatNumber(blocks.length) });
    showToast([text, ...warnings].join(' '), [undoButton(modId, undoIds)], warnings.size > 0);
  }

  // Rename in the header: the new name is checked as it is typed (a plan from the server), Enter renames, Escape cancels.
  function startRename(block) {
    const s = stateOf(current.modId);
    s.rename = { value: idOf(block), migration: true, plan: null, error: null, seq: 0 };
    render();
    const input = root.querySelector('[data-testid=blocks-rename-input]');
    input?.focus();
    input?.select();
  }

  async function previewRename(block) {
    const { modId } = current;
    const s = stateOf(modId);
    const r = s.rename;
    if (!r) return;
    const mine = ++r.seq;
    if (r.value.trim() === idOf(block)) { r.plan = null; r.error = null; render(); return; }
    try {
      const plan = await post(modId, 'rename', { file: block.file, name: r.value.trim(), migration: r.migration, dryRun: true });
      if (s.rename !== r || mine !== r.seq) return;
      r.plan = plan; r.error = null;
    } catch (error) {
      if (s.rename !== r || mine !== r.seq) return;
      r.plan = null; r.error = failText(error);
    }
    renderKeepingFocus();
  }

  async function applyRename(block) {
    const { modId } = current;
    const s = stateOf(modId);
    const r = s.rename;
    if (!r?.plan) return;
    try {
      const done = await post(modId, 'rename', { file: block.file, name: r.value.trim(), migration: r.migration });
      const newFile = done.changes.find((c) => !c.delete && /^prototypes\/[^/]+\.lua$/.test(c.path) && c.path !== block.file && !/fms-copy/.test(c.path))?.path;
      s.rename = null; s.edits.clear(); s.details.delete(block.file);
      if (newFile) s.selected = newFile;
      showToast(t('blockops.renamed', { from: idOf(block), to: r.value.trim() }), [undoButton(modId, [done.undoId])]);
      render();
    } catch (error) {
      r.error = failText(error);
      renderKeepingFocus();
    }
  }

  // ------------------------------------------------------------ rendering

  function renderKeepingFocus() {
    const active = document.activeElement;
    const testid = active?.getAttribute?.('data-testid');
    const at = active?.selectionStart;
    render();
    if (testid) {
      const again = root.querySelector(`[data-testid="${testid}"]`);
      again?.focus();
      if (again && typeof at === 'number') again.setSelectionRange?.(at, at);
    }
  }

  function listPane(o, s) {
    const blocks = blocksOf(o);
    const query = s.filter.trim().toLowerCase();
    const shown = blocks.filter((b) => !query || [b.title, idOf(b), kindTitle(b), b.copyOf].some((x) => (x || '').toLowerCase().includes(query)));
    const old = blocks.filter((b) => b.old);
    return el('aside', { class: 'bv-list', 'aria-label': t('blocks.listAria') },
      el('input', { type: 'search', class: 'bv-filter', placeholder: t('blocks.filter'), 'aria-label': t('blocks.filter'), value: s.filter, 'data-testid': 'blocks-filter',
        oninput: (e) => { s.filter = e.target.value; renderKeepingFocus(); } }),
      el('div', { class: 'bv-items', role: 'listbox', 'aria-label': t('blocks.listAria') },
        shown.map((b) => {
          const icon = iconOf(o, b);
          return el('button', {
            type: 'button', class: 'bv-item', role: 'option', 'aria-selected': b.file === s.selected ? 'true' : 'false', 'data-testid': 'blocks-item', title: b.file,
            onclick: () => select(b.file),
          },
          icon ? el('img', { class: 'bv-icon', alt: '', loading: 'lazy', src: iconSrc(icon) }) : el('span', { class: 'bv-icon bv-gear', 'aria-hidden': 'true', text: '⚙' }),
          el('span', { class: 'bv-names' },
            el('span', { class: 'bv-title', text: b.title || idOf(b) }),
            el('span', { class: 'muted small mono', text: idOf(b) })),
          kindTitle(b) && el('span', { class: 'chip bv-kind', text: kindTitle(b) }));
        }),
        !shown.length && el('p', { class: 'muted small', text: t('prototypes.noMatch') })),
      old.length > 0 && current.editable && el('div', { class: 'bv-old', 'data-testid': 'blocks-old', title: t('blockops.oldBlock') },
        el('span', { class: 'small', text: t('blocks.oldCount', { n: formatNumber(old.length) }) }),
        el('button', { type: 'button', 'data-testid': 'blocks-upgrade-all', title: t('blockops.upgradeHint'), onclick: () => upgrade(old), text: t('blocks.upgradeAll') })));
  }

  function originalsStatus(s, data) {
    if (s.dump) return el('div', { class: 'bv-status', role: 'status', 'data-testid': 'blocks-dump' },
      el('span', { class: 'spinner', 'aria-hidden': 'true' }), el('span', { text: t('blocks.loadingOriginals') }),
      s.dump.line && el('span', { class: 'muted small mono gd-line', text: s.dump.line }));
    if (s.dumpError) return el('div', { class: 'gd-error', role: 'alert' }, el('span', { text: s.dumpError }), ' ',
      el('button', { type: 'button', class: 'linklike small', onclick: retryDump, text: t('blocks.retry') }));
    if (data.gameData && data.gameDataStale) return el('p', { class: 'muted small bv-stale-note', role: 'status', 'data-testid': 'blocks-stale' },
      el('span', { text: t('gamedata.stale') }), ' ',
      current?.editable && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'blocks-reload', onclick: refreshDump, text: t('gamedata.refresh') }));
    if (data.gameData) return null;
    return el('p', { class: 'muted small', text: t('blockprops.noGameData') });
  }

  function header(o, s, summary, data) {
    const block = data.block;
    const icon = iconOf(o, summary);
    const r = s.rename;
    const more = current.editable && el('button', { type: 'button', class: 'bv-more', 'aria-haspopup': 'menu', 'data-testid': 'blocks-more', title: t('sidebar.menu.more'), 'aria-label': t('sidebar.menu.more'),
      onclick: (e) => openMenu(e.currentTarget, [
        ['blocks.openCode', () => openFile(block.file), false, 'code'],
        summary.old && ['blockops.upgrade', () => upgrade([summary]), false, 'upgrade'],
        ['blockops.remove', () => removeBlock(summary), true, 'remove'],
      ]) }, '⋯');

    const nameLine = r
      ? el('span', { class: 'bv-rename' },
        el('input', { type: 'text', class: 'mono', value: r.value, spellcheck: 'false', autocomplete: 'off', 'aria-label': t('blockops.newName'), 'data-testid': 'blocks-rename-input',
          oninput: (e) => { r.value = e.target.value; clearTimeout(r.timer); r.timer = setTimeout(() => previewRename(summary), 250); },
          onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); applyRename(summary); } else if (e.key === 'Escape') { s.rename = null; render(); } } }),
        el('button', { type: 'button', class: 'primary', disabled: !r.plan, 'data-testid': 'blocks-rename-ok', onclick: () => applyRename(summary), text: t('blockops.renameOk') }),
        el('button', { type: 'button', onclick: () => { s.rename = null; render(); }, text: t('files.dialog.cancel') }))
      : el('span', { class: 'bv-id' },
        el('span', { class: 'mono', text: idOf(summary) }),
        current.editable && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'blocks-rename', title: t('blocks.renameHint'), onclick: () => startRename(summary), text: t('blocks.rename') }));

    return el('div', { class: 'bv-head' },
      icon ? el('img', { class: 'bv-head-icon', alt: '', src: iconSrc(icon) }) : el('span', { class: 'bv-head-icon bv-gear', 'aria-hidden': 'true', text: '⚙' }),
      el('div', { class: 'bv-head-text' },
        el('h2', { text: summary.title || idOf(summary) }),
        nameLine,
        el('span', { class: 'muted small' }, [kindTitle(summary), summary.copyOf && t('prototypes.copyOf', { name: summary.copyOf })].filter(Boolean).join(' · '))),
      more);
  }

  function renameDetails(s) {
    const r = s.rename;
    if (!r) return null;
    return el('div', { class: 'bv-rename-box' },
      el('label', { class: 'bp-check' },
        el('input', { type: 'checkbox', checked: r.migration, 'data-testid': 'blocks-rename-migration', onchange: (e) => { r.migration = e.target.checked; previewRename(blocksOf(current.overview).find((b) => b.file === s.selected)); } }),
        el('span', { text: t('blockops.migration') })),
      r.error && el('p', { class: 'small bv-rename-error', text: r.error }),
      r.plan && el('details', { class: 'bv-plan-details' },
        el('summary', { class: 'small', text: t('blocks.renameFiles', { n: formatNumber(r.plan.changes.length) }) }), planList(r.plan)));
  }

  function table(s, data) {
    const props = data.block.properties;
    if (!props.length) return el('p', { class: 'muted ov-empty', text: t('blockprops.none') });
    const groups = new Map();
    for (const p of props) {
      const key = `${p.prototypeType}/${p.prototypeName}`;
      if (!groups.has(key)) groups.set(key, { type: p.prototypeType, name: p.prototypeName, copyOf: p.copyOf, items: [] });
      groups.get(key).items.push(p);
    }
    const editable = current.editable && data.editable;
    const row = (p) => {
      const factor = p.kind === 'multiplier' || p.kind === 'damage';
      const value = s.edits.get(p.key) ?? p.value;
      const changed = s.edits.has(p.key);
      const reset = resetText(p);
      const original = shownValue(p.original);
      const game = shownValue(p.game);
      const label = p.kind === 'damage' ? t('blockprops.damage') : fieldLabel(p.path) + (factor ? ' ×' : '');
      return el('tr', { class: changed ? 'changed' : null, 'data-testid': 'blocks-row' },
        el('th', { scope: 'row' },
          el('span', { class: 'bv-label', title: p.comment || p.path, text: label }),
          el('span', { class: 'muted small mono', text: p.kind === 'damage' ? t('blockprops.damageHint') : p.path }),
          p.alsoFor.length > 0 && el('span', { class: 'muted small', text: t('blockprops.alsoFor', { names: p.alsoFor.join(', ') }) })),
        el('td', { class: 'mono bv-original', text: factor ? '×1' : original ?? '—' }),
        el('td', { class: 'bv-yours' },
          el('input', { type: 'text', class: 'mono bp-input', value, disabled: !editable, inputmode: p.valueType === 'number' ? 'decimal' : 'text',
            'aria-label': label, 'data-testid': 'blocks-input', 'data-key': p.key,
            oninput: (e) => { const v = e.target.value.trim(); if (v === p.value) s.edits.delete(p.key); else s.edits.set(p.key, v); renderKeepingFocus(); },
            onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); save(); } } }),
          editable && reset !== null && value !== reset && el('button', { type: 'button', class: 'bv-reset', title: t('blockprops.resetTitle'), 'aria-label': t('blockprops.resetTitle'),
            'data-testid': 'blocks-reset', onclick: () => { if (reset === p.value) s.edits.delete(p.key); else s.edits.set(p.key, reset); render(); }, text: '↺' })),
        el('td', { class: data.gameDataStale ? 'mono bv-game bv-stale' : 'mono bv-game',
          title: data.gameData ? t(data.gameDataStale ? 'gamedata.stale' : 'blocks.gameHint') : '', text: game ?? '—' }));
    };
    return el('table', { class: 'bv-table', 'data-testid': 'blocks-table' },
      el('thead', {}, el('tr', {},
        el('th', { scope: 'col', text: t('blocks.colProperty') }), el('th', { scope: 'col', text: t('blocks.colOriginal') }),
        el('th', { scope: 'col', text: t('blocks.colYours') }), el('th', { scope: 'col', text: t('blocks.colGame') }))),
      [...groups.values()].map((g) => el('tbody', {},
        el('tr', { class: 'bv-group' }, el('th', { colspan: '4', scope: 'colgroup' },
          el('span', { class: 'bv-group-line' },
            el('span', { class: 'mono', text: g.name }), el('span', { class: 'chip proto-type mono', title: g.type, text: g.type }),
            g.copyOf && el('span', { class: 'muted small', text: t('prototypes.copyOf', { name: g.copyOf }) })))),
        g.items.map(row))));
  }

  // One chip per prototype name (the belt is an item, a recipe and an entity), opening its first definition.
  function prototypesLine(o, summary) {
    const byName = new Map();
    for (const p of o.prototypes.filter((x) => x.file === summary.file)) {
      if (!byName.has(p.name)) byName.set(p.name, []);
      byName.get(p.name).push(p);
    }
    if (!byName.size) return null;
    return el('div', { class: 'bv-protos' },
      el('span', { class: 'muted small', text: t('blocks.prototypes') }),
      [...byName].map(([name, list]) => el('button', { type: 'button', class: 'chip proto-chip', 'data-testid': 'blocks-prototype',
        title: t('readiness.action.open', { file: `${list[0].file}:${list[0].line}` }), onclick: () => openFile(list[0].file, list[0].line) },
      el('span', { class: 'mono', text: name }), el('span', { class: 'muted small', text: ` · ${list.map((p) => p.type).join(', ')}` }))));
  }

  function detailPane(o, s) {
    const summary = blocksOf(o).find((b) => b.file === s.selected);
    if (!summary) return el('section', { class: 'bv-detail bv-empty' }, el('p', { class: 'muted', text: t('blocks.pick') }));
    const data = s.details.get(summary.file);
    if (!data) return el('section', { class: 'bv-detail' }, el('p', { class: 'muted small', text: t('gamedata.loadingItem') }));
    if (data.error) return el('section', { class: 'bv-detail' }, el('div', { class: 'gd-error', text: data.error }));

    const dirty = s.edits.size;
    return el('section', { class: 'bv-detail', 'aria-label': summary.title || idOf(summary) },
      header(o, s, summary, data),
      renameDetails(s),
      colourSection(s, summary, data),
      originalsStatus(s, data),
      table(s, data),
      prototypesLine(o, summary),
      dirty > 0 && el('div', { class: 'bv-savebar', role: 'region', 'aria-label': t('blocks.unsaved', { n: formatNumber(dirty) }), 'data-testid': 'blocks-savebar' },
        el('span', { text: t('blocks.unsaved', { n: formatNumber(dirty) }) }),
        el('button', { type: 'button', 'data-testid': 'blocks-revert', onclick: () => { s.edits.clear(); render(); }, text: t('blocks.revert') }),
        el('button', { type: 'button', class: 'primary', 'data-testid': 'blocks-save', onclick: save, text: t('blockprops.save') })));
  }

  function render() {
    if (!current) return;
    const o = current.overview;
    const s = stateOf(current.modId);
    const blocks = blocksOf(o);
    if (!blocks.length) {
      root.replaceChildren(el('div', { class: 'bv-none', 'data-testid': 'blocks-none' },
        el('h3', { text: t('blocks.noneTitle') }), el('p', { class: 'muted', text: t('blocks.none') })));
      return;
    }
    if (!blocks.some((b) => b.file === s.selected)) {
      s.selected = blocks[0].file;
      s.edits.clear();
      if (!s.details.has(s.selected)) loadDetail(current.modId, s.selected);
    }
    root.replaceChildren(listPane(o, s), detailPane(o, s));
  }

  return {
    /** The tab for a mod; a new overview (the mod's files changed) reads the open block again. */
    show(overview, modId, editable) {
      current = { modId, editable, overview };
      const s = stateOf(modId);
      if (s.overview && s.overview !== overview && !s.edits.size) s.details.clear();
      s.overview = overview;
      render();
      if (s.selected && !s.details.has(s.selected)) loadDetail(modId, s.selected);
      return root;
    },
    /** Opens a block (from the Prototypes tab); the tab itself is switched to by the host. */
    select(modId, file) { const s = stateOf(modId); if (s.selected !== file) { s.selected = file; s.edits.clear(); s.rename = null; s.colourEdit = null; } },
    hasUnsaved(modId) { return (mods.get(modId)?.edits.size ?? 0) > 0; },
  };
}
