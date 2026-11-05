// Factorio Mod Studio UI. Plain ES module, no build step.
// Everything that comes from a mod (names, descriptions, file content) is untrusted:
// it is only ever inserted with textContent / attributes, never as HTML.

import { createLocaleView } from '/locale-view.js';
import { createBuildView } from '/build-view.js';
import { createEditorTabs } from '/editor-tabs.js';
import { createUiState, SIDEBAR_DEFAULT, SIDEBAR_MIN, SIDEBAR_MAX } from '/ui-state.js';
import { createTheme } from '/theme.js';
import { createSearchPanel } from '/search-panel.js';
import { createBottomPanel } from '/bottom-panel.js';
import { createActionBar } from '/action-bar.js';
import { createPalette } from '/palette.js';
import { createFilesView } from '/files-view.js';
import { LANGUAGES, initI18n, setLanguage, getLanguage, onLanguageChange, t, formatNumber } from '/i18n.js';

const $ = (selector, root = document) => root.querySelector(selector);

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const sessionToken = document.querySelector('meta[name="fms-token"]')?.content || '';

class ApiError extends Error {
  constructor(message, status, body) { super(message); this.status = status; this.body = body; }
}

/** An error whose text is UI chrome (translated when shown), as opposed to server text that is shown as received. */
class UiError extends Error {
  constructor(key, params) { super(key); this.key = key; this.params = params; }
}
const errorText = (error) => (error instanceof UiError ? t(error.key, error.params) : error.message);

async function api(url, options = {}) {
  const headers = { ...(options.headers || {}) };
  headers['X-FMS-Token'] = sessionToken; // writes need it; the folder listing (a GET) does too
  const response = await fetch(url, { ...options, headers });
  if (!response.ok) {
    let body = {};
    try { body = await response.json(); } catch { /* not JSON */ }
    throw new ApiError(body.error || response.statusText, response.status, body);
  }
  return response.status === 204 ? null : response.json();
}

const state = {
  project: null,
  mods: [],
  modFilter: '',
  currentId: null,
  overview: null,
  tab: 'overview',
  protoFilter: '',
  protoType: '',
  protoMode: 'defined', // 'defined' (prototypes of this mod) or 'touched' (existing prototypes referenced via data.raw)
  token: 0,
};

const TABS = ['overview', 'files', 'prototypes', 'recipes', 'resources', 'locale', 'problems', 'build'];

// The localisation editor keeps its own state and DOM; this page only hosts it and forwards live updates.
const localeView = createLocaleView({ api, el });

// The Build tab (validate / pack / smoke / run with a live log) is likewise a persistent view hosted by this page.
const buildView = createBuildView({ api, el, openFile: (path, line) => openFile(path, line), modName: () => currentMod()?.name ?? null });

const fileUrl = (path) => `/api/mods/${encodeURIComponent(state.currentId)}/file?path=${encodeURIComponent(path)}`;
const fmtNum = (n) => (n === null || n === undefined ? '?' : formatNumber(n, { maximumFractionDigits: 3, useGrouping: false }));

function formatSize(bytes) {
  const one = { maximumFractionDigits: 1, minimumFractionDigits: 1, useGrouping: false };
  if (bytes < 1024) return t('size.b', { value: formatNumber(bytes, { useGrouping: false }) });
  if (bytes < 1024 * 1024) return t('size.kb', { value: formatNumber(bytes / 1024, one) });
  return t('size.mb', { value: formatNumber(bytes / 1024 / 1024, one) });
}

// What the main content area currently shows when it is a plain message (so a language switch can redraw it).
let contentMessage = null;
function showMessage(key, params) {
  contentMessage = { key, params };
  renderContent(el('div', { class: 'empty', text: t(key, params) }));
}

// ---------------------------------------------------------------- routing

function parseHash() {
  const [id, tab] = location.hash.replace(/^#\/?/, '').split('/');
  return { id: id || null, tab: TABS.includes(tab) ? tab : 'overview' };
}

function go(id, tab) {
  location.hash = `#/${id}/${tab}`;
}

window.addEventListener('hashchange', route);

async function route() {
  const { id, tab } = parseHash();
  if (!id) return;
  state.tab = tab;

  if (id !== state.currentId || !state.overview) {
    if (localeView.hasUnsaved() && !confirm(t('locale.discardConfirm'))) {
      history.replaceState(null, '', `#/${state.currentId}/${state.tab}`);
      return;
    }
    suspendTabMemory = true; // closing the tabs of the mod we leave must not erase what is remembered for it
    const closed = closeViewer();
    suspendTabMemory = false;
    if (!closed) { // unsaved edits and the user chose to stay: put the address back
      history.replaceState(null, '', `#/${state.currentId}/${state.tab}`);
      return;
    }
    localeView.reset();
    buildView.reset();
    filesView.reset();
    searchPanel.setMod(id);
    bottom.setMod();
    state.currentId = id;
    state.overview = null;
    syncChrome();
    renderModList();
    showMessage('content.loading');
    const token = ++state.token;
    try {
      const overview = await api(`/api/mods/${encodeURIComponent(id)}`);
      if (token !== state.token) return;
      state.overview = overview;
    } catch (error) {
      if (token !== state.token) return;
      showMessage('content.loadError', { message: error.message });
      return;
    }
  }
  renderModList();
  renderMod();
  uiState.setLastMod(state.currentId);
  uiState.setLastTab(state.currentId, state.tab);
  if (pendingRestore && pendingRestore.id === state.currentId) {
    const restore = pendingRestore;
    pendingRestore = null;
    restoreEditorTabs(restore);
  }
}

// ---------------------------------------------------------------- sidebar

function renderModList() {
  const list = $('#mod-list');
  list.replaceChildren();
  const filter = state.modFilter.toLowerCase();
  const groups = ['project', 'workspace', 'game', 'installed'];

  for (const source of groups) {
    const items = state.mods.filter((m) => m.source === source && m.name.toLowerCase().includes(filter));
    if (!items.length) continue;
    list.append(el('div', { class: 'group-label', text: t(`sidebar.group.${source}`) }));
    for (const mod of items) {
      list.append(el('button', {
        class: 'mod-item', type: 'button', 'aria-current': mod.id === state.currentId ? 'true' : null,
        onclick: () => go(mod.id, state.tab),
      }, el('span', { class: 'name', text: mod.name }), mod.editable && el('span', { class: 'pencil', title: t('sidebar.editable'), text: '✎' }), el('span', { class: 'ver', text: mod.version })));
    }
  }

  if (!list.children.length) {
    list.append(el('p', { class: 'muted small', text: state.mods.length ? t('sidebar.noMatch') : t('sidebar.noMods', { folder: '<folder>' }) }));
  }
}

// ---------------------------------------------------------------- main content

function renderContent(...nodes) {
  contentMessage = null;
  $('#content').replaceChildren(...nodes);
}

function renderMod(keepScroll = false) {
  const o = state.overview;
  const scroll = $('#content').scrollTop;
  const info = o.info || {};
  const counts = categoryCounts(o);

  syncChrome();
  buildView.activate(state.currentId, !!currentMod()?.editable);

  // The title and the main actions live in the persistent action bar above; this block keeps the details.
  const header = el('div', { class: 'mod-header' },
    el('div', { class: 'meta' },
      // Version and target Factorio version are shown in the action bar above; the internal name only when the title differs.
      info.name && info.title && info.title !== info.name && el('span', { class: 'mono', text: info.name }),
      info.author && el('span', { text: t('mod.by', { author: info.author }) })),
    info.description && el('p', { class: 'muted', text: info.description }),
    info.dependencies && info.dependencies.length > 0 && el('div', { class: 'chips' },
      info.dependencies.map((d) => el('span', { class: 'chip' + (/^(\?|\(\?\)|~)/.test(d) ? ' optional' : '') + (d.startsWith('!') ? ' accent' : ''), text: d }))));

  const tabCount = {
    files: o.files.length, prototypes: o.prototypes.length, recipes: o.recipes.length,
    resources: counts.graphics + counts.sound, problems: o.issues.length,
  };

  const tabs = el('div', { class: 'tabs', role: 'tablist' }, TABS.map((key) =>
    el('button', {
      class: 'tab', type: 'button', role: 'tab', 'aria-selected': key === state.tab ? 'true' : 'false',
      'data-testid': key === 'locale' ? 'locale-tab' : key === 'build' ? 'build-tab' : null,
      onclick: () => go(state.currentId, key),
    }, t(`tabs.${key}`), tabCount[key] !== undefined && el('span', { class: 'count', text: tabCount[key] }))));

  const panels = { overview: renderOverview, files: renderFiles, prototypes: renderPrototypes, recipes: renderRecipes, resources: renderResources, problems: renderProblems, locale: renderLocale, build: renderBuild };
  renderContent(header, tabs, panels[state.tab](o, counts));
  $('#content').scrollTop = keepScroll ? scroll : 0;
  // The tab strip is one scrollable row: keep the selected tab in view.
  const selected = tabs.querySelector('[aria-selected="true"]');
  if (selected) tabs.scrollLeft = Math.max(0, selected.offsetLeft - (tabs.clientWidth - selected.offsetWidth) / 2);
}

function categoryCounts(o) {
  const counts = { script: 0, locale: 0, graphics: 0, sound: 0, data: 0, text: 0, other: 0 };
  for (const f of o.files) counts[f.category] = (counts[f.category] || 0) + 1;
  return counts;
}

const READINESS_ICONS = { ok: '✓', warn: '!', fail: '✕', info: 'i' };
const READINESS_KEYS = { thumbnail: 'thumbnail', changelog: 'changelog', 'locale-name': 'localeName', errors: 'errors', packed: 'packed' };

function readinessAction(c) {
  if (c.file) return () => openFile(c.file, c.line);
  if (c.id === 'errors') return () => go(state.currentId, 'problems');
  if (c.id === 'locale-name') return () => go(state.currentId, 'locale');
  return null;
}

function renderReadiness(checks) {
  if (!checks || !checks.length) return null;
  return el('section', { class: 'readiness', 'data-testid': 'readiness' },
    el('h3', { text: t('readiness.heading') }),
    el('ul', { class: 'readiness-list' }, checks.map((c) => {
      const key = READINESS_KEYS[c.id] || c.id;
      const text = t(`readiness.${key}.${c.status}`, { version: c.version ?? '?', n: c.count ?? 0 });
      const action = readinessAction(c);
      return el('li', { class: `readiness-row ${c.status}`, 'data-testid': 'readiness-row', 'data-id': c.id, 'data-status': c.status },
        el('span', { class: `readiness-icon ${c.status}`, 'aria-hidden': 'true', text: READINESS_ICONS[c.status] || '?' }),
        action ? el('button', { class: 'readiness-text', type: 'button', onclick: action, text }) : el('span', { class: 'readiness-text', text }));
    })));
}

/**
 * Coverage of every language but the default one (the first entry). Up to 3 languages: "ru 92% · de 60%".
 * More: "{full}/{total} complete · fa 0% · eu 0% · sq 0%" (the 3 weakest); the full list goes to the tooltip.
 */
function coverage(cov) {
  if (!cov || cov.length < 2 || !(cov[0].keys > 0)) return { text: '', title: null };
  const total = cov[0].keys;
  const langs = cov.slice(1).map((c) => ({ lang: c.language, pct: Math.max(0, Math.round(((total - c.missing) / total) * 100)) }));
  const fmt = (l) => `${l.lang} ${l.pct}%`;
  const all = langs.map(fmt).join(' · ');
  if (langs.length <= 3) return { text: all, title: null };
  const weakest = langs.filter((l) => l.pct < 100).sort((a, b) => a.pct - b.pct || a.lang.localeCompare(b.lang)).slice(0, 3);
  const summary = t('overview.coverageComplete', { full: langs.length - langs.filter((l) => l.pct < 100).length, total: langs.length });
  return { text: [summary, ...weakest.map(fmt)].join(' · '), title: all };
}

function showTouched(type) {
  state.protoMode = 'touched';
  state.protoType = type || '';
  state.protoFilter = '';
  if (state.tab === 'prototypes') renderMod(); else go(state.currentId, 'prototypes');
}

function renderOverview(o, counts) {
  const card = (num, label, onclick, extra = {}) => el('div', { class: 'card' + (extra.class ? ` ${extra.class}` : ''), 'data-testid': extra.testid },
    el('div', { class: 'num', text: num }), el('div', { class: 'label' }, onclick
      ? el('button', { class: 'link', type: 'button', onclick, text: label })
      : label),
    extra.sub && el('div', { class: 'sub muted small', 'data-testid': extra.testid ? `${extra.testid}-sub` : null, title: extra.title || null, text: extra.sub }));
  const toTab = (tab) => () => go(state.currentId, tab);
  const cov = coverage(o.localeCoverage);

  const entryPoints = o.files.filter((f) => f.role);
  const errors = o.issues.filter((i) => i.severity === 'error').length;
  const warnings = o.issues.filter((i) => i.severity === 'warning').length;

  const touchedTypes = new Map();
  for (const p of o.touched) touchedTypes.set(p.type, (touchedTypes.get(p.type) || 0) + 1);
  const touchedChips = [...touchedTypes].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

  return el('div', {},
    renderReadiness(o.readiness),
    el('div', { class: 'cards' },
      card(counts.script, t('overview.scripts'), toTab('files')),
      card(o.prototypes.length, t('overview.prototypes'), toTab('prototypes')),
      card(o.recipes.length, t('overview.recipes'), toTab('recipes')),
      card(counts.graphics, t('overview.graphics'), toTab('resources')),
      card(counts.sound, t('overview.sounds'), toTab('resources')),
      card(o.languages.length, t('overview.translations'), toTab('locale'), { testid: 'card-translations', sub: cov.text, title: cov.title }),
      card(o.touched.length, t('overview.touched'), () => showTouched(''), { testid: 'card-touched' }),
      card(errors, t('overview.errors'), toTab('problems'), { testid: 'card-errors', class: errors > 0 ? 'bad' : '' }),
      card(warnings, t('overview.warnings'), toTab('problems'), { testid: 'card-warnings', class: warnings > 0 ? 'warn' : '' })),
    o.notes.map((n) => el('p', { class: 'note', text: n })),
    el('h3', { class: 'section', text: t('overview.entryPoints') }),
    entryPoints.length
      ? el('table', {}, el('tbody', {}, entryPoints.map((f) => el('tr', { class: 'clickable', onclick: () => openFile(f.path) },
          el('td', {}, el('span', { class: 'mono', text: f.path })), el('td', { class: 'muted', text: f.role })))))
      : el('p', { class: 'muted', text: t('overview.noEntryPoints') }),
    touchedChips.length > 0 && [
      el('h3', { class: 'section', text: t('overview.touchedHeading') }),
      el('div', { class: 'chips', 'data-testid': 'touched-chips' }, touchedChips.map(([type, n]) =>
        el('button', { class: 'chip touched-chip', type: 'button', 'data-testid': 'touched-chip', 'data-type': type, onclick: () => showTouched(type) },
          el('span', { class: 'mono', text: type }), el('span', { class: 'muted', text: String(n) })))),
    ]);
}


const currentMod = () => state.mods.find((m) => m.id === state.currentId);

// The Files tab (tree, filters, context menu, drag & drop, trash with undo) lives in files-view.js.
function renderFiles(o) {
  return filesView.render(o);
}

function renderLocale() {
  localeView.show(state.currentId, !!currentMod()?.editable);
  return localeView.element;
}

function renderBuild() {
  buildView.show(state.currentId, !!currentMod()?.editable);
  return buildView.element;
}

function renderPrototypes(o) {
  const touched = state.protoMode === 'touched';
  const list = touched ? o.touched : o.prototypes;
  const types = [...new Set(list.map((p) => p.type))];
  if (state.protoType && !types.includes(state.protoType)) state.protoType = '';
  const body = el('tbody');

  const refresh = () => {
    const filter = state.protoFilter.toLowerCase();
    const rows = list.filter((p) =>
      (!state.protoType || p.type === state.protoType) &&
      (!filter || p.name.toLowerCase().includes(filter) || (p.displayName || '').toLowerCase().includes(filter)));

    body.replaceChildren(...rows.slice(0, 500).map((p) => el('tr', { class: 'clickable', 'data-testid': 'proto-row', onclick: () => openFile(p.file, p.line) },
      !touched && el('td', { class: 'icon' }, p.icon ? el('img', { class: 'thumb', loading: 'lazy', alt: '', src: fileUrl(p.icon) }) : null),
      el('td', { class: 'mono', text: p.type }),
      el('td', {}, el('div', { class: 'mono', text: p.name }), p.displayName && el('div', { class: 'muted small', text: p.displayName })),
      el('td', { class: 'muted mono', text: `${p.file}:${p.line}` }))));
    count.textContent = rows.length > 500 ? t('prototypes.showing', { shown: 500, total: rows.length }) : t('count.prototype', { n: rows.length });
  };

  const setMode = (mode) => { if (state.protoMode !== mode) { state.protoMode = mode; state.protoType = ''; renderMod(true); } };
  const modeSwitch = (o.touched.length > 0 || touched) && el('div', { class: 'segmented', role: 'group', 'aria-label': t('prototypes.modeAria'), 'data-testid': 'proto-mode' },
    [['defined', 'prototypes.modeDefined'], ['touched', 'prototypes.modeTouched']].map(([mode, key]) =>
      el('button', { type: 'button', class: 'toggle', 'data-mode': mode, 'aria-pressed': (touched ? mode === 'touched' : mode === 'defined') ? 'true' : 'false',
        onclick: () => setMode(mode), text: t(key) })));

  const count = el('span', { class: 'muted small', 'data-testid': 'proto-count' });
  const search = el('input', { type: 'search', placeholder: t('prototypes.filter'), 'aria-label': t('prototypes.filterAria'), value: state.protoFilter,
    oninput: (e) => { state.protoFilter = e.target.value; refresh(); } });
  const select = el('select', { 'aria-label': t('prototypes.typeAria'), 'data-testid': 'proto-type', onchange: (e) => { state.protoType = e.target.value; refresh(); } },
    el('option', { value: '', text: t('prototypes.allTypes') }),
    types.map((t) => el('option', { value: t, text: t, selected: t === state.protoType })));

  if (!list.length) {
    return el('div', {}, modeSwitch && el('div', { class: 'toolbar' }, modeSwitch),
      el('p', { class: 'muted', text: t(touched ? 'prototypes.touchedEmpty' : 'prototypes.empty') }));
  }

  refresh();
  const headers = [!touched && '', t('table.type'), t('table.name'), t(touched ? 'table.firstSeen' : 'table.definedAt')].filter((h) => h !== false);
  return el('div', {},
    el('div', { class: 'toolbar' }, modeSwitch, search, select, count),
    el('table', {}, el('thead', {}, el('tr', {}, headers.map((h) => el('th', { text: h })))), body));
}

function itemChip(item) {
  const amount = item.amount === null || item.amount === undefined ? '' : `${fmtNum(item.amount)}× `;
  return el('span', { class: 'chip' + (item.type === 'fluid' ? ' accent' : ''), text: `${amount}${item.name}`, title: item.type });
}

function renderRecipes(o) {
  if (!o.recipes.length) return el('p', { class: 'muted', text: t('recipes.empty') });

  return el('div', {}, o.recipes.map((r) => el('div', { class: 'recipe' },
    el('div', { class: 'recipe-head' },
      el('div', {}, el('strong', { class: 'mono', text: r.name }), r.displayName && el('span', { class: 'muted', text: `  ${r.displayName}` })),
      el('div', { class: 'muted small' },
        r.category && `${r.category} · `, r.energyRequired !== null && r.energyRequired !== undefined && `${t('recipes.seconds', { value: fmtNum(r.energyRequired) })} · `,
        el('button', { class: 'link', type: 'button', onclick: () => openFile(r.file, r.line), text: `${r.file}:${r.line}` }))),
    el('div', { class: 'recipe-flow' },
      r.ingredients.length ? r.ingredients.map(itemChip) : el('span', { class: 'muted', text: t('recipes.noIngredients') }),
      el('span', { class: 'arrow', text: '→' }),
      r.results.length ? r.results.map(itemChip) : el('span', { class: 'muted', text: t('recipes.noResults') })))));
}

function renderResources(o) {
  const graphics = o.files.filter((f) => f.category === 'graphics');
  const sounds = o.files.filter((f) => f.category === 'sound');
  const locales = o.files.filter((f) => f.category === 'locale');

  return el('div', {},
    el('h3', { text: t('resources.graphics', { n: graphics.length }) }),
    graphics.length
      ? el('div', { class: 'gallery section' }, graphics.slice(0, 400).map((f) => el('button', { class: 'tile', type: 'button', onclick: () => openFile(f.path), title: f.path },
          el('img', { loading: 'lazy', alt: '', src: fileUrl(f.path) }), el('span', { text: f.path.split('/').pop() }))))
      : el('p', { class: 'muted', text: t('resources.noImages') }),
    graphics.length > 400 && el('p', { class: 'muted small', text: t('resources.more', { shown: 400, total: graphics.length }) }),
    el('h3', { class: 'section', text: t('resources.sounds', { n: sounds.length }) }),
    sounds.length
      ? sounds.slice(0, 100).map((f) => el('div', { class: 'sound-row' }, el('span', { class: 'mono', text: f.path }),
          el('audio', { controls: true, preload: 'none', src: fileUrl(f.path) })))
      : el('p', { class: 'muted', text: t('resources.noSounds') }),
    el('h3', { class: 'section', text: t('resources.locale', { n: o.languages.length }) }),
    locales.length
      ? el('div', { class: 'chips' }, locales.map((f) => el('button', { class: 'chip', type: 'button', onclick: () => openFile(f.path), text: f.path.replace(/^locale\//, '') })))
      : el('p', { class: 'muted', text: t('resources.noLocale') }),
    renderLocaleCoverage(o.localeCoverage || []));
}

function renderLocaleCoverage(coverage) {
  if (!coverage.length) return null;
  return el('table', { class: 'locale-coverage' },
    el('thead', {}, el('tr', {}, ['language', 'keys', 'missing', 'extra'].map((h) => el('th', { text: t(`table.${h}`) })))),
    el('tbody', {}, coverage.map((c) => el('tr', {},
      el('td', { class: 'mono', text: c.language }),
      el('td', { text: String(c.keys) }),
      el('td', {}, c.missing > 0 ? el('span', { class: 'badge warning', text: String(c.missing) }) : String(c.missing)),
      el('td', { text: String(c.extra) })))));
}

function severityLabel(severity) {
  const label = t(`severity.${severity}`);
  return label === `severity.${severity}` ? severity : label;
}

function renderProblems(o) {
  if (!o.issues.length) return el('p', { class: 'muted', text: t('problems.none') });

  return el('table', {},
    el('thead', {}, el('tr', {}, el('th', { text: t('table.level') }), el('th', { text: t('table.code') }), el('th', { text: t('table.message') }), el('th', { text: t('table.where') }))),
    el('tbody', {}, o.issues.map((i) => el('tr', {},
      el('td', {}, el('span', { class: `badge ${i.severity}`, text: severityLabel(i.severity) })),
      el('td', { class: 'mono', text: i.code }),
      el('td', { text: i.message }),
      el('td', {}, i.file && el('button', { class: 'link', type: 'button', onclick: () => openFile(i.file, i.line), text: i.line ? `${i.file}:${i.line}` : i.file }))))));
}

// ---------------------------------------------------------------- file viewer / editor (tabs live in editor-tabs.js)

const editorTabs = createEditorTabs({
  api, el, fileUrl,
  getModId: () => state.currentId,
  isEditable: () => !!currentMod()?.editable,
  onChange: () => onEditorTabsChanged(),
  onSaved: () => scheduleRefresh(),
});

const closeViewer = (force = false) => editorTabs.closeViewer(force);

const filesView = createFilesView({
  api, el, editorTabs, formatSize,
  getModId: () => state.currentId,
  isEditable: () => !!currentMod()?.editable,
  openFile: (path, line) => openFile(path, line),
  refresh: () => refreshOverview(),
  setDropOverlay: (on) => setOverlay(on),
});

/** Opens a file of the current mod in an editor tab; with `column` / `length` the matched range is selected. */
function openFile(path, line, column, length) {
  if (!state.currentId) return Promise.resolve(false);
  rememberRecent(path);
  return editorTabs.openFile(path, line, column, length);
}

// ---------------------------------------------------------------- layout chrome: state memory, theme, panels, palette

const isMac = (() => {
  try { return /Mac|iPhone|iPad|iPod/i.test(navigator.platform || '') || /Mac OS X|iPhone|iPad/i.test(navigator.userAgent || ''); } catch { return false; }
})();
/** "Ctrl+Shift+F" as shown to the user: the command key on macOS. */
const shortcutText = (keys) => (isMac ? keys.replace(/Ctrl/g, '⌘') : keys);

const uiState = createUiState();
let pendingRestore = null; // { id, paths, active } for the mod that is being opened at startup
let suspendTabMemory = false;

const themeSelect = $('#theme-select');
const theme = createTheme({ uiState, onChange: (mode) => { themeSelect.value = mode; } });
theme.apply();
themeSelect.addEventListener('change', () => theme.setMode(themeSelect.value));

// Sidebar width: a CSS custom property set through the CSSOM (allowed under the strict CSP), never an inline style attribute.
const sidebarSeparator = $('#sidebar-resize');
let sidebarWidth = SIDEBAR_DEFAULT;
function setSidebarWidth(px, save = true) {
  sidebarWidth = Math.max(SIDEBAR_MIN, Math.min(SIDEBAR_MAX, Math.round(px)));
  $('.app').style.setProperty('--sidebar-w', `${sidebarWidth}px`);
  sidebarSeparator.setAttribute('aria-valuenow', String(sidebarWidth));
  if (save) uiState.setSidebarWidth(sidebarWidth === SIDEBAR_DEFAULT ? null : sidebarWidth);
}
setSidebarWidth(uiState.get().sidebarWidth ?? SIDEBAR_DEFAULT, false);
{
  let drag = null;
  sidebarSeparator.addEventListener('pointerdown', (e) => {
    if (e.button !== undefined && e.button > 0) return;
    drag = { startX: e.clientX, startW: sidebarWidth };
    sidebarSeparator.setPointerCapture?.(e.pointerId);
    sidebarSeparator.classList.add('dragging');
    e.preventDefault();
  });
  sidebarSeparator.addEventListener('pointermove', (e) => { if (drag) setSidebarWidth(drag.startW + (e.clientX - drag.startX), false); });
  const end = (e) => {
    if (!drag) return;
    drag = null;
    sidebarSeparator.classList.remove('dragging');
    try { sidebarSeparator.releasePointerCapture?.(e.pointerId); } catch { /* already released */ }
    setSidebarWidth(sidebarWidth);
  };
  sidebarSeparator.addEventListener('pointerup', end);
  sidebarSeparator.addEventListener('pointercancel', end);
  sidebarSeparator.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') { e.preventDefault(); setSidebarWidth(sidebarWidth - 16); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); setSidebarWidth(sidebarWidth + 16); }
    else if (e.key === 'Home') { e.preventDefault(); setSidebarWidth(SIDEBAR_MIN); }
    else if (e.key === 'End') { e.preventDefault(); setSidebarWidth(SIDEBAR_MAX); }
  });
  sidebarSeparator.addEventListener('dblclick', () => setSidebarWidth(SIDEBAR_DEFAULT));
}

const searchPanel = createSearchPanel({
  api, el,
  getModId: () => state.currentId,
  openMatch: (path, line, column, length) => openFile(path, line, column, length),
});

const bottom = createBottomPanel({
  el, api, uiState, buildView, search: searchPanel, shortcutText,
  mount: $('#bottom-panel'), statusbar: $('#statusbar'),
  openLocation: (path, line) => openFile(path, line),
});

const actionBar = createActionBar({
  el, mount: $('#action-bar'), buildView,
  onFactorio: () => { if (state.currentId) go(state.currentId, 'build'); },
  onJob: () => bottom.open('output'),
  onProblems: () => { if (bottom.isOpen() && bottom.getTab() === 'problems') bottom.close(); else bottom.open('problems'); },
});

// A job started anywhere shows its output; only a start from the action bar or the palette also opens the panel.
buildView.onStarted(({ origin }) => {
  bottom.setTab('output');
  if (origin === 'actionbar' || origin === 'palette') bottom.open('output');
});

/** Pushes the open mod (or its absence) to the persistent action bar and the Problems list. */
function syncChrome() {
  actionBar.setOverview(state.overview);
  bottom.setIssues(state.overview ? state.overview.issues : null);
}

// "Open files: n" in the status strip: brings back the viewer after the Back button hid it (the tabs were kept).
const revealButton = el('button', {
  type: 'button', class: 'statusbar-reveal', 'data-testid': 'viewer-reveal', hidden: true, onclick: () => editorTabs.reveal(),
});
$('#statusbar').append(revealButton);
function revealButtonRender() {
  const show = editorTabs.isParked() && editorTabs.count() > 0;
  revealButton.hidden = !show;
  revealButton.textContent = t('ui.viewer.reveal', { n: editorTabs.count() });
  revealButton.setAttribute('title', t('ui.viewer.revealTitle'));
}

// Open editor tabs are remembered per mod.
function onEditorTabsChanged() {
  revealButtonRender();
  if (state.tab === 'files') filesView.onTabsChanged();
  if (suspendTabMemory || !state.currentId) return;
  uiState.setEditorTabs(state.currentId, editorTabs.paths(), editorTabs.activePath());
}

async function restoreEditorTabs({ id, paths, active }) {
  suspendTabMemory = true;
  try {
    for (const path of paths) {
      if (state.currentId !== id) return;
      await editorTabs.openFile(path, undefined, undefined, undefined, { quiet: true, activateTab: false });
    }
    if (state.currentId === id && active) editorTabs.activatePath(active);
  } finally {
    suspendTabMemory = false;
    onEditorTabsChanged();
  }
}

function rememberRecent(path) {
  const id = state.currentId;
  if (!id) return;
  const recent = uiState.get().recent[id] ?? [];
  if (recent[0] === path) return;
  uiState.setRecent(id, [path, ...recent.filter((p) => p !== path)]);
}

// ---- quick open and command palette

function commandList() {
  const out = [];
  const add = (id, title, run, hint = '', kind = 'command') => out.push({ id, kind, title, hint, run });
  const mod = !!(state.currentId && state.overview);
  const job = buildView.getState();
  const editable = !!currentMod()?.editable;

  if (mod) for (const key of TABS) add(`tab:${key}`, t('ui.cmd.goto', { tab: t(`tabs.${key}`) }), () => go(state.currentId, key));
  for (const m of state.mods) add(`mod:${m.id}`, t('ui.cmd.openMod', { name: m.name }), () => go(m.id, state.tab), '', 'mod');
  if (mod && !job.busy) {
    add('validate', t('ui.cmd.validate'), () => buildView.start('validate', 'palette'));
    if (editable) add('pack', t('ui.cmd.pack'), () => buildView.start('pack', 'palette'));
    if (editable && job.factorioFound) {
      add('smoke', t('ui.cmd.smoke'), () => buildView.start('smoke', 'palette'));
      add('run', t('ui.cmd.run'), () => buildView.start('run', 'palette'));
    }
  }
  add('new-mod', t('ui.cmd.newMod'), openNewModDialog);
  add('add-mod', t('ui.cmd.addMod'), () => $('#zip-input').click());
  add('add-folder', t('ui.cmd.addFolder'), () => $('#folder-input').click());
  add('toggle-bottom', t('ui.cmd.togglePanel'), () => bottom.toggle(), shortcutText('Ctrl+J'));
  add('show-problems', t('ui.cmd.showProblems'), () => bottom.open('problems'));
  add('show-output', t('ui.cmd.showOutput'), () => bottom.open('output'));
  add('show-search', t('ui.cmd.showSearch'), () => bottom.focusSearch(), shortcutText('Ctrl+Shift+F'));
  if (mod) add('find', t('ui.cmd.find'), () => bottom.focusSearch(), shortcutText('Ctrl+Shift+F'));
  if (editorTabs.canSave()) add('save', t('ui.cmd.save'), () => editorTabs.save(), shortcutText('Ctrl+S'));
  if (editorTabs.count() > 0) {
    add('close-tab', t('ui.cmd.closeTab'), () => editorTabs.closeActive(), shortcutText('Ctrl+W'));
    add('close-all', t('ui.cmd.closeAll'), () => editorTabs.closeAll());
  }
  for (const mode of ['auto', 'light', 'dark']) add(`theme:${mode}`, t('ui.cmd.theme', { mode: t(`ui.theme.${mode}`) }), () => theme.setMode(mode));
  for (const l of LANGUAGES) add(`lang:${l.code}`, t('ui.cmd.language', { name: l.name }), () => setLanguage(l.code));
  return out;
}

const palette = createPalette({
  el, shortcutText,
  getFiles: () => (state.currentId && state.overview ? state.overview.files.map((f) => f.path) : null),
  getRecent: () => uiState.get().recent[state.currentId] ?? [],
  getMods: () => state.mods,
  getCommands: commandList,
  openFile: (path, line, column) => openFile(path, line, column),
  openMod: (id) => go(id, state.tab),
});
$('#palette-open').addEventListener('click', () => palette.openFiles());

document.addEventListener('keydown', (e) => {
  if (e.defaultPrevented || !(e.ctrlKey || e.metaKey) || e.altKey) return;
  if (document.querySelector('dialog[open]')) return;
  const key = e.key.toLowerCase();
  if (key === 'j' && !e.shiftKey) { e.preventDefault(); bottom.toggle(); }
  else if (key === 'f' && e.shiftKey) { e.preventDefault(); bottom.focusSearch(); }
});

// ---------------------------------------------------------------- live updates

let refreshTimer = null;

function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(refreshOverview, 400);
}

async function refreshOverview() {
  const id = state.currentId;
  if (!id) return;
  // Do not rebuild the page under the user's hands while they type in a filter box.
  const active = document.activeElement;
  if (active && $('#content').contains(active) && /^(INPUT|SELECT|TEXTAREA)$/.test(active.tagName)) { scheduleRefresh(); return; }
  try {
    const overview = await api(`/api/mods/${encodeURIComponent(id)}`);
    if (id !== state.currentId) return;
    state.overview = overview;
    renderMod(true);
  } catch { /* mod may have been removed; the 'mods' event handles that */ }
}

async function refreshMods() {
  try { state.mods = await api('/api/mods'); } catch { return; }
  if (state.project?.workspaceExists === false && state.mods.some((m) => m.source === 'workspace')) {
    state.project.workspaceExists = true; // the folder was created by an import
    setLive(!!state.live);
  }
  if (state.currentId && !state.mods.some((m) => m.id === state.currentId)) {
    suspendTabMemory = true;
    closeViewer(true);
    suspendTabMemory = false;
    localeView.reset();
    buildView.reset();
    searchPanel.setMod(null);
    bottom.setMod();
    state.currentId = null;
    state.overview = null;
    syncChrome();
    showMessage('content.unavailable');
  }
  renderModList();
}

function setLive(connected) {
  const root = $('#live-status');
  root.replaceChildren(...[
    el('span', { class: 'dot' + (connected ? ' on' : '') }),
    el('span', { text: connected ? t('live.on') : t('live.reconnecting') }),
    state.project?.workspaceExists === false && el('span', { class: 'muted small', text: ` · ${t('live.noWorkspace')}` })].filter(Boolean));
}

function connectEvents() {
  const source = new EventSource('/api/events');
  source.onopen = () => { state.live = true; setLive(true); };
  source.onerror = () => { state.live = false; setLive(false); }; // EventSource reconnects by itself
  source.onmessage = async (message) => {
    let event;
    try { event = JSON.parse(message.data); } catch { return; }

    if (event.type === 'mods') await refreshMods();
    else if (event.type === 'resync') { await refreshMods(); scheduleRefresh(); localeView.onExternalChange(); editorTabs.onResync(); searchPanel.markOutdated(); }
    else if (event.type === 'changed' && event.modId === state.currentId) {
      scheduleRefresh();
      if (typeof event.path === 'string' && event.path.startsWith('locale/')) localeView.onExternalChange();
      editorTabs.onFileChanged(event.modId, event.path);
      searchPanel.markOutdated();
    }
  };
}


// ---------------------------------------------------------------- importing mods (drag & drop, zip, folder)

const IMPORT_EXT = new Set(['lua', 'cfg', 'json', 'txt', 'md', 'csv', 'png', 'jpg', 'jpeg', 'ogg', 'wav', 'mp3', 'flac']);
const MAX_IMPORT_FILES = 20000;
const UPLOAD_CONCURRENCY = 4;
const importQueue = [];
let importRunning = false;

const extOf = (name) => { const i = name.lastIndexOf('.'); return i < 0 ? '' : name.slice(i + 1).toLowerCase(); };
const isUsableFile = (path) => {
  const parts = path.split('/');
  return !parts.some((p) => p.startsWith('.')) && IMPORT_EXT.has(extOf(parts[parts.length - 1]));
};

// `build` returns { text, actions }; it is kept so a language switch can build the message again in the new language.
let importBuild = null;

function showImportStatus(build) {
  importBuild = build || null;
  const box = $('#import-status');
  if (!importBuild) { box.hidden = true; box.replaceChildren(); return; }
  const { text, actions = [] } = importBuild();
  box.hidden = false;
  box.replaceChildren(
    el('span', { class: 'import-text', text }),
    ...actions,
    el('button', { type: 'button', class: 'linklike', 'aria-label': t('import.dismissAria'), text: t('import.dismiss'), onclick: () => showImportStatus(null) }));
}
const importText = (key, params) => showImportStatus(() => ({ text: t(key, params) }));

async function readAllEntries(directory) {
  const reader = directory.createReader();
  const all = [];
  for (;;) {
    const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
    if (batch.length === 0) break; // readEntries returns chunks; an empty array marks the end
    all.push(...batch);
  }
  return all;
}

async function collectEntry(entry, prefix, out, counts) {
  if (entry.isFile) {
    const path = prefix + entry.name;
    if (!isUsableFile(path)) { counts.skipped++; return; }
    const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
    out.push({ file, path });
  } else if (entry.isDirectory) {
    if (entry.name.startsWith('.')) { counts.skipped++; return; }
    for (const child of await readAllEntries(entry)) await collectEntry(child, `${prefix}${entry.name}/`, out, counts);
  }
}

async function collectDroppedFolder(entry) {
  const files = [];
  const counts = { skipped: 0 };
  for (const child of await readAllEntries(entry)) await collectEntry(child, '', files, counts);
  return { files, skipped: counts.skipped, label: entry.name };
}

function collectInputFolder(fileList) {
  const files = [];
  let skipped = 0;
  let label = t('import.folderLabel');
  for (const file of fileList) {
    const parts = (file.webkitRelativePath || file.name).split('/');
    label = parts.length > 1 ? parts[0] : label;
    const path = parts.length > 1 ? parts.slice(1).join('/') : parts[0];
    if (isUsableFile(path)) files.push({ file, path }); else skipped++;
  }
  return { files, skipped, label };
}

async function importZip(file) {
  importText('import.uploading', { name: file.name });
  return api('/api/workspace/import-zip', { method: 'POST', headers: { 'Content-Type': 'application/zip' }, body: file });
}

async function importFolder({ files, skipped, label }) {
  if (files.length === 0) throw new UiError('import.noFiles', { label });
  if (files.length > MAX_IMPORT_FILES) throw new UiError('import.tooMany', { label, max: MAX_IMPORT_FILES });

  const { importId } = await api('/api/workspace/imports', { method: 'POST' });
  const base = `/api/workspace/imports/${importId}`;
  try {
    let done = 0;
    let next = 0;
    let failure = null;
    importText('import.progress', { done: 0, total: files.length });
    const worker = async () => {
      while (!failure && next < files.length) {
        const { file, path } = files[next++];
        try {
          await api(`${base}/file?path=${encodeURIComponent(path)}`, { method: 'PUT', headers: { 'Content-Type': 'application/octet-stream' }, body: file });
          importText('import.progress', { done: ++done, total: files.length });
        } catch (error) { failure = failure || error; }
      }
    };
    await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, files.length) }, worker));
    if (failure) throw failure;

    importText('import.finishing');
    const result = await api(`${base}/commit`, { method: 'POST' });
    return { ...result, skipped: (result.skipped || 0) + skipped };
  } catch (error) {
    try { await api(base, { method: 'DELETE' }); } catch { /* session already gone */ }
    throw error;
  }
}

function describeFailure(label, error) {
  if (error instanceof ApiError && error.status === 409) {
    const existing = error.body?.existing;
    return () => ({
      text: t('import.exists', { label }),
      actions: existing ? [el('button', { type: 'button', text: t('import.openExisting'), onclick: () => go(existing, 'overview') })] : [],
    });
  }
  if (error instanceof ApiError && error.status === 403) return () => ({ text: t('import.expired', { label }), actions: [] });
  return () => ({ text: t('import.failure', { label, message: errorText(error) }), actions: [] });
}

async function runImports() {
  if (importRunning) return;
  importRunning = true;
  const outcomes = [];
  let total = 0;
  try {
    while (importQueue.length) {
      const job = importQueue.shift();
      total++;
      try {
        let result;
        if (job.kind === 'error') throw job.error;
        if (job.kind === 'zip') result = await importZip(job.file);
        else {
          const folder = job.folder ? job.folder : await collectDroppedFolder(job.entry);
          result = await importFolder(folder);
        }
        await refreshMods();
        outcomes.push({ ok: true, label: job.label, result });
        go(result.id, 'overview');
      } catch (error) {
        outcomes.push({ ok: false, label: job.label, failure: describeFailure(job.label, error) }); // failure: () => ({ text, actions })
      }
    }
  } finally {
    importRunning = false;
  }

  const good = outcomes.filter((o) => o.ok);
  const bad = outcomes.filter((o) => !o.ok);
  const okText = (o) => t(o.result.skipped ? 'import.importedSkipped' : 'import.imported',
    { name: o.result.name, version: o.result.version, files: t('count.file', { n: o.result.files }), skipped: o.result.skipped });
  if (total === 1) {
    if (good.length) showImportStatus(() => ({ text: okText(good[0]) }));
    else showImportStatus(bad[0].failure);
  } else {
    showImportStatus(() => {
      const parts = [...good.map(okText), ...bad.map((o) => o.failure().text)];
      return {
        text: t('import.summary', { good: good.length, total, details: parts.join(' · ') }),
        actions: bad.length === 1 ? bad[0].failure().actions : [],
      };
    });
  }
}

function enqueue(jobs) {
  if (!jobs.length) return;
  importQueue.push(...jobs);
  runImports();
}

const zipJob = (file) => ({ kind: 'zip', file, label: file.name });

// Drag & drop. The browser must never navigate to a dropped file, so dragover and drop are always cancelled.
let dragDepth = 0;
const hasFiles = (e) => Array.from(e.dataTransfer?.types || []).includes('Files');
const setOverlay = (on) => { $('#drop-overlay').hidden = !on; if (!on) dragDepth = 0; };

window.addEventListener('dragenter', (e) => { e.preventDefault(); if (hasFiles(e)) { dragDepth++; setOverlay(true); } });
window.addEventListener('dragover', (e) => { e.preventDefault(); if (hasFiles(e)) { e.dataTransfer.dropEffect = 'copy'; $('#drop-overlay').hidden = false; } });
window.addEventListener('dragleave', (e) => { e.preventDefault(); if (hasFiles(e) && --dragDepth <= 0) setOverlay(false); });
window.addEventListener('dragend', () => setOverlay(false));
window.addEventListener('drop', (e) => {
  e.preventDefault();
  setOverlay(false);
  if (!hasFiles(e)) return;

  // Entries must be taken synchronously; the DataTransfer is emptied once this handler returns.
  const jobs = [];
  for (const item of Array.from(e.dataTransfer.items || [])) {
    if (item.kind !== 'file') continue;
    const entry = item.webkitGetAsEntry?.();
    const file = item.getAsFile();
    if (entry?.isDirectory) jobs.push({ kind: 'folder', entry, label: entry.name });
    else if (file && extOf(file.name) === 'zip') jobs.push(zipJob(file));
    else jobs.push({ kind: 'error', label: file?.name || 'item', error: new UiError('import.dropHint') });
  }
  if (!jobs.length) for (const file of Array.from(e.dataTransfer.files || [])) jobs.push(extOf(file.name) === 'zip' ? zipJob(file) : { kind: 'error', label: file.name, error: new UiError('import.dropHint') });
  enqueue(jobs);
});

$('#add-mod').addEventListener('click', () => $('#zip-input').click());
$('#add-folder').addEventListener('click', () => $('#folder-input').click());
$('#zip-input').addEventListener('change', (e) => {
  enqueue(Array.from(e.target.files).map(zipJob));
  e.target.value = '';
});
$('#folder-input').addEventListener('change', (e) => {
  const folder = collectInputFolder(Array.from(e.target.files));
  e.target.value = '';
  if (folder.files.length === 0 && folder.skipped === 0) return;
  enqueue([{ kind: 'folder', folder, label: folder.label }]);
});

// ---------------------------------------------------------------- creating a new mod from a template

const NAME_PATTERN = /^[A-Za-z0-9_-]{1,100}$/;
const RESERVED_NAMES = new Set(['CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$', ...Array.from({ length: 9 }, (_, i) => `COM${i + 1}`), ...Array.from({ length: 9 }, (_, i) => `LPT${i + 1}`)]);
const AUTHOR_KEY = 'fms.author';

const newMod = { templates: null, busy: false };

function rememberedAuthor() {
  try { return localStorage.getItem(AUTHOR_KEY) || ''; } catch { return ''; }
}
function rememberAuthor(author) {
  try { if (author) localStorage.setItem(AUTHOR_KEY, author); } catch { /* storage unavailable: not essential */ }
}

/** "hello-world" -> "Hello World" (mirrors the server's default title, used as a placeholder). */
const humanize = (name) => name.split(/[-_]+/).filter(Boolean).map((w) => w[0].toUpperCase() + w.slice(1)).join(' ');

/** Key of the translated problem description, or '' when the name is fine. */
function nameProblem(name) {
  if (!name) return 'dialog.error.enter';
  if (name.length > 100) return 'dialog.error.tooLong';
  if (!/^[A-Za-z0-9_-]+$/.test(name)) return 'dialog.error.chars';
  if (RESERVED_NAMES.has(name.toUpperCase())) return 'dialog.error.reserved';
  return NAME_PATTERN.test(name) ? '' : 'dialog.error.invalid';
}

function updateNameHint() {
  const input = $('#nm-name');
  const hint = $('#nm-name-hint');
  const name = input.value;
  const problem = name ? nameProblem(name) : '';
  input.setAttribute('aria-invalid', problem ? 'true' : 'false');
  hint.className = 'small nm-hint ' + (problem ? 'bad' : name ? 'good' : 'muted');
  hint.textContent = problem ? t(problem) : name ? t('dialog.folder', { name }) : t('dialog.nameHint');
  $('#nm-title').placeholder = name && !problem ? humanize(name) || name : t('dialog.titlePlaceholder');
}

// `build` returns { text, actions }; kept for redrawing after a language switch.
let newModBuild = null;
let newModBad = false;

function setNewModStatus(build, bad = false) {
  newModBuild = build || null;
  newModBad = bad;
  renderNewModStatus();
}

function renderNewModStatus() {
  const box = $('#nm-status');
  box.className = 'nm-status' + (newModBad ? ' bad' : '');
  const { text, actions = [] } = newModBuild ? newModBuild() : {};
  box.replaceChildren(...(text ? [el('span', { text })] : []), ...actions);
}
const newModText = (key, params, bad = false) => setNewModStatus(() => ({ text: t(key, params) }), bad);

async function loadTemplates() {
  if (newMod.templates) return newMod.templates;
  newMod.templates = await api('/api/templates');
  const list = $('#nm-template-list');
  list.replaceChildren(...newMod.templates.map((tpl, i) => el('label', { class: 'nm-template', 'data-template': tpl.id },
    el('input', { type: 'radio', name: 'nm-template', value: tpl.id, checked: i === 0 }),
    el('span', {}, el('strong', { text: tpl.title }), el('span', { class: 'muted small', text: tpl.description }), el('span', { class: 'small nm-template-reason' })))));
  updateTemplateAvailability();
  return newMod.templates;
}

const VERSION_PATTERN = /^\d+\.\d+$/;

/** The Factorio version chosen in the new-mod dialog, or null while a custom value is not a valid MAJOR.MINOR. */
function newModVersion() {
  const chosen = $('#nm-version').value;
  if (chosen !== 'other') return chosen;
  const custom = $('#nm-version-custom').value.trim();
  return VERSION_PATTERN.test(custom) ? custom.replace(/\b0+(?=\d)/g, '') : null;
}

/** Templates that do not support the selected version are disabled (with the reason shown); the others stay usable. */
function updateTemplateAvailability() {
  const custom = $('#nm-version').value === 'other';
  $('#nm-version-custom').hidden = !custom;
  const version = newModVersion();
  const list = $('#nm-template-list');
  let selectedGone = false;
  for (const label of list.querySelectorAll('.nm-template')) {
    const info = newMod.templates?.find((tpl) => tpl.id === label.dataset.template);
    const supported = !version || !info || !Array.isArray(info.supportedVersions) || info.supportedVersions.includes(version);
    const input = label.querySelector('input');
    input.disabled = !supported;
    if (supported) label.removeAttribute('data-unsupported'); else label.setAttribute('data-unsupported', 'true');
    label.querySelector('.nm-template-reason').textContent = supported ? '' : t('newmod.unsupported', { version, versions: (info.supportedVersions || []).join(', ') });
    if (!supported && input.checked) { input.checked = false; selectedGone = true; }
  }
  if (selectedGone) {
    const fallback = list.querySelector('input:not(:disabled)');
    if (fallback) fallback.checked = true;
  }
  const hint = $('#nm-version-hint');
  hint.textContent = custom && !version ? t('newmod.versionInvalid') : '';
  hint.className = 'small nm-hint ' + (custom && !version ? 'bad' : 'muted');
}

function openNewModDialog() {
  const dialog = $('#new-mod-dialog');
  $('#nm-form').reset();
  $('#nm-author').value = rememberedAuthor();
  setNewModStatus(null);
  updateNameHint();
  updateTemplateAvailability();
  $('#nm-create').disabled = false;
  newMod.busy = false;
  dialog.showModal();
  $('#nm-name').focus();
  loadTemplates().then(() => {
    const first = $('#nm-template-list input');
    if (first && !$('#nm-template-list input:checked')) first.checked = true;
  }).catch((error) => newModText('dialog.loadTemplatesFailed', { message: error.message }, true));
}

async function showNewMod(id) {
  await refreshMods();
  go(id, 'files');
  for (let i = 0; i < 80 && !(state.currentId === id && state.overview); i++) await new Promise((r) => setTimeout(r, 50));
  if (state.currentId !== id || !state.overview) return;
  const paths = (state.overview.files || []).map((f) => f.path);
  const entry = ['data.lua', 'control.lua'].find((p) => paths.includes(p));
  if (entry) await openFile(entry);
}

async function submitNewMod(event) {
  event.preventDefault();
  if (newMod.busy) return;
  const name = $('#nm-name').value;
  const problem = nameProblem(name);
  if (problem) { updateNameHint(); newModText(problem, {}, true); $('#nm-name').focus(); return; }

  const factorioVersion = newModVersion();
  if (!factorioVersion) { updateTemplateAvailability(); newModText('newmod.versionInvalid', {}, true); $('#nm-version-custom').focus(); return; }
  const template = $('#nm-template-list input:checked')?.value;
  const author = $('#nm-author').value.trim();
  const body = {
    name,
    template: template || 'empty',
    factorioVersion,
    title: $('#nm-title').value.trim(),
    author,
    description: $('#nm-description').value.trim(),
  };

  newMod.busy = true;
  $('#nm-create').disabled = true;
  newModText('dialog.creating');
  try {
    const result = await api('/api/workspace/new', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    rememberAuthor(author);
    $('#new-mod-dialog').close();
    showImportStatus(() => ({ text: t('import.created', { name: result.name, version: result.version, files: t('count.file', { n: result.files }) }) }));
    await showNewMod(result.id);
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      const existing = error.body?.existing;
      setNewModStatus(() => ({
        text: t('dialog.exists', { name }),
        actions: existing ? [el('button', { type: 'button', text: t('dialog.open'), onclick: () => { $('#new-mod-dialog').close(); go(existing, 'overview'); } })] : [],
      }), true);
    } else if (error instanceof ApiError && error.status === 403) {
      newModText('common.sessionExpired', {}, true);
    } else if (error.message) {
      setNewModStatus(() => ({ text: error.message }), true);
    } else {
      newModText('dialog.createFailed', {}, true);
    }
  } finally {
    newMod.busy = false;
    $('#nm-create').disabled = false;
  }
}

$('#new-mod').addEventListener('click', openNewModDialog);
$('#nm-name').addEventListener('input', () => { updateNameHint(); setNewModStatus(null); });
$('#nm-version').addEventListener('change', () => { updateTemplateAvailability(); setNewModStatus(null); if (!$('#nm-version-custom').hidden) $('#nm-version-custom').focus(); });
$('#nm-version-custom').addEventListener('input', () => { updateTemplateAvailability(); setNewModStatus(null); });
$('#nm-form').addEventListener('submit', submitNewMod);
$('#nm-cancel').addEventListener('click', () => $('#new-mod-dialog').close());

// ---------------------------------------------------------------- startup

$('#mod-search').addEventListener('input', (e) => { state.modFilter = e.target.value; renderModList(); });

// ---------------------------------------------------------------- language

let ready = false;

/** Redraws everything that carries translated text, in place: the editor textarea (unsaved content, caret, scroll) is never recreated. */
function rerender() {
  if (!ready) return;
  $('#lang-select').value = getLanguage();
  renderModList();
  setLive(!!state.live);
  if (contentMessage) showMessage(contentMessage.key, contentMessage.params);
  else if (state.overview) renderMod(true);

  localeView.retranslate();
  buildView.retranslate();
  editorTabs.retranslate();
  actionBar.retranslate();
  bottom.retranslate();
  searchPanel.retranslate();
  palette.retranslate();
  themeSelect.value = theme.getMode();

  if (importBuild) showImportStatus(importBuild);
  updateNameHint();
  updateTemplateAvailability();
  renderNewModStatus();
}

function setupLanguageSelect() {
  const select = $('#lang-select');
  select.replaceChildren(...LANGUAGES.map((l) => el('option', { value: l.code, text: l.name })));
  select.value = getLanguage();
  select.addEventListener('change', () => { setLanguage(select.value); });
  onLanguageChange(rerender);
}

async function init() {
  await initI18n();
  localeView.retranslate(); // the view was built before the dictionaries were loaded
  buildView.retranslate();
  editorTabs.retranslate();
  actionBar.retranslate();
  bottom.retranslate();
  searchPanel.retranslate();
  palette.retranslate();
  revealButtonRender();
  themeSelect.value = theme.getMode();
  setupLanguageSelect();
  ready = true;
  try {
    [state.project, state.mods] = await Promise.all([api('/api/project'), api('/api/mods')]);
  } catch (error) {
    showMessage('content.serverUnreachable', { message: error.message });
    return;
  }

  $('#project-path').textContent = state.project.root;
  $('#project-path').title = state.project.root;
  renderModList();
  setLive(false);
  connectEvents();

  const { id } = parseHash();
  const first = state.mods.find((m) => m.source === 'project') || state.mods[0];
  const remembered = uiState.get();
  if (!id && remembered.lastMod && state.mods.some((m) => m.id === remembered.lastMod)) {
    // No address in the URL: come back to where the user was (a mod that no longer exists is ignored).
    const stored = remembered.editorTabs[remembered.lastMod];
    if (stored) pendingRestore = { id: remembered.lastMod, paths: stored.paths, active: stored.active };
    go(remembered.lastMod, remembered.lastTab[remembered.lastMod] || 'overview');
  } else if (!id && first) go(first.id, 'overview');
  else if (id) {
    // The address wins for the mod and the tab; the stored editor tabs come back only when it points to the mod they belong to.
    const stored = remembered.lastMod === id ? remembered.editorTabs[id] : null;
    if (stored) pendingRestore = { id, paths: stored.paths, active: stored.active };
    route();
  } else showMessage('content.noModsYet', { folder: '<folder>' });
}

init();
