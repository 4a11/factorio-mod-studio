// Tests for wwwroot/locale-placeholders.js, written from the localization-editor contract only.
// The module must export extract(text) -> string[] and compare(source, target) -> { missing, unexpected } (normalised tokens),
// mirroring LocalePlaceholders.Extract / Compare of the C# core.
import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const modulePath = path.resolve(here, '../../src/FactorioModStudio.Server/wwwroot/locale-placeholders.js');

async function load() {
  return await import(pathToFileURL(modulePath).href);
}

test('module loads and exports extract and compare', async () => {
  const m = await load();
  assert.equal(typeof m.extract, 'function');
  assert.equal(typeof m.compare, 'function');
});

const EXTRACT = [
  ['', []],
  ['plain text without anything', []],
  ['a __1__ b __2__', ['__1__', '__2__']],
  ['__1__ and __1__', ['__1__', '__1__']],
  ['__12__', ['__12__']],
  ['__ENTITY__iron-chest__', ['__ENTITY__iron-chest__']],
  ['take __ITEM__iron-plate__ now', ['__ITEM__iron-plate__']],
  ['__FLUID__water__', ['__FLUID__water__']],
  ['__RECIPE__iron-gear-wheel__', ['__RECIPE__iron-gear-wheel__']],
  ['__TECHNOLOGY__automation__', ['__TECHNOLOGY__automation__']],
  ['__TILE__stone-path__', ['__TILE__stone-path__']],
  ['press __CONTROL__open-gui__ to open', ['__CONTROL__open-gui__']],
  ['__ALT_CONTROL__1__open-gui__', ['__ALT_CONTROL__1__open-gui__']],
  ['__CONTROL_STYLE_BEGIN__x__CONTROL_STYLE_END__', ['__CONTROL_STYLE_BEGIN__', '__CONTROL_STYLE_END__']],
  ['__REMARK_COLOR_BEGIN__x__REMARK_COLOR_END__', ['__REMARK_COLOR_BEGIN__', '__REMARK_COLOR_END__']],
  ['__ENTITY__a.b/c_d-e1__ tail', ['__ENTITY__a.b/c_d-e1__']],
  ['__1____2__', ['__1__', '__2__']],
  ['__foo__', []],
  ['__UNKNOWN__x__', []],
  ['__ENTITY__iron chest__', []],
  ['_1_ and ___', []],
  ['see [item=iron-plate] and [/color]', ['[item=iron-plate]', '[/color]']],
  ['[color=1,0,0]red[/color]', ['[color=1,0,0]', '[/color]']],
  ['[color=1, 0, 0]x', ['[color=1, 0, 0]']],
  ['[img=item/iron-plate]', ['[img=item/iron-plate]']],
  ['[font=default-bold]x[/font]', ['[font=default-bold]', '[/font]']],
  ['[gps=1,2]', ['[gps=1,2]']],
  ['[bold]x[/bold]', ['[bold]', '[/bold]']],
  ['[ 5 ]', []],
  ['[A-Z]', []],
  ['[x=', []],
  ['[x=abc', []],
  ['[a1]', []],
  ['[Item=x]', []],
  ['[-x]', []],
  ['[]', []],
  ['Level [ 5 ] and [item=a]', ['[item=a]']],
  ['line\\nbreak', ['\\n']],
  ['a\\nb\\nc', ['\\n', '\\n']],
  ['real\nnewline', []],
  ['tab\\tstays', []],
  ['[x=abc\ndef]', []],
  ['__plural_for_parameter_1_{1=file|rest=files}__', ['__plural_for_parameter_1_{1=file|rest=files}__']],
  ['You have __1__ __plural_for_parameter_1_{1=file|rest=files}__ left', ['__1__', '__plural_for_parameter_1_{1=file|rest=files}__']],
  ['[color=1,0,0]__ENTITY__x__\\n[/color]', ['[color=1,0,0]', '__ENTITY__x__', '\\n', '[/color]']],
  ['Привет __1__ 日本 [item=камень]', ['__1__', '[item=камень]']],
];

for (const [text, expected] of EXTRACT) {
  test(`extract(${JSON.stringify(text)})`, async () => {
    const { extract } = await load();
    assert.deepEqual([...extract(text)], expected);
  });
}

test('extract of null/undefined is empty', async () => {
  const { extract } = await load();
  assert.deepEqual([...extract(null)], []);
  assert.deepEqual([...extract(undefined)], []);
});

test('extract returns an array', async () => {
  const { extract } = await load();
  assert.ok(Array.isArray(extract('x __1__')));
});

test('extract of a huge string is fast', async () => {
  const { extract } = await load();
  const text = 'word [item=a] __1__ \\n [ not '.repeat(20000);
  const t0 = Date.now();
  const tokens = extract(text);
  assert.ok(Date.now() - t0 < 5000, 'too slow');
  assert.equal(tokens.length, 60000);
});

test('extract is not exploded by backtracking bait', async () => {
  const { extract } = await load();
  // Each piece is at most the maximum value length the API accepts (65536 chars).
  for (const text of ['[a='.repeat(21000), '__ENTITY__aaaa'.repeat(4600), '__plural_for_parameter_1_{'.repeat(2500), '['.repeat(65536), '_'.repeat(65536)]) {
    const t0 = Date.now();
    extract(text);
    assert.ok(Date.now() - t0 < 3000, `too slow for ${text.slice(0, 12)}`);
  }
});

test('extract is stateless across calls (no sticky/global regex lastIndex leaks)', async () => {
  const { extract } = await load();
  for (let i = 0; i < 5; i++) {
    assert.deepEqual([...extract('a __1__ [b]')], ['__1__', '[b]']);
    assert.deepEqual([...extract('no tokens')], []);
  }
});

const sp = (s) => (s === '' ? [] : s.split('¦'));
const COMPARE = [
  ['a __1__', 'b __1__', '', ''],
  ['a __1__', 'b', '__1__', ''],
  ['a', 'b __1__', '', '__1__'],
  ['', '', '', ''],
  ['__1__ __2__', '__2__ __1__', '', ''],
  ['__2__ __1__ __3__', '', '__2__¦__1__¦__3__', ''],
  ['', '__3__ __1__ __2__', '', '__3__¦__1__¦__2__'],
  ['__1__ __2__', '__2__ __3__', '__1__', '__3__'],
  ['__plural_for_parameter_1_{1=file|rest=files}__', '__plural_for_parameter_1_{1=Datei|rest=Dateien}__', '', ''],
  ['__plural_for_parameter_1_{1=a|rest=b}__', 'x', '__plural_for_parameter_1__', ''],
  ['x', '__plural_for_parameter_2_{1=a|rest=b}__', '', '__plural_for_parameter_2__'],
  ['__plural_for_parameter_1_{1=a|rest=b}__', '__plural_for_parameter_2_{1=a|rest=b}__', '__plural_for_parameter_1__', '__plural_for_parameter_2__'],
  ['[color=1,0,0]x[/color]', 'x', '[color=1,0,0]¦[/color]', ''],
  ['[color=red]x', '[color=RED]x', '[color=red]', '[color=RED]'],
  ['a\\nb', 'a b', '\\n', ''],
  ['a b', 'a\\nb', '', '\\n'],
  ['__ENTITY__a__', '__ENTITY__b__', '__ENTITY__a__', '__ENTITY__b__'],
  ['__CONTROL__open-gui__', '__ALT_CONTROL__1__open-gui__', '__CONTROL__open-gui__', '__ALT_CONTROL__1__open-gui__'],
  ['Level [ 5 ]', 'Niveau', '', ''],
  ['Hello __1__ [item=a]', 'Привет [item=a] __1__', '', ''],
];

for (const [source, target, missing, unexpected] of COMPARE) {
  test(`compare(${JSON.stringify(source)}, ${JSON.stringify(target)})`, async () => {
    const { compare } = await load();
    const d = compare(source, target);
    assert.deepEqual([...d.missing], sp(missing));
    assert.deepEqual([...d.unexpected], sp(unexpected));
  });
}

test('compare is a multiset comparison', async () => {
  const { compare } = await load();
  const less = compare('__1__ __1__ __1__', '__1__');
  assert.ok(less.missing.length >= 1);
  assert.ok(less.missing.every((t) => t === '__1__'));
  assert.equal(less.unexpected.length, 0);
  const more = compare('__1__', '__1__ __1__');
  assert.equal(more.missing.length, 0);
  assert.ok(more.unexpected.length >= 1);
  assert.ok(more.unexpected.every((t) => t === '__1__'));
  const same = compare('__1__ __1__', '__1__ __1__');
  assert.equal(same.missing.length, 0);
  assert.equal(same.unexpected.length, 0);
});

test('compare ignores free translation of plural contents', async () => {
  const { compare } = await load();
  const d = compare('__plural_for_parameter_1_{1=one|2=two|rest=many}__ x', 'x __plural_for_parameter_1_{rest=много}__');
  assert.equal(d.missing.length, 0);
  assert.equal(d.unexpected.length, 0);
});

test('compare tolerates null/undefined sides', async () => {
  const { compare } = await load();
  const d = compare('a __1__', null);
  assert.deepEqual([...d.missing], ['__1__']);
  const e = compare(undefined, 'b __2__');
  assert.deepEqual([...e.unexpected], ['__2__']);
});

test('compare does not mutate or share state between calls', async () => {
  const { compare } = await load();
  const a = compare('__1__', '');
  const b = compare('__2__', '');
  assert.deepEqual([...a.missing], ['__1__']);
  assert.deepEqual([...b.missing], ['__2__']);
});

test('compare of the C# unit-test corpus agrees pairwise with extract', async () => {
  // Consistency property: tokens of source minus tokens of target, via extract(), equal compare().missing for token kinds without plural.
  const { extract, compare } = await load();
  const samples = ['a __1__ [item=x] \\n', '[item=x] __2__', '', 'x', '[color=red]__ENTITY__e__[/color]', '__1__ __1__'];
  for (const s of samples) for (const t of samples) {
    const ex = extract(s), et = extract(t);
    const pool = [...et];
    const missing = [];
    for (const tok of ex) { const i = pool.indexOf(tok); if (i >= 0) pool.splice(i, 1); else missing.push(tok); }
    assert.deepEqual([...compare(s, t).missing], missing, `${JSON.stringify(s)} vs ${JSON.stringify(t)}`);
  }
});
