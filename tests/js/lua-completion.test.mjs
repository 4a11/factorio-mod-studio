import test from 'node:test';
import assert from 'node:assert/strict';
import { completionAt, completionNames, eventAt, onEventArgument } from '../../src/FactorioModStudio.Server/wwwroot/lua-completion.js';

test('completion recognizes runtime globals and completes only the event identifier', () => {
  const text = 'defines.events.on_tfoo';
  const context = completionAt(text, 'defines.events.on_t'.length);
  assert.deepEqual(context, { group: 'events', prefix: 'on_t', start: 15, end: text.length });
  assert.deepEqual(completionNames({ events: ['on_tick', 'on_init'] }, context), ['on_tick']);
  assert.deepEqual(completionNames({ globals: ['game', 'script'] }, completionAt('ga', 2)), ['game']);
  assert.deepEqual(completionNames(null, context), []);
});

test('hover targets event identifiers, ignoring comments, quoted text and other objects', () => {
  for (const text of ['defines.events.on_tick', '  script.on_event(defines.events.on_tick, handler)']) {
    assert.equal(eventAt(text, text.indexOf('on_tick') + 3), 'on_tick');
  }
  for (const text of ['-- defines.events.on_tick', '"defines.events.on_tick"', 'obj.defines.events.on_tick']) {
    assert.equal(eventAt(text, text.indexOf('on_tick') + 3), null);
  }
  assert.equal(eventAt('defines.events.on_tick', -1), null);
});

test('parameter help counts only top-level commas in the on_event call', () => {
  const argument = text => onEventArgument(text, text.length);
  assert.equal(argument('script.on_event('), 0);
  assert.equal(argument('script.on_event({event1, event2}, get_handler(a, b'), 1);
  assert.equal(argument('script.on_event("a,b", handler, {'), 2);
  assert.equal(argument('script.on_event(event, function() local a, b'), null);
  assert.equal(argument('script.on_event(event, handler)'), null);
  assert.equal(argument('-- script.on_event(event, '), null);
});

test('completion ignores comments, strings, and unrelated receivers', () => {
  for (const text of ['-- defines.events.on_', '[[defines.events.on_', 'foo.game', 'obj.defines.events.on_']) {
    assert.equal(completionAt(text, text.length), null, text);
  }
});
