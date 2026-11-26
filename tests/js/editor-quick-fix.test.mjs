import test from 'node:test';
import assert from 'node:assert/strict';
import { quickFixEdit } from '../../src/FactorioModStudio.Server/wwwroot/editor-quick-fix.js';

const apply = (text, edit) => text.slice(0, edit.start) + edit.value + text.slice(edit.end);

test('path fix edits the validated literal range and refuses a stale offset', () => {
  const text = 'local icon = "__mod__/icon.png"';
  const fix = { pathCaseFix: { offset: 14, expectedText: '__mod__/icon.png', replacement: '__mod__/Icon.png' } };
  assert.equal(apply(text, quickFixEdit(text, fix)), 'local icon = "__mod__/Icon.png"');
  assert.equal(quickFixEdit('--\n' + text, fix), null);
});

test('locale fix inserts an empty key in the existing section and leaves the caret after equals', () => {
  const text = '[one]\nold=Old\n[two]\nx=X\n';
  const fix = { localeKeyFix: { section: 'one', key: 'new' } };
  const edit = quickFixEdit(text, fix);
  const after = apply(text, edit);
  assert.equal(after, '[one]\nnew=\nold=Old\n[two]\nx=X\n');
  assert.equal(after[edit.caret], '\n');
  assert.equal(quickFixEdit(after, fix), null);
  assert.equal(quickFixEdit(text + '[one]\n', fix), null);
});
