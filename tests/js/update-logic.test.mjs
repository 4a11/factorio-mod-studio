// Tests for wwwroot/update-logic.js: the version chip's state, download sizes and speed, and the release notes as blocks.
import test from 'node:test';
import assert from 'node:assert/strict';

const { chipState, percentOf, formatBytes, addSample, parseNotes, parseInline } =
  await import('../../src/FactorioModStudio.Server/wwwroot/update-logic.js');

test('chipState: downloading and installed win over a newer version; a failed download with a newer version is "failed"', () => {
  assert.equal(chipState(null), 'current');
  assert.equal(chipState({ latest: null }), 'current');
  assert.equal(chipState({ latest: { version: '0.3.1' } }), 'available');
  assert.equal(chipState({ latest: { version: '0.3.1' }, install: { state: 'downloading' } }), 'downloading');
  assert.equal(chipState({ latest: null, install: { state: 'succeeded' } }), 'installed');
  assert.equal(chipState({ latest: { version: '0.3.1' }, install: { state: 'failed' } }), 'failed');
  assert.equal(chipState({ latest: { version: '0.3.1' }, install: { state: 'cancelled' } }), 'failed');
  assert.equal(chipState({ latest: null, install: { state: 'failed' } }), 'current');
});

test('percentOf: whole percent, null without a size, never above 100', () => {
  assert.equal(percentOf({ received: 0, total: null }), null);
  assert.equal(percentOf({ received: 5, total: 0 }), null);
  assert.equal(percentOf({ received: 999, total: 1000 }), 99);
  assert.equal(percentOf({ received: 1000, total: 1000 }), 100);
  assert.equal(percentOf({ received: 1200, total: 1000 }), 100);
});

test('formatBytes: units and decimals', () => {
  const plain = (n, o) => n.toFixed(o.maximumFractionDigits);
  assert.equal(formatBytes(18, plain), '18 B');
  assert.equal(formatBytes(1536, plain), '1.5 KB');
  assert.equal(formatBytes(640 * 1024, plain), '640 KB');
  assert.equal(formatBytes(12.3 * 1024 * 1024, plain), '12.3 MB');
  assert.equal(formatBytes(-5, plain), '0 B');
  assert.equal(formatBytes('x', plain), '0 B');
});

test('addSample: speed over the recent window, reset when a new download starts', () => {
  const samples = [];
  assert.equal(addSample(samples, 0, 0), null);
  assert.equal(addSample(samples, 1000, 400), null); // under half a second
  assert.equal(addSample(samples, 2000, 1000), 2000); // 2000 bytes in 1 s
  assert.equal(addSample(samples, 10000, 5000), 2000); // older samples leave the 4 s window: (10000 - 2000) bytes in 4 s
  assert.equal(addSample(samples, 100, 6000), null); // fewer bytes than before: a new download
});

test('parseNotes: headings, bullets with continuation lines, paragraphs', () => {
  const notes = '## [0.3.1] - 2026-11-12\n\n### Fixed\n- **Far fewer** false problems: about\n  4600 became 950.\n- `FMS040` no longer\n\nInstall: unpack the archive.\r\n';
  const blocks = parseNotes(notes);
  assert.deepEqual(blocks.map((b) => [b.type, b.level ?? null]), [['heading', 2], ['heading', 3], ['item', null], ['item', null], ['para', null]]);
  assert.deepEqual(blocks[2].inline, [{ strong: 'Far fewer' }, { text: ' false problems: about 4600 became 950.' }]);
  assert.deepEqual(blocks[3].inline, [{ code: 'FMS040' }, { text: ' no longer' }]);
  assert.deepEqual(parseNotes(''), []);
  assert.deepEqual(parseNotes(null), []);
});

test('parseInline: links only for http(s), emphasis, plain text kept', () => {
  assert.deepEqual(parseInline('see [the page](https://example.org/x) now'),
    [{ text: 'see ' }, { link: 'the page', href: 'https://example.org/x' }, { text: ' now' }]);
  assert.deepEqual(parseInline('[bad](javascript:alert(1))'), [{ text: 'bad' }, { text: ')' }]);
  assert.deepEqual(parseInline('*Smoke test* and _ticks_'), [{ em: 'Smoke test' }, { text: ' and ' }, { em: 'ticks' }]);
  assert.deepEqual(parseInline('snake_case_name stays'), [{ text: 'snake_case_name stays' }]);
  assert.deepEqual(parseInline('no markup'), [{ text: 'no markup' }]);
});
