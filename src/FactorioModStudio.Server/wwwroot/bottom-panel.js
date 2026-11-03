// Collapsible bottom panel with three tabs (Problems, Output, Search) and the thin status strip with its toggle button.
//
//  * Problems: the problems of the open mod (same data as the Problems tab), errors first.
//  * Output: live log of the latest job of the open mod. It follows the build view (which owns the jobs API polling); a finished
//    job that the build view does not know (after a reload) is fetched once.
//  * Search: see search-panel.js.
// Everything shown comes from the server or from a mod and is only ever set with textContent / attributes.

import { t, getLanguage } from '/i18n.js';
import { cleanLine } from '/build-view.js';

export const PANEL_TABS = ['problems', 'output', 'search'];
export const MIN_HEIGHT = 120;
export const DEFAULT_HEIGHT = 260;
const STEP = 24;
const SEVERITY_ORDER = { error: 0, warning: 1, info: 2 };

const clampHeight = (px) => Math.max(MIN_HEIGHT, Math.min(Math.floor(window.innerHeight * 0.7), Math.round(px)));

export function formatElapsed(ms) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(total / 60);
  const sec = String(total % 60).padStart(2, '0');
  return m >= 60 ? `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
}

export const sortIssues = (issues) => (Array.isArray(issues) ? issues : [])
  .map((issue, index) => ({ issue, index }))
  .sort((a, b) => ((SEVERITY_ORDER[a.issue.severity] ?? 3) - (SEVERITY_ORDER[b.issue.severity] ?? 3)) || (a.index - b.index))
  .map((x) => x.issue);

/**
 * @param {{ el: Function, api: Function, uiState: object, buildView: object, search: object, mount: HTMLElement, statusbar: HTMLElement,
 *           openLocation: (path: string, line?: number) => void, shortcutText: (keys: string) => string }} host
 */
export function createBottomPanel({ el, api, uiState, buildView, search, mount, statusbar, openLocation, shortcutText }) {
  const saved = uiState.get().bottom;
  const s = {
    open: saved.open === true,
    tab: PANEL_TABS.includes(saved.tab) ? saved.tab : 'problems',
    height: saved.height ? clampHeight(saved.height) : DEFAULT_HEIGHT,
    issues: [],
    hasMod: false,
    // output
    outputId: null, renderedId: null, renderedCount: 0, stick: true, fetched: new Map(), fetchSeq: 0, tick: 0,
  };

  // ------------------------------------------------------------ structure

  const resize = el('div', {
    class: 'bottom-resize', 'data-testid': 'bottom-resize', role: 'separator', 'aria-orientation': 'horizontal', tabindex: '0',
    'aria-valuemin': String(MIN_HEIGHT),
  });

  const tabButtons = {};
  const tabLabel = (name) => t(`ui.bottom.${name}`);
  for (const name of PANEL_TABS) {
    tabButtons[name] = el('button', {
      type: 'button', class: 'ptab', role: 'tab', id: `bottom-tab-${name}`, 'data-testid': `bottom-tab-${name}`, 'aria-controls': `bottom-view-${name}`,
      onclick: () => setTab(name),
      onkeydown: (e) => {
        const index = PANEL_TABS.indexOf(name);
        const move = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
        if (!move) return;
        e.preventDefault();
        const next = PANEL_TABS[(index + move + PANEL_TABS.length) % PANEL_TABS.length];
        setTab(next);
        tabButtons[next].focus();
      },
    });
  }
  const closeBtn = el('button', { type: 'button', class: 'panel-close', 'data-testid': 'bottom-close', onclick: () => close(), text: '×' });
  const tablist = el('div', { class: 'ptabs', role: 'tablist' }, ...PANEL_TABS.map((n) => tabButtons[n]));
  const header = el('div', { class: 'bottom-header' }, tablist, closeBtn);

  // Problems
  const problemList = el('ul', { class: 'problem-list', 'data-testid': 'problem-list' });
  const problemsEmpty = el('p', { class: 'muted bottom-empty', 'data-testid': 'problems-empty' });
  const problemsView = el('div', { class: 'bottom-view', id: 'bottom-view-problems', role: 'tabpanel', 'aria-labelledby': 'bottom-tab-problems' }, problemsEmpty, problemList);

  // Output
  const outputTitle = el('div', { class: 'output-title', 'data-testid': 'output-title', role: 'status' });
  const outputEmpty = el('p', { class: 'muted bottom-empty', 'data-testid': 'output-empty' });
  const logText = document.createTextNode('');
  const outputLog = el('pre', { class: 'output-log', 'data-testid': 'output-log', tabindex: '0', role: 'log' }, logText);
  outputLog.addEventListener('scroll', () => { s.stick = outputLog.scrollTop + outputLog.clientHeight >= outputLog.scrollHeight - 12; });
  const outputView = el('div', { class: 'bottom-view', id: 'bottom-view-output', role: 'tabpanel', 'aria-labelledby': 'bottom-tab-output' }, outputTitle, outputEmpty, outputLog);

  // Search
  const searchView = el('div', { class: 'bottom-view', id: 'bottom-view-search', role: 'tabpanel', 'aria-labelledby': 'bottom-tab-search' }, search.element);

  const views = { problems: problemsView, output: outputView, search: searchView };
  const panel = el('div', { class: 'bottom-inner' }, resize, header, problemsView, outputView, searchView);
  mount.append(panel);

  const toggleBtn = el('button', {
    type: 'button', class: 'statusbar-toggle', 'data-testid': 'bottom-toggle', 'aria-controls': mount.id, 'aria-expanded': 'false', onclick: () => toggle(),
  });
  const toggleText = el('span', { class: 'statusbar-label' });
  const toggleKeys = el('kbd', { class: 'statusbar-keys' });
  toggleBtn.append(el('span', { class: 'statusbar-chevron', 'aria-hidden': 'true' }), toggleText, toggleKeys);
  statusbar.prepend(toggleBtn);

  // ------------------------------------------------------------ open / close / tabs / height

  function persist() { uiState.setBottom({ open: s.open, height: s.height, tab: s.tab }); }

  function applyHeight() {
    s.height = clampHeight(s.height);
    mount.style.setProperty('--bottom-h', `${s.height}px`);
    resize.setAttribute('aria-valuenow', String(s.height));
    resize.setAttribute('aria-valuemax', String(Math.floor(window.innerHeight * 0.7)));
    resize.setAttribute('aria-valuetext', t('ui.bottom.heightText', { n: s.height }));
  }

  function render() {
    mount.hidden = !s.open;
    mount.dataset.open = s.open ? 'true' : 'false';
    toggleBtn.setAttribute('aria-expanded', s.open ? 'true' : 'false');
    for (const name of PANEL_TABS) {
      const selected = name === s.tab;
      tabButtons[name].setAttribute('aria-selected', selected ? 'true' : 'false');
      tabButtons[name].tabIndex = selected ? 0 : -1;
      views[name].hidden = !selected;
    }
    applyHeight();
  }

  function open(tab) {
    if (tab && PANEL_TABS.includes(tab)) s.tab = tab;
    s.open = true;
    render();
    if (s.tab === 'output') refreshOutput();
    persist();
  }
  function close() { s.open = false; render(); persist(); }
  function toggle() { if (s.open) close(); else open(); }
  function setTab(tab) {
    if (!PANEL_TABS.includes(tab)) return;
    s.tab = tab;
    render();
    if (tab === 'output') refreshOutput();
    persist();
  }
  function setHeight(px, save = true) {
    s.height = clampHeight(px);
    applyHeight();
    if (save) persist();
  }

  // Separator: pointer drag (mouse, touch, pen), arrow keys, double click.
  let drag = null;
  resize.addEventListener('pointerdown', (e) => {
    if (e.button !== undefined && e.button > 0) return;
    drag = { startY: e.clientY, startH: s.height };
    resize.setPointerCapture?.(e.pointerId);
    resize.classList.add('dragging');
    e.preventDefault();
  });
  resize.addEventListener('pointermove', (e) => { if (drag) setHeight(drag.startH + (drag.startY - e.clientY), false); });
  const endDrag = (e) => {
    if (!drag) return;
    drag = null;
    resize.classList.remove('dragging');
    try { resize.releasePointerCapture?.(e.pointerId); } catch { /* already released */ }
    persist();
  };
  resize.addEventListener('pointerup', endDrag);
  resize.addEventListener('pointercancel', endDrag);
  resize.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowUp') { e.preventDefault(); setHeight(s.height + STEP); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); setHeight(s.height - STEP); }
    else if (e.key === 'Home') { e.preventDefault(); setHeight(MIN_HEIGHT); }
    else if (e.key === 'End') { e.preventDefault(); setHeight(window.innerHeight); }
  });
  resize.addEventListener('dblclick', () => setHeight(DEFAULT_HEIGHT));
  window.addEventListener('resize', () => { if (s.open) applyHeight(); });

  // ------------------------------------------------------------ problems

  const severityLabel = (severity) => {
    const label = t(`severity.${severity}`);
    return label === `severity.${severity}` ? severity : label;
  };

  function renderProblems() {
    const sorted = sortIssues(s.issues);
    problemsEmpty.textContent = s.hasMod ? t('problems.none') : t('ui.bottom.noMod');
    problemsEmpty.hidden = sorted.length > 0;
    problemList.hidden = sorted.length === 0;
    problemList.replaceChildren(...sorted.map((issue) => {
      const file = typeof issue.file === 'string' && issue.file ? issue.file : '';
      const line = Number.isInteger(issue.line) && issue.line > 0 ? issue.line : 0;
      const open = () => { if (file) openLocation(file, line || undefined); };
      return el('li', {},
        el('button', {
          type: 'button', class: 'problem-row', 'data-testid': 'problem-row', 'data-severity': issue.severity,
          'data-code': issue.code ?? '', 'data-file': file, 'data-line': line ? String(line) : '',
          disabled: !file ? true : null, onclick: open,
        },
        el('span', { class: `badge ${issue.severity}`, text: severityLabel(issue.severity) }),
        el('span', { class: 'mono problem-code', text: issue.code ?? '' }),
        el('span', { class: 'problem-text', text: issue.message }),
        file ? el('span', { class: 'mono muted problem-where', text: line ? `${file}:${line}` : file }) : null));
    }));
    tabButtons.problems.replaceChildren(tabLabel('problems'), sorted.length ? el('span', { class: 'count', text: String(sorted.length) }) : null);
  }

  // ------------------------------------------------------------ output

  const kindText = (kind) => {
    const label = t(`build.${kind}`);
    return label === `build.${kind}` ? String(kind) : label;
  };
  const stateText = (state) => t(`build.state.${state}`);
  const isActiveState = (state) => state === 'running' || state === 'queued';

  function logFor(latest) {
    const own = buildView.getLog(latest.id);
    if (own?.loaded) return own;
    const cached = s.fetched.get(latest.id);
    if (cached) return cached;
    if (!isActiveState(latest.state) && !s.fetched.has(latest.id)) {
      s.fetched.set(latest.id, null);
      const seq = ++s.fetchSeq;
      api(`/api/jobs/${encodeURIComponent(latest.id)}?after=0`).then((snap) => {
        if (seq !== s.fetchSeq && !s.fetched.has(latest.id)) return;
        const lines = (Array.isArray(snap.lines) ? snap.lines : []).map((l) => cleanLine(l.text));
        s.fetched.set(latest.id, { lines, lastN: lines.length, truncated: !!snap.truncated, loaded: true });
        refreshOutput();
      }).catch(() => s.fetched.delete(latest.id));
    }
    return null;
  }

  function writeLog(id, lines, truncated) {
    const changedJob = s.renderedId !== id;
    const rewrite = changedJob || lines.length < s.renderedCount || s.renderedCount === 0 || lines.length >= 5000;
    if (changedJob) s.stick = true;
    if (rewrite) {
      logText.data = lines.join('\n');
    } else if (lines.length > s.renderedCount) {
      logText.appendData('\n' + lines.slice(s.renderedCount).join('\n'));
    } else {
      return;
    }
    s.renderedId = id;
    s.renderedCount = lines.length;
    if (s.stick) outputLog.scrollTop = outputLog.scrollHeight;
    outputLog.dataset.truncated = truncated ? 'true' : 'false';
  }

  function refreshOutput() {
    const state = buildView.getState();
    const latest = state.latest;
    outputEmpty.hidden = !!latest;
    outputEmpty.textContent = t('ui.output.empty');
    outputLog.hidden = !latest;
    if (!latest) {
      outputTitle.textContent = t('ui.output.none');
      outputTitle.dataset.state = 'idle';
      logText.data = '';
      s.renderedId = null;
      s.renderedCount = 0;
      stopTick();
      return;
    }
    s.outputId = latest.id;
    const startedAt = Date.parse(latest.startedAt);
    const endedAt = latest.finishedAt ? Date.parse(latest.finishedAt) : Date.now();
    const elapsed = Number.isFinite(startedAt) ? formatElapsed(endedAt - startedAt) : '';
    const shownState = latest.state === 'queued' ? 'running' : latest.state;
    outputTitle.dataset.state = shownState;
    outputTitle.dataset.kind = latest.kind;
    outputTitle.textContent = t('ui.output.title', { kind: kindText(latest.kind), state: stateText(latest.state), elapsed });
    const log = logFor(latest);
    if (log) writeLog(latest.id, log.lines, log.truncated);
    else if (s.renderedId !== latest.id) { logText.data = ''; s.renderedId = latest.id; s.renderedCount = 0; }
    if (isActiveState(latest.state)) startTick(); else stopTick();
  }

  function startTick() {
    if (s.tick) return;
    s.tick = setInterval(refreshOutput, 1000);
  }
  function stopTick() {
    if (s.tick) { clearInterval(s.tick); s.tick = 0; }
  }

  buildView.onChange(() => refreshOutput());

  // ------------------------------------------------------------ labels

  function renderLabels() {
    for (const name of PANEL_TABS) if (name !== 'problems') tabButtons[name].textContent = tabLabel(name);
    renderProblems();
    resize.setAttribute('aria-label', t('ui.bottom.resize'));
    resize.setAttribute('title', t('ui.bottom.resize'));
    closeBtn.setAttribute('aria-label', t('ui.bottom.close'));
    closeBtn.setAttribute('title', t('ui.bottom.close'));
    tablist.setAttribute('aria-label', t('ui.bottom.label'));
    mount.setAttribute('aria-label', t('ui.bottom.label'));
    toggleText.textContent = t('ui.bottom.panel');
    const keys = shortcutText('Ctrl+J');
    toggleKeys.textContent = keys;
    toggleBtn.setAttribute('title', t('ui.bottom.toggleTitle', { keys }));
    applyHeight();
    refreshOutput();
  }

  renderLabels();
  render();

  return {
    isOpen: () => s.open,
    getTab: () => s.tab,
    open, close, toggle, setTab, setHeight,
    getHeight: () => s.height,
    /** Problems of the open mod (null when no mod is open). */
    setIssues(issues) { s.hasMod = Array.isArray(issues); s.issues = s.hasMod ? issues : []; renderProblems(); },
    /** The open mod changed: forget what belongs to the previous one. */
    setMod() {
      s.fetched.clear();
      s.renderedId = null;
      s.renderedCount = 0;
      refreshOutput();
    },
    focusSearch() { open('search'); search.focus(); },
    retranslate() { renderLabels(); },
    languageTag: getLanguage,
  };
}
