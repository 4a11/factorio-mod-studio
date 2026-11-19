import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../../src/FactorioModStudio.Server/wwwroot/editor-tabs.js', import.meta.url), 'utf8');
const handlers = source.slice(source.indexOf('  async function save('), source.indexOf('  async function revert('));
function setup({ api = async () => ({ etag: 'saved' }), fetch = async () => diskResponse() } = {}) {
  const tab = { kind: 'text', modId: 'demo', path: 'data.lua', original: 'before', area: { value: 'mine' }, etag: 'opened' };
  const requests = [];
  const context = vm.createContext({
    active: tab, isDirty: t => t.area.value !== t.original, fileUrl: path => path, fetch,
    api(url, options) { requests.push(options); return api(url, options); },
    setStatus() {}, setBanner(t, key) { t.banner = key; },
    updateEditorState() {}, updateTabEl() {}, onSaved() {}, getModId: () => 'demo',
    afterTextChange() {},
  });
  vm.runInContext(handlers, context);
  return { tab, requests, save: () => context.save(), overwrite: () => context.overwriteWithMine(tab), reload: () => context.reloadFromDisk(tab) };
}

function diskResponse(text = async () => 'external') {
  return { ok: true, status: 200, headers: { get: () => 'external-tag' }, text };
}

test('repeated Save cannot overwrite a conflict, but explicit overwrite uses the latest disk ETag', async () => {
  const h = setup({ api: async (_, { headers }) => {
    if (headers['If-Match'] !== 'external-tag') throw Object.assign(new Error('conflict'), { status: 409, body: { etag: 'external-tag' } });
    return { etag: 'saved' };
  } });
  await h.save();
  await h.save();
  assert.equal(h.requests.length, 1);
  assert.equal(h.tab.etag, 'opened');
  assert.equal(h.tab.area.value, 'mine');
  assert.equal(h.tab.banner, 'editor.conflict');
  await h.overwrite();
  assert.equal(h.requests[1].headers['If-Match'], 'external-tag');
  assert.equal(h.tab.original, 'mine');
  assert.equal(h.tab.conflict, false);
});

test('saving an earlier buffer keeps subsequent edits dirty and avoids overlapping writes', async () => {
  let complete;
  const h = setup({ api: () => new Promise(resolve => { complete = resolve; }) });
  const pending = h.save();
  h.tab.area.value = 'new typing';
  await h.save();
  complete({ etag: 'saved' });
  await pending;
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].body, 'mine');
  assert.equal(h.tab.original, 'mine');
  assert.equal(h.tab.area.value, 'new typing');
});

test('a background reload cannot replace typing that starts while its response body is pending', async () => {
  let finishRead;
  let startRead;
  const started = new Promise(resolve => { startRead = resolve; });
  const h = setup({ fetch: async () => diskResponse(() => new Promise(resolve => { finishRead = resolve; startRead(); })) });
  h.tab.area.value = 'before';
  const pending = h.reload();
  await started;
  h.tab.area.value = 'typed while loading';
  finishRead('external');
  await pending;
  assert.equal(h.tab.original, 'before');
  assert.equal(h.tab.area.value, 'typed while loading');
});
