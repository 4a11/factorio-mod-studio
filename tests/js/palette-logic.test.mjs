// Tests for wwwroot/palette-logic.js, written from the UI-refresh contract (section 4) only.
import test from 'node:test';
import assert from 'node:assert/strict';

const mod = await import('../../src/FactorioModStudio.Server/wwwroot/palette-logic.js');
const { fuzzyScore, fuzzyFilter, fuzzyPositions, parsePaletteInput } = mod;

// small deterministic PRNG so failures reproduce
function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const timed = (fn) => { const t = process.hrtime.bigint(); const r = fn(); return { r, ms: Number(process.hrtime.bigint() - t) / 1e6 }; };

// ------------------------------------------------------------------ module surface

test('module exports the four documented functions and is importable without a DOM', () => {
  for (const n of ['fuzzyScore', 'fuzzyFilter', 'fuzzyPositions', 'parsePaletteInput']) assert.equal(typeof mod[n], 'function', n);
  assert.equal(typeof globalThis.document, 'undefined');
  assert.equal(typeof globalThis.window, 'undefined');
});

// ------------------------------------------------------------------ fuzzyScore: basics

test('fuzzyScore: empty query is 0 for any text, including empty text', () => {
  assert.equal(fuzzyScore('', 'anything'), 0);
  assert.equal(fuzzyScore('', ''), 0);
  assert.equal(fuzzyScore('', '日本語'), 0);
});

test('fuzzyScore: non-subsequence is null', () => {
  assert.equal(fuzzyScore('xyz', 'abc'), null);
  assert.equal(fuzzyScore('ba', 'ab'), null);          // order matters
  assert.equal(fuzzyScore('aa', 'a'), null);           // each text char used once
  assert.equal(fuzzyScore('abc', 'ab'), null);         // query longer than text
  assert.equal(fuzzyScore('a', ''), null);
});

test('fuzzyScore: subsequence gives a finite number', () => {
  for (const [q, t] of [['abc', 'abc'], ['ac', 'abc'], ['a', 'xxxxa'], ['lua', 'src/prototypes/Item.LUA'], ['é', 'café']]) {
    const s = fuzzyScore(q, t);
    assert.equal(typeof s, 'number', `${q} in ${t}`);
    assert.ok(Number.isFinite(s), `${q} in ${t}`);
  }
});

test('fuzzyScore: case-insensitive both ways', () => {
  assert.notEqual(fuzzyScore('FOO', 'foo.lua'), null);
  assert.notEqual(fuzzyScore('foo', 'FOO.LUA'), null);
  assert.notEqual(fuzzyScore('fOo', 'Foo.lua'), null);
  assert.notEqual(fuzzyScore('ДОМ', 'мой дом'), null);
  assert.notEqual(fuzzyScore('дом', 'ДОМ'), null);
  assert.notEqual(fuzzyScore('ÜBER', 'über.lua'), null);
});

test('fuzzyScore: single-character query and single-character text', () => {
  assert.notEqual(fuzzyScore('a', 'a'), null);
  assert.equal(fuzzyScore('a', 'b'), null);
  assert.notEqual(fuzzyScore('.', 'a.lua'), null);
  assert.notEqual(fuzzyScore('/', 'a/b'), null);
  assert.notEqual(fuzzyScore(' ', 'a b'), null);
});

test('fuzzyScore: regex metacharacters in the query are plain characters', () => {
  assert.notEqual(fuzzyScore('a.b', 'a.b'), null);
  assert.doesNotThrow(() => fuzzyScore('a.b', 'axb'));
  assert.equal(fuzzyScore('(', 'f(x)') !== null, true);
  assert.equal(fuzzyScore('[', 'x[1]') !== null, true);
  assert.equal(fuzzyScore('\\', 'a\\b') !== null, true);
  assert.equal(fuzzyScore('*', 'abc'), null);
  assert.equal(fuzzyScore('.*', 'abc'), null);
  assert.doesNotThrow(() => fuzzyScore('(((', 'abc'));
});

// ------------------------------------------------------------------ invariants (a)-(e)

test('(a) exact full-string match scores >= every other candidate', () => {
  const q = 'foo.lua';
  const exact = fuzzyScore(q, q);
  assert.notEqual(exact, null);
  const others = [
    'src/foo.lua', 'a/b/c/foo.lua', 'foo.lua.bak', 'foo.lua.lua', 'food.lua', 'xfoo.lua', 'f-o-o.lua', 'foo_x.lua',
    'foo/o.lua', 'FOO.LUA', 'my foo.lua', 'foo.lua/x', 'a/foo.lua', '-foo.lua', 'foo-.lua', 'f.o.o.l.u.a', 'ffoooo..lluuaa',
  ];
  for (const o of others) {
    const s = fuzzyScore(q, o);
    if (s !== null) assert.ok(exact >= s, `exact ${exact} < ${s} for ${o}`);
  }
});

test('(a) exact match wins for assorted queries against paths that merely contain it', () => {
  const pairs = [
    ['data.lua', ['data.lua', 'src/data.lua', 'data.lua.old', 'metadata.lua', 'x/y/data.lua', 'data_final.lua']],
    ['a', ['a', 'a.lua', 'b/a', 'aa', 'ba', 'a/a']],
    ['info.json', ['info.json', 'locale/info.json', 'info.json5', 'xinfo.json', 'i-n-f-o.json']],
    ['control', ['control', 'control.lua', 'src/control', 'controller', 'uncontrolled']],
  ];
  for (const [q, cs] of pairs) {
    const ex = fuzzyScore(q, q);
    for (const c of cs) { const s = fuzzyScore(q, c); if (s !== null) assert.ok(ex >= s, `${q}: exact ${ex} < ${s} for ${c}`); }
  }
});

test('(a) property: exact match >= any other candidate, random strings', () => {
  const r = rng(1);
  const alpha = 'abc/-_. ';
  const rs = (n) => Array.from({ length: n }, () => alpha[Math.floor(r() * alpha.length)]).join('');
  for (let i = 0; i < 1500; i++) {
    const q = rs(1 + Math.floor(r() * 5));
    const ex = fuzzyScore(q, q);
    assert.notEqual(ex, null, q);
    for (let k = 0; k < 8; k++) {
      const c = rs(q.length + Math.floor(r() * 12));
      const s = fuzzyScore(q, c);
      if (s !== null) assert.ok(ex >= s, `q=${JSON.stringify(q)} exact=${ex} cand=${JSON.stringify(c)} s=${s}`);
    }
  }
});

test('(b) contiguous substring beats scattered match within equal-length strings', () => {
  const cases = [
    ['abc', 'zzabczz', 'zazzbzc'],
    ['abc', 'xxxabcxx', 'xaxxbxcx'],
    ['lua', 'xxluaxx', 'xlxxuxa'],
    ['foo', 'xxfooxxxx', 'xxfxxoxxo'],
    ['ab', 'xxabxx', 'xaxxbx'],
    ['ab', 'xxabxxxx', 'xaxxxxbx'],
  ];
  for (const [q, contig, scat] of cases) {
    assert.equal(contig.length, scat.length);
    const a = fuzzyScore(q, contig), b = fuzzyScore(q, scat);
    assert.ok(a !== null && b !== null);
    assert.ok(a > b, `${q}: ${contig}=${a} should be > ${scat}=${b}`);
  }
});

test('(b) property: contiguous > scattered, no separators, never at string start', () => {
  const r = rng(2);
  for (let i = 0; i < 800; i++) {
    const n = 2 + Math.floor(r() * 3);                       // 2..4 query chars
    const q = Array.from({ length: n }, () => 'abcd'[Math.floor(r() * 4)]).join('');
    const gaps = Array.from({ length: n }, () => 1 + Math.floor(r() * 3)); // >=1 filler before each char
    let scat = ''; for (let k = 0; k < n; k++) scat += 'x'.repeat(gaps[k]) + q[k];
    const extra = Math.floor(r() * 3);
    scat += 'x'.repeat(extra);
    const L = scat.length;
    const lead = 1 + Math.floor(r() * (L - n));              // >=1 leading filler
    const contig = 'x'.repeat(lead) + q + 'x'.repeat(L - lead - n);
    assert.equal(contig.length, L);
    if (scat === contig) continue;
    const a = fuzzyScore(q, contig), b = fuzzyScore(q, scat);
    assert.ok(a > b, `q=${q} contig=${contig}(${a}) scat=${scat}(${b})`);
  }
});

test('(c) match at basename start beats the same match mid-word', () => {
  const cases = [
    ['foo', 'foobar', 'barfoo'],
    ['foo', 'a/foobar', 'a/barfoo'],
    ['foo', 'bar/foo.lu', 'bar/xfoo.l'],
    ['foo', 'src/deep/foox', 'src/deep/xfoo'],
    ['ite', 'prototypes/item', 'prototypes/site'],
  ];
  for (const [q, good, bad] of cases) {
    assert.equal(good.length, bad.length, `${good}/${bad}`);
    const a = fuzzyScore(q, good), b = fuzzyScore(q, bad);
    assert.ok(a !== null && b !== null);
    assert.ok(a > b, `${q}: ${good}=${a} should be > ${bad}=${b}`);
  }
});

test('(c) basename start also wins for scattered matches', () => {
  const a = fuzzyScore('fb', 'dir/fxxbxx');   // f at basename start
  const b = fuzzyScore('fb', 'dir/xfxbxx');   // f mid-word
  assert.ok(a > b, `${a} vs ${b}`);
});

test('(d) matches after a word boundary (/ - _ . space) beat mid-word matches', () => {
  for (const sep of ['/', '-', '_', '.', ' ']) {
    const boundary = `xx${sep}bar`;     // bar after separator
    const midword = `xx${'y'}bar`;      // bar mid-word, same length
    const a = fuzzyScore('bar', boundary), b = fuzzyScore('bar', midword);
    assert.ok(a !== null && b !== null, sep);
    assert.ok(a > b, `sep ${JSON.stringify(sep)}: ${a} !> ${b}`);
  }
});

test('(d) boundary also helps scattered matches', () => {
  for (const sep of ['-', '_', '.', ' ', '/']) {
    const a = fuzzyScore('fb', `xfoo${sep}bar`);  // f mid-word, b at boundary
    const b = fuzzyScore('fb', `xfoob${'a'}ra`);  // f mid-word, b mid-word  (len: 8 vs 8)
    assert.equal(`xfoo${sep}bar`.length, `xfoobara`.length);
    assert.ok(a > b, `sep ${JSON.stringify(sep)}: ${a} !> ${b}`);
  }
});

test('(d) multiple boundary hits beat a single one', () => {
  const a = fuzzyScore('ab', 'x-a-b-x');  // not contiguous but both at boundaries
  const b = fuzzyScore('ab', 'xxa-bxx');
  assert.ok(a !== null && b !== null);
  assert.ok(a >= b, `${a} vs ${b}`);
});

test('(e) pure: same inputs give same outputs, independent of call history', () => {
  const calls = [['foo', 'src/foo.lua'], ['bar', 'xbarx'], ['zzz', 'abc'], ['', 'x'], ['é', 'café'], ['foo', 'src/foo.lua']];
  const first = calls.map(([q, t]) => fuzzyScore(q, t));
  for (let i = 0; i < 3; i++) assert.deepEqual(calls.map(([q, t]) => fuzzyScore(q, t)), first);
  const rev = [...calls].reverse().map(([q, t]) => fuzzyScore(q, t)).reverse();
  assert.deepEqual(rev, first);
  assert.equal(fuzzyScore('foo', 'src/foo.lua'), first[0]);
  assert.equal(first[0], first[5]);
});

test('(e) does not use regex state or global mutation (lastIndex / frozen strings)', () => {
  const t = Object.freeze({ s: 'src/foo.lua' });
  for (let i = 0; i < 50; i++) assert.equal(fuzzyScore('foo', t.s), fuzzyScore('foo', 'src/foo.lua'));
});

test('fuzzyScore: score is a pure function of its arguments, even after huge calls', () => {
  const a = fuzzyScore('abc', 'a/b/c');
  fuzzyScore('a'.repeat(50), 'a'.repeat(5000));
  assert.equal(fuzzyScore('abc', 'a/b/c'), a);
});

test('fuzzyScore: more compact match ranks above a spread-out one in the same string length', () => {
  const tight = fuzzyScore('abc', 'xxabcxxxxxxxx');
  const loose = fuzzyScore('abc', 'xaxxxxbxxxxcx');
  assert.ok(tight > loose);
});

// ------------------------------------------------------------------ fuzzyScore: unicode and odd input

test('fuzzyScore: unicode text does not throw and matches scripts', () => {
  assert.notEqual(fuzzyScore('日本', '日本語.lua'), null);
  assert.notEqual(fuzzyScore('本語', '日本語.lua'), null);
  assert.equal(fuzzyScore('語日', '日本語.lua'), null);
  assert.notEqual(fuzzyScore('😀', 'a😀b'), null);
  assert.notEqual(fuzzyScore('a', '😀a'), null);
  assert.doesNotThrow(() => fuzzyScore('\ud83d', 'x\ud83dy'));      // lone surrogates
  assert.doesNotThrow(() => fuzzyScore('a', '\ude00a'));
  assert.doesNotThrow(() => fuzzyScore('i', 'İstanbul'));
  assert.doesNotThrow(() => fuzzyScore('ß', 'STRASSE'));
});

test('fuzzyScore: strings containing NUL / newlines / tabs do not throw', () => {
  assert.doesNotThrow(() => fuzzyScore('a', 'x\0a\n\tb'));
  assert.notEqual(fuzzyScore('a\tb', 'a\tb'), null);
});

// ------------------------------------------------------------------ fuzzyPositions

test('fuzzyPositions: null iff fuzzyScore is null; empty query gives an empty array', () => {
  assert.equal(fuzzyPositions('xyz', 'abc'), null);
  assert.equal(fuzzyPositions('ba', 'ab'), null);
  assert.equal(fuzzyPositions('a', ''), null);
  assert.deepEqual(fuzzyPositions('', 'abc'), []);
  assert.deepEqual(fuzzyPositions('', ''), []);
});

test('fuzzyPositions: contiguous and simple cases', () => {
  assert.deepEqual(fuzzyPositions('abc', 'abc'), [0, 1, 2]);
  assert.deepEqual(fuzzyPositions('foo', 'bar/foo.lua'), [4, 5, 6]);
  assert.deepEqual(fuzzyPositions('lua', 'x.lua'), [2, 3, 4]);
  assert.deepEqual(fuzzyPositions('FOO', 'bar/foo.lua'), [4, 5, 6]);        // case-insensitive, positions index the original text
  assert.deepEqual(fuzzyPositions('a', 'a'), [0]);
});

test('fuzzyPositions: scattered match when no contiguous alternative', () => {
  assert.deepEqual(fuzzyPositions('ace', 'abcde'), [0, 2, 4]);
  assert.deepEqual(fuzzyPositions('dl', 'data.lua'), [0, 5]);
});

test('fuzzyPositions: prefers the contiguous / boundary alignment the score credits', () => {
  // scattered start (f,o,o at 0,2,4) vs contiguous word at index 6
  assert.deepEqual(fuzzyPositions('foo', 'fxoxo foo'), [6, 7, 8]);
  assert.deepEqual(fuzzyPositions('bar', 'bxaxr/bar.lua'), [6, 7, 8]);
});

test('fuzzyPositions: property - ascending, in range, characters match, consistent with fuzzyScore', () => {
  const r = rng(3);
  const alpha = 'abcAB/-_. ';
  const rs = (n) => Array.from({ length: n }, () => alpha[Math.floor(r() * alpha.length)]).join('');
  let matched = 0;
  for (let i = 0; i < 4000; i++) {
    const q = rs(Math.floor(r() * 5));
    const t = rs(Math.floor(r() * 16));
    const s = fuzzyScore(q, t), p = fuzzyPositions(q, t);
    if (s === null) { assert.equal(p, null, `q=${JSON.stringify(q)} t=${JSON.stringify(t)}`); continue; }
    matched++;
    assert.ok(Array.isArray(p), `q=${JSON.stringify(q)} t=${JSON.stringify(t)}`);
    assert.equal(p.length, q.length);
    for (let k = 0; k < p.length; k++) {
      assert.ok(Number.isInteger(p[k]) && p[k] >= 0 && p[k] < t.length);
      if (k) assert.ok(p[k] > p[k - 1], `not strictly ascending ${p}`);
      assert.equal(t[p[k]].toLowerCase(), q[k].toLowerCase());
    }
  }
  assert.ok(matched > 200, 'generator should produce plenty of matches');
});

test('fuzzyPositions: indices refer to the ORIGINAL text even when lower-casing changes its length', () => {
  // "İ".toLowerCase() is two UTF-16 units; positions must still index the original string
  const t = 'İstanbul';
  const p = fuzzyPositions('stanbul', t);
  assert.deepEqual(p, [1, 2, 3, 4, 5, 6, 7]);
  assert.equal(t.length, 8);
  const p2 = fuzzyPositions('bul', 'İİİ/bul');
  assert.deepEqual(p2, [4, 5, 6]);
});

test('fuzzyPositions: unicode (BMP) text', () => {
  assert.deepEqual(fuzzyPositions('дом', 'мой ДОМ'), [4, 5, 6]);
  assert.deepEqual(fuzzyPositions('本語', '日本語.lua'), [1, 2]);
});

test('fuzzyPositions: astral characters stay in range and ascending', () => {
  const t = 'a😀b😀c';
  for (const q of ['abc', '😀', 'a😀b']) {
    const p = fuzzyPositions(q, t);
    assert.ok(Array.isArray(p), q);
    for (let i = 0; i < p.length; i++) { assert.ok(p[i] >= 0 && p[i] < t.length); if (i) assert.ok(p[i] > p[i - 1]); }
  }
  assert.deepEqual(fuzzyPositions('abc', t).map((i) => t[i]), ['a', 'b', 'c']);
});

test('fuzzyPositions: pure and stable', () => {
  const a = fuzzyPositions('foo', 'src/foo.lua');
  assert.deepEqual(fuzzyPositions('foo', 'src/foo.lua'), a);
  a.push(99);   // mutating a returned array must not corrupt later calls
  assert.deepEqual(fuzzyPositions('foo', 'src/foo.lua'), [4, 5, 6]);
});

// ------------------------------------------------------------------ fuzzyFilter

test('fuzzyFilter: empty query returns all items in original order (copy, not the same array)', () => {
  const items = ['b', 'a', 'c', 'a'];
  const out = fuzzyFilter('', items);
  assert.deepEqual(out, items);
  assert.notEqual(out, items);
  assert.deepEqual(fuzzyFilter('', []), []);
});

test('fuzzyFilter: empty query honours limit', () => {
  assert.deepEqual(fuzzyFilter('', ['a', 'b', 'c', 'd'], undefined, 2), ['a', 'b']);
  assert.deepEqual(fuzzyFilter('', ['a', 'b'], (x) => x, 10), ['a', 'b']);
  assert.deepEqual(fuzzyFilter('', ['a', 'b'], (x) => x, 0), []);
});

test('fuzzyFilter: drops non-matches, orders by score descending', () => {
  const items = ['xxxaxxbxxc', 'abc', 'src/abc.lua', 'zzz', 'a-b-c', 'xabcx', 'ab'];
  const out = fuzzyFilter('abc', items);
  assert.ok(!out.includes('zzz') && !out.includes('ab'));
  assert.equal(out.length, 5);
  const scores = out.map((x) => fuzzyScore('abc', x));
  for (let i = 1; i < scores.length; i++) assert.ok(scores[i - 1] >= scores[i], scores.join());
  assert.equal(out[0], 'abc');       // exact match first, by invariant (a)
  assert.deepEqual(new Set(out), new Set(items.filter((x) => fuzzyScore('abc', x) !== null)));
});

test('fuzzyFilter: result is exactly the non-null items (no losses, no dups)', () => {
  const r = rng(4);
  const alpha = 'abc/-_.';
  for (let i = 0; i < 200; i++) {
    const items = Array.from({ length: 30 }, () => Array.from({ length: 1 + Math.floor(r() * 10) }, () => alpha[Math.floor(r() * alpha.length)]).join(''));
    const q = Array.from({ length: 1 + Math.floor(r() * 3) }, () => alpha[Math.floor(r() * alpha.length)]).join('');
    const out = fuzzyFilter(q, items);
    const expectCount = items.filter((x) => fuzzyScore(q, x) !== null).length;
    assert.equal(out.length, expectCount);
    const sc = out.map((x) => fuzzyScore(q, x));
    for (let k = 1; k < sc.length; k++) assert.ok(sc[k - 1] >= sc[k]);
  }
});

test('fuzzyFilter: ties keep original order (stable)', () => {
  const items = Array.from({ length: 40 }, (_, i) => ({ t: 'same.lua', i }));
  const out = fuzzyFilter('same', items, (x) => x.t);
  assert.deepEqual(out.map((x) => x.i), items.map((x) => x.i));
  // mixed: ties interleaved with different scores
  const mixed = [];
  for (let i = 0; i < 30; i++) mixed.push({ t: i % 3 === 0 ? 'abc' : i % 3 === 1 ? 'xxabc' : 'zzz', i });
  const o2 = fuzzyFilter('abc', mixed, (x) => x.t);
  assert.ok(!o2.some((x) => x.t === 'zzz'));
  for (const grp of ['abc', 'xxabc']) {
    const idx = o2.filter((x) => x.t === grp).map((x) => x.i);
    assert.deepEqual(idx, [...idx].sort((a, b) => a - b), grp);
  }
});

test('fuzzyFilter: stable when equal-scored but different strings (same shape)', () => {
  const items = ['a1b', 'a2b', 'a3b', 'a4b', 'a5b'];
  const out = fuzzyFilter('ab', items);
  const s = out.map((x) => fuzzyScore('ab', x));
  // all scores equal => original order
  if (new Set(s).size === 1) assert.deepEqual(out, items);
  else assert.ok(true);
});

test('fuzzyFilter: getText is used (and called with the item only)', () => {
  const items = [{ path: 'src/a.lua' }, { path: 'zzz' }, { path: 'a.lua' }];
  const seen = [];
  const out = fuzzyFilter('alua', items, (x) => { seen.push(x); return x.path; });
  assert.deepEqual(out.map((x) => x.path).sort(), ['a.lua', 'src/a.lua']);
  assert.ok(seen.length >= 3);
  assert.equal(out[0].path, 'a.lua');
  assert.equal(out[0], items[2]);       // returns the original objects
});

test('fuzzyFilter: limit truncates after sorting, keeps the best', () => {
  const items = ['xxabc', 'abc', 'xabcx', 'a-b-c', 'abcx'];
  const full = fuzzyFilter('abc', items);
  for (const lim of [0, 1, 2, 3, 5, 6, 100, Infinity]) {
    const out = fuzzyFilter('abc', items, (x) => x, lim);
    assert.deepEqual(out, full.slice(0, lim === Infinity ? undefined : lim), `limit ${lim}`);
  }
  assert.equal(full[0], 'abc');
});

test('fuzzyFilter: does not mutate input; no match gives empty array', () => {
  const items = Object.freeze(['b', 'a']);
  assert.deepEqual(fuzzyFilter('zz', items), []);
  assert.deepEqual(fuzzyFilter('a', items), ['a']);
  assert.deepEqual([...items], ['b', 'a']);
});

test('fuzzyFilter: duplicates are kept', () => {
  assert.deepEqual(fuzzyFilter('a', ['a', 'a', 'b', 'a']), ['a', 'a', 'a']);
});

test('fuzzyFilter: realistic paths rank what a user expects', () => {
  const files = ['info.json', 'data.lua', 'control.lua', 'prototypes/item.lua', 'prototypes/recipe.lua', 'locale/en/main.cfg', 'locale/de/main.cfg', 'scripts/item-handler.lua', 'graphics/icons/item.png', 'settings.lua'];
  assert.equal(fuzzyFilter('data', files)[0], 'data.lua');
  assert.equal(fuzzyFilter('info', files)[0], 'info.json');
  const item = fuzzyFilter('item', files);
  assert.equal(item[0].endsWith('item.lua') || item[0].endsWith('item.png') || item[0].includes('item-handler'), true);
  assert.ok(item.indexOf('prototypes/item.lua') < item.indexOf('locale/en/main.cfg') || !item.includes('locale/en/main.cfg'));
  const loc = fuzzyFilter('lmain', files);
  assert.ok(loc.includes('locale/en/main.cfg') && loc.includes('locale/de/main.cfg'));
  assert.deepEqual(loc.slice(0, 2), ['locale/en/main.cfg', 'locale/de/main.cfg']);       // tie keeps order
});

// ------------------------------------------------------------------ performance

test('performance: 10k realistic paths filter in well under a second', () => {
  const r = rng(5);
  const words = ['prototypes', 'item', 'recipe', 'locale', 'en', 'de', 'graphics', 'icons', 'entity', 'technology', 'scripts', 'util', 'main', 'data', 'control'];
  const items = Array.from({ length: 10000 }, (_, i) => `${words[Math.floor(r() * words.length)]}/${words[Math.floor(r() * words.length)]}/${words[Math.floor(r() * words.length)]}-${i}.lua`);
  for (const q of ['', 'i', 'it', 'pritem', 'prototypes/item', 'zzzz', 'loc en main', 'graphicsicons']) {
    const { r: out, ms } = timed(() => fuzzyFilter(q, items, (x) => x, 50));
    assert.ok(ms < 1000, `query ${JSON.stringify(q)} took ${ms} ms`);
    assert.ok(out.length <= 50);
  }
  const { ms } = timed(() => fuzzyFilter('item', items));
  assert.ok(ms < 1000, `unlimited took ${ms} ms`);
});

test('performance: pathological repeated characters do not blow up', () => {
  const text = 'a'.repeat(5000);
  let t = timed(() => fuzzyScore('a'.repeat(50) + 'b', text));
  assert.equal(t.r, null); assert.ok(t.ms < 1000, `no-match took ${t.ms}`);
  t = timed(() => fuzzyScore('a'.repeat(200), text));
  assert.notEqual(t.r, null); assert.ok(t.ms < 1000, `match took ${t.ms}`);
  t = timed(() => fuzzyPositions('a'.repeat(200) + 'b', text));
  assert.equal(t.r, null); assert.ok(t.ms < 1000, `positions no-match took ${t.ms}`);
  t = timed(() => fuzzyPositions('ab'.repeat(100), 'ab'.repeat(2500)));
  assert.ok(t.ms < 1000, `positions took ${t.ms}`);
  assert.equal(t.r.length, 200);
});

test('performance: very long text and very long query', () => {
  const big = ('src/' + 'x'.repeat(100) + '/').repeat(2000) + 'target.lua';
  let t = timed(() => fuzzyScore('target', big));
  assert.notEqual(t.r, null); assert.ok(t.ms < 1000, `${t.ms}`);
  t = timed(() => fuzzyPositions('target', big));
  assert.equal(t.r.length, 6); assert.ok(t.ms < 1000, `${t.ms}`);
  t = timed(() => fuzzyScore('q'.repeat(10000), 'q'.repeat(10000)));
  assert.notEqual(t.r, null); assert.ok(t.ms < 2000, `${t.ms}`);
});

// ------------------------------------------------------------------ parsePaletteInput

const P = (mode, query, line = null, column = null) => ({ mode, query, line, column });

test('parsePaletteInput: table', () => {
  const rows = [
    ['', P('files', '')],
    ['   ', P('files', '')],
    ['foo', P('files', 'foo')],
    ['  foo  ', P('files', 'foo')],
    ['foo.lua:42', P('files', 'foo.lua', 42)],
    ['foo.lua:42:7', P('files', 'foo.lua', 42, 7)],
    ['src/a b.lua:3', P('files', 'src/a b.lua', 3)],
    ['foo:abc', P('files', 'foo:abc')],
    ['foo:', P('files', 'foo:')],
    ['foo:42:', P('files', 'foo:42:')],
    ['foo:4x', P('files', 'foo:4x')],
    ['foo:x4', P('files', 'foo:x4')],
    ['foo:4:x', P('files', 'foo:4:x')],
    ['foo:-3', P('files', 'foo:-3')],
    ['foo:3.5', P('files', 'foo:3.5')],
    ['foo: 42', P('files', 'foo: 42')],
    ['a:0', P('files', 'a:0')],
    ['a:123456789', P('files', 'a', 123456789)],
    ['a:1234567890', P('files', 'a:1234567890')],
    ['a:1:123456789', P('files', 'a', 1, 123456789)],
    ['a:1:1234567890', P('files', 'a:1:1234567890')],
    ['a:1:2:3', P('files', 'a:1', 2, 3)],
    ['a::5', P('files', 'a:', 5)],
    ['C:\\x', P('files', 'C:\\x')],
    ['C:\\x:5', P('files', 'C:\\x', 5)],
    ['C:42', P('files', 'C', 42)],
    ['foo:\uff14\uff12', P('files', 'foo:\uff14\uff12')],           // full-width digits are not digits here
    ['foo:\u0664\u0662', P('files', 'foo:\u0664\u0662')],           // Arabic-Indic digits neither
    ['>run', P('commands', 'run')],
    ['  > run ', P('commands', 'run')],
    ['>', P('commands', '')],
    ['  >  ', P('commands', '')],
    ['>>run', P('commands', '>run')],
    ['> foo:42', P('commands', 'foo:42')],
    ['>foo:42:7', P('commands', 'foo:42:7')],
    ['> Theme: dark', P('commands', 'Theme: dark')],
    ['foo>bar', P('files', 'foo>bar')],
    ['a > b', P('files', 'a > b')],
  ];
  for (const [raw, want] of rows) assert.deepEqual(parsePaletteInput(raw), want, JSON.stringify(raw));
});

test('parsePaletteInput: result always has exactly the four documented keys', () => {
  for (const raw of ['', 'x', '>y', 'x:1', 'x:1:2', ' ', '>']) {
    const r = parsePaletteInput(raw);
    assert.deepEqual(Object.keys(r).sort(), ['column', 'line', 'mode', 'query']);
    assert.ok(r.mode === 'files' || r.mode === 'commands');
    assert.equal(typeof r.query, 'string');
    assert.ok(r.line === null || Number.isInteger(r.line));
    assert.ok(r.column === null || Number.isInteger(r.column));
  }
});

test('parsePaletteInput: query is trimmed; line/column are null or >= 1; column implies line', () => {
  const r = rng(6);
  const bits = ['a', 'b', ':', '0', '1', '9', ' ', '>', '/', '.', '\\', 'é'];
  for (let i = 0; i < 5000; i++) {
    const raw = Array.from({ length: Math.floor(r() * 12) }, () => bits[Math.floor(r() * bits.length)]).join('');
    const p = parsePaletteInput(raw);
    assert.equal(p.query, p.query.trim(), JSON.stringify(raw));
    if (p.line !== null) assert.ok(p.line >= 1 && p.line <= 999999999, `${JSON.stringify(raw)} -> ${JSON.stringify(p)}`);
    if (p.column !== null) { assert.ok(p.column >= 1 && p.column <= 999999999); assert.notEqual(p.line, null); }
    if (p.mode === 'commands') { assert.equal(p.line, null); assert.equal(p.column, null); assert.ok(raw.trimStart().startsWith('>')); }
    else assert.ok(!raw.trimStart().startsWith('>'));
    // pure: same input, same output
    assert.deepEqual(parsePaletteInput(raw), p);
  }
});

test('parsePaletteInput: splitting is lossless - query + suffix reconstructs the trimmed input', () => {
  const r = rng(7);
  const bits = ['a', 'b', ':', '0', '1', '2', '/', '.'];
  for (let i = 0; i < 3000; i++) {
    const raw = Array.from({ length: 1 + Math.floor(r() * 10) }, () => bits[Math.floor(r() * bits.length)]).join('');
    const p = parsePaletteInput(raw);
    if (p.mode !== 'files') continue;
    let rebuilt = p.query;
    if (p.line !== null) rebuilt += ':' + p.line;
    if (p.column !== null) rebuilt += ':' + p.column;
    // leading zeros in digits (e.g. "a:01") may normalise, so compare numerically-normalised forms
    const norm = (s) => s.replace(/:0+(\d)/g, ':$1');
    assert.equal(norm(rebuilt), norm(raw.trim()), JSON.stringify(raw));
  }
});

test('parsePaletteInput: whitespace variants', () => {
  assert.deepEqual(parsePaletteInput('  foo.lua:42  '), P('files', 'foo.lua', 42));
  assert.deepEqual(parsePaletteInput('\t'), P('files', ''));
  assert.deepEqual(parsePaletteInput('     >run'), P('commands', 'run'));
  assert.deepEqual(parsePaletteInput('>   run   now  '), P('commands', 'run   now'));
});

test('parsePaletteInput: unicode and emoji do not throw', () => {
  assert.deepEqual(parsePaletteInput('日本語.lua:3'), P('files', '日本語.lua', 3));
  assert.deepEqual(parsePaletteInput('😀:2'), P('files', '😀', 2));
  assert.deepEqual(parsePaletteInput('> 😀'), P('commands', '😀'));
  assert.doesNotThrow(() => parsePaletteInput('\ud83d:1'));
});

test('parsePaletteInput: huge inputs are handled quickly', () => {
  let t = timed(() => parsePaletteInput('a'.repeat(1_000_000) + ':5:6'));
  assert.equal(t.r.line, 5); assert.equal(t.r.column, 6); assert.equal(t.r.query.length, 1_000_000); assert.ok(t.ms < 500, `${t.ms}`);
  t = timed(() => parsePaletteInput(' '.repeat(1_000_000) + '>x'));
  assert.deepEqual(t.r, P('commands', 'x')); assert.ok(t.ms < 500, `leading spaces took ${t.ms}`);
  t = timed(() => parsePaletteInput('x' + ' '.repeat(200_000) + 'y' + ' '.repeat(200_000)));
  assert.equal(t.r.query.length, 200_002); assert.ok(t.ms < 500, `inner spaces took ${t.ms}`);
  t = timed(() => parsePaletteInput('1:'.repeat(25_000)));
  assert.ok(t.ms < 1000, `many colons took ${t.ms}`);
  t = timed(() => parsePaletteInput(':'.repeat(100_000) + '1'));
  assert.ok(t.ms < 1000, `colon run took ${t.ms}`);
  t = timed(() => parsePaletteInput('a:' + '1'.repeat(100_000)));
  assert.equal(t.r.line, null); assert.ok(t.ms < 500, `long digit run took ${t.ms}`);
});

test('parsePaletteInput: leading-digit-only and colon-only inputs do not throw and honour the invariants', () => {
  for (const raw of [':', '::', ':42', ':42:7', '42', '0', ':0', '>:', '> :5']) {
    const p = parsePaletteInput(raw);
    assert.equal(p.query, p.query.trim());
    if (p.line !== null) assert.ok(p.line >= 1);
  }
  assert.deepEqual(parsePaletteInput('42'), P('files', '42'));
  assert.deepEqual(parsePaletteInput('>:'), P('commands', ':'));
});

test('parsePaletteInput + fuzzyFilter: the typical flow', () => {
  const files = ['data.lua', 'control.lua', 'prototypes/item.lua'];
  const p = parsePaletteInput('item.lua:12:3');
  const hits = fuzzyFilter(p.query, files);
  assert.deepEqual(hits, ['prototypes/item.lua']);
  assert.equal(p.line, 12); assert.equal(p.column, 3);
});
