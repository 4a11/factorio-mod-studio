// Interface translations for Factorio Mod Studio. Plain ES module: nothing here touches the DOM at import time,
// so it can be imported from Node as well.
//
// Server-originated texts (API errors, validator messages, template descriptions, mod content) are intentionally not
// translated; only the UI chrome goes through `t()`.

export const LANGUAGES = [
  { code: 'en', name: 'English' },
  { code: 'ru', name: 'Русский' },
  { code: 'de', name: 'Deutsch' },
  { code: 'zh-CN', name: '简体中文' },
  { code: 'uk', name: 'Українська' },
  { code: 'pl', name: 'Polski' },
  { code: 'fr', name: 'Français' },
  { code: 'es', name: 'Español' },
];

const DEFAULT_LANGUAGE = 'en';
const STORAGE_KEY = 'fms-lang';

const isDict = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const lookup = (dict, key) => (isDict(dict) && Object.prototype.hasOwnProperty.call(dict, key) && typeof dict[key] === 'string' ? dict[key] : undefined);

/**
 * Pure translator factory.
 *  - `t(key)` -> dict[key], else fallback[key], else the key itself.
 *  - `{name}` placeholders are replaced from params (unknown ones stay as they are); `{{` and `}}` give literal braces.
 *  - When `params.n` is a number, `key.<plural category of n>` is preferred, then `key.other`, then plain `key`.
 * Never throws.
 */
export function createTranslator({ dict, fallback = {}, locale = DEFAULT_LANGUAGE } = {}) {
  let rules = null;
  try { rules = new Intl.PluralRules(locale); } catch {
    try { rules = new Intl.PluralRules(DEFAULT_LANGUAGE); } catch { rules = null; }
  }

  const find = (source, key, n) => {
    if (n !== undefined && rules) {
      let category = 'other';
      try { category = rules.select(n); } catch { /* keep 'other' */ }
      const exact = lookup(source, `${key}.${category}`);
      if (exact !== undefined) return exact;
      const other = lookup(source, `${key}.other`);
      if (other !== undefined) return other;
    }
    return lookup(source, key);
  };

  return function t(key, params = {}) {
    try {
      if (typeof key !== 'string') return String(key);
      const p = isDict(params) ? params : {};
      const n = typeof p.n === 'number' && Number.isFinite(p.n) ? p.n : undefined;
      const template = find(dict, key, n) ?? find(fallback, key, n);
      if (template === undefined) return key;
      return template.replace(/\{\{|\}\}|\{([^{}\s]+)\}/g, (match, name) => {
        if (match === '{{') return '{';
        if (match === '}}') return '}';
        return Object.prototype.hasOwnProperty.call(p, name) ? String(p[name]) : match;
      });
    } catch {
      return typeof key === 'string' ? key : '';
    }
  };
}

// ------------------------------------------------------------------ global state

let currentCode = DEFAULT_LANGUAGE;
let englishDict = {};
let activeDict = {};
let globalT = createTranslator({ dict: {}, fallback: {}, locale: DEFAULT_LANGUAGE });
const cache = new Map();
const listeners = new Set();

const isSupported = (code) => LANGUAGES.some((l) => l.code === code);
const normalize = (code) => (isSupported(code) ? code : DEFAULT_LANGUAGE);

function readStored() {
  try { return globalThis.localStorage?.getItem(STORAGE_KEY) || null; } catch { return null; }
}
function writeStored(code) {
  try { globalThis.localStorage?.setItem(STORAGE_KEY, code); } catch { /* storage unavailable: the choice just is not remembered */ }
}

async function loadDictionary(code, fetchImpl) {
  if (cache.has(code)) return cache.get(code);
  let dict = {};
  try {
    const response = await fetchImpl(`/i18n/${code}.json`);
    if (response && response.ok) {
      const parsed = await response.json();
      if (isDict(parsed)) dict = parsed;
    }
  } catch { /* missing dictionary: fall back to English / keys */ }
  if (Object.keys(dict).length) cache.set(code, dict);
  return dict;
}

let lastFetch = null;

async function activate(code, fetchImpl) {
  const target = normalize(code);
  englishDict = await loadDictionary(DEFAULT_LANGUAGE, fetchImpl);
  activeDict = target === DEFAULT_LANGUAGE ? englishDict : await loadDictionary(target, fetchImpl);
  currentCode = target;
  globalT = createTranslator({ dict: activeDict, fallback: englishDict, locale: target });
  return target;
}

/** Loads English (the fallback) and the remembered language; call (and await) before the first render. */
export async function initI18n(fetchImpl = globalThis.fetch) {
  lastFetch = fetchImpl;
  await activate(readStored() ?? DEFAULT_LANGUAGE, fetchImpl);
  applyStaticTranslations();
  return currentCode;
}

/** Switches the language, remembers the choice, retranslates static markup and notifies listeners. */
export async function setLanguage(code) {
  const target = await activate(code, lastFetch || globalThis.fetch);
  writeStored(target);
  applyStaticTranslations();
  for (const listener of [...listeners]) {
    try { listener(target); } catch { /* a broken listener must not block the others */ }
  }
  return target;
}

export function getLanguage() { return currentCode; }

/** Calls `listener(code)` after every successful language change. Returns an unsubscribe function. */
export function onLanguageChange(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function t(key, params) { return globalT(key, params); }

/** Number formatting in the active language (used for sizes and recipe amounts). */
export function formatNumber(value, options = {}) {
  try { return new Intl.NumberFormat(currentCode, options).format(value); } catch { return String(value); }
}

/** Translates static markup: data-i18n (text), data-i18n-placeholder, data-i18n-aria-label, data-i18n-title. */
export function applyStaticTranslations(root = globalThis.document) {
  const doc = globalThis.document;
  if (!root || !doc) return;
  doc.documentElement.lang = currentCode;
  const attributes = [['data-i18n-placeholder', 'placeholder'], ['data-i18n-aria-label', 'aria-label'], ['data-i18n-title', 'title']];
  const visit = (node) => {
    if (node.hasAttribute?.('data-i18n')) node.textContent = t(node.getAttribute('data-i18n'));
    for (const [source, target] of attributes) {
      if (node.hasAttribute?.(source)) node.setAttribute(target, t(node.getAttribute(source)));
    }
  };
  if (root.nodeType === 1) visit(root);
  for (const node of root.querySelectorAll('[data-i18n],[data-i18n-placeholder],[data-i18n-aria-label],[data-i18n-title]')) visit(node);
}
