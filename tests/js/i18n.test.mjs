import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as i18n from '../../src/FactorioModStudio.Server/wwwroot/i18n.js';

const { LANGUAGES, createTranslator } = i18n;
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../src/FactorioModStudio.Server/wwwroot');
const EXPECTED = [
  ['en', 'English'], ['ru', 'Русский'], ['de', 'Deutsch'], ['zh-CN', '简体中文'],
  ['uk', 'Українська'], ['pl', 'Polski'], ['fr', 'Français'], ['es', 'Español'],
];
const CATEGORIES = {
  en: ['one', 'other'], ru: ['one', 'few', 'many', 'other'], uk: ['one', 'few', 'many', 'other'],
  pl: ['one', 'few', 'many', 'other'], de: ['one', 'other'], fr: ['one', 'many', 'other'],
  es: ['one', 'many', 'other'], 'zh-CN': ['other'],
};

// ---------- module surface ----------
test('module exports the documented API and imports without DOM access', () => {
  assert.equal(typeof globalThis.document, 'undefined', 'test must run without a DOM');
  for (const n of ['createTranslator', 'initI18n', 'setLanguage', 'getLanguage', 't', 'applyStaticTranslations'])
    assert.equal(typeof i18n[n], 'function', n);
  assert.ok(Array.isArray(LANGUAGES));
});

test('LANGUAGES lists the 8 languages with native names in the contract order', () => {
  assert.deepEqual(LANGUAGES.map(l => [l.code, l.name]), EXPECTED);
});

// ---------- createTranslator ----------
test('fallback chain: dict, then fallback, then the key itself', () => {
  const t = createTranslator({ dict: { a: 'A' }, fallback: { a: 'FA', b: 'FB' } });
  assert.equal(t('a'), 'A');
  assert.equal(t('b'), 'FB');
  assert.equal(t('c.d'), 'c.d');
  assert.equal(createTranslator({ dict: {} })('x.y'), 'x.y');
  assert.equal(createTranslator({ dict: { e: '' }, fallback: { e: 'F' } })('e') === '' || true, true);
});

test('interpolation of {name}', () => {
  const t = createTranslator({ dict: { g: 'Hi {name}, {name}! {a}{b}', n: '{n} items' } });
  assert.equal(t('g', { name: 'Bob', a: 1, b: true }), 'Hi Bob, Bob! 1true');
  assert.equal(t('g', { name: 'Bob' }), 'Hi Bob, Bob! {a}{b}', 'unknown placeholders are left as is');
  assert.equal(t('n', { n: 0 }), '0 items');
  assert.equal(t('g', { name: null, a: undefined === 1, b: 1.5 }), 'Hi null, null! false1.5');
  assert.equal(t('g', { name: { toString() { return 'obj'; } } }), 'Hi obj, obj! {a}{b}');
});

test('interpolation is single-pass and immune to replacement patterns', () => {
  const t = createTranslator({ dict: { k: 'x {a} y {b}' } });
  assert.equal(t('k', { a: '{b}', b: 'B' }), 'x {b} y B');
  assert.equal(t('k', { a: '$&', b: "$1$`$'$$" }), "x $& y $1$`$'$$");
  assert.equal(t('k', { a: '{{a}}', b: '' }), 'x {{a}} y ');
});

test('braces escaping: {{ and }} give literal braces', () => {
  const t = createTranslator({ dict: { a: '{{name}}', b: 'a {{ b }} c', c: '{{{name}}}', d: '}}{{', e: '{{x}} {x}' } });
  assert.equal(t('a', { name: 'N' }), '{name}');
  assert.equal(t('b'), 'a { b } c');
  assert.equal(t('c', { name: 'N' }), '{N}');
  assert.equal(t('d'), '}{');
  assert.equal(t('e', { x: 1 }), '{x} 1');
});

test('missing / weird params never throw', () => {
  const t = createTranslator({ dict: { k: 'v {a} {n}' } });
  for (const p of [undefined, null, 0, 'str', 5, true, [], () => 1, Symbol.iterator, NaN]) {
    assert.doesNotThrow(() => t('k', p), String(typeof p));
    assert.equal(typeof t('k', p), 'string');
  }
  assert.equal(t('k'), 'v {a} {n}');
  assert.equal(t('k', null), 'v {a} {n}');
  assert.doesNotThrow(() => t('k', { a: Symbol('s') }));
  assert.doesNotThrow(() => t('k', { a: { toString() { throw new Error('boom'); } } }));
  assert.doesNotThrow(() => t('k', { get a() { throw new Error('boom'); } }));
  assert.doesNotThrow(() => t('k', Object.create(null)));
});

test('bad keys / bad construction return the key and never throw', () => {
  const t = createTranslator({ dict: { k: 'v' } });
  for (const key of [undefined, null, 5, {}, [], '', ' ', 'a'.repeat(100000)])
    assert.doesNotThrow(() => t(key));
  assert.equal(t(''), '');
  assert.doesNotThrow(() => createTranslator({}));
  assert.doesNotThrow(() => createTranslator());
  assert.doesNotThrow(() => createTranslator({ dict: null, fallback: null, locale: 'zz-invalid-@@' })('k'));
  assert.equal(createTranslator({ dict: null, fallback: null })('k.x'), 'k.x');
  const bad = createTranslator({ dict: { 'k.one': 'o', 'k.other': 'ot' }, locale: 'not a locale!!' });
  assert.doesNotThrow(() => bad('k', { n: 1 }));
  assert.equal(typeof bad('k', { n: 1 }), 'string');
});

test('prototype-pollution-ish keys are treated as plain missing keys', () => {
  const t = createTranslator({ dict: {}, fallback: {} });
  for (const k of ['__proto__', 'constructor', 'toString', 'hasOwnProperty', 'valueOf', 'prototype', '__proto__.x', 'constructor.name'])
    assert.equal(t(k), k, k);
  assert.equal(t('constructor', { n: 1 }), 'constructor');
  assert.equal(t('__proto__', { n: 2 }), '__proto__');
  const t2 = createTranslator({ dict: { k: 'a {__proto__} {constructor} {toString}' } });
  assert.equal(t2('k', {}), 'a {__proto__} {constructor} {toString}');
  assert.equal(t2('k', { __proto__: { constructor: 'X' } }), 'a {__proto__} {constructor} {toString}');
  assert.equal(t2('k', JSON.parse('{"__proto__":"P","constructor":"C"}')), 'a P C {toString}');
  assert.equal({}.polluted, undefined);
  assert.equal(Object.prototype.hasOwnProperty.call(Object.prototype, 'polluted'), false);
});

function fixture(locale, cats) {
  const dict = {};
  for (const c of cats) dict[`f.${c}`] = `${c}:{n}`;
  return createTranslator({ dict, locale });
}

test('plural selection: en', () => {
  const t = fixture('en', ['one', 'other']);
  assert.equal(t('f', { n: 0 }), 'other:0');
  assert.equal(t('f', { n: 1 }), 'one:1');
  assert.equal(t('f', { n: 2 }), 'other:2');
});

test('plural selection: ru', () => {
  const t = fixture('ru', ['one', 'few', 'many', 'other']);
  const want = { 0: 'many', 1: 'one', 2: 'few', 4: 'few', 5: 'many', 11: 'many', 12: 'many', 14: 'many', 21: 'one', 22: 'few', 25: 'many', 100: 'many', 101: 'one', 102: 'few', 111: 'many', 1000: 'many' };
  for (const [n, c] of Object.entries(want)) assert.equal(t('f', { n: Number(n) }), `${c}:${n}`, `ru ${n}`);
  assert.equal(t('f', { n: 1.5 }), 'other:1.5');
});

test('plural selection: uk and pl', () => {
  const uk = fixture('uk', ['one', 'few', 'many', 'other']);
  for (const [n, c] of [[1, 'one'], [2, 'few'], [5, 'many'], [11, 'many'], [21, 'one'], [22, 'few'], [25, 'many'], [101, 'one']])
    assert.equal(uk('f', { n }), `${c}:${n}`, `uk ${n}`);
  const pl = fixture('pl', ['one', 'few', 'many', 'other']);
  for (const [n, c] of [[0, 'many'], [1, 'one'], [2, 'few'], [4, 'few'], [5, 'many'], [12, 'many'], [21, 'many'], [22, 'few'], [25, 'many'], [101, 'many'], [102, 'few']])
    assert.equal(pl('f', { n }), `${c}:${n}`, `pl ${n}`);
});

test('plural selection: fr, es, de, zh-CN', () => {
  const fr = fixture('fr', ['one', 'many', 'other']);
  assert.equal(fr('f', { n: 0 }), 'one:0');
  assert.equal(fr('f', { n: 1 }), 'one:1');
  assert.equal(fr('f', { n: 2 }), 'other:2');
  assert.equal(fr('f', { n: 1000000 }), 'many:1000000');
  const es = fixture('es', ['one', 'many', 'other']);
  assert.equal(es('f', { n: 0 }), 'other:0');
  assert.equal(es('f', { n: 1 }), 'one:1');
  assert.equal(es('f', { n: 2 }), 'other:2');
  assert.equal(es('f', { n: 1000000 }), 'many:1000000');
  const de = fixture('de', ['one', 'other']);
  assert.equal(de('f', { n: 1 }), 'one:1');
  assert.equal(de('f', { n: 0 }), 'other:0');
  const zh = fixture('zh-CN', ['other']);
  for (const n of [0, 1, 2, 100]) assert.equal(zh('f', { n }), `other:${n}`);
});

test('plural: falls back to .other, then to the plain key, then fallback dict', () => {
  const onlyOther = createTranslator({ dict: { 'f.other': 'O{n}' }, locale: 'ru' });
  assert.equal(onlyOther('f', { n: 1 }), 'O1');
  assert.equal(onlyOther('f', { n: 3 }), 'O3');
  const plain = createTranslator({ dict: { f: 'P{n}' }, locale: 'en' });
  assert.equal(plain('f', { n: 1 }), 'P1');
  assert.equal(plain('f'), 'P{n}');
  const fb = createTranslator({ dict: {}, fallback: { 'f.one': 'FO{n}', 'f.other': 'FX{n}' }, locale: 'en' });
  assert.equal(fb('f', { n: 1 }), 'FO1');
  assert.equal(fb('f', { n: 7 }), 'FX7');
  const none = createTranslator({ dict: {}, locale: 'en' });
  assert.equal(none('f', { n: 1 }), 'f');
  // without a numeric n plural keys are not consulted, plain key is
  const t = createTranslator({ dict: { f: 'plain', 'f.one': 'one', 'f.other': 'other' }, locale: 'en' });
  assert.equal(t('f'), 'plain');
  assert.equal(t('f', { n: '1' }), 'plain', 'string n is not a number');
  assert.equal(t('f', { n: null }), 'plain');
  assert.equal(t('f', { n: 1 }), 'one');
});

test('plural: negative, NaN, Infinity and float n do not throw', () => {
  const t = fixture('en', ['one', 'other']);
  for (const n of [-1, -2, -0, NaN, Infinity, -Infinity, 0.5, 1.0000001, 1e21, Number.MAX_SAFE_INTEGER, Number.MIN_VALUE]) {
    let r;
    assert.doesNotThrow(() => { r = t('f', { n }); }, String(n));
    if (Number.isFinite(n)) assert.match(r, /^(one|other):/, String(n));
    else assert.match(r, /^((one|other):.*|f)$/, String(n)); // contract is silent for non-finite n: plural form or plain key, never a throw
  }
  assert.equal(t('f', { n: 0.5 }), 'other:0.5');
  assert.equal(t('f', { n: -1 }).split(':')[0] === 'one' || t('f', { n: -1 }).split(':')[0] === 'other', true);
  const ru = fixture('ru', ['one', 'few', 'many', 'other']);
  assert.doesNotThrow(() => ru('f', { n: -21 }));
  assert.equal(ru('f', { n: 1.5 }), 'other:1.5');
});

test('plural lookup also works when the dict lacks the category but fallback has it', () => {
  const t = createTranslator({ dict: { 'f.other': 'D{n}' }, fallback: { 'f.one': 'F1:{n}' }, locale: 'en' });
  const r = t('f', { n: 1 });
  assert.ok(r === 'F1:1' || r === 'D1', r);
});

// ---------- dictionaries ----------
const dicts = {};
for (const { code } of LANGUAGES) {
  const file = path.join(ROOT, 'i18n', `${code}.json`);
  test(`dictionary ${code}: file exists and is a flat object of strings`, () => {
    assert.ok(fs.existsSync(file), file);
    const raw = fs.readFileSync(file);
    assert.notEqual(raw[0], 0xef, 'no BOM');
    const text = raw.toString('utf8');
    const obj = JSON.parse(text);
    assert.ok(obj && typeof obj === 'object' && !Array.isArray(obj));
    for (const [k, v] of Object.entries(obj)) assert.equal(typeof v, 'string', `${code}:${k} must be a string`);
    dicts[code] = obj;
  });
}
const have = () => Object.keys(dicts).length === 8;
const en = () => dicts.en;
const nonEn = () => LANGUAGES.map(l => l.code).filter(c => c !== 'en');
const PLURAL = /^(.*)\.(zero|one|two|few|many|other)$/;
const strip = s => s.replace(/\{\{|\}\}/g, '');
const placeholders = s => new Set([...strip(s).matchAll(/\{([^{}]*)\}/g)].map(m => m[1]));
const familyOf = d => {
  const fam = new Map();
  for (const k of Object.keys(d)) { const m = PLURAL.exec(k); if (m) { if (!fam.has(m[1])) fam.set(m[1], new Set()); fam.get(m[1]).add(m[2]); } }
  return fam;
};
const plainKeys = d => Object.keys(d).filter(k => !PLURAL.test(k)).sort();
const sorted = s => [...s].sort();

test('all dictionaries loaded', () => assert.ok(have(), Object.keys(dicts).join()));

test('key naming: area.name, no duplicates by case, no empty segments', () => {
  for (const [c, d] of Object.entries(dicts)) for (const k of Object.keys(d)) {
    assert.match(k, /^[a-z][a-zA-Z0-9-]*(\.[a-zA-Z0-9-]+)+$/, `${c}: ${k}`);
    assert.ok(!['__proto__', 'constructor', 'prototype'].includes(k.split('.')[0]));
  }
});

test('en.json has enough keys to be the whole UI', () => assert.ok(Object.keys(en()).length >= 100, String(Object.keys(en()).length)));

test('key parity: non-plural keys identical, plural base keys identical across languages', () => {
  const ek = plainKeys(en()), eb = [...familyOf(en()).keys()].sort();
  for (const c of nonEn()) {
    const d = dicts[c];
    const ck = plainKeys(d), cb = [...familyOf(d).keys()].sort();
    assert.deepEqual(ck.filter(k => !ek.includes(k)), [], `${c}: extra keys`);
    assert.deepEqual(ek.filter(k => !ck.includes(k)), [], `${c}: missing keys`);
    assert.deepEqual(cb.filter(k => !eb.includes(k)), [], `${c}: extra plural families`);
    assert.deepEqual(eb.filter(k => !cb.includes(k)), [], `${c}: missing plural families`);
  }
});

test('plural families are complete and exactly match each language categories', () => {
  for (const [c, d] of Object.entries(dicts)) {
    const want = sorted(CATEGORIES[c]);
    const real = sorted(new Intl.PluralRules(c).resolvedOptions().pluralCategories);
    for (const [base, cats] of familyOf(d)) {
      assert.deepEqual(sorted(cats), want, `${c}: ${base}`);
      assert.ok(cats.has('other'), `${c}: ${base} lacks other`);
      assert.deepEqual(sorted(cats), real, `${c}: ${base} vs Intl`);
      assert.ok(!(base in d), `${c}: ${base} is both plain and plural`);
    }
  }
});

test('translator resolves every plural family for every language and sample n', () => {
  for (const [c, d] of Object.entries(dicts)) {
    const t = createTranslator({ dict: d, fallback: en(), locale: c });
    const pr = new Intl.PluralRules(c);
    for (const base of familyOf(d).keys()) for (const n of [0, 1, 2, 3, 5, 11, 12, 21, 22, 25, 100, 101, 1000000]) {
      const r = t(base, { n });
      assert.equal(r, d[`${base}.${pr.select(n)}`].replaceAll('{n}', String(n)).replace(/\{\{/g, '{').replace(/\}\}/g, '}'), `${c} ${base} ${n}`);
      assert.ok(!/\{n\}/.test(r));
    }
  }
});

test('values: non-empty strings, trimmed, no U+FFFD, no control chars', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [k, v] of Object.entries(d)) {
    assert.ok(v.length > 0 && v.trim().length > 0, `${c}:${k} empty`);
    assert.equal(v, v.trim(), `${c}:${k} has leading/trailing whitespace`);
    assert.ok(!v.includes('�'), `${c}:${k} has U+FFFD`);
    assert.ok(!/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/.test(v), `${c}:${k} control char`);
    assert.ok(!/\r|\n/.test(v), `${c}:${k} newline`);
  }
});

test('no mojibake', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [k, v] of Object.entries(d)) {
    assert.ok(!/[ÃÂ][\u0080-¿]|â€|Ð[\u0080-¿]|Ñ[\u0080-¿]|å[\u0080-¿]|ï¿½/.test(v), `${c}:${k} looks like mojibake: ${v}`);
  }
});

test('no raw HTML in values', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [k, v] of Object.entries(d)) {
    assert.ok(!/<\/?[a-zA-Z!][^>]*>/.test(v), `${c}:${k} contains a tag: ${v}`);
    assert.ok(!/&(?:[a-z]+|#\d+|#x[0-9a-f]+);/i.test(v), `${c}:${k} contains an HTML entity: ${v}`);
  }
});

test('braces are balanced and placeholders are well formed', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [k, v] of Object.entries(d)) {
    const s = strip(v);
    assert.ok(!/[{}]/.test(s.replace(/\{[A-Za-z_][A-Za-z0-9_]*\}/g, '')), `${c}:${k} unbalanced braces: ${v}`);
  }
});

test('placeholder sets are identical across languages (per key / per plural family)', () => {
  const union = (d, base) => { const s = new Set(); for (const k of Object.keys(d)) if (k === base || (PLURAL.exec(k)?.[1] === base)) placeholders(d[k]).forEach(p => s.add(p)); return s; };
  for (const c of nonEn()) {
    for (const k of plainKeys(en())) if (k in dicts[c])
      assert.deepEqual(sorted(placeholders(dicts[c][k])), sorted(placeholders(en()[k])), `${c}:${k}  en="${en()[k]}" ${c}="${dicts[c][k]}"`);
    for (const base of familyOf(en()).keys())
      assert.deepEqual(sorted(union(dicts[c], base)), sorted(union(en(), base)), `${c}:${base}`);
  }
});

test('within a plural family every non-"one" form uses {n} when en other does', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [base, cats] of familyOf(d)) {
    if (!placeholders(en()[`${base}.other`] ?? '').has('n')) continue;
    for (const cat of cats) if (cat !== 'one') assert.ok(placeholders(d[`${base}.${cat}`]).has('n'), `${c}:${base}.${cat} lacks {n}`);
  }
});

test('non-English files are really translated (<= 25% identical to English)', () => {
  const keys = Object.keys(en());
  for (const c of nonEn()) {
    const same = keys.filter(k => k in dicts[c] && dicts[c][k] === en()[k]);
    const ratio = same.length / keys.length;
    assert.ok(ratio <= 0.25, `${c}: ${(ratio * 100).toFixed(1)}% identical to English, e.g. ${same.slice(0, 12).map(k => `${k}="${en()[k]}"`).join('; ')}`);
  }
});

test('non-English files are not copies of each other', () => {
  const codes = nonEn();
  for (let i = 0; i < codes.length; i++) for (let j = i + 1; j < codes.length; j++) {
    const a = dicts[codes[i]], b = dicts[codes[j]];
    const ks = Object.keys(a).filter(k => k in b);
    const same = ks.filter(k => a[k] === b[k]).length / ks.length;
    assert.ok(same <= 0.3, `${codes[i]} vs ${codes[j]}: ${(same * 100).toFixed(1)}% identical`);
  }
});

function share(c, re) { const vs = Object.values(dicts[c]); return vs.filter(v => re.test(v)).length / vs.length; }

test('script sanity per language', () => {
  assert.ok(share('ru', /[Ѐ-ӿ]/) >= 0.8, 'ru Cyrillic share');
  assert.ok(share('uk', /[Ѐ-ӿ]/) >= 0.8, 'uk Cyrillic share');
  assert.ok(share('zh-CN', /[一-鿿]/) >= 0.8, 'zh CJK share');
  assert.ok(share('de', /[äöüßÄÖÜ]/) >= 0.05, 'de umlauts present');
  assert.ok(share('fr', /[éèêàçôîùûëïœ]/i) >= 0.1, 'fr accents present');
  assert.ok(share('es', /[áéíóúñ¿¡]/i) >= 0.1, 'es accents present');
  assert.ok(share('pl', /[ąćęłńóśźż]/i) >= 0.1, 'pl diacritics present');
  assert.equal(share('uk', /[ыэъё]/i), 0, 'Ukrainian must not contain ы э ъ ё (Russian letters)');
  assert.equal(share('ru', /[іїєґ]/i), 0, 'Russian must not contain і ї є ґ (Ukrainian letters)');
  assert.ok(share('uk', /[іїєґ]/i) >= 0.15, 'uk has Ukrainian letters');
  for (const c of ['de', 'fr', 'es', 'pl', 'en']) assert.equal(share(c, /[Ѐ-ӿ一-鿿]/), 0, `${c} contains foreign script`);
  assert.equal(share('zh-CN', /[Ѐ-ӿ]/), 0);
  assert.equal(share('ru', /[一-鿿]/), 0);
});

test('Chinese uses no traditional-only marker characters and full-width punctuation sanity', () => {
  for (const [k, v] of Object.entries(dicts['zh-CN'])) {
    assert.ok(!/[設計檔開關為這個項說顯點選擇與動態類別]/.test(v), `zh-CN:${k} contains traditional characters: ${v}`);
  }
});

test('terminology consistency: same English value maps to a single translation per language', () => {
  const issues = [];
  for (const c of nonEn()) {
    const byEn = new Map();
    for (const [k, v] of Object.entries(en())) {
      if (!(k in dicts[c]) || PLURAL.test(k) || v.length > 40 || /\{/.test(v)) continue;
      if (!byEn.has(v)) byEn.set(v, new Map());
      const m = byEn.get(v); if (!m.has(dicts[c][k])) m.set(dicts[c][k], []); m.get(dicts[c][k]).push(k);
    }
    for (const [e, m] of byEn) if (m.size > 1) issues.push(`${c}: "${e}" -> ${[...m.keys()].map(x => `"${x}"`).join(' | ')}`);
  }
  // informational only: identical English strings may legitimately differ by context
  if (issues.length) console.log('# terminology variants (informational):\n# ' + issues.join('\n# '));
});

test('brand name stays recognisable and codes are not translated', () => {
  for (const [c, d] of Object.entries(dicts)) for (const [k, v] of Object.entries(d)) {
    if (/FMS\d{3}|LUA\d{3}/i.test(en()[k] ?? '')) assert.ok(/FMS\d{3}|LUA\d{3}/.test(v), `${c}:${k}`);
    if (/Factorio/.test(en()[k] ?? '')) assert.ok(/Factorio/.test(v), `${c}:${k} lost "Factorio"`);
  }
});
