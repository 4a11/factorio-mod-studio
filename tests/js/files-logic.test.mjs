// Tests for wwwroot/files-logic.js, written from the Files tab v2 contract only.
//
// Assumed shapes (the contract names the functions, not their data shapes):
//  - buildTree(files): files are API file entries { path, size, category, role, unused }. Returns either a root node
//    or an array of top-level nodes. A folder node has `children` (array); a file node does not. Every node has
//    `name` (last segment) and `path` (full '/'-separated relative path). Folder paths have no trailing slash.
//  - filterTree(tree, { name, unusedOnly }) returns a tree of the same shape with only matching files and the folders
//    leading to them. Name matching is a case-insensitive substring of the file name.
//  - sort: exported as `sortTree` or `sort`; (nodes array) -> sorted array (or sorts in place), recursively.
//    Folders before files, then case-insensitive by name.
//  - validateName(name): falsy / true / { ok: true } means valid; anything else (string error key, false,
//    { ok: false }) means invalid.
import test from 'node:test';
import assert from 'node:assert/strict';

const mod = await import('../../src/FactorioModStudio.Server/wwwroot/files-logic.js');
const { buildTree, filterTree, validateName } = mod;
const sortFn = mod.sortTree ?? mod.sort;

const f = (path, extra = {}) => ({ path, size: 10, category: 'other', role: null, unused: null, ...extra });

const kids = (t) => (Array.isArray(t) ? t : t.children);
const isFolder = (n) => Array.isArray(n.children);

function allFiles(t) {
  const out = [];
  (function walk(nodes) { for (const n of nodes) (isFolder(n) ? walk(n.children) : out.push(n.path)); })(kids(t));
  return out;
}
function allFolders(t) {
  const out = [];
  (function walk(nodes) { for (const n of nodes) if (isFolder(n)) { out.push(n.path); walk(n.children); } })(kids(t));
  return out;
}
function find(t, path) {
  let hit = null;
  (function walk(nodes) { for (const n of nodes) { if (n.path === path) hit = n; if (isFolder(n)) walk(n.children); } })(kids(t));
  return hit;
}
const names = (nodes) => nodes.map((n) => n.name);

const isValid = (r) => r === undefined || r === null || r === '' || r === true || (typeof r === 'object' && r !== null && r.ok === true);

const sample = () => [
  f('info.json', { category: 'data' }),
  f('data.lua', { category: 'script', unused: false }),
  f('control.lua', { category: 'script', unused: false }),
  f('graphics/icons/gear.png', { category: 'graphics', unused: false }),
  f('graphics/icons/Orphan.png', { category: 'graphics', unused: true }),
  f('graphics/entity/tank.png', { category: 'graphics', unused: false }),
  f('sound/beep.ogg', { category: 'sound', unused: true }),
  f('scripts/util.lua', { category: 'script', unused: false }),
  f('scripts/dead.lua', { category: 'script', unused: true }),
  f('locale/en/strings.cfg', { category: 'locale' }),
];

// ------------------------------------------------------------------ module surface

test('module exports the documented pure functions and needs no DOM', () => {
  for (const n of ['buildTree', 'filterTree', 'validateName']) assert.equal(typeof mod[n], 'function', n);
  assert.equal(typeof sortFn, 'function', 'sortTree or sort');
  assert.equal(typeof globalThis.document, 'undefined');
});

// ------------------------------------------------------------------ buildTree

test('buildTree: empty input gives an empty tree', () => {
  assert.deepEqual(kids(buildTree([])), []);
});

test('buildTree: nests files into folders and keeps every file exactly once', () => {
  const files = sample();
  const tree = buildTree(files);
  assert.deepEqual(allFiles(tree).sort(), files.map((x) => x.path).sort());
  assert.deepEqual(allFolders(tree).sort(),
    ['graphics', 'graphics/entity', 'graphics/icons', 'locale', 'locale/en', 'scripts', 'sound'].sort());
});

test('buildTree: nodes carry name and full path; top level holds root files and first-level folders', () => {
  const tree = buildTree(sample());
  const top = names(kids(tree)).sort();
  assert.deepEqual(top, ['control.lua', 'data.lua', 'graphics', 'info.json', 'locale', 'scripts', 'sound'].sort());
  const gear = find(tree, 'graphics/icons/gear.png');
  assert.ok(gear, 'gear.png present');
  assert.equal(gear.name, 'gear.png');
  assert.equal(isFolder(gear), false);
  const icons = find(tree, 'graphics/icons');
  assert.equal(icons.name, 'icons');
  assert.deepEqual(names(icons.children).sort(), ['Orphan.png', 'gear.png'].sort());
});

test('buildTree: file nodes keep the entry data (unused flag reachable)', () => {
  const tree = buildTree(sample());
  const orphan = find(tree, 'graphics/icons/Orphan.png');
  const flag = orphan.unused ?? orphan.file?.unused ?? orphan.entry?.unused;
  assert.equal(flag, true);
});

test('buildTree: does not mutate its input', () => {
  const files = sample();
  const copy = JSON.parse(JSON.stringify(files));
  buildTree(files);
  assert.deepEqual(files, copy);
});

test('buildTree: deep single-file chains and same-named file/folder in different places', () => {
  const tree = buildTree([f('a/b/c/d/e.lua'), f('x/a/b.lua'), f('a.lua')]);
  assert.deepEqual(allFolders(tree).sort(), ['a', 'a/b', 'a/b/c', 'a/b/c/d', 'x', 'x/a'].sort());
  assert.deepEqual(allFiles(tree).sort(), ['a.lua', 'a/b/c/d/e.lua', 'x/a/b.lua'].sort());
});

test('buildTree: folders that differ only by case are kept apart (zip/Linux semantics)', () => {
  const tree = buildTree([f('Graphics/a.png'), f('graphics/b.png')]);
  assert.deepEqual(allFolders(tree).sort(), ['Graphics', 'graphics']);
});

test('buildTree: handles a few thousand files quickly', () => {
  const many = [];
  for (let i = 0; i < 5000; i++) many.push(f(`dir${i % 50}/sub${i % 7}/file${i}.png`, { category: 'graphics', unused: i % 3 === 0 }));
  const t0 = Date.now();
  const tree = buildTree(many);
  assert.equal(allFiles(tree).length, 5000);
  assert.ok(Date.now() - t0 < 1000, 'buildTree under 1s for 5000 files');
});

// ------------------------------------------------------------------ filterTree

test('filterTree: no filter keeps everything', () => {
  const tree = buildTree(sample());
  const out = filterTree(tree, { name: '', unusedOnly: false });
  assert.deepEqual(allFiles(out).sort(), allFiles(tree).sort());
});

test('filterTree: name filter is a case-insensitive substring and keeps ancestor folders', () => {
  const tree = buildTree(sample());
  const out = filterTree(tree, { name: 'ORPH', unusedOnly: false });
  assert.deepEqual(allFiles(out), ['graphics/icons/Orphan.png']);
  assert.deepEqual(allFolders(out).sort(), ['graphics', 'graphics/icons']);
});

test('filterTree: name filter matching several files across folders', () => {
  const out = filterTree(buildTree(sample()), { name: '.lua', unusedOnly: false });
  assert.deepEqual(allFiles(out).sort(), ['control.lua', 'data.lua', 'scripts/dead.lua', 'scripts/util.lua']);
  assert.deepEqual(allFolders(out), ['scripts']);
});

test('filterTree: no match gives an empty tree (no empty folders left)', () => {
  const out = filterTree(buildTree(sample()), { name: 'zzz-nothing', unusedOnly: false });
  assert.deepEqual(allFiles(out), []);
  assert.deepEqual(allFolders(out), []);
});

test('filterTree: unusedOnly keeps only files flagged unused === true', () => {
  const out = filterTree(buildTree(sample()), { name: '', unusedOnly: true });
  assert.deepEqual(allFiles(out).sort(), ['graphics/icons/Orphan.png', 'scripts/dead.lua', 'sound/beep.ogg']);
  assert.ok(!allFolders(out).includes('graphics/entity'), 'folder without unused files is dropped');
  assert.ok(!allFolders(out).includes('locale'), 'null unused (not applicable) is not unused');
});

test('filterTree: name and unusedOnly combine (AND)', () => {
  const out = filterTree(buildTree(sample()), { name: 'dead', unusedOnly: true });
  assert.deepEqual(allFiles(out), ['scripts/dead.lua']);
  const none = filterTree(buildTree(sample()), { name: 'gear', unusedOnly: true });
  assert.deepEqual(allFiles(none), []);
});

test('filterTree: whitespace-only filter acts like no filter', () => {
  const tree = buildTree(sample());
  const out = filterTree(tree, { name: '   ', unusedOnly: false });
  assert.equal(allFiles(out).length, allFiles(tree).length);
});

test('filterTree: does not mutate the input tree', () => {
  const tree = buildTree(sample());
  const before = JSON.stringify(tree);
  filterTree(tree, { name: 'gear', unusedOnly: true });
  assert.equal(JSON.stringify(tree), before);
});

// ------------------------------------------------------------------ sort

// sort takes the node array (top-level children) and returns it sorted (or sorts in place), recursively.
function sorted(tree) {
  const nodes = kids(tree);
  const r = sortFn(nodes);
  return r === undefined ? nodes : r;
}

test('sort: folders first, then files, each case-insensitively by name', () => {
  const tree = sorted(buildTree([
    f('zeta.lua'), f('Alpha.lua'), f('beta.lua'),
    f('zfolder/x.lua'), f('Afolder/x.lua'), f('bfolder/x.lua'),
  ]));
  assert.deepEqual(names(kids(tree)), ['Afolder', 'bfolder', 'zfolder', 'Alpha.lua', 'beta.lua', 'zeta.lua']);
});

test('sort: applies recursively to nested folders', () => {
  const tree = sorted(buildTree([f('g/c.png'), f('g/A.png'), f('g/b/x.png'), f('g/B.png')]));
  const g = find(tree, 'g');
  assert.equal(g.children[0].name, 'b');
  assert.equal(g.children.length, 4);
  assert.deepEqual(names(g.children).slice(1).map((n) => n.toLowerCase()), ['a.png', 'b.png', 'c.png']);
});

test('sort: numbered files sort naturally or at least stably', () => {
  const tree = sorted(buildTree([f('f10.png'), f('f2.png'), f('f1.png')]));
  const order = names(kids(tree));
  assert.equal(order[0], 'f1.png');
  assert.equal(order.length, 3);
});

// ------------------------------------------------------------------ validateName

test('validateName: accepts ordinary file and folder names', () => {
  for (const n of ['data.lua', 'my-file_2.png', 'Graphics', 'a', 'name with spaces.txt', 'файл.lua', '数据.lua', 'auxiliary.lua', 'a.b.c'])
    assert.ok(isValid(validateName(n)), `${n} should be valid, got ${JSON.stringify(validateName(n))}`);
});

test('validateName: rejects empty and whitespace-only names', () => {
  for (const n of ['', ' ', '   '])
    assert.ok(!isValid(validateName(n)), `${JSON.stringify(n)} should be invalid`);
});

test('validateName: rejects separators, dot names and leading dots', () => {
  for (const n of ['a/b', '/a', 'a\\b', '\\', '.', '..', '.hidden', '.git', '..a', 'a/../b'])
    assert.ok(!isValid(validateName(n)), `${n} should be invalid`);
});

test('validateName: rejects Windows-reserved characters', () => {
  for (const c of ['<', '>', ':', '"', '|', '?', '*'])
    assert.ok(!isValid(validateName(`bad${c}name.lua`)), `char ${c} should be invalid`);
});

test('validateName: rejects reserved Windows device names, with or without extension, any case', () => {
  for (const n of ['CON', 'con', 'PRN', 'AUX', 'NUL', 'nul.txt', 'COM1', 'com9.lua', 'LPT1', 'lpt3.png', 'Con.Lua', 'nul.tar.gz'])
    assert.ok(!isValid(validateName(n)), `${n} should be invalid`);
});

test('validateName: accepts names that merely start with a reserved word', () => {
  for (const n of ['console.lua', 'nullable.lua', 'company.png', 'lpt.lua', 'com.lua'])
    assert.ok(isValid(validateName(n)), `${n} should be valid`);
});
