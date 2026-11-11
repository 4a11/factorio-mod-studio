// "Localization" tab of the mod page: a translation table (source language next to one target language) with live
// placeholder checks, filters, a local translation memory and optimistic-concurrency saving.
// Top: one chip per language with its progress, a segmented progress bar of the chosen language and filter chips with counts.
// Bottom: a sticky save bar that appears while there are unsaved changes (Ctrl/Cmd+S saves while focus is in the tab).
// The table is grouped by locale section ([item-name], [entity-name], ...) with per-section progress; complete sections start
// collapsed. Placeholder chips show only on the row being edited. Enter moves to the next string, Shift+Enter inserts a line
// break (stored as \n), Alt+Down / Alt+Up jump to the next / previous untranslated string.
//
// The view owns one persistent DOM subtree (`element`) and its state lives outside the DOM, so the host page can detach and
// re-attach it (it redraws the whole mod page on live updates) without losing unsaved edits.
// Mod content (keys, strings) is untrusted: it is only ever set with textContent / value / attributes, never as HTML.

import { t } from '/i18n.js';
import { compare, extract } from '/locale-placeholders.js';

const CHUNK = 200; // rows rendered per section at once; "show more" adds another chunk
const FILTERS = ['all', 'untranslated', 'identical', 'extra', 'problems'];
const CODE_PATTERN = /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$/;
const RELOAD_DELAY_MS = 300;
const STATUS_MS = 5000; // how long "Saved" / "Created" stay in the save bar
const TOKEN_LABEL_LIMIT = 48;

const idOf = (section, key) => `${section}\u0000${key}`;
const enc = encodeURIComponent;

/**
 * @param {{ api: (url: string, options?: object) => Promise<any>, el: (tag: string, props?: object, ...children: any[]) => HTMLElement }} host
 */
export function createLocaleView({ api, el }) {
  const s = {
    modId: null,
    editable: false,
    info: null, // { sourceLanguage, editable, languages }
    lang: null,
    table: null, // LocaleTable of s.lang
    byId: new Map(), // id -> row
    edits: new Map(), // id -> edited text
    removals: new Set(), // ids of Extra rows staged for removal
    filter: 'all',
    search: '',
    limits: new Map(), // section -> rows shown (CHUNK unless "show more" was used)
    open: new Map(), // section -> expanded, when the user toggled it (otherwise: open unless complete)
    status: null, // { key, params } | { text } | null
    conflict: false,
    saving: false,
    loading: false,
    error: null, // server text
    seq: 0,
    own: new Set(), // revisions produced by our own saves
    reloadTimer: 0,
    statusTimer: 0,
  };

  const labels = []; // [node, key] pairs retranslated on a language switch
  const placeholders = []; // [node, key]
  const rowRefs = new Map(); // id -> { tr, area, slot, row, remove }
  const refs = {};

  const label = (node, key) => { labels.push([node, key]); node.textContent = t(key); return node; };
  const withPlaceholder = (node, key) => { placeholders.push([node, key]); node.setAttribute('placeholder', t(key)); return node; };
  const base = () => `/api/mods/${enc(s.modId)}/locale`;
  const sourceLang = () => s.info?.sourceLanguage ?? null;
  const isSourceView = () => !!s.lang && s.lang === sourceLang();
  const dirtyCount = () => { const ids = new Set([...s.edits.keys(), ...s.removals]); return ids.size; };
  const originalOf = (row) => row.target ?? '';
  const currentOf = (row) => { const id = idOf(row.section, row.key); return s.edits.has(id) ? s.edits.get(id) : originalOf(row); };

  // ------------------------------------------------------------ static structure

  const root = el('div', { class: 'locale-panel', 'data-testid': 'locale-panel' });

  refs.readonly = label(el('div', { class: 'note', 'data-testid': 'locale-readonly', role: 'note' }), 'locale.readonly');
  refs.empty = label(el('div', { class: 'empty', 'data-testid': 'locale-empty' }), 'locale.empty');
  refs.error = el('div', { class: 'note locale-error', 'data-testid': 'locale-error', role: 'alert' });
  refs.loading = label(el('div', { class: 'muted', 'data-testid': 'locale-loading' }), 'locale.loading');

  // Languages: one chip each (code, percent, a small bar); the original language is marked and has no percent.
  refs.langs = el('div', { class: 'locale-langs', role: 'group', 'data-testid': 'locale-lang' });
  refs.newLang = el('button', { type: 'button', class: 'files-btn', 'data-testid': 'locale-new-lang' },
    el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: '+' }), label(el('span'), 'locale.newLanguage'));
  refs.newLang.addEventListener('click', openNewLanguage);

  // Progress of the chosen language: translated / same as original / untranslated as segments of one bar.
  refs.progressBar = el('div', { class: 'locale-segments', 'aria-hidden': 'true' });
  refs.progressText = el('span', { class: 'locale-progress-text' });
  refs.progress = el('div', { class: 'locale-progress', 'data-testid': 'locale-progress', 'data-percent': '0', role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': '0' }, refs.progressBar, refs.progressText);

  refs.filter = el('div', { class: 'locale-filters', role: 'group', 'data-testid': 'locale-filter' });
  refs.search = withPlaceholder(el('input', { type: 'search', class: 'locale-search', 'data-testid': 'locale-search', autocomplete: 'off', spellcheck: 'false' }), 'locale.search');
  refs.search.addEventListener('input', () => { s.search = refs.search.value; s.limits.clear(); renderRows(); });

  refs.dirty = el('span', { class: 'locale-dirty', 'data-testid': 'locale-dirty' });
  refs.save = label(el('button', { type: 'button', class: 'primary', 'data-testid': 'locale-save' }), 'locale.save');
  refs.save.setAttribute('aria-keyshortcuts', 'Control+S');
  refs.save.addEventListener('click', save);
  refs.discard = label(el('button', { type: 'button', 'data-testid': 'locale-discard' }), 'locale.discard');
  refs.discard.addEventListener('click', discardAll);
  refs.status = el('span', { class: 'locale-status small', 'data-testid': 'locale-status', role: 'status', 'aria-live': 'polite' });

  refs.conflictText = label(el('span'), 'locale.conflict');
  refs.reload = label(el('button', { type: 'button', 'data-testid': 'locale-reload' }), 'locale.reload');
  refs.reload.addEventListener('click', () => { refresh(true); });
  refs.conflict = el('div', { class: 'banner locale-conflict', 'data-testid': 'locale-conflict', role: 'alert' }, refs.conflictText, refs.reload);

  refs.noRows = label(el('p', { class: 'muted ov-empty', 'data-testid': 'locale-norows' }), 'locale.noRows');
  refs.keysHint = label(el('p', { class: 'muted small locale-keys-hint', 'data-testid': 'locale-keys-hint' }), 'locale.keysHint');

  // One <tbody> per section, each starting with its header row.
  const head = (key) => label(el('th', { scope: 'col' }), key);
  refs.thead = el('thead', {}, el('tr', {}, head('locale.colKey'), head('locale.colSource'), head('locale.colTarget')));
  refs.table = el('table', { class: 'locale-table' }, refs.thead);

  // Shown while there are unsaved changes, or briefly after a save; sticks to the bottom of the page while the table scrolls.
  refs.saveBar = el('div', { class: 'locale-savebar', 'data-testid': 'locale-savebar' },
    refs.dirty, refs.status, el('span', { class: 'locale-savebar-actions' }, refs.discard, refs.save));

  refs.main = el('div', { class: 'locale-main' },
    el('div', { class: 'locale-bar' }, refs.langs, refs.newLang),
    refs.progress,
    el('div', { class: 'locale-bar' }, refs.filter, refs.search),
    refs.conflict,
    refs.keysHint,
    refs.table, refs.noRows,
    refs.saveBar);

  // Ctrl/Cmd+S while focus is in the tab saves the translations (and keeps the editor tabs' Ctrl+S from firing).
  root.addEventListener('keydown', (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.key.toLowerCase() !== 's') return;
    e.preventDefault();
    save();
  });

  // New language dialog
  refs.dialog = el('dialog', { class: 'locale-dialog', 'aria-labelledby': 'locale-dialog-heading' });
  refs.newCode = withPlaceholder(el('input', { type: 'text', 'data-testid': 'locale-new-code', maxlength: '12', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', 'aria-describedby': 'locale-new-hint' }), 'locale.newCodePlaceholder');
  refs.newError = el('p', { class: 'locale-new-error small', 'data-testid': 'locale-new-error', role: 'alert' });
  refs.newConfirm = label(el('button', { type: 'button', class: 'primary', 'data-testid': 'locale-new-confirm' }), 'locale.create');
  refs.newCancel = label(el('button', { type: 'button', 'data-testid': 'locale-new-cancel' }), 'locale.cancel');
  refs.dialog.append(
    label(el('h2', { id: 'locale-dialog-heading' }), 'locale.newTitle'),
    label(el('p', { class: 'small muted', id: 'locale-new-hint' }), 'locale.newHint'),
    refs.newCode, refs.newError,
    el('div', { class: 'nm-actions' }, refs.newCancel, refs.newConfirm));
  refs.newCancel.addEventListener('click', () => refs.dialog.close());
  refs.newConfirm.addEventListener('click', createLanguage);
  refs.newCode.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); createLanguage(); } });
  refs.newCode.addEventListener('input', () => { refs.newError.textContent = ''; refs.newError.hidden = true; });

  root.append(refs.readonly, refs.empty, refs.error, refs.loading, refs.main, refs.dialog);

  // ------------------------------------------------------------ rendering

  /** A status line in the save bar; success messages disappear after a while, errors stay until the next action. */
  function setStatus(key, params) {
    clearTimeout(s.statusTimer);
    s.status = key ? { key, params } : null;
    if (key) s.statusTimer = setTimeout(() => { s.status = null; renderStatus(); renderControls(); }, STATUS_MS);
    renderStatus();
    renderControls();
  }
  function setStatusText(text) { clearTimeout(s.statusTimer); s.status = text ? { text } : null; renderStatus(); renderControls(); }
  function renderStatus() {
    const spec = s.status;
    refs.status.textContent = !spec ? '' : spec.key ? t(spec.key, spec.params) : spec.text;
    refs.status.classList.toggle('bad', !!spec?.text);
  }

  const percentOf = (l) => (l.total === 0 ? 100 : Math.floor((l.translated * 100) / l.total));

  // Live counts of the filters (placeholder problems follow unsaved edits).
  function filterCounts() {
    const rows = s.table?.rows ?? [];
    return {
      all: rows.length,
      untranslated: rows.filter((r) => r.status === 'untranslated').length,
      identical: rows.filter((r) => r.identical).length,
      extra: rows.filter((r) => r.status === 'extra').length,
      problems: rows.filter((r) => problemsOf(r)).length,
    };
  }

  // Filter chips with counts; empty filters are hidden, except the chosen one (so fixing the last problem does not jump away).
  function renderFilterOptions() {
    refs.filter.setAttribute('aria-label', t('locale.filterLabel'));
    const counts = filterCounts();
    refs.filter.replaceChildren(...FILTERS.filter((f) => f === 'all' || counts[f] > 0 || s.filter === f).map((f) => el('button', {
      type: 'button', class: `chip proto-chip locale-filter-chip ${f}`, 'data-filter': f, 'aria-pressed': s.filter === f ? 'true' : 'false',
      onclick: () => { s.filter = f; s.limits.clear(); renderFilterOptions(); renderRows(); },
    }, el('span', { text: t(`locale.filter.${f}`) }), el('span', { class: 'ov-count', text: String(counts[f]) }))));
  }

  function renderLanguageSelect() {
    const languages = s.info?.languages ?? [];
    refs.langs.setAttribute('aria-label', t('locale.language'));
    refs.langs.replaceChildren(...languages.map((l) => {
      const source = l.code === sourceLang();
      const percent = percentOf(l);
      const fill = el('span', { class: 'locale-lang-fill' });
      fill.style.setProperty('width', `${percent}%`); // CSSOM, allowed under the strict CSP
      return el('button', {
        type: 'button', class: `chip proto-chip locale-lang-chip${source ? ' source' : ''}${percent === 100 ? ' done' : ''}`, 'data-lang': l.code,
        'aria-pressed': l.code === s.lang ? 'true' : 'false',
        title: source ? t('locale.sourceOption', { code: l.code }) : t('locale.progress', { percent, translated: l.translated, total: l.total }),
        onclick: () => switchLanguage(l.code),
      },
      el('span', { class: 'mono', text: l.code }),
      source
        ? el('span', { class: 'muted small', text: t('locale.originalTag') })
        : [el('span', { class: 'locale-lang-pct', text: `${percent}%` }), el('span', { class: 'locale-lang-bar', 'aria-hidden': 'true' }, fill)]);
    }));
  }

  function renderSections() {
    const hasLocale = !!s.info && !!sourceLang() && (s.info.languages?.length ?? 0) > 0;
    const showMain = hasLocale && !!s.table;
    refs.readonly.hidden = s.editable;
    refs.empty.hidden = !(s.info && !hasLocale);
    refs.error.hidden = !s.error;
    refs.error.textContent = s.error || '';
    refs.loading.hidden = !(s.loading && !s.info && !s.error);
    refs.main.hidden = !showMain;
    refs.conflict.hidden = !s.conflict;

    // Creating a language needs an existing language to copy the file layout from.
    refs.newLang.hidden = !hasLocale && !!s.info;
    refs.newLang.disabled = !s.editable || !hasLocale;
  }

  function renderSummary() {
    const sum = s.table?.summary;
    if (!sum) return;
    const percent = sum.total === 0 ? 100 : Math.floor((sum.translated * 100) / sum.total);
    refs.progress.setAttribute('data-percent', String(percent));
    refs.progress.setAttribute('aria-valuenow', String(percent));
    refs.progressText.textContent = t('locale.progress', { percent, translated: sum.translated, total: sum.total });
    // Identical rows count as translated on the server; the bar shows them as their own (blue) segment.
    const segments = [['ok', sum.translated - sum.identical, 'locale.legend.translated'], ['identical', sum.identical, 'locale.filter.identical'],
      ['untranslated', sum.untranslated, 'locale.filter.untranslated']];
    refs.progressBar.replaceChildren(...segments.filter(([, n]) => n > 0).map(([kind, n, key]) => {
      const part = el('span', { class: kind, title: `${t(key)}: ${n}` });
      part.style.setProperty('flex-grow', String(n));
      return part;
    }));
  }

  function renderControls() {
    const dirty = dirtyCount();
    refs.dirty.textContent = dirty ? t('locale.dirty', { n: dirty }) : '';
    refs.save.disabled = !s.editable || dirty === 0 || s.saving;
    refs.save.title = t('locale.saveTitle');
    refs.discard.disabled = dirty === 0 || s.saving;
    refs.discard.hidden = dirty === 0;
    refs.save.hidden = dirty === 0 && !s.saving;
    refs.saveBar.hidden = dirty === 0 && !s.saving && !s.status;
    refs.saveBar.classList.toggle('dirty', dirty > 0);
    refs.conflict.hidden = !s.conflict;
    if (s.table) renderFilterOptions(); // problem counts follow the edits
  }

  const problemsOf = (row) => {
    if (row.status === 'extra' || isSourceView()) return null;
    const id = idOf(row.section, row.key);
    if (!s.edits.has(id)) {
      const missing = row.placeholderMissing || [];
      const unexpected = row.placeholderUnexpected || [];
      return missing.length || unexpected.length ? { missing, unexpected } : null;
    }
    const text = s.edits.get(id);
    if (text.trim() === '') return null;
    const diff = compare(row.source, text);
    return diff.isEmpty ? null : diff;
  };

  function warningText(problems) {
    const parts = [];
    if (problems.missing.length) parts.push(t('locale.warnMissing', { tokens: problems.missing.join(' ') }));
    if (problems.unexpected.length) parts.push(t('locale.warnUnexpected', { tokens: problems.unexpected.join(' ') }));
    return parts.join(' · ');
  }

  function updateRow(ref) {
    const { tr, row, slot } = ref;
    const id = idOf(row.section, row.key);
    const removing = s.removals.has(id);
    const dirty = removing || s.edits.has(id);

    if (dirty) tr.setAttribute('data-dirty', 'true'); else tr.removeAttribute('data-dirty');
    tr.classList.toggle('removing', removing);
    if (ref.area) ref.area.disabled = removing;
    if (ref.remove) ref.remove.textContent = removing ? t('locale.undoRemove') : t('locale.remove');

    const problems = removing ? null : problemsOf(row);
    let warn = slot.querySelector('[data-testid="locale-warn"]');
    if (!problems) { warn?.remove(); return; }
    if (!warn) {
      warn = el('div', { class: 'locale-warn small', 'data-testid': 'locale-warn', role: 'alert' });
      slot.append(warn);
    }
    warn.textContent = warningText(problems);
  }

  function insertToken(area, token) {
    if (area.readOnly || area.disabled) return;
    area.focus();
    const start = area.selectionStart ?? area.value.length;
    const end = area.selectionEnd ?? start;
    area.setRangeText(token, start, end, 'end');
    area.dispatchEvent(new Event('input', { bubbles: true }));
  }

  function setEdit(row, text, area) {
    const id = idOf(row.section, row.key);
    if (text === originalOf(row)) s.edits.delete(id); else s.edits.set(id, text);
    if (area && area.value !== text) area.value = text;
    const ref = rowRefs.get(id);
    if (ref) updateRow(ref);
    renderControls();
  }

  function autosize(area) {
    const lines = area.value.split('\n').reduce((n, line) => n + Math.max(1, Math.ceil(line.length / 56)), 0);
    area.rows = Math.min(8, Math.max(2, lines));
  }

  function buildRow(row) {
    const id = idOf(row.section, row.key);
    const tr = el('tr', {
      'data-testid': 'locale-row', 'data-section': row.section, 'data-key': row.key, 'data-status': row.status,
    });

    // Key cell (the section is the group header; the file and line are in the tooltip)
    const where = row.targetFile ? `${row.targetFile}:${row.targetLine}` : row.sourceFile;
    const keyCell = el('td', { class: 'locale-key', 'data-label': t('locale.colKey') },
      el('div', { class: 'mono locale-key-name', title: where || null, text: row.key }),
      row.identical && el('span', { class: 'badge info locale-identical', title: t('locale.identicalHint'), text: t('locale.identical') }),
      row.marked && el('span', { class: 'badge info locale-marked', 'data-testid': 'locale-marked', title: t('locale.markedHint'), text: t('locale.marked') }),
      row.status === 'extra' && el('span', { class: 'badge warning', title: t('locale.extraHint'), text: t('locale.extra') }));

    // Source cell
    const tokens = row.status === 'extra' || isSourceView() ? [] : extract(row.source);
    const sourceCell = el('td', { class: 'locale-src', 'data-label': t('locale.colSource') },
      el('div', { class: 'locale-source', 'data-testid': 'locale-source', text: row.source }));

    // Target cell
    const area = el('textarea', {
      class: 'locale-target', 'data-testid': 'locale-target', spellcheck: 'false', rows: '2',
      'aria-label': t('locale.targetAria', { key: row.key }),
      readonly: !s.editable,
    });
    area.value = currentOf(row);
    autosize(area);
    area.addEventListener('input', () => {
      let text = area.value;
      if (/[\r\n]/.test(text)) { // a value is one line; line breaks are written as the two characters \n
        const caret = area.selectionStart ?? text.length;
        const before = text.slice(0, caret).replace(/\r\n|\r|\n/g, '\\n');
        text = text.replace(/\r\n|\r|\n/g, '\\n');
        area.value = text;
        area.setSelectionRange(before.length, before.length);
      }
      autosize(area);
      setEdit(row, text, null);
    });
    area.addEventListener('keydown', (e) => {
      if (e.isComposing) return; // Enter confirms an IME composition (Chinese, Japanese, ...), it must not move on
      if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); jump(row, 1, () => true); }
      else if (e.altKey && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) { e.preventDefault(); jump(row, e.key === 'ArrowDown' ? 1 : -1, isOpen); }
    });

    const slot = el('div', { class: 'locale-slot' });
    const ref = { tr, area, slot, row, remove: null };

    const chips = tokens.map((token) => el('button', {
      type: 'button', class: 'chip locale-chip', 'data-testid': 'locale-chip', title: token, disabled: !s.editable,
      onclick: () => insertToken(area, token),
    }, token.length > TOKEN_LABEL_LIMIT ? `${token.slice(0, TOKEN_LABEL_LIMIT - 1)}…` : token));

    const actions = [];
    if (row.status === 'untranslated' && row.suggestion) {
      // The remembered translation is shown, not hidden in a tooltip.
      actions.push(el('span', { class: 'locale-suggestion small' },
        el('span', { class: 'muted', text: t('locale.fromMemory') }),
        el('span', { class: 'locale-suggestion-text', text: row.suggestion }),
        el('button', {
          type: 'button', class: 'locale-suggest', 'data-testid': 'locale-suggest', disabled: !s.editable,
          onclick: () => { setEdit(row, row.suggestion, area); autosize(area); },
        }, t('locale.useSuggestion'))));
    }
    if (row.status === 'extra') {
      ref.remove = el('button', {
        type: 'button', class: 'locale-remove', 'data-testid': 'locale-remove', disabled: !s.editable,
        onclick: () => { if (s.removals.has(id)) s.removals.delete(id); else s.removals.add(id); updateRow(ref); renderControls(); },
      }, t('locale.remove'));
      actions.push(ref.remove);
    }

    const targetCell = el('td', { class: 'locale-tgt', 'data-label': t('locale.colTarget') },
      area,
      chips.length > 0 && el('div', { class: 'locale-chips' }, chips),
      actions.length > 0 && el('div', { class: 'locale-row-actions' }, actions),
      slot);

    tr.append(keyCell, sourceCell, targetCell);
    rowRefs.set(id, ref);
    updateRow(ref);
    return tr;
  }

  function matches(row) {
    const id = idOf(row.section, row.key);
    switch (s.filter) {
      case 'untranslated': if (row.status !== 'untranslated') return false; break;
      case 'identical': if (!row.identical) return false; break;
      case 'extra': if (row.status !== 'extra') return false; break;
      case 'problems': {
        const p = problemsOf(row);
        if (!p) return false;
        break;
      }
      default: break;
    }
    const needle = s.search.trim().toLowerCase();
    if (!needle) return true;
    const target = s.edits.has(id) ? s.edits.get(id) : (row.target ?? '');
    return row.section.toLowerCase().includes(needle) || row.key.toLowerCase().includes(needle)
      || row.source.toLowerCase().includes(needle) || target.toLowerCase().includes(needle);
  }

  /** The rows that pass the filters, grouped by section in table order: Map section -> rows. */
  function visibleGroups() {
    const groups = new Map();
    for (const row of s.table ? s.table.rows : []) {
      if (!matches(row)) continue;
      if (!groups.has(row.section)) groups.set(row.section, []);
      groups.get(row.section).push(row);
    }
    return groups;
  }

  /** Progress of every section over all its rows (not only the filtered ones); extra keys do not count. */
  function sectionStats() {
    const stats = new Map();
    for (const row of s.table ? s.table.rows : []) {
      if (!stats.has(row.section)) stats.set(row.section, { total: 0, done: 0, extra: 0 });
      const st = stats.get(row.section);
      if (row.status === 'extra') { st.extra++; continue; }
      st.total++;
      if (row.status !== 'untranslated') st.done++;
    }
    return stats;
  }

  // A complete section starts collapsed, unless a filter or search is active; a click overrides that until the mod changes.
  const sectionOpen = (section, st) => (s.open.has(section) ? s.open.get(section) : s.filter !== 'all' || !!s.search.trim() || st.done < st.total || st.extra > 0);

  function sectionHeader(section, st, open, shown) {
    const fill = el('span', { class: 'locale-lang-fill' });
    fill.style.setProperty('width', `${st.total ? Math.floor((st.done * 100) / st.total) : 100}%`);
    const complete = st.done === st.total;
    return el('tr', { class: 'locale-section-head' }, el('th', { colspan: '3', scope: 'rowgroup' },
      el('button', {
        type: 'button', class: `locale-section-toggle${complete ? ' done' : ''}`, 'aria-expanded': open ? 'true' : 'false', 'data-testid': 'locale-section',
        onclick: () => { s.open.set(section, !open); renderRows(); },
      },
      el('span', { class: 'files-twisty', 'aria-hidden': 'true', text: open ? '▾' : '▸' }),
      el('span', { class: 'mono', text: `[${section}]` }),
      el('span', { class: 'locale-section-count', text: `${st.done}/${st.total}` }),
      el('span', { class: 'locale-lang-bar', 'aria-hidden': 'true' }, fill),
      shown < st.total + st.extra && el('span', { class: 'muted small', text: t('locale.sectionShown', { n: shown }) }))));
  }

  function renderRows() {
    rowRefs.clear();
    const groups = visibleGroups();
    const stats = sectionStats();
    const bodies = [...groups].map(([section, rows]) => {
      const st = stats.get(section);
      const open = sectionOpen(section, st);
      const limit = s.limits.get(section) ?? CHUNK;
      const rest = rows.length - limit;
      return el('tbody', { 'data-section': section },
        sectionHeader(section, st, open, rows.length),
        open ? rows.slice(0, limit).map(buildRow) : null,
        open && rest > 0 && el('tr', { class: 'locale-more-row' }, el('td', { colspan: '3' }, el('button', {
          type: 'button', class: 'locale-more', 'data-testid': 'locale-more',
          onclick: () => { s.limits.set(section, limit + CHUNK); renderRows(); },
        }, t('locale.showMore', { n: Math.min(rest, CHUNK), total: rest })))));
    });
    refs.table.replaceChildren(refs.thead, ...bodies);
    refs.noRows.hidden = groups.size > 0;
    refs.table.hidden = groups.size === 0;
    refs.keysHint.hidden = !s.editable || isSourceView() || groups.size === 0;
    renderControls();
  }

  /** Still needs a translation, counting unsaved edits. */
  const isOpen = (row) => row.status !== 'extra' && currentOf(row).trim() === '';

  /**
   * Moves the focus from `row` to the next (dir 1) or previous (dir -1) visible row that satisfies `want`, opening its section
   * and showing more rows when it is not drawn yet.
   */
  function jump(row, dir, want) {
    const order = [...visibleGroups().values()].flat();
    let i = order.indexOf(row) + dir;
    while (i >= 0 && i < order.length && !want(order[i])) i += dir;
    const target = order[i];
    if (!target) return;
    const id = idOf(target.section, target.key);
    if (!rowRefs.has(id)) {
      s.open.set(target.section, true);
      const index = order.filter((r) => r.section === target.section).indexOf(target);
      if (index >= (s.limits.get(target.section) ?? CHUNK)) s.limits.set(target.section, Math.ceil((index + 1) / CHUNK) * CHUNK);
      renderRows();
    }
    const ref = rowRefs.get(id);
    if (!ref?.area) return;
    ref.area.focus();
    ref.tr.scrollIntoView?.({ block: 'center' });
  }

  function renderAll() {
    renderSections();
    renderLanguageSelect();
    renderFilterOptions();
    renderSummary();
    renderRows();
    renderStatus();
  }

  // ------------------------------------------------------------ loading

  function indexTable() {
    s.byId = new Map();
    for (const row of s.table?.rows ?? []) s.byId.set(idOf(row.section, row.key), row);
    // Edits for rows that vanished, or that now equal the stored text, are no longer changes.
    for (const [id, text] of [...s.edits]) {
      const row = s.byId.get(id);
      if (!row || text === originalOf(row)) s.edits.delete(id);
    }
    for (const id of [...s.removals]) if (!s.byId.get(id)) s.removals.delete(id);
  }

  const pickLanguage = (info) => {
    const codes = (info.languages ?? []).map((l) => l.code);
    if (s.lang && codes.includes(s.lang)) return s.lang;
    return codes.find((c) => c !== info.sourceLanguage) ?? codes[0] ?? null;
  };

  /** Loads the language list and the current table. `discard` drops local edits (used by "reload" and language switches). */
  async function refresh(discard = false) {
    const modId = s.modId;
    if (!modId) return;
    const seq = ++s.seq;
    s.loading = true;
    let info; let lang = null; let table = null;
    try {
      info = await api(base());
      lang = pickLanguage(info);
      if (lang) table = await api(`${base()}/${enc(lang)}`);
    } catch (error) {
      if (modId !== s.modId || seq !== s.seq) return;
      s.loading = false;
      s.error = error.message;
      renderSections();
      return;
    }
    if (modId !== s.modId || seq !== s.seq) return;

    s.loading = false;
    s.error = null;
    s.editable = !!info.editable;
    const langChanged = lang !== s.lang;
    const revisionChanged = !s.table || !table || table.revision !== s.table.revision;
    s.info = info;
    s.lang = lang;

    if (discard || langChanged) {
      s.edits.clear();
      s.removals.clear();
      s.conflict = false;
      s.table = table;
      indexTable();
      s.limits.clear();
      s.open.clear();
      renderAll();
      return;
    }

    if (revisionChanged && dirtyCount() > 0 && !s.own.has(table?.revision)) {
      // Somebody else changed the files while we hold unsaved edits: keep what the user sees and let them decide.
      s.conflict = true;
      renderSections();
      renderLanguageSelect();
      renderControls();
      return;
    }

    if (revisionChanged) {
      s.table = table;
      s.conflict = false;
      indexTable();
      renderAll();
    } else {
      s.table = table ?? s.table; // same files; the summary and memory suggestions may still differ
      renderSections();
      renderLanguageSelect();
      renderSummary();
    }
  }

  async function switchLanguage(code) {
    if (code === s.lang) return;
    if (dirtyCount() > 0 && !confirm(t('locale.discardConfirm'))) return;
    s.lang = code;
    s.status = null;
    await refresh(true);
  }

  // ------------------------------------------------------------ saving

  async function save() {
    if (s.saving || !s.editable || !s.table) return;
    const ids = [...new Set([...s.edits.keys(), ...s.removals])];
    if (!ids.length) return;

    const changes = ids.map((id) => {
      const row = s.byId.get(id);
      return { section: row.section, key: row.key, value: s.removals.has(id) ? null : s.edits.get(id) };
    });

    s.saving = true;
    renderControls();
    try {
      const result = await api(`${base()}/${enc(s.lang)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'If-Match': `"${s.table.revision}"` },
        body: JSON.stringify({ changes }),
      });
      s.own.add(result.revision);
      for (const id of ids) { s.edits.delete(id); s.removals.delete(id); }
      s.conflict = false;
      setStatus('locale.saved', { n: result.added + result.updated + result.removed, added: result.added, updated: result.updated, removed: result.removed });
      s.saving = false;
      await refresh();
    } catch (error) {
      if (error.status === 409) { s.conflict = true; setStatus(null); }
      else setStatusText(error.message);
    } finally {
      s.saving = false;
      renderControls();
    }
  }

  function discardAll() {
    if (s.saving) return;
    s.edits.clear();
    s.removals.clear();
    setStatus(null);
    renderRows();
  }

  // ------------------------------------------------------------ new language

  function openNewLanguage() {
    if (!s.editable || refs.newLang.disabled) return;
    refs.newCode.value = '';
    refs.newError.textContent = '';
    refs.newError.hidden = true;
    if (!refs.dialog.open) refs.dialog.showModal();
    refs.newCode.focus();
  }

  function showNewError(text) {
    refs.newError.textContent = text;
    refs.newError.hidden = false;
  }

  async function createLanguage() {
    const code = refs.newCode.value.trim();
    if (!CODE_PATTERN.test(code)) { showNewError(t('locale.newInvalid')); return; }
    refs.newConfirm.disabled = true;
    try {
      await api(base(), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code }),
      });
    } catch (error) {
      showNewError(error.message);
      refs.newConfirm.disabled = false;
      return;
    }
    refs.newConfirm.disabled = false;
    refs.dialog.close();
    s.lang = code;
    await refresh(true);
    setStatus('locale.created', { code });
  }

  // ------------------------------------------------------------ public surface

  function reset() {
    clearTimeout(s.reloadTimer);
    clearTimeout(s.statusTimer);
    s.seq++;
    Object.assign(s, {
      modId: null, info: null, lang: null, table: null, conflict: false, saving: false, loading: false,
      error: null, status: null, filter: 'all', search: '',
    });
    s.byId = new Map();
    s.edits.clear();
    s.removals.clear();
    s.own.clear();
    rowRefs.clear();
    s.limits.clear();
    s.open.clear();
    refs.table.replaceChildren(refs.thead);
    refs.search.value = '';
    if (refs.dialog.open) refs.dialog.close();
    renderFilterOptions();
    renderSections();
    renderControls();
    renderStatus();
  }

  renderFilterOptions();
  renderSections();
  renderControls();

  return {
    element: root,

    /** Called every time the tab is drawn. Loads the mod's translations when it is a different mod than before. */
    show(modId, editable) {
      if (s.modId !== modId) {
        reset();
        s.modId = modId;
        s.editable = !!editable;
        s.loading = true;
        renderSections();
        refresh();
      } else if (s.editable !== !!editable) {
        s.editable = !!editable;
        renderAll();
      }
    },

    hasUnsaved: () => dirtyCount() > 0,
    reset,

    /** Locale files of the current mod changed on disk (or the watcher lost track): reload quietly unless that would lose edits. */
    onExternalChange() {
      if (!s.modId || s.saving) return;
      clearTimeout(s.reloadTimer);
      s.reloadTimer = setTimeout(() => { refresh(); }, RELOAD_DELAY_MS);
    },

    /** Interface language changed: redraw every translated text in place. */
    retranslate() {
      for (const [node, key] of labels) node.textContent = t(key);
      for (const [node, key] of placeholders) node.setAttribute('placeholder', t(key));
      renderAll();
    },
  };
}
