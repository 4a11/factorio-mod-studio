// Factorio Mod Studio UI. Plain ES module, no build step.
// Everything that comes from a mod (names, descriptions, file content) is untrusted:
// it is only ever inserted with textContent / attributes, never as HTML.

import { createLocaleView } from '/locale-view.js';
import { createBuildView, chosenInstallation } from '/build-view.js';
import { createEditorTabs } from '/editor-tabs.js';
import { createUiState, SIDEBAR_DEFAULT, SIDEBAR_MIN, SIDEBAR_MAX, VIEWER_DEFAULT, VIEWER_MIN, VIEWER_MAX } from '/ui-state.js';
import { createTheme } from '/theme.js';
import { createSearchPanel } from '/search-panel.js';
import { createBottomPanel } from '/bottom-panel.js';
import { createActionBar } from '/action-bar.js';
import { createUpdateView } from '/update-view.js';
import { createModMenu } from '/mod-menu.js';
import { createGameDataView } from '/game-data-view.js';
import { createBlocksView } from '/blocks-view.js';
import { createPalette } from '/palette.js';
import { createFilesView } from '/files-view.js';
import { createProblemsView } from '/problems-view.js';
import { findMods, readInfo, zipIdentity, assess, MAX_IMPORT_FILES } from '/import-logic.js';
import { createContentDialog, loadBlocks, blockTitle, blockDescription, supportsVersion, CATEGORIES } from '/content-dialog.js';
import { renderPrototypes as renderPrototypesView } from '/prototypes-view.js';
import { renderResources as renderResourcesView, stopResourceSound } from '/resources-view.js';
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
  protoMode: 'defined', // 'defined' (prototypes of this mod), 'touched' (existing prototypes referenced via data.raw) or 'game' (--dump-data)
  protoFamily: '', // family chip ('items', 'recipes', ...); protoType narrows it to one type
  protoTypesExpanded: false, // the family's type chips beyond the first ten are shown
  protoView: 'type', // 'type' (sections per family) or 'name' (one row per object name)
  protoMissingOnly: false,
  protoCollapsed: new Set(), // collapsed family sections
  protoAllRows: new Set(), // family sections (or 'name') shown without the row limit
  resKind: 'graphics', // Resources tab: 'graphics' or 'sound'
  resFilter: '',
  resUnusedOnly: false,
  resSize: 'm', // preview size: 's', 'm' or 'l'
  resExpanded: new Set(), // opened folder sections ('kind:folder'); they start collapsed
  resAllRows: new Set(), // folder sections shown without the tile limit
  token: 0,
};

const TABS = ['overview', 'files', 'prototypes', 'blocks', 'resources', 'locale', 'problems', 'build'];

// The localisation editor keeps its own state and DOM; this page only hosts it and forwards live updates.
const localeView = createLocaleView({ api, el });

// The Build tab (validate / pack / smoke / run with a live log) is likewise a persistent view hosted by this page.
const buildView = createBuildView({ api, el, openFile: (path, line) => openFile(path, line), modName: () => currentMod()?.name ?? null });

const fileUrl = (path) => `/api/mods/${encodeURIComponent(state.currentId)}/file?path=${encodeURIComponent(path)}`;

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
  // Recipes used to have their own tab; old links land on the recipe family of the Prototypes tab.
  const [oldId, oldTab] = location.hash.replace(/^#\/?/, '').split('/');
  if (oldId && oldTab === 'recipes') { history.replaceState(null, '', `#/${oldId}/prototypes`); selectRecipeFamily(); }
  const { id, tab } = parseHash();
  if (!id) return;
  if (tab !== 'resources' || id !== state.currentId) stopResourceSound();
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
    state.resFilter = '';
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

const SIDEBAR_GROUPS = ['pinned', 'project', 'workspace', 'game', 'installed'];

const isPinned = (id) => uiState.get().pinnedMods.includes(id);
function togglePin(id) {
  uiState.setPinned(id, !isPinned(id));
  renderModList();
}

// Right click (or "⋯") on a mod in the sidebar: open, pin, show in the file manager, delete a workspace mod with undo.
const modMenu = createModMenu({
  el, api, isPinned, togglePin,
  open: (mod) => go(mod.id, state.tab),
  hasUnsaved: (mod) => mod.id === state.currentId && (editorTabs.hasDirty() || localeView.hasUnsaved()),
  refresh: () => refreshMods(),
});

/** The menu of a mod row: at the pointer, or under the row when opened from the keyboard (the context-menu key). */
function openModMenu(e, mod) {
  e.preventDefault();
  const row = e.currentTarget.closest('.mod-row');
  const item = row.querySelector('.mod-item');
  const fromPointer = e.type === 'contextmenu' && (e.clientX || e.clientY);
  const box = (e.type === 'click' ? e.currentTarget : row).getBoundingClientRect();
  modMenu.show(mod, fromPointer ? e.clientX : box.left, fromPointer ? e.clientY : box.bottom + 2, e.type === 'click' ? e.currentTarget : item);
}

/** A stable hue per mod name, so a mod without a thumbnail keeps the same coloured letter. */
function hueOf(name) {
  let h = 0;
  for (const ch of name) h = (h * 31 + ch.codePointAt(0)) % 360;
  return h;
}

function letterIcon(mod) {
  const icon = el('span', { class: 'mod-icon letter', 'aria-hidden': 'true', text: (mod.name.match(/[A-Za-z0-9]/)?.[0] || '?').toUpperCase() });
  icon.style.setProperty('--hue', String(hueOf(mod.name))); // CSSOM: the page's CSP has no inline styles
  return icon;
}

function modIcon(mod) {
  if (!mod.thumbnail) return letterIcon(mod);
  const img = el('img', { class: 'mod-icon', alt: '', loading: 'lazy', decoding: 'async', src: `/api/mods/${encodeURIComponent(mod.id)}/file?path=thumbnail.png` });
  img.addEventListener('error', () => img.replaceWith(letterIcon(mod)), { once: true });
  return img;
}

function renderModList() {
  const list = $('#mod-list');
  list.replaceChildren();
  const filter = state.modFilter.trim().toLowerCase();
  const matches = (m) => !filter || m.name.toLowerCase().includes(filter) || (m.title || '').toLowerCase().includes(filter);
  const collapsed = new Set(uiState.get().collapsedGroups);
  let shown = 0;

  // Pinned mods stand in their own group at the top, in the order they were pinned, and not in their usual group.
  const pinned = uiState.get().pinnedMods;
  const groupItems = (source) => source === 'pinned'
    ? pinned.map((id) => state.mods.find((m) => m.id === id)).filter((m) => m && matches(m))
    : state.mods.filter((m) => m.source === source && !pinned.includes(m.id) && matches(m));

  for (const source of SIDEBAR_GROUPS) {
    const items = groupItems(source);
    if (!items.length) continue;
    shown += items.length;
    // While searching every group with a match is open, whatever was collapsed before.
    const open = !!filter || !collapsed.has(source);
    const readOnly = source !== 'pinned' && items.every((m) => !m.editable);
    const body = el('div', { class: 'mod-group-body', role: 'group', id: `mod-group-${source}`, hidden: !open });
    list.append(el('button', {
      class: 'mod-group', type: 'button', 'aria-expanded': open ? 'true' : 'false', 'aria-controls': body.id, 'data-group': source,
      onclick: () => { if (!filter) { uiState.setGroupCollapsed(source, open); renderModList(); } },
    },
    el('span', { class: 'chevron', 'aria-hidden': 'true' }),
    source === 'pinned' ? el('span', { class: 'pin-icon', 'aria-hidden': 'true' }) : null,
    el('span', { class: 'mod-group-name', text: t(`sidebar.group.${source}`) }),
    readOnly ? el('span', { class: 'lock', title: t('sidebar.readOnly'), 'aria-label': t('sidebar.readOnly') }) : null,
    el('span', { class: 'count-pill', text: String(items.length) })));

    for (const mod of items) {
      const tip = [mod.title && mod.title !== mod.name ? mod.title : null, `${mod.name} ${mod.version}`.trim(), mod.editable ? null : t('sidebar.readOnly')]
        .filter(Boolean).join('\n');
      const pinnedNow = pinned.includes(mod.id);
      body.append(el('div', { class: 'mod-row' + (pinnedNow ? ' pinned' : ''), 'data-mod': mod.id, oncontextmenu: (e) => openModMenu(e, mod) },
        el('button', {
          class: 'mod-item', type: 'button', title: tip, 'aria-current': mod.id === state.currentId ? 'true' : null,
          onclick: () => go(mod.id, state.tab),
        }, modIcon(mod), el('span', { class: 'name', text: mod.name }), el('span', { class: 'ver', text: mod.version })),
        el('button', {
          class: 'pin', type: 'button', 'aria-pressed': pinnedNow ? 'true' : 'false', 'data-testid': 'pin-mod',
          title: t(pinnedNow ? 'sidebar.unpin' : 'sidebar.pin'), 'aria-label': t(pinnedNow ? 'sidebar.unpin' : 'sidebar.pin') + ': ' + mod.name,
          onclick: () => togglePin(mod.id),
        }, el('span', { class: 'pin-icon', 'aria-hidden': 'true' })),
        el('button', {
          class: 'mod-more', type: 'button', 'aria-haspopup': 'menu', 'data-testid': 'mod-more',
          title: t('sidebar.menu.more'), 'aria-label': t('sidebar.menu.more') + ': ' + mod.name,
          onclick: (e) => openModMenu(e, mod),
        }, '⋯')));
    }
    list.append(body);
  }

  const count = $('#mod-count');
  count.textContent = state.mods.length ? (filter ? `${shown} / ${state.mods.length}` : String(state.mods.length)) : '';
  count.hidden = !state.mods.length;

  if (!shown) {
    list.append(el('p', { class: 'muted small sidebar-empty', text: state.mods.length ? t('sidebar.noMatch') : t('sidebar.noMods', { folder: '<folder>' }) }));
  }
}

// "Add" menu: a mod archive, or a folder that may hold one mod or many.
function setAddMenu(open, focusFirst = false) {
  const menu = $('#add-menu');
  const button = $('#add-open');
  if (open) {
    const r = button.getBoundingClientRect();
    menu.hidden = false;
    menu.style.setProperty('left', `${Math.round(r.left)}px`);
    menu.style.setProperty('top', `${Math.round(r.bottom + 4)}px`);
    if (focusFirst) menu.querySelector('[role="menuitem"]')?.focus();
  } else menu.hidden = true;
  button.setAttribute('aria-expanded', open ? 'true' : 'false');
}
$('#add-open').addEventListener('click', (e) => setAddMenu($('#add-menu').hidden, e.detail === 0));
$('#add-menu').addEventListener('click', () => setAddMenu(false));
$('#add-menu').addEventListener('keydown', (e) => {
  const items = [...$('#add-menu').querySelectorAll('[role="menuitem"]')];
  const i = items.indexOf(document.activeElement);
  if (e.key === 'Escape') { e.preventDefault(); setAddMenu(false); $('#add-open').focus(); }
  else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus(); }
});
document.addEventListener('pointerdown', (e) => {
  if (!$('#add-menu').hidden && !e.target.closest('#add-menu, #add-open')) setAddMenu(false);
});

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
      info.dependencies.map((d) => el('span', { class: 'chip' + (/^(\?|\(\?\)|~)/.test(d) ? ' optional' : '') + (d.startsWith('!') ? ' accent' : ''), title: t(dependencyKind(d)), text: d }))));

  const tabCount = {
    files: o.files.length, prototypes: o.prototypes.length, blocks: (o.blocks || []).length,
    resources: counts.graphics + counts.sound, problems: o.issues.length,
  };

  // The Blocks tab is there when the mod has building blocks (or it is the one open).
  const shownTabs = TABS.filter((key) => key !== 'blocks' || (o.blocks || []).length > 0 || state.tab === 'blocks');
  const tabs = el('div', { class: 'tabs', role: 'tablist' }, shownTabs.map((key) =>
    el('button', {
      class: 'tab', type: 'button', role: 'tab', 'aria-selected': key === state.tab ? 'true' : 'false',
      'data-testid': key === 'locale' ? 'locale-tab' : key === 'build' ? 'build-tab' : null,
      onclick: () => go(state.currentId, key),
    }, t(`tabs.${key}`), tabCount[key] !== undefined && el('span', { class: 'count', text: tabCount[key] }))));

  const panels = { overview: renderOverview, files: renderFiles, prototypes: renderPrototypes, blocks: renderBlocks, resources: renderResources, problems: renderProblems, locale: renderLocale, build: renderBuild };
  renderContent(header, tabs, panels[state.tab](o, counts));
  $('#content').scrollTop = keepScroll ? scroll : 0;
  // The tab strip is one scrollable row: keep the selected tab in view.
  const selected = tabs.querySelector('[aria-selected="true"]');
  if (selected) tabs.scrollLeft = Math.max(0, selected.offsetLeft - (tabs.clientWidth - selected.offsetWidth) / 2);
}

/** i18n key explaining a dependency string's prefix: "!" incompatible, "?" optional, "(?)" hidden optional, "~" no load order. */
function dependencyKind(d) {
  const s = d.trim();
  if (s.startsWith('!')) return 'dep.incompatible';
  if (s.startsWith('(?)')) return 'dep.hidden';
  if (s.startsWith('?')) return 'dep.optional';
  if (s.startsWith('~')) return 'dep.noOrder';
  return 'dep.required';
}

function categoryCounts(o) {
  const counts = { script: 0, locale: 0, graphics: 0, sound: 0, data: 0, text: 0, other: 0 };
  for (const f of o.files) counts[f.category] = (counts[f.category] || 0) + 1;
  return counts;
}

const READINESS_ICONS = { ok: '✓', warn: '!', fail: '✕', info: 'i' };
const READINESS_KEYS = { thumbnail: 'thumbnail', changelog: 'changelog', 'locale-name': 'localeName', errors: 'errors', packed: 'packed' };

/** What to do about a check that is not done yet: an explicit button label and its action, or null. */
function readinessAction(c, o) {
  if (c.status === 'ok') return null;
  const toTab = (tab, key) => ({ label: t(key), run: () => go(state.currentId, tab) });
  if (c.file) return { label: t('readiness.action.open', { file: c.file }), run: () => openFile(c.file, c.line) };
  if (c.id === 'changelog' && o.files.some((f) => f.path === 'changelog.txt'))
    return { label: t('readiness.action.open', { file: 'changelog.txt' }), run: () => openFile('changelog.txt') };
  if (c.id === 'changelog' || c.id === 'thumbnail') return toTab('files', 'readiness.action.files');
  if (c.id === 'errors') return toTab('problems', 'readiness.action.problems');
  if (c.id === 'locale-name') return toTab('locale', 'readiness.action.locale');
  if (c.id === 'packed') return toTab('build', 'readiness.action.build');
  return null;
}

// Checklist with a segmented progress bar; every open item gets a button that says what it does.
function renderReadiness(checks, o) {
  if (!checks || !checks.length) return null;
  const done = checks.filter((c) => c.status === 'ok').length;
  return el('section', { class: 'readiness', 'data-testid': 'readiness' },
    el('div', { class: 'readiness-head' },
      el('h3', { text: t('readiness.heading') }),
      el('div', { class: 'readiness-bar', 'aria-hidden': 'true' }, checks.map((c) => el('span', { class: c.status }))),
      el('span', { class: 'muted small', 'data-testid': 'readiness-progress', text: t('readiness.progress', { ok: done, total: checks.length }) })),
    el('ul', { class: 'readiness-list' }, checks.map((c) => {
      const key = READINESS_KEYS[c.id] || c.id;
      const text = t(`readiness.${key}.${c.status}`, { version: c.version ?? '?', n: c.count ?? 0 });
      const action = readinessAction(c, o);
      return el('li', { class: `readiness-row ${c.status}`, 'data-testid': 'readiness-row', 'data-id': c.id, 'data-status': c.status },
        el('span', { class: `readiness-icon ${c.status}`, 'aria-hidden': 'true', text: READINESS_ICONS[c.status] || '?' }),
        el('span', { class: 'readiness-text', text }),
        action && el('button', { class: 'readiness-action', type: 'button', 'data-testid': 'readiness-action', onclick: action.run, text: action.label }));
    })));
}

/**
 * Coverage of every language but the default one (the first entry). Up to 3 languages: "ru 92% · de 60%".
 * More: "{full}/{total} complete · fa 0% · eu 0% · sq 0%" (the 3 weakest); the full list goes to the tooltip.
 */
function coverage(cov) {
  if (!cov || cov.length < 2 || !(cov[0].keys > 0)) return { text: '', title: null };
  const total = cov[0].keys;
  // Rounded down like the Localization tab, so 1515 of 1519 is 99%, not a "complete" 100%.
  const langs = cov.slice(1).map((c) => ({ lang: c.language, pct: Math.max(0, Math.floor(((total - c.missing) * 100) / total)) }));
  const fmt = (l) => `${l.lang} ${l.pct}%`;
  const all = langs.map(fmt).join(' · ');
  if (langs.length <= 3) return { text: all, title: null };
  const weakest = langs.filter((l) => l.pct < 100).sort((a, b) => a.pct - b.pct || a.lang.localeCompare(b.lang)).slice(0, 3);
  const summary = t('overview.coverageComplete', { full: langs.length - langs.filter((l) => l.pct < 100).length, total: langs.length });
  return { text: [summary, ...weakest.map(fmt)].join(' · '), title: all };
}

/** Prototypes tab filters for "the recipes of this mod" (the Overview card and old #/mod/recipes links). */
function selectRecipeFamily() {
  state.protoMode = 'defined';
  state.protoFamily = 'recipes';
  state.protoType = '';
  state.protoFilter = '';
  state.protoMissingOnly = false;
}

function showTouched(type) {
  state.protoMode = 'touched';
  state.protoFamily = '';
  state.protoType = type || '';
  state.protoFilter = '';
  if (state.tab === 'prototypes') renderMod(); else go(state.currentId, 'prototypes');
}

function renderOverview(o, counts) {
  // Every card is one button (icon + label, the number, an optional detail line) that leads to the tab with the details.
  const card = (num, label, icon, onclick, extra = {}) => el('button', {
    type: 'button', onclick, 'data-testid': extra.testid,
    class: ['card', 'ov-card', num === 0 && 'zero', extra.class].filter(Boolean).join(' '),
  },
  el('span', { class: 'ov-card-head' }, el('span', { class: `fi-icon ${icon}`, 'aria-hidden': 'true' }), el('span', { class: 'label', text: label })),
  el('span', { class: 'num', text: num }),
  extra.sub && el('span', { class: 'sub muted small', 'data-testid': extra.testid ? `${extra.testid}-sub` : null, title: extra.title || null, text: extra.sub }));
  const toTab = (tab) => () => go(state.currentId, tab);
  const cov = coverage(o.localeCoverage);

  const entryPoints = o.files.filter((f) => f.role);
  const errors = o.issues.filter((i) => i.severity === 'error').length;
  const warnings = o.issues.filter((i) => i.severity === 'warning').length;

  const touchedTypes = new Map();
  for (const p of o.touched) touchedTypes.set(p.type, (touchedTypes.get(p.type) || 0) + 1);
  const touchedChips = [...touchedTypes].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

  return el('div', { class: 'overview' },
    // A read-only mod (the game's own, an installed one) is never packed or released from here.
    currentMod()?.editable ? renderReadiness(o.readiness, o) : null,
    o.notes.map((n) => el('p', { class: 'note', text: n })),
    el('div', { class: 'cards' },
      card(counts.script, t('overview.scripts'), 'fi-file cat-script', toTab('files')),
      card(o.prototypes.length, t('overview.prototypes'), 'fi-cube', toTab('prototypes')),
      card(o.recipes.length, t('overview.recipes'), 'fi-recipe', () => { selectRecipeFamily(); go(state.currentId, 'prototypes'); }),
      card(counts.graphics, t('overview.graphics'), 'fi-image', () => { state.resKind = 'graphics'; go(state.currentId, 'resources'); }),
      card(counts.sound, t('overview.sounds'), 'fi-sound', () => { state.resKind = 'sound'; go(state.currentId, 'resources'); }),
      card(o.languages.length, t('overview.translations'), 'fi-globe', toTab('locale'), { testid: 'card-translations', sub: cov.text, title: cov.title }),
      card(o.touched.length, t('overview.touched'), 'fi-pencil', () => showTouched(''), { testid: 'card-touched' }),
      card(errors, t('overview.errors'), 'fi-error', toTab('problems'), { testid: 'card-errors', class: errors > 0 ? 'bad' : '' }),
      card(warnings, t('overview.warnings'), 'fi-warning', toTab('problems'), { testid: 'card-warnings', class: warnings > 0 ? 'warn' : '' })),
    el('h3', { class: 'section', text: t('overview.entryPoints') }),
    entryPoints.length
      ? el('div', { class: 'ov-list', 'data-testid': 'entry-points' }, entryPoints.map((f) =>
          el('button', { class: 'ov-row', type: 'button', title: t('readiness.action.open', { file: f.path }), onclick: () => openFile(f.path) },
            el('span', { class: 'fi-icon fi-file cat-script', 'aria-hidden': 'true' }),
            el('span', { class: 'mono ov-row-path', text: f.path }),
            el('span', { class: 'muted small ov-row-role', text: f.role }),
            el('span', { class: 'ov-row-go', 'aria-hidden': 'true', text: '›' }))))
      : el('p', { class: 'muted ov-empty', text: t('overview.noEntryPoints') }),
    touchedChips.length > 0 && [
      el('h3', { class: 'section' }, t('overview.touchedHeading'), el('span', { class: 'ov-count', text: String(o.touched.length) })),
      el('p', { class: 'muted small ov-hint', text: t('overview.touchedHint') }),
      el('div', { class: 'chips', 'data-testid': 'touched-chips' }, touchedChips.map(([type, n]) =>
        el('button', { class: 'chip touched-chip', type: 'button', 'data-testid': 'touched-chip', 'data-type': type, onclick: () => showTouched(type) },
          el('span', { class: 'mono', text: type }), el('span', { class: 'ov-count', text: String(n) })))),
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

// The Prototypes tab (families / by name, type chips, missing localised names) lives in prototypes-view.js.
// Its "In the game" mode (the game's own data, from --dump-data) is game-data-view.js; it keeps its state per mod.
// The Blocks tab (blocks-view.js): the mod's building blocks with their properties, rename, remove and upgrade. Saving changes the
// block's files, which the live updates then show everywhere. Other tabs open a block in it.
const blocksView = createBlocksView({ api, el, openFile: (path, line) => openFile(path, line), fileUrl, installation: chosenInstallation });
const openBlock = (file) => {
  if (!state.currentId) return;
  blocksView.select(state.currentId, file);
  if (state.tab === 'blocks') renderMod(true); else go(state.currentId, 'blocks');
};

function renderBlocks(o) {
  return blocksView.show(o, state.currentId, !!currentMod()?.editable);
}
const gameDataView = createGameDataView({ api, el, openFile: (path, line) => openFile(path, line), fileUrl, installation: chosenInstallation, openBlock });

function renderPrototypes(o) {
  return renderPrototypesView(o, { el, state, openFile, fileUrl, openBlock, rerender: () => renderMod(true),
    gameData: () => gameDataView.show(o, state.currentId, !!currentMod()?.editable) });
}

// The Resources tab (graphics and sounds by folder, unused filter, icon users, copy Lua path) lives in resources-view.js.
function renderResources(o) {
  return renderResourcesView(o, { el, state, openFile, fileUrl, formatSize });
}

function renderProblems(o) {
  return problemsView.show(o);
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

// The Problems tab (problems-view.js): filter, severity toggles, grouping by rule / file, copy for bug reports.
const problemsView = createProblemsView({ el, getModId: () => state.currentId, openFile: (path, line) => openFile(path, line) });

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

// Editor width: its share of the area right of the sidebar, so it keeps its proportion when the window is resized.
const viewerSeparator = $('#viewer-resize');
let viewerFraction = VIEWER_DEFAULT;
function setViewerFraction(fraction, save = true) {
  viewerFraction = Math.max(VIEWER_MIN, Math.min(VIEWER_MAX, fraction));
  $('.app').style.setProperty('--viewer-frac', viewerFraction.toFixed(3));
  viewerSeparator.setAttribute('aria-valuenow', String(Math.round(viewerFraction * 100)));
  if (save) uiState.setViewerFraction(Math.abs(viewerFraction - VIEWER_DEFAULT) < 0.005 ? null : viewerFraction);
}
setViewerFraction(uiState.get().viewerFraction ?? VIEWER_DEFAULT, false);
{
  let drag = null;
  const fractionAt = (clientX) => {
    const app = $('.app').getBoundingClientRect();
    const area = app.width - sidebarWidth - 6;
    return area > 0 ? (app.right - clientX) / area : viewerFraction;
  };
  viewerSeparator.addEventListener('pointerdown', (e) => {
    if (e.button !== undefined && e.button > 0) return;
    drag = true;
    viewerSeparator.setPointerCapture?.(e.pointerId);
    viewerSeparator.classList.add('dragging');
    e.preventDefault();
  });
  viewerSeparator.addEventListener('pointermove', (e) => { if (drag) setViewerFraction(fractionAt(e.clientX), false); });
  const end = (e) => {
    if (!drag) return;
    drag = null;
    viewerSeparator.classList.remove('dragging');
    try { viewerSeparator.releasePointerCapture?.(e.pointerId); } catch { /* already released */ }
    setViewerFraction(viewerFraction);
  };
  viewerSeparator.addEventListener('pointerup', end);
  viewerSeparator.addEventListener('pointercancel', end);
  viewerSeparator.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') { e.preventDefault(); setViewerFraction(viewerFraction + 0.03); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); setViewerFraction(viewerFraction - 0.03); }
    else if (e.key === 'Home') { e.preventDefault(); setViewerFraction(VIEWER_MAX); }
    else if (e.key === 'End') { e.preventDefault(); setViewerFraction(VIEWER_MIN); }
  });
  viewerSeparator.addEventListener('dblclick', () => setViewerFraction(VIEWER_DEFAULT));
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

// "Add to mod": building blocks (copies of vanilla prototypes) for the open, editable mod.
const contentDialog = createContentDialog({
  el, api,
  onAdded: async (mod, result) => {
    const files = [...result.create, ...result.change].join(', ');
    showImportStatus(() => ({ text: t('content.added', { title: result.title, files }) }));
    if (state.currentId !== mod.id) return;
    await refreshOverview();
    await openFile(`prototypes/${result.name}.lua`);
  },
});

function openContentDialog() {
  const mod = currentMod();
  if (!mod?.editable) return;
  const info = state.overview?.info || {};
  contentDialog.open({ id: mod.id, name: info.name || mod.name, title: info.title, factorioVersion: info.factorioVersion });
}

const actionBar = createActionBar({
  el, mount: $('#action-bar'), buildView, onAddContent: openContentDialog,
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
  syncProblems();
}

// Problems: the mod's own checks plus what the game said in its newest smoke test or run.
let problemsSignature = '';
function syncProblems() {
  if (!state.overview) { problemsSignature = ''; bottom.setIssues(null); return; }
  const game = buildView.gameIssues();
  const signature = `${state.currentId}|${state.overview.issues?.length ?? 0}|${JSON.stringify(game)}`;
  if (signature === problemsSignature) return;
  problemsSignature = signature;
  bottom.setIssues([...(state.overview.issues || []), ...game]);
}
buildView.onChange(syncProblems);

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
      add('try', t('ui.cmd.try'), () => buildView.start('try', 'palette'));
    }
  }
  add('new-mod', t('ui.cmd.newMod'), openNewModDialog);
  if (currentMod()?.editable) add('add-content', t('ui.cmd.addContent'), openContentDialog);
  if (currentMod()) {
    const mod = currentMod();
    add('pin-mod', t(isPinned(mod.id) ? 'ui.cmd.unpinMod' : 'ui.cmd.pinMod', { name: mod.name }), () => togglePin(mod.id));
  }
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

// A collected folder is `{ entries, label }`: every file as `{ file, path, size }` with its path relative to the folder.
// Hidden files and folders are left out; which files a mod may contain is decided once the mods in it are known.

async function collectEntry(entry, prefix, out) {
  if (entry.name.startsWith('.')) return;
  if (entry.isFile) {
    const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
    out.push({ file, path: prefix + entry.name, size: file.size });
  } else if (entry.isDirectory) {
    for (const child of await readAllEntries(entry)) await collectEntry(child, `${prefix}${entry.name}/`, out);
  }
}

async function collectDroppedFolder(entry) {
  const entries = [];
  for (const child of await readAllEntries(entry)) await collectEntry(child, '', entries);
  return { entries, label: entry.name };
}

function collectInputFolder(fileList) {
  const entries = [];
  let label = t('import.folderLabel');
  for (const file of fileList) {
    const parts = (file.webkitRelativePath || file.name).split('/');
    if (parts.length > 1) label = parts[0];
    const path = parts.length > 1 ? parts.slice(1).join('/') : parts[0];
    if (!path.split('/').some((p) => p.startsWith('.'))) entries.push({ file, path, size: file.size });
  }
  return { entries, label };
}

/** What importFolder uploads for one mod: its usable files and how many others were left out. */
function uploadOf(files, label) {
  const usable = files.filter((f) => isUsableFile(f.path));
  return { files: usable, skipped: files.length - usable.length, label };
}

// ---------------------------------------------------------------- pre-check of a folder before anything is uploaded

const MAX_INFO_BYTES = 1024 * 1024;

async function infoOf(files) {
  const info = files.find((f) => f.path === 'info.json');
  if (!info || info.size > MAX_INFO_BYTES) return { problem: 'import.check.infoJson' };
  try { return readInfo(await info.file.text()); } catch { return { problem: 'import.check.infoJson' }; }
}

const workspaceModNamed = (name) => state.mods.find((m) => m.source === 'workspace' && m.name.toLowerCase() === name?.toLowerCase());

/**
 * Looks at a collected folder before uploading it. A single mod with no problems is added right away; a problem is shown
 * without uploading anything. A folder of mods (like Factorio's mods folder) opens a list to choose from.
 */
async function checkFolder({ entries, label }) {
  const found = findMods(entries);
  if (found.layout === 'none') { importText('import.check.none', { label }); return; }

  importText('import.check.reading', { label });
  const candidates = await Promise.all([
    ...found.mods.map(async (m) => {
      const upload = uploadOf(m.files, m.folder || label);
      return { kind: 'folder', label: upload.label, ...(await infoOf(m.files)), files: upload.files.length, upload };
    }),
    ...found.zips.map((z) => ({ kind: 'zip', label: z.path, ...zipIdentity(z.path), size: z.size, file: z.file })),
  ]);
  const checked = assess(candidates, state.mods.filter((m) => m.source === 'workspace').map((m) => m.name));
  showImportStatus(null);

  if (found.layout === 'single') {
    const only = checked[0];
    if (only.status === 'ok') { enqueue([{ kind: 'folder', folder: only.upload, label }]); return; }
    const existing = only.reason === 'import.check.exists' ? workspaceModNamed(only.name) : null;
    showImportStatus(() => ({
      text: t('import.failure', { label, message: t(only.reason, only.params) }),
      actions: existing ? [el('button', { type: 'button', text: t('import.openExisting'), onclick: () => go(existing.id, 'overview') })] : [],
    }));
    return;
  }
  await chooseMods(label, checked, found.others);
}

let importCheck = null;

function importCheckDialog() {
  if (importCheck) return importCheck;
  const refs = {
    dialog: el('dialog', { class: 'import-check-dialog', 'aria-labelledby': 'ic-heading', 'data-testid': 'import-check' }),
    heading: el('h2', { id: 'ic-heading' }),
    hint: el('p', { class: 'small muted' }),
    summary: el('p', { class: 'ic-summary', 'data-testid': 'import-check-summary' }),
    list: el('div', { class: 'ic-list', role: 'list' }),
    others: el('p', { class: 'small muted ic-others' }),
    cancel: el('button', { type: 'button', 'data-testid': 'import-check-cancel' }),
    confirm: el('button', { type: 'button', class: 'primary', 'data-testid': 'import-check-add' }),
  };
  refs.dialog.append(refs.heading, refs.hint, refs.summary, refs.list, refs.others, el('div', { class: 'nm-actions' }, refs.cancel, refs.confirm));
  document.body.append(refs.dialog);
  importCheck = refs;
  return refs;
}

/** Shows the mods found in a folder with what the pre-check says about each; resolves once the dialog is closed. */
function chooseMods(label, checked, others) {
  const refs = importCheckDialog();
  const ready = checked.filter((c) => c.status === 'ok');
  refs.heading.textContent = t('import.check.heading', { label });
  refs.hint.textContent = t('import.check.hint');
  refs.summary.textContent = t('import.check.summary', { found: checked.length, ready: ready.length });
  refs.others.hidden = others.length === 0;
  refs.others.textContent = others.length ? t('import.check.others', { names: others.slice(0, 8).join(', ') + (others.length > 8 ? ', …' : '') }) : '';
  refs.cancel.textContent = t('dialog.cancel');

  const boxes = new Map();
  refs.list.replaceChildren(...checked.map((c) => {
    const ok = c.status === 'ok';
    const box = el('input', { type: 'checkbox', checked: ok, disabled: !ok, 'aria-label': c.name || c.label });
    if (ok) boxes.set(box, c);
    const shownName = c.name || c.label;
    return el('label', { class: 'ic-row' + (ok ? '' : ' blocked'), role: 'listitem' },
      box,
      el('span', { class: 'ic-main' },
        el('span', {}, el('strong', { text: shownName }), c.version ? el('span', { class: 'muted', text: ` ${c.version}` }) : null),
        c.title ? el('span', { class: 'small muted ic-title', text: c.title }) : null,
        c.label !== shownName ? el('span', { class: 'small muted ic-folder', text: c.label }) : null),
      el('span', { class: 'small muted ic-size', text: c.kind === 'zip' ? formatSize(c.size) : t('count.file', { n: c.files }) }),
      el('span', { class: 'small ic-status ' + (ok ? 'good' : 'bad'), text: t(c.reason, c.params) }));
  }));

  const selected = () => [...boxes].filter(([box]) => box.checked).map(([, c]) => c);
  const update = () => {
    const n = selected().length;
    refs.confirm.textContent = t('import.check.add', { n });
    refs.confirm.disabled = n === 0;
  };
  refs.list.onchange = update;
  update();

  return new Promise((resolve) => {
    refs.cancel.onclick = () => refs.dialog.close();
    refs.confirm.onclick = () => {
      const jobs = selected().map((c) => (c.kind === 'zip' ? zipJob(c.file) : { kind: 'folder', folder: c.upload, label: c.label }));
      refs.dialog.close();
      enqueue(jobs);
    };
    refs.dialog.onclose = () => resolve();
    refs.dialog.showModal();
    (ready.length ? refs.confirm : refs.cancel).focus();
  });
}

// Folders are checked one after another, so two drops never share the list.
let folderChecks = Promise.resolve();
function checkFolderLater(collect) {
  folderChecks = folderChecks.then(async () => {
    try { await checkFolder(await collect()); }
    catch (error) { showImportStatus(() => ({ text: t('import.failure', { label: t('import.folderLabel'), message: errorText(error) }), actions: [] })); }
  });
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
        else result = await importFolder(job.folder);
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
    // Short: the count and the names that failed; every line (failures first, with the reason) under "Details".
    const shortName = (label) => zipIdentity(label)?.name || label;
    showImportStatus(() => ({
      text: t('import.summary', {
        good: good.length, total,
        details: bad.length ? t('import.notImported', { names: bad.map((o) => shortName(o.label)).join(', ') }) : '',
      }).trim(),
      actions: [
        ...(bad.length === 1 ? bad[0].failure().actions : []),
        el('details', { class: 'import-details' },
          el('summary', { text: t('import.details') }),
          el('ul', {},
            ...bad.map((o) => el('li', { class: 'bad', text: o.failure().text })),
            ...good.map((o) => el('li', { text: okText(o) })))),
      ],
    }));
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
  let folders = 0;
  for (const item of Array.from(e.dataTransfer.items || [])) {
    if (item.kind !== 'file') continue;
    const entry = item.webkitGetAsEntry?.();
    const file = item.getAsFile();
    if (entry?.isDirectory) { folders++; checkFolderLater(() => collectDroppedFolder(entry)); } // checked before uploading
    else if (file && extOf(file.name) === 'zip') jobs.push(zipJob(file));
    else jobs.push({ kind: 'error', label: file?.name || 'item', error: new UiError('import.dropHint') });
  }
  if (!jobs.length && !folders) for (const file of Array.from(e.dataTransfer.files || [])) jobs.push(extOf(file.name) === 'zip' ? zipJob(file) : { kind: 'error', label: file.name, error: new UiError('import.dropHint') });
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
  if (folder.entries.length === 0) return;
  checkFolderLater(() => folder);
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
  renderStartContent();
}

// Starting content: building blocks put into the new mod right away, grouped by category. Ticks survive re-rendering.
let startBlocks = null;

function renderStartContent() {
  const list = $('#nm-content-list');
  const hint = $('#nm-content-hint');
  if (!startBlocks) { list.replaceChildren(); hint.textContent = ''; return; }
  const version = newModVersion();
  const usable = !!version && startBlocks.length > 0 && supportsVersion(startBlocks[0], version);
  const ticked = new Set([...list.querySelectorAll('input:checked')].map((i) => i.value));
  hint.textContent = usable ? t('content.startHint') : t('content.startUnsupported', { versions: startBlocks[0]?.supportedVersions.join(', ') || '' });
  hint.className = 'small nm-hint ' + (usable ? 'muted' : 'bad');
  list.replaceChildren(...CATEGORIES.filter((c) => startBlocks.some((b) => b.category === c)).map((category) => el('div', { class: 'nm-content-group' },
    el('span', { class: 'nm-content-category small muted', text: t(`content.category.${category}`) }),
    el('div', { class: 'nm-content-chips' }, ...startBlocks.filter((b) => b.category === category).map((block) => el('label', { class: 'chip', title: blockDescription(block) },
      el('input', { type: 'checkbox', value: block.id, checked: usable && ticked.has(block.id), disabled: !usable }),
      el('span', { text: blockTitle(block) })))))));
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
  $('#nm-content-list').replaceChildren();
  loadBlocks(api).then((blocks) => { startBlocks = blocks; renderStartContent(); }).catch(() => { /* the dialog works without blocks */ });
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
    content: [...$('#nm-content-list').querySelectorAll('input:checked')].map((i) => i.value),
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

// ---------------------------------------------------------------- updates

const updateView = createUpdateView({ el, api, notice: $('#update-notice'), version: $('#studio-version'), channel: $('#update-channel') });

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
  contentDialog.retranslate();
  renderStartContent();
  bottom.retranslate();
  searchPanel.retranslate();
  palette.retranslate();
  updateView.retranslate();
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
  updateView.load(); // in the background: GitHub may be slow or unreachable
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
