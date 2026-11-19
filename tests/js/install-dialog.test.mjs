import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Execute the actual lifecycle handlers with a small dialog/API harness; no browser or network required.
const source = fs.readFileSync(new URL('../../src/FactorioModStudio.Server/wwwroot/build-view.js', import.meta.url), 'utf8');
const handler = (name, next) => source.slice(source.indexOf('  async function ' + name + '('), source.indexOf('  async function ' + next + '('));
function setup(available, loading = false) {
  const paints = [];
  const calls = [];
  const dialog = { open: false, showModal() { this.open = true; } };
  const get = { available, loading, availableError: null };
  let resolve;
  const response = new Promise(r => { resolve = r; });
  const context = vm.createContext({
    get, refs: { installDialog: dialog, instClose: { focus() {} } },
    inst: {}, s: { installs: [] }, setPathError() {}, showAddForm() {}, renderInstallList() {}, applyInstalls() {}, t: k => k,
    renderGet() { if (dialog.open) paints.push(get.available ? 'ready' : get.loading ? 'loading' : 'empty'); },
    api(url) { calls.push(url); return url.endsWith('/available') ? response : Promise.resolve({ installations: [] }); },
  });
  vm.runInContext(handler('openInstallDialog', 'submitAdd') + handler('loadAvailable', 'startInstall'), context);
  return { context, paints, calls, dialog, get, resolve };
}

test('opening a dialog paints cached releases without waiting for another interaction', async () => {
  const h = setup({ branches: [{ branch: '2.0' }] });
  await h.context.openInstallDialog();
  assert.deepEqual(h.paints, ['ready']);
  assert.ok(!h.calls.includes('/api/factorio/available'));
  h.dialog.open = false;
  await h.context.openInstallDialog();
  assert.deepEqual(h.paints, ['ready', 'ready']);
});

test('opening while releases are loading immediately paints loading state', async () => {
  const h = setup(null, true);
  await h.context.openInstallDialog();
  assert.deepEqual(h.paints, ['loading']);
  assert.ok(!h.calls.includes('/api/factorio/available'));
});

test('first load paints releases after the response without another interaction', async () => {
  const h = setup(null);
  await h.context.openInstallDialog();
  assert.ok(h.paints.includes('loading'));
  h.resolve({ branches: [{ branch: '2.0' }] });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.paints.at(-1), 'ready');
});
