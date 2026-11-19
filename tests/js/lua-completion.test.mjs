import test from 'node:test';
import assert from 'node:assert/strict';
import { completionAt, completionNames } from '../../src/FactorioModStudio.Server/wwwroot/lua-completion.js';

test('completion recognizes runtime globals and completes only the event identifier', () => {
  const text = 'defines.events.on_tfoo';
  const context = completionAt(text, 'defines.events.on_t'.length);
  assert.deepEqual(context, { group: 'events', prefix: 'on_t', start: 15, end: text.length });
  assert.deepEqual(completionNames({ events: ['on_tick', 'on_init'] }, context), ['on_tick']);
  assert.deepEqual(completionNames({ globals: ['game', 'script'] }, completionAt('ga', 2)), ['game']);
  assert.deepEqual(completionNames(null, context), []);
});

test('completion ignores comments, strings, and unrelated receivers', () => {
  for (const text of ['-- defines.events.on_', '[[defines.events.on_', 'foo.game', 'obj.defines.events.on_']) {
    assert.equal(completionAt(text, text.length), null, text);
  }
});
