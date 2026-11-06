// Tests for wwwroot/import-logic.js: the pre-check of a folder before it is uploaded.
import test from 'node:test';
import assert from 'node:assert/strict';

const { findMods, readInfo, zipIdentity, compareVersions, assess, MAX_IMPORT_FILES, MAX_ZIP_BYTES } =
  await import('../../src/FactorioModStudio.Server/wwwroot/import-logic.js');

const files = (...paths) => paths.map((path) => ({ path, size: 1 }));
const info = (name, version, extra = {}) => JSON.stringify({ name, version, ...extra });

test('a folder with info.json at its top is a single mod', () => {
  const found = findMods(files('info.json', 'data.lua', 'graphics/icon.png'));
  assert.equal(found.layout, 'single');
  assert.equal(found.mods.length, 1);
  assert.equal(found.mods[0].folder, '');
  assert.deepEqual(found.mods[0].files.map((f) => f.path), ['info.json', 'data.lua', 'graphics/icon.png']);
});

test('a folder of mods: sub-folders with info.json, top-level zips, the rest skipped', () => {
  const found = findMods(files(
    'mod-list.json', 'mod-settings.dat',
    'zeta/info.json', 'zeta/data.lua',
    'alpha_1.0.0/info.json', 'alpha_1.0.0/locale/en/a.cfg',
    'beta_2.0.1.zip',
    'notes/readme.md',
    'deep/inner/info.json'));
  assert.equal(found.layout, 'many');
  assert.deepEqual(found.mods.map((m) => m.folder), ['alpha_1.0.0', 'zeta']);
  assert.deepEqual(found.mods[0].files.map((f) => f.path), ['info.json', 'locale/en/a.cfg'], 'paths become relative to the mod');
  assert.deepEqual(found.zips.map((z) => z.path), ['beta_2.0.1.zip']);
  assert.deepEqual(found.others, ['deep', 'notes'], 'info.json deeper than one level does not make a mod');
});

test('a folder without any mod', () => {
  assert.equal(findMods(files('readme.md', 'pictures/a.png')).layout, 'none');
  assert.equal(findMods([]).layout, 'none');
});

test('readInfo follows the server rules', () => {
  assert.deepEqual(readInfo(info('my-mod', '1.2.3', { title: 'My Mod' })), { name: 'my-mod', version: '1.2.3', title: 'My Mod' });
  assert.deepEqual(readInfo('﻿' + info('a', '0.1')), { name: 'a', version: '0.1', title: '' }, 'BOM and two-part versions are fine');
  assert.equal(readInfo('{ not json').problem, 'import.check.infoJson');
  assert.equal(readInfo('[1, 2]').problem, 'import.check.infoJson');
  assert.equal(readInfo('null').problem, 'import.check.infoJson');
  assert.equal(readInfo(info('has space', '1.0.0')).problem, 'import.check.infoName');
  assert.equal(readInfo(info('dot.ted', '1.0.0')).problem, 'import.check.infoName');
  assert.equal(readInfo(info('CON', '1.0.0')).problem, 'import.check.infoName');
  assert.equal(readInfo(info('x'.repeat(101), '1.0.0')).problem, 'import.check.infoName');
  assert.equal(readInfo(JSON.stringify({ version: '1.0.0' })).problem, 'import.check.infoName');
  assert.equal(readInfo(info('ok', '1')).problem, 'import.check.infoVersion');
  assert.equal(readInfo(info('ok', '1.0.0.0')).problem, 'import.check.infoVersion');
  assert.equal(readInfo(JSON.stringify({ name: 'ok', version: 1 })).problem, 'import.check.infoVersion');
});

test('zipIdentity reads Factorio zip names', () => {
  assert.deepEqual(zipIdentity('flib_0.15.0.zip'), { name: 'flib', version: '0.15.0' });
  assert.deepEqual(zipIdentity('my_mod_name_1.2.3.ZIP'), { name: 'my_mod_name', version: '1.2.3' });
  assert.equal(zipIdentity('random.zip'), null);
  assert.equal(zipIdentity('bad name_1.0.0.zip'), null);
});

test('compareVersions is numeric', () => {
  assert.equal(compareVersions('1.2.10', '1.2.9'), 1);
  assert.equal(compareVersions('1.0', '1.0.0'), 0);
  assert.equal(compareVersions('0.9.0', '1.0.0'), -1);
});

test('assess: ready, blocked by a broken info.json, by limits and by mods already in the workspace', () => {
  const result = assess([
    { kind: 'folder', label: 'a', name: 'a', version: '1.0.0', files: 3 },
    { kind: 'folder', label: 'broken', problem: 'import.check.infoName', files: 3 },
    { kind: 'folder', label: 'empty', name: 'empty', version: '1.0.0', files: 0 },
    { kind: 'folder', label: 'huge', name: 'huge', version: '1.0.0', files: MAX_IMPORT_FILES + 1 },
    { kind: 'zip', label: 'big_1.0.0.zip', name: 'big', version: '1.0.0', size: MAX_ZIP_BYTES + 1 },
    { kind: 'folder', label: 'Taken', name: 'Taken', version: '1.0.0', files: 3 },
    { kind: 'zip', label: 'odd.zip', size: 10 },
  ], ['taken']);
  assert.deepEqual(result.map((r) => [r.label, r.status, r.reason]), [
    ['a', 'ok', 'import.check.ready'],
    ['broken', 'blocked', 'import.check.infoName'],
    ['empty', 'blocked', 'import.check.noFiles'],
    ['huge', 'blocked', 'import.check.tooMany'],
    ['big_1.0.0.zip', 'blocked', 'import.check.tooLarge'],
    ['Taken', 'blocked', 'import.check.exists'],
    ['odd.zip', 'ok', 'import.check.zipUnknown'],
  ]);
  assert.equal(result[3].params.max, MAX_IMPORT_FILES);
  assert.equal(result[4].params.max, 200);
});

test('assess: of one mod in several versions only the newest is offered', () => {
  const result = assess([
    { kind: 'folder', label: 'flib_0.14.0', name: 'flib', version: '0.14.0', files: 5 },
    { kind: 'zip', label: 'flib_0.15.0.zip', name: 'flib', version: '0.15.0', size: 10 },
    { kind: 'folder', label: 'FLIB', name: 'FLIB', version: '0.9.0', files: 5 },
  ], []);
  assert.deepEqual(result.map((r) => r.status), ['blocked', 'ok', 'blocked']);
  assert.equal(result[0].reason, 'import.check.duplicate');
  assert.equal(result[0].params.version, '0.15.0');
});
