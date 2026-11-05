// Editor tabs of the file viewer: every open file has its own tab and its own state (text, dirty flag, etag, caret and scroll
// position, native undo history, diagnostics, conflict banner). Text files of editable mods get a textarea with a highlight layer
// and a gutter; everything else is a read-only view, an image or an audio player.
//
// Everything that comes from a mod (file names, file content, validator messages) is only ever set with textContent / attributes.
// The DOM this module drives lives in index.html (#viewer, #editor-tabs, #viewer-body, ...); the host passes in what it needs.

import { tokenize } from '/lua-highlight.js';
import { t } from '/i18n.js';

export const EDITABLE_EXT = ['lua', 'cfg', 'json', 'txt', 'md'];
const IMAGE_EXT = ['png', 'jpg', 'jpeg', 'gif', 'bmp'];
const AUDIO_EXT = ['ogg', 'wav', 'mp3', 'flac'];
const HIGHLIGHT_LIMIT = 300 * 1024; // above this the editor shows plain text
const LINT_DELAY_MS = 400;
export const MAX_TABS = 20;
const READONLY_LINE_LIMIT = 8000;

const $ = (selector, root = document) => root.querySelector(selector);
const extensionOf = (path) => path.split('.').pop().toLowerCase();
const baseName = (path) => path.slice(path.lastIndexOf('/') + 1);

/** Spans for text[from, to) coloured by `tokens`; ranges in `marks` (sorted, disjoint) additionally get the problem underline.
 *  `st` carries the token/mark cursors so consecutive calls (one per line) stay linear. */
function highlightNodes(el, text, tokens, from, to, marks, st) {
  const out = [];
  let pos = from;
  while (pos < to) {
    while (st.ti < tokens.length && tokens[st.ti].to <= pos) st.ti++;
    while (st.mi < marks.length && marks[st.mi].to <= pos) st.mi++;
    const tok = tokens[st.ti];
    const mark = marks[st.mi];
    const type = tok && tok.from <= pos ? tok.type : 'plain';
    let end = Math.min(to, tok ? tok.to : to);
    let marked = false;
    if (mark) {
      if (mark.from <= pos) { marked = true; end = Math.min(end, mark.to); } else end = Math.min(end, mark.from);
    }
    const piece = text.slice(pos, end);
    if (type === 'plain' && !marked) out.push(document.createTextNode(piece));
    else out.push(el('span', { class: [type !== 'plain' && `tok-${type}`, marked && 'diag'].filter(Boolean).join(' '), text: piece }));
    pos = end;
  }
  return out;
}

/** Offset in `text` of the 1-based (line, column); clamped to the line. */
function offsetOf(text, line, column = 1) {
  let start = 0;
  for (let i = 1; i < line; i++) {
    const nl = text.indexOf('\n', start);
    if (nl < 0) return { start: text.length, end: text.length, at: text.length };
    start = nl + 1;
  }
  let end = text.indexOf('\n', start);
  if (end < 0) end = text.length;
  return { start, end, at: Math.min(start + Math.max(0, column - 1), end) };
}

/**
 * @param {{ api: Function, el: Function, getModId: () => string|null, isEditable: () => boolean,
 *           fileUrl: (path: string) => string, onChange?: () => void, onSaved?: () => void }} host
 */
export function createEditorTabs({ api, el, getModId, isEditable, fileUrl, onChange = () => {}, onSaved = () => {} }) {
  const viewer = $('#viewer');
  const body = $('#viewer-body');
  const strip = $('#editor-tabs');
  const titleEl = $('#viewer-title');
  const statusEl = $('#viewer-status');
  const bannerEl = $('#viewer-banner');
  const problemsBtn = $('#viewer-problems');
  const saveBtn = $('#viewer-save');
  const revertBtn = $('#viewer-revert');
  const closeBtn = $('#viewer-close');
  const backBtn = $('#viewer-back');
  let parked = false; // the viewer is hidden but its tabs are kept (the "Back" button on small screens)
  const app = $('.app');

  /** @type {Array<object>} open tabs in opening order */
  const tabs = [];
  let active = null;
  let clock = 0;
  let seq = 0;

  const isDirty = (tab) => !!tab && tab.kind === 'text' && !!tab.area && tab.area.value !== tab.original;
  const anyDirty = () => tabs.some(isDirty);

  // ------------------------------------------------------------ status line, banner, header

  function renderStatus() {
    const spec = active?.statusSpec;
    statusEl.textContent = !spec ? '' : spec.key ? t(spec.key, spec.params) : spec.text;
  }
  function setStatus(tab, key, params) {
    tab.statusSpec = key ? { key, params } : null;
    if (tab === active) renderStatus();
  }
  function setStatusText(tab, text) {
    tab.statusSpec = text ? { text } : null;
    if (tab === active) renderStatus();
  }

  function renderBanner() {
    const spec = active?.bannerSpec;
    bannerEl.hidden = !spec;
    if (!spec) { bannerEl.replaceChildren(); return; }
    bannerEl.replaceChildren(el('span', { text: t(spec.key, spec.params) }), ...(spec.conflict ? [
      el('button', { type: 'button', text: t('editor.loadDisk'), onclick: () => reloadFromDisk(active, true) }),
      el('button', { type: 'button', text: t('editor.overwrite'), onclick: () => overwriteWithMine(active) })] : []));
  }
  function setBanner(tab, key, params, conflict = false) {
    tab.bannerSpec = key ? { key, params, conflict } : null;
    if (tab === active) renderBanner();
  }

  function renderProblemsButton() {
    const count = active?.diags.length ?? 0;
    problemsBtn.hidden = count === 0;
    problemsBtn.textContent = t('count.problem', { n: count });
  }

  function renderHeader() {
    const text = active?.kind === 'text';
    saveBtn.hidden = !text;
    revertBtn.hidden = !text;
    const dirty = isDirty(active);
    saveBtn.disabled = !dirty;
    revertBtn.disabled = !dirty;
    titleEl.textContent = active ? active.titleText : '';
    renderProblemsButton();
    renderStatus();
    renderBanner();
  }

  // ------------------------------------------------------------ tab strip

  const tabLabel = (tab) => {
    const name = baseName(tab.path);
    if (tabs.some((other) => other !== tab && baseName(other.path) === name)) {
      const parts = tab.path.split('/');
      return parts.slice(-2).join('/');
    }
    return name;
  };

  function updateTabEl(tab) {
    const node = tab.tabEl;
    node.querySelector('.etab-name').textContent = tabLabel(tab);
    node.setAttribute('aria-selected', tab === active ? 'true' : 'false');
    node.tabIndex = tab === active ? 0 : -1;
    node.dataset.dirty = isDirty(tab) ? 'true' : 'false';
    if (tab.missing) node.dataset.missing = 'true'; else delete node.dataset.missing;
    node.setAttribute('aria-label', isDirty(tab) ? t('ui.tabs.tabDirty', { path: tab.path }) : tab.path);
    node.querySelector('.etab-close').setAttribute('aria-label', t('ui.tabs.close', { path: tab.path }));
  }
  const updateAllTabEls = () => tabs.forEach(updateTabEl);

  function moveFocus(tab, delta) {
    const index = tabs.indexOf(tab);
    const next = tabs[(index + delta + tabs.length) % tabs.length];
    if (next) { activate(next); next.tabEl.focus(); }
  }

  function makeTabEl(tab) {
    const node = el('div', {
      class: 'etab', role: 'tab', 'data-testid': 'editor-tab', 'data-path': tab.path, title: tab.path, tabindex: '-1', 'aria-selected': 'false',
    },
    el('span', { class: 'etab-name' }),
    el('span', { class: 'etab-dot', 'aria-hidden': 'true', text: '●' }),
    el('button', { type: 'button', class: 'etab-close', 'data-testid': 'editor-tab-close', tabindex: '-1', text: '×' }));
    node.addEventListener('click', (e) => {
      if (e.target.closest('.etab-close')) { e.stopPropagation(); closeTab(tab); return; }
      activate(tab);
    });
    node.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); });
    node.addEventListener('auxclick', (e) => { if (e.button === 1) { e.preventDefault(); closeTab(tab); } });
    node.addEventListener('keydown', (e) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === 'ArrowRight') { e.preventDefault(); moveFocus(tab, 1); }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); moveFocus(tab, -1); }
      else if (e.key === 'Home') { e.preventDefault(); activate(tabs[0]); tabs[0].tabEl.focus(); }
      else if (e.key === 'End') { e.preventDefault(); const last = tabs[tabs.length - 1]; activate(last); last.tabEl.focus(); }
      else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(tab, true); }
      else if (e.key === 'Delete') { e.preventDefault(); closeTab(tab); }
    });
    return node;
  }

  // ------------------------------------------------------------ activation and closing

  function saveView(tab) {
    if (tab?.area) tab.view = { top: tab.area.scrollTop, left: tab.area.scrollLeft };
    else if (tab?.pane) tab.view = { top: tab.pane.scrollTop, left: tab.pane.scrollLeft };
  }

  function restoreView(tab) {
    if (!tab.view) return;
    const { top, left } = tab.view;
    requestAnimationFrame(() => {
      if (tab.area) {
        tab.area.scrollTop = top;
        tab.area.scrollLeft = left;
        tab.gutter.scrollTop = top;
        tab.hl.scrollTop = top;
        tab.hl.scrollLeft = left;
      } else if (tab.pane) {
        tab.pane.scrollTop = top;
        tab.pane.scrollLeft = left;
      }
    });
  }

  function activate(tab, focusEditor = false) {
    if (!tab || !tabs.includes(tab)) return;
    if (active && active !== tab) {
      saveView(active);
      if (active.lintScheduled) { clearTimeout(active.lintTimer); active.lintScheduled = false; active.lintPending = true; } // checked again when it is shown
    }
    const changed = active !== tab;
    active = tab;
    tab.lastActive = ++clock;
    for (const other of tabs) other.pane.hidden = other !== tab;
    updateAllTabEls();
    renderHeader();
    if (changed) restoreView(tab);
    if (tab.lintPending) { tab.lintPending = false; scheduleLint(tab, 0); }
    tab.tabEl.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    if (focusEditor && tab.area) tab.area.focus();
    onChange();
  }

  function showViewer() {
    parked = false;
    viewer.hidden = false;
    app.classList.add('with-viewer');
  }

  function hideViewer() {
    parked = false;
    viewer.hidden = true;
    app.classList.remove('with-viewer');
    active = null;
    renderHeader();
  }

  function disposeTab(tab) {
    tab.closed = true;
    clearTimeout(tab.lintTimer);
    tab.lintAbort?.abort();
    if (tab.hlFrame) cancelAnimationFrame(tab.hlFrame);
    tab.pane.querySelector('audio')?.pause();
    tab.pane.remove();
    tab.tabEl.remove();
    const index = tabs.indexOf(tab);
    if (index >= 0) tabs.splice(index, 1);
  }

  /** Closes one tab; a tab with unsaved changes asks first (unless `force`). Returns whether it was closed. */
  function closeTab(tab, force = false) {
    if (!tab || !tabs.includes(tab)) return true;
    if (!force && isDirty(tab) && !confirm(t('editor.discardConfirm'))) return false;
    const wasActive = tab === active;
    disposeTab(tab);
    if (wasActive) {
      active = null;
      const next = tabs.slice().sort((a, b) => b.lastActive - a.lastActive)[0];
      if (next) activate(next); else hideViewer();
    }
    updateAllTabEls();
    onChange();
    return true;
  }

  /** Hides the viewer without closing anything; `reveal` brings it back with all tabs. */
  function park() {
    if (viewer.hidden || tabs.length === 0) return;
    viewer.hidden = true;
    app.classList.remove('with-viewer');
    parked = true;
    onChange();
  }

  function reveal() {
    if (!parked || tabs.length === 0) return;
    showViewer();
    if (active) restoreView(active);
    onChange();
  }

  /** Closes every tab and hides the viewer. One confirmation covers all unsaved tabs. Returns false when the user chose to stay. */
  function closeViewer(force = false) {
    if (!force && anyDirty() && !confirm(t('editor.discardConfirm'))) return false;
    for (const tab of tabs.slice()) disposeTab(tab);
    hideViewer();
    onChange();
    return true;
  }

  // ------------------------------------------------------------ editor internals (per tab)

  function syncGutter(tab) {
    const count = tab.area.value.split('\n').length;
    const marked = [...tab.diagLines].filter((l) => l <= count).sort((a, b) => a - b);
    const key = `${count}|${marked.join(',')}`;
    if (tab.gutter.dataset.key !== key) {
      tab.gutter.dataset.key = key;
      if (!marked.length) {
        tab.gutter.textContent = Array.from({ length: count }, (_, i) => i + 1).join('\n');
      } else {
        // Plain runs of numbers stay single text nodes; only lines with a problem get their own span.
        const run = (from, to) => Array.from({ length: to - from + 1 }, (_, i) => from + i).join('\n');
        const nodes = [];
        let next = 1;
        for (const line of marked) {
          if (line > next) nodes.push(document.createTextNode(run(next, line - 1) + '\n'));
          nodes.push(el('span', { class: 'gl-err', text: line }));
          nodes.push(document.createTextNode(line < count ? '\n' : ''));
          next = line + 1;
        }
        if (next <= count) nodes.push(document.createTextNode(run(next, count)));
        tab.gutter.replaceChildren(...nodes);
      }
    }
    tab.gutter.scrollTop = tab.area.scrollTop;
  }

  /** Underline ranges for the current diagnostics: from the reported column to the end of that word/token on the line. */
  function diagnosticMarks(tab, text, tokens) {
    if (!tab.diags.length) return [];
    const starts = [0];
    for (let i = text.indexOf('\n'); i >= 0; i = text.indexOf('\n', i + 1)) starts.push(i + 1);

    const marks = [];
    for (const d of tab.diags) {
      if (d.line < 1 || d.line > starts.length) continue;
      const lineStart = starts[d.line - 1];
      let lineEnd = d.line < starts.length ? starts[d.line] - 1 : text.length;
      if (lineEnd > lineStart && text[lineEnd - 1] === '\r') lineEnd--;
      let from = Math.min(lineStart + Math.max(0, d.column - 1), lineEnd);
      if (from >= lineEnd) from = Math.max(lineStart, lineEnd - 1);
      if (from >= lineEnd) continue; // empty line: nothing to underline

      let lo = 0;
      let hi = tokens.length - 1;
      while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (tokens[mid].from <= from) lo = mid; else hi = mid - 1; }
      const tok = tokens[lo];
      let to = from + 1;
      if (tok && tok.type !== 'plain') to = tok.to;
      else { const word = /^\w+/.exec(text.slice(from, Math.min(lineEnd, from + 200))); if (word) to = from + word[0].length; }
      marks.push({ from, to: Math.min(Math.max(to, from + 1), lineEnd) });
    }

    marks.sort((a, b) => a.from - b.from);
    const merged = [];
    for (const m of marks) {
      const last = merged[merged.length - 1];
      if (last && m.from <= last.to) last.to = Math.max(last.to, m.to); else merged.push({ ...m });
    }
    return merged;
  }

  function renderHighlight(tab) {
    tab.hlFrame = 0;
    const { area, hl, wrap } = tab;
    if (!area || !hl || tab.closed) return;
    const text = area.value;
    const plain = !tab.lua || text.length > HIGHLIGHT_LIMIT;
    wrap.classList.toggle('plain', plain);
    if (plain) { hl.replaceChildren(); return; }

    const tokens = tokenize(text);
    const marks = diagnosticMarks(tab, text, tokens);
    const nodes = highlightNodes(el, text, tokens, 0, text.length, marks, { ti: 0, mi: 0 });
    nodes.push(document.createTextNode('\n')); // a trailing newline must still produce a last (empty) line, as in the textarea
    hl.replaceChildren(...nodes);
    hl.scrollTop = area.scrollTop;
    hl.scrollLeft = area.scrollLeft;
  }

  function scheduleHighlight(tab) {
    if (!tab.hlFrame) tab.hlFrame = requestAnimationFrame(() => renderHighlight(tab));
  }

  function scheduleLint(tab, delay = LINT_DELAY_MS) {
    clearTimeout(tab.lintTimer);
    if (!tab.area || !tab.lua) return;
    tab.lintScheduled = true;
    tab.lintTimer = setTimeout(() => { tab.lintScheduled = false; runLint(tab); }, delay);
  }

  async function runLint(tab) {
    const { area, modId, path } = tab;
    if (!area || !tab.lua || tab.closed) return;
    if (tab !== active) { tab.lintPending = true; return; } // a hidden tab is checked when it becomes active again
    const seqNo = ++tab.lintSeq;
    tab.lintAbort?.abort();
    const controller = new AbortController();
    tab.lintAbort = controller;
    try {
      const result = await api(`/api/lint?id=${encodeURIComponent(modId)}&path=${encodeURIComponent(path)}`, {
        method: 'POST', headers: { 'Content-Type': 'text/plain; charset=utf-8' }, body: area.value, signal: controller.signal,
      });
      if (seqNo !== tab.lintSeq || tab.area !== area || tab.closed) return; // stale answer
      setDiagnostics(tab, Array.isArray(result?.diagnostics) ? result.diagnostics : []);
    } catch { /* linting is a convenience: network errors, aborts and 403/413 stay silent */ }
  }

  function setDiagnostics(tab, diags) {
    tab.diags = diags;
    tab.diagLines = new Set(diags.map((d) => d.line));
    tab.diagCursor = -1;
    syncGutter(tab);
    scheduleHighlight(tab);
    if (tab === active) renderProblemsButton();
    updateEditorState(tab);
  }

  function caretLine(tab) {
    const { area } = tab;
    const caret = area.selectionStart;
    let line = 1;
    for (let i = area.value.indexOf('\n'); i >= 0 && i < caret; i = area.value.indexOf('\n', i + 1)) line++;
    return line;
  }

  function caretDiagnostic(tab) {
    if (!tab.diags.length) return '';
    const line = caretLine(tab);
    const d = tab.diags.find((x) => x.line === line);
    return d ? `${d.code}: ${d.message}` : '';
  }

  function updateEditorState(tab) {
    if (tab.kind !== 'text' || !tab.area) return;
    const dirty = isDirty(tab);
    if (tab === active) {
      saveBtn.disabled = !dirty;
      revertBtn.disabled = !dirty;
    }
    if (tab.wasDirty !== dirty) { tab.wasDirty = dirty; updateTabEl(tab); onChange(); }
    if (tab.conflict) return;
    const diagnostic = caretDiagnostic(tab);
    if (diagnostic) setStatusText(tab, diagnostic); // validator text: shown as received
    else setStatus(tab, dirty ? 'editor.unsaved' : 'editor.saved');
  }

  function afterTextChange(tab) {
    syncGutter(tab);
    scheduleHighlight(tab);
    scheduleLint(tab);
    updateEditorState(tab);
  }

  let charWidth = 0;
  function measureCharWidth(area) {
    if (charWidth) return charWidth;
    const context = document.createElement('canvas').getContext('2d');
    const style = getComputedStyle(area);
    context.font = `${style.fontSize} ${style.fontFamily}`;
    charWidth = context.measureText('M').width || 8;
    return charWidth;
  }

  /** Puts the caret / selection at (line, column[, length]) and scrolls it into view. The tab must be visible. */
  function placeCaret(tab, line, column, length) {
    const { area } = tab;
    if (!area || !line) return;
    const { start, end, at } = offsetOf(area.value, line, column || 1);
    area.focus();
    if (column) area.setSelectionRange(at, length ? Math.min(at + length, end) : at);
    else area.setSelectionRange(start, end);
    const lineHeight = parseFloat(getComputedStyle(area).lineHeight) || 18;
    const top = (line - 1) * lineHeight;
    if (top < area.scrollTop || top > area.scrollTop + area.clientHeight - lineHeight * 2) area.scrollTop = Math.max(0, top - area.clientHeight / 3);
    if (column) {
      const left = (column - 1) * measureCharWidth(area);
      if (left < area.scrollLeft || left > area.scrollLeft + area.clientWidth - 48) area.scrollLeft = Math.max(0, left - area.clientWidth / 2);
    }
    tab.gutter.scrollTop = area.scrollTop;
    tab.hl.scrollTop = area.scrollTop;
    tab.hl.scrollLeft = area.scrollLeft;
    updateEditorState(tab);
  }

  function jump(tab, line, column, length) {
    if (!line) return;
    if (tab.kind === 'text') {
      activate(tab);
      requestAnimationFrame(() => placeCaret(tab, line, column, length));
    } else if (tab.kind === 'readonly') {
      activate(tab);
      markReadonlyLine(tab, line);
    } else {
      activate(tab);
    }
  }

  function markReadonlyLine(tab, line) {
    for (const row of tab.pane.querySelectorAll('.ln.hl')) row.classList.remove('hl');
    const row = tab.pane.querySelector(`.ln[data-line="${line}"]`);
    if (row) { row.classList.add('hl'); row.scrollIntoView({ block: 'center' }); }
  }

  function goToDiagnostic(tab, index) {
    const d = tab.diags[index];
    if (!d || !tab.area) return;
    placeCaret(tab, d.line, d.column);
  }

  function buildEditor(tab, text) {
    const gutter = el('pre', { class: 'gutter', 'aria-hidden': 'true' });
    const area = el('textarea', {
      class: 'editor', wrap: 'off', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', 'aria-label': t('editor.ariaLabel', { path: tab.path }),
    });
    area.value = text;
    // Highlight layer: a <pre> under the transparent textarea with identical metrics. Only .lua files get colours.
    const hl = el('pre', { class: 'hl', 'aria-hidden': 'true' });
    const wrap = el('div', { class: 'editor-wrap' }, gutter, el('div', { class: 'editor-stack' }, hl, area));
    Object.assign(tab, { area, gutter, hl, wrap, lua: /\.lua$/i.test(tab.path), diags: [], diagLines: new Set(), diagCursor: -1, wasDirty: false });
    renderHighlight(tab);

    area.addEventListener('input', () => afterTextChange(tab));
    area.addEventListener('scroll', () => {
      gutter.scrollTop = area.scrollTop;
      hl.scrollTop = area.scrollTop;
      hl.scrollLeft = area.scrollLeft;
    });
    area.addEventListener('keydown', (e) => {
      if (e.key === 'Tab' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault(); // keep focus in the editor; insert two spaces (Lua convention in Factorio mods)
        area.setRangeText('  ', area.selectionStart, area.selectionEnd, 'end');
        area.dispatchEvent(new Event('input'));
      }
    });
    scheduleLint(tab, 0);
    return wrap;
  }

  // ------------------------------------------------------------ opening a file

  /** A message inside a tab; remembers its key so a language switch can retranslate it in place. */
  function viewerMsg(key, params = {}) {
    return el('div', { class: 'viewer-msg', 'data-tk': key, 'data-tp': JSON.stringify(params), text: t(key, params) });
  }

  function newTab(path, line) {
    const tab = {
      id: ++seq, path, modId: getModId(), kind: 'loading', etag: null, original: '', conflict: false, missing: false, closed: false,
      area: null, gutter: null, hl: null, wrap: null, lua: false, diags: [], diagLines: new Set(), diagCursor: -1,
      lintTimer: 0, lintSeq: 0, lintAbort: null, hlFrame: 0, statusSpec: null, bannerSpec: null, wasDirty: false,
      titleText: line ? `${path}:${line}` : path, lastActive: 0, view: null, pending: null,
      pane: el('div', { class: 'editor-pane', role: 'tabpanel', 'data-testid': 'editor-pane', 'data-path': path, hidden: true }),
    };
    tab.tabEl = makeTabEl(tab);
    return tab;
  }

  /** Makes room for one more tab: closes the least recently used clean tab. Returns false when every tab has unsaved changes. */
  function makeRoom() {
    if (tabs.length < MAX_TABS) return true;
    const victim = tabs.filter((tab) => !isDirty(tab)).sort((a, b) => a.lastActive - b.lastActive)[0];
    if (!victim) return false;
    closeTab(victim, true);
    return true;
  }

  async function load(tab, { line, column, length, quiet = false } = {}) {
    const { path, modId } = tab;
    const extension = extensionOf(path);
    const url = fileUrl(path);
    const stale = () => tab.closed || modId !== getModId();
    tab.pane.replaceChildren(viewerMsg('viewer.loading'));

    if (IMAGE_EXT.includes(extension)) {
      tab.kind = 'image';
      tab.pane.replaceChildren(el('div', { class: 'viewer-image' }, el('img', { alt: path, src: `${url}&v=${Date.now()}` })));
      return true;
    }
    if (AUDIO_EXT.includes(extension)) {
      tab.kind = 'audio';
      tab.pane.replaceChildren(el('div', { class: 'viewer-msg' }, el('audio', { controls: true, src: url })));
      return true;
    }

    try {
      const response = await fetch(url);
      if (!response.ok) {
        const error = new Error((await response.json().catch(() => ({}))).error || response.statusText);
        error.status = response.status;
        throw error;
      }
      if (stale()) return false;
      const text = await response.text();
      if (stale()) return false;
      const etag = response.headers.get('ETag');

      if (isEditable() && EDITABLE_EXT.includes(extension)) {
        tab.kind = 'text';
        Object.assign(tab, { etag, original: text, conflict: false });
        tab.pane.replaceChildren(buildEditor(tab, text));
        updateEditorState(tab);
        if (tab === active) renderHeader();
        const jumpTo = tab.pending || (line ? { line, column, length } : null);
        tab.pending = null;
        if (jumpTo) requestAnimationFrame(() => { syncGutter(tab); if (tab === active) placeCaret(tab, jumpTo.line, jumpTo.column, jumpTo.length); });
        else requestAnimationFrame(() => syncGutter(tab));
        return true;
      }

      const lines = text.split(/\r?\n/);
      // Lua files get the same colours as in the editor; tokens are computed once and sliced per line.
      const colour = extension === 'lua' && text.length <= HIGHLIGHT_LIMIT;
      const tokens = colour ? tokenize(text) : [];
      const cursor = { ti: 0, mi: 0 };
      let offset = 0;
      const rows = [];
      for (let index = 0; index < Math.min(lines.length, READONLY_LINE_LIMIT); index++) {
        const content = lines[index];
        const cells = !content ? [' '] : colour ? highlightNodes(el, text, tokens, offset, offset + content.length, [], cursor) : [content];
        rows.push(el('div', { class: 'ln', 'data-line': index + 1 },
          el('span', { class: 'no', text: index + 1 }), el('span', { class: 'tx' }, cells)));
        offset += content.length + (text.startsWith('\r\n', offset + content.length) ? 2 : 1);
      }
      const code = el('div', { class: 'code' }, rows);
      tab.kind = 'readonly';
      tab.etag = etag;
      tab.original = text;
      tab.pane.replaceChildren(...[code, lines.length > READONLY_LINE_LIMIT ? viewerMsg('viewer.truncated', { shown: READONLY_LINE_LIMIT, total: lines.length }) : null].filter(Boolean));
      setStatus(tab, 'editor.readOnly');
      const jumpTo = tab.pending || (line ? { line } : null);
      tab.pending = null;
      if (jumpTo && tab === active) markReadonlyLine(tab, jumpTo.line);
      else if (jumpTo) tab.pendingMark = jumpTo.line;
      return true;
    } catch (error) {
      if (stale()) return false;
      if (quiet) return false;
      tab.kind = 'error';
      tab.pane.replaceChildren(viewerMsg('viewer.cannotShow', { message: error.message }));
      return true;
    }
  }

  /**
   * Opens `path` in a tab (or activates its tab) and optionally puts the caret at line/column, selecting `length` characters.
   * With `quiet` a file that cannot be loaded leaves no tab behind (used when restoring remembered tabs).
   * @returns {Promise<boolean>} whether the file is open afterwards
   */
  async function openFile(path, line, column, length, { quiet = false, activateTab = true } = {}) {
    if (typeof path !== 'string' || !path) return false;
    const existing = tabs.find((tab) => tab.path === path && tab.modId === getModId());
    if (existing) {
      if (activateTab) { if (parked) reveal(); activate(existing); }
      if (line) {
        if (existing.kind === 'loading') existing.pending = { line, column, length };
        else jump(existing, line, column, length);
      }
      return true;
    }
    if (!makeRoom()) {
      if (active) setStatus(active, 'ui.tabs.limit', { max: MAX_TABS });
      return false;
    }

    const tab = newTab(path, line);
    tabs.push(tab);
    strip.append(tab.tabEl);
    body.append(tab.pane);
    showViewer();
    if (activateTab || !active) activate(tab); else updateAllTabEls();
    onChange();

    const ok = await load(tab, { line, column, length, quiet });
    if (!ok && !tab.closed && quiet) { closeTab(tab, true); return false; }
    if (tab.closed) return false;
    if (tab === active) renderHeader();
    if (tab.pendingMark && tab === active) { markReadonlyLine(tab, tab.pendingMark); tab.pendingMark = 0; }
    onChange();
    return true;
  }

  // ------------------------------------------------------------ saving and external changes

  async function save(tab = active) {
    if (!tab || tab.kind !== 'text' || !isDirty(tab)) return;
    const { modId, path } = tab;
    const content = tab.area.value;
    setStatus(tab, 'editor.saving');
    try {
      const result = await api(`/api/mods/${encodeURIComponent(modId)}/file?path=${encodeURIComponent(path)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'text/plain; charset=utf-8', ...(tab.etag ? { 'If-Match': tab.etag } : { 'If-None-Match': '*' }) },
        body: content,
      });
      if (tab.closed) return;
      tab.etag = result.etag;
      tab.original = content;
      tab.conflict = false;
      tab.missing = false;
      setBanner(tab, null);
      updateEditorState(tab);
      updateTabEl(tab);
      onSaved();
    } catch (error) {
      if (tab.closed) return;
      if (error.status === 409) {
        tab.conflict = true;
        tab.etag = error.body.etag || tab.etag;
        setBanner(tab, 'editor.conflict', {}, true);
        setStatus(tab, 'editor.notSaved');
      } else {
        setStatus(tab, 'editor.notSaved');
        setBanner(tab, 'editor.saveFailed', { message: error.message });
      }
    }
  }

  async function overwriteWithMine(tab) {
    // Fetch the current validator, then save over it deliberately.
    try {
      const response = await fetch(fileUrl(tab.path));
      if (response.ok) tab.etag = response.headers.get('ETag');
      else if (response.status === 404) tab.etag = null;
    } catch { /* save() reports problems */ }
    tab.conflict = false;
    setBanner(tab, null);
    await save(tab);
  }

  async function reloadFromDisk(tab, force = false) {
    if (!tab || tab.closed || tab.modId !== getModId()) return;
    const { path } = tab;
    try {
      const response = await fetch(fileUrl(path));
      if (tab.closed) return;
      if (response.status === 404) {
        tab.missing = true;
        updateTabEl(tab);
        setBanner(tab, 'editor.deleted');
        return;
      }
      if (!response.ok) return;
      const etag = response.headers.get('ETag');
      if (!force && etag === tab.etag && !tab.missing) return; // our own save, or nothing really changed
      if (tab.kind === 'loading') return; // the first load is still running and will see the current file

      if (tab.kind !== 'text') { // read-only view, image, audio or an error: just draw it again
        tab.missing = false;
        updateTabEl(tab);
        const keep = tab.view;
        await load(tab, {});
        tab.view = keep;
        if (tab === active) renderHeader();
        return;
      }
      if (!force && isDirty(tab)) {
        tab.conflict = true;
        setBanner(tab, 'editor.conflict', {}, true);
        setStatus(tab, 'editor.changedOnDisk');
        return;
      }
      const text = await response.text();
      if (tab.closed) return;
      const top = tab.area.scrollTop;
      tab.area.value = text;
      tab.original = text;
      tab.etag = etag;
      tab.conflict = false;
      tab.missing = false;
      updateTabEl(tab);
      setBanner(tab, null);
      afterTextChange(tab);
      tab.area.scrollTop = top;
      if (!force) setStatus(tab, 'editor.reloaded');
    } catch { /* transient; the next event retries */ }
  }

  async function revert() {
    const tab = active;
    if (!tab || !isDirty(tab) || !confirm(t('editor.revertConfirm'))) return;
    tab.area.value = tab.original;
    tab.conflict = false;
    setBanner(tab, null);
    afterTextChange(tab);
  }

  // ------------------------------------------------------------ events

  closeBtn.addEventListener('click', () => closeViewer());
  backBtn.addEventListener('click', () => park());
  saveBtn.addEventListener('click', () => save());
  revertBtn.addEventListener('click', revert);
  problemsBtn.addEventListener('click', () => {
    const tab = active;
    if (!tab?.diags.length) return;
    tab.diagCursor = (tab.diagCursor + 1) % tab.diags.length;
    goToDiagnostic(tab, tab.diagCursor);
  });

  const modalOpen = () => !!document.querySelector('dialog[open]') || !!document.querySelector('[data-modal-open="true"]');

  function cycle(delta) {
    if (tabs.length < 2 || !active) return;
    const next = tabs[(tabs.indexOf(active) + delta + tabs.length) % tabs.length];
    activate(next, !!next.area);
    if (!next.area) next.tabEl.focus();
  }

  document.addEventListener('keydown', (e) => {
    if (e.defaultPrevented) return;
    const mod = e.ctrlKey || e.metaKey;
    const key = e.key;
    if (key === 'Escape' && !viewer.hidden && active && !modalOpen()) { closeTab(active); return; }
    if (!mod || e.altKey) return;
    if (key.toLowerCase() === 's' && !e.shiftKey && active?.kind === 'text') { e.preventDefault(); save(); return; }
    if (viewer.hidden || !active || modalOpen()) return;
    if (key.toLowerCase() === 'w' && !e.shiftKey) { e.preventDefault(); closeTab(active); }
    else if (key === 'Tab') { e.preventDefault(); cycle(e.shiftKey ? -1 : 1); }
    else if (key === 'PageDown') { e.preventDefault(); cycle(1); }
    else if (key === 'PageUp') { e.preventDefault(); cycle(-1); }
  });

  document.addEventListener('selectionchange', () => {
    if (active?.area && document.activeElement === active.area && active.diags.length) updateEditorState(active);
  });

  window.addEventListener('beforeunload', (e) => { if (anyDirty()) { e.preventDefault(); e.returnValue = ''; } });

  // ------------------------------------------------------------ public API

  return {
    openFile,
    closeViewer,
    closeActive: () => closeTab(active),
    closeTab: (path) => closeTab(tabs.find((tab) => tab.path === path)),
    closeAll: () => closeViewer(),
    save: () => save(active),
    activatePath: (path) => activate(tabs.find((tab) => tab.path === path)),
    hasDirty: anyDirty,
    count: () => tabs.length,
    paths: () => tabs.map((tab) => tab.path),
    activePath: () => active?.path ?? null,
    activeKind: () => active?.kind ?? null,
    canSave: () => active?.kind === 'text',
    isOpen: () => !viewer.hidden && tabs.length > 0,
    park,
    reveal,
    isParked: () => parked,
    focusActive: () => { if (active?.area) active.area.focus(); },
    /** Paths of the current mod's tabs with unsaved changes. */
    dirtyPaths: () => tabs.filter(isDirty).map((tab) => tab.path),

    /** A file or folder of the current mod was renamed / moved: tabs under `from` keep their state and follow it to `to`. */
    renamePath(from, to) {
      let changed = false;
      for (const tab of tabs) {
        if (tab.modId !== getModId()) continue;
        const next = tab.path === from ? to : tab.path.startsWith(`${from}/`) ? to + tab.path.slice(from.length) : null;
        if (next === null) continue;
        tab.path = next;
        tab.titleText = next;
        tab.pane.dataset.path = next;
        tab.tabEl.dataset.path = next;
        tab.tabEl.setAttribute('title', next);
        tab.area?.setAttribute('aria-label', t('editor.ariaLabel', { path: next }));
        if (tab.area) { tab.lua = extensionOf(next) === 'lua'; scheduleHighlight(tab); }
        changed = true;
      }
      if (!changed) return;
      updateAllTabEls();
      renderHeader();
      onChange();
    },

    /** Closes the tabs of `path` and everything under it (a deleted file or folder) without asking; the caller confirms. */
    closePaths(path) {
      for (const tab of tabs.slice()) if (tab.path === path || tab.path.startsWith(`${path}/`)) closeTab(tab, true);
    },

    /** A file of the current mod changed on disk (change feed). */
    onFileChanged(modId, path) {
      if (modId !== getModId()) return;
      const tab = tabs.find((x) => x.path === path);
      if (tab) reloadFromDisk(tab);
    },

    /** The change feed lost events: look at every open file again. */
    onResync() {
      for (const tab of tabs.slice()) reloadFromDisk(tab);
    },

    /** The interface language changed: translated texts are redrawn in place; editors are never recreated. */
    retranslate() {
      updateAllTabEls();
      renderStatus();
      renderBanner();
      renderProblemsButton();
      for (const tab of tabs) {
        tab.area?.setAttribute('aria-label', t('editor.ariaLabel', { path: tab.path }));
        for (const node of tab.pane.querySelectorAll('[data-tk]')) {
          let params = {};
          try { params = JSON.parse(node.dataset.tp || '{}'); } catch { /* keep empty params */ }
          node.textContent = t(node.dataset.tk, params);
        }
      }
    },
  };
}
