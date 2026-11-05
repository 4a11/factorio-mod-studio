// Small, defensive memory of the user's layout choices: theme, sidebar width, bottom panel, last mod / tab, open editor tabs.
// One JSON document in localStorage under `fms.ui.v1`. Storage can be missing, blocked or hold garbage: every access is wrapped,
// unknown shapes are ignored field by field, and the UI works exactly the same without it (the state then lives in memory only).
// Plain ES module, no DOM access at import time.

export const STORAGE_KEY = 'fms.ui.v1';
export const THEMES = ['auto', 'light', 'dark'];
export const MOD_TABS = ['overview', 'files', 'prototypes', 'resources', 'locale', 'problems', 'build'];
export const BOTTOM_TABS = ['problems', 'output', 'search'];
export const SIDEBAR_MIN = 200;
export const SIDEBAR_MAX = 480;
export const SIDEBAR_DEFAULT = 280;
const MAX_MODS = 40;
const MAX_PATHS = 20;
const MAX_RECENT = 30;

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const isString = (v, max = 4096) => typeof v === 'string' && v.length > 0 && v.length <= max;
const finite = (v) => typeof v === 'number' && Number.isFinite(v);

export const defaults = () => ({
  theme: 'auto',
  sidebarWidth: null,
  bottom: { open: false, height: null, tab: 'problems' },
  lastMod: null,
  lastTab: {},
  editorTabs: {},
  recent: {},
});

function perMod(raw, convert) {
  const out = {};
  if (!isObject(raw)) return out;
  for (const [id, value] of Object.entries(raw).slice(-MAX_MODS)) {
    if (!isString(id, 512)) continue;
    const converted = convert(value);
    if (converted !== undefined) out[id] = converted;
  }
  return out;
}

const pathList = (raw, max) => (Array.isArray(raw) ? [...new Set(raw.filter((p) => isString(p)))].slice(0, max) : []);

/** Turns whatever was stored into a valid state object; anything unexpected falls back to the default for that field. */
export function sanitize(raw) {
  const state = defaults();
  if (!isObject(raw)) return state;
  if (THEMES.includes(raw.theme)) state.theme = raw.theme;
  if (finite(raw.sidebarWidth)) state.sidebarWidth = Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN, Math.round(raw.sidebarWidth)));
  if (isObject(raw.bottom)) {
    state.bottom.open = raw.bottom.open === true;
    if (finite(raw.bottom.height) && raw.bottom.height > 0) state.bottom.height = Math.round(raw.bottom.height);
    if (BOTTOM_TABS.includes(raw.bottom.tab)) state.bottom.tab = raw.bottom.tab;
  }
  if (isString(raw.lastMod, 512)) state.lastMod = raw.lastMod;
  state.lastTab = perMod(raw.lastTab, (v) => (MOD_TABS.includes(v) ? v : undefined));
  state.editorTabs = perMod(raw.editorTabs, (v) => {
    if (!isObject(v)) return undefined;
    const paths = pathList(v.paths, MAX_PATHS);
    return { paths, active: isString(v.active) && paths.includes(v.active) ? v.active : (paths[paths.length - 1] ?? null) };
  });
  state.recent = perMod(raw.recent, (v) => (Array.isArray(v) ? pathList(v, MAX_RECENT) : undefined));
  return state;
}

function defaultStorage() {
  try { return globalThis.localStorage ?? null; } catch { return null; }
}

/** @param {{ getItem(k: string): string|null, setItem(k: string, v: string): void }|null} [storage] */
export function createUiState(storage = defaultStorage()) {
  let state = defaults();
  try {
    const raw = storage?.getItem(STORAGE_KEY);
    if (raw) state = sanitize(JSON.parse(raw));
  } catch { /* corrupt JSON or blocked storage: start from the defaults */ }

  const persist = () => {
    try { storage?.setItem(STORAGE_KEY, JSON.stringify(state)); } catch { /* not remembered, still works */ }
  };

  const remember = (map, id, value) => {
    delete map[id]; // re-insert so the most recently used mod is last
    map[id] = value;
    for (const key of Object.keys(map).slice(0, Math.max(0, Object.keys(map).length - MAX_MODS))) delete map[key];
  };

  return {
    get: () => state,
    persist,
    setTheme(mode) { if (THEMES.includes(mode)) { state.theme = mode; persist(); } },
    setSidebarWidth(width) { state.sidebarWidth = finite(width) ? Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN, Math.round(width))) : null; persist(); },
    setBottom(patch) {
      if (typeof patch.open === 'boolean') state.bottom.open = patch.open;
      if (patch.height === null || (finite(patch.height) && patch.height > 0)) state.bottom.height = patch.height === null ? null : Math.round(patch.height);
      if (BOTTOM_TABS.includes(patch.tab)) state.bottom.tab = patch.tab;
      persist();
    },
    setLastMod(id) { state.lastMod = isString(id, 512) ? id : null; persist(); },
    setLastTab(id, tab) { if (isString(id, 512) && MOD_TABS.includes(tab)) { remember(state.lastTab, id, tab); persist(); } },
    setEditorTabs(id, paths, active) {
      if (!isString(id, 512)) return;
      const list = pathList(paths, MAX_PATHS);
      if (list.length === 0) delete state.editorTabs[id];
      else remember(state.editorTabs, id, { paths: list, active: list.includes(active) ? active : list[list.length - 1] });
      persist();
    },
    setRecent(id, paths) { if (isString(id, 512)) { remember(state.recent, id, pathList(paths, MAX_RECENT)); persist(); } },
  };
}
