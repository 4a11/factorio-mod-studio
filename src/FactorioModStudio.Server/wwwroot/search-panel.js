// "Search" tab of the bottom panel: text / regex search across the files of the open mod (GET /api/mods/{id}/search).
// Results are drawn with DOM APIs only (textContent); the matched range is wrapped in <mark>. Everything that comes from the
// server (file names, line text, error messages) is untrusted text.

import { t } from '/i18n.js';

const DEBOUNCE_MS = 250;

/**
 * @param {{ api: Function, el: Function, getModId: () => string|null, openMatch: (path: string, line: number, column: number, length: number) => void }} host
 */
export function createSearchPanel({ api, el, getModId, openMatch }) {
  const s = {
    modId: null, controller: null, timer: 0, seq: 0,
    result: null, // last answer
    error: null, // server text, shown as received
    busy: false, outdated: false, collapsed: new Set(),
  };

  const input = el('input', {
    type: 'search', class: 'search-input', 'data-testid': 'search-input', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off',
  });
  const caseBtn = el('button', { type: 'button', class: 'toggle', 'data-testid': 'search-case', 'aria-pressed': 'false', text: 'Aa' });
  const regexBtn = el('button', { type: 'button', class: 'toggle', 'data-testid': 'search-regex', 'aria-pressed': 'false', text: '.*' });
  const summary = el('div', { class: 'search-summary small muted', 'data-testid': 'search-summary', role: 'status', 'aria-live': 'polite' });
  const hint = el('div', { class: 'search-outdated small', 'data-testid': 'search-outdated', role: 'status' });
  const errorBox = el('div', { class: 'search-error small', 'data-testid': 'search-error', role: 'alert' });
  const results = el('div', { class: 'search-results', 'data-testid': 'search-results' });
  const bar = el('div', { class: 'search-bar' }, input, caseBtn, regexBtn);
  const root = el('div', { class: 'search-panel' }, bar, summary, hint, errorBox, results);

  const pressed = (button) => button.getAttribute('aria-pressed') === 'true';
  function toggle(button) {
    button.setAttribute('aria-pressed', pressed(button) ? 'false' : 'true');
    run();
  }
  caseBtn.addEventListener('click', () => toggle(caseBtn));
  regexBtn.addEventListener('click', () => toggle(regexBtn));

  input.addEventListener('input', () => {
    clearTimeout(s.timer);
    if (input.value === '') { cancel(); clearResults(); return; }
    s.timer = setTimeout(run, DEBOUNCE_MS);
  });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); clearTimeout(s.timer); run(); }
  });

  function cancel() {
    clearTimeout(s.timer);
    s.controller?.abort();
    s.controller = null;
    s.busy = false;
    s.seq++;
  }

  function clearResults() {
    s.result = null;
    s.error = null;
    s.outdated = false;
    s.collapsed.clear();
    render();
  }

  async function run() {
    clearTimeout(s.timer);
    const query = input.value;
    const modId = getModId();
    if (!modId || query === '') { cancel(); clearResults(); return; }

    s.controller?.abort();
    const controller = new AbortController();
    s.controller = controller;
    const seq = ++s.seq;
    s.busy = true;
    s.outdated = false;
    render();
    try {
      const url = `/api/mods/${encodeURIComponent(modId)}/search?q=${encodeURIComponent(query)}&regex=${pressed(regexBtn) ? 1 : 0}&case=${pressed(caseBtn) ? 1 : 0}`;
      const answer = await api(url, { signal: controller.signal });
      if (seq !== s.seq || modId !== getModId()) return;
      s.result = answer;
      s.error = null;
      s.collapsed.clear();
    } catch (error) {
      if (seq !== s.seq || error?.name === 'AbortError') return;
      s.result = null;
      s.error = error?.message || String(error);
    }
    s.busy = false;
    render();
  }

  function matchNode(path, match) {
    const text = String(match.text ?? '');
    const from = Math.max(0, Math.min(text.length, (match.column ?? 1) - 1));
    const to = Math.max(from, Math.min(text.length, from + (match.length ?? 0)));
    const row = el('button', {
      type: 'button', class: 'search-match', 'data-testid': 'search-match',
      'data-path': path, 'data-line': String(match.line), 'data-column': String(match.column),
      onclick: () => openMatch(path, match.line, match.column, match.length),
    },
    el('span', { class: 'search-ln', text: String(match.line) }),
    el('span', { class: 'search-text' }, text.slice(0, from), to > from ? el('mark', { text: text.slice(from, to) }) : null, text.slice(to)));
    return row;
  }

  function render() {
    const answer = s.result;
    errorBox.textContent = s.error ?? '';
    errorBox.hidden = !s.error;
    hint.textContent = s.outdated ? t('ui.search.outdated') : '';
    hint.hidden = !s.outdated;

    if (!answer) {
      summary.textContent = s.busy ? t('ui.search.searching') : '';
      results.replaceChildren();
      return;
    }

    const files = Array.isArray(answer.files) ? answer.files : [];
    const parts = [t('ui.search.summary', {
      matches: t('ui.search.matches', { n: answer.totalMatches ?? 0 }),
      files: t('ui.search.inFiles', { n: files.length }),
    })];
    if (answer.truncated) parts.push(t('ui.search.truncated'));
    if (answer.skippedFiles > 0) parts.push(t('ui.search.skipped', { n: answer.skippedFiles }));
    summary.textContent = (files.length === 0 && !answer.truncated ? t('ui.search.none') : parts.join(' ')) + (s.busy ? ` ${t('ui.search.searching')}` : '');

    results.replaceChildren(...files.map((file) => {
      const group = el('details', { class: 'search-file', 'data-testid': 'search-file', 'data-path': file.path, open: !s.collapsed.has(file.path) },
        el('summary', {}, el('span', { class: 'mono search-path', text: file.path }), el('span', { class: 'muted small', text: ` (${file.matches.length})` })),
        el('div', { class: 'search-matches' }, file.matches.map((m) => matchNode(file.path, m))));
      group.addEventListener('toggle', () => { if (group.open) s.collapsed.delete(file.path); else s.collapsed.add(file.path); });
      return group;
    }));
  }

  function applyLabels() {
    input.setAttribute('placeholder', t('ui.search.placeholder'));
    input.setAttribute('aria-label', t('ui.search.label'));
    caseBtn.setAttribute('aria-label', t('ui.search.case'));
    caseBtn.title = t('ui.search.case');
    regexBtn.setAttribute('aria-label', t('ui.search.regex'));
    regexBtn.title = t('ui.search.regex');
  }
  applyLabels();
  render();

  return {
    element: root,
    focus() { input.focus(); input.select(); },
    /** Presets the query (for example the selected text) without searching. */
    setQuery(text) { input.value = text; },
    /** The mod changed: results are cleared, the query stays. */
    setMod(modId) {
      if (modId === s.modId) return;
      s.modId = modId;
      cancel();
      clearResults();
    },
    /** Files of the mod changed: keep the results but say they may be outdated. */
    markOutdated() {
      if (s.result && !s.outdated) { s.outdated = true; render(); }
    },
    run,
    retranslate() { applyLabels(); render(); },
  };
}
