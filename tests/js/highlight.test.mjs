import test from 'node:test';
import assert from 'node:assert/strict';
import { tokenize } from '../../src/FactorioModStudio.Server/wwwroot/lua-highlight.js';

const TYPES = new Set(['keyword', 'string', 'number', 'comment', 'global', 'plain']);

function check(text) {
  const toks = tokenize(text);
  assert.ok(Array.isArray(toks), 'tokenize returns an array');
  let pos = 0;
  for (const t of toks) {
    assert.ok(TYPES.has(t.type), `unknown type ${t.type}`);
    assert.equal(t.from, pos, `gap or overlap at ${pos} (token from=${t.from} to=${t.to} ${t.type}) in ${JSON.stringify(text.slice(0, 80))}`);
    assert.ok(t.to > t.from, `empty or negative token at ${t.from}`);
    pos = t.to;
  }
  assert.equal(pos, text.length, 'tokens must cover the entire text');
  assert.equal(toks.map(t => text.slice(t.from, t.to)).join(''), text);
  return toks;
}

// token (type, slice) pairs without the plain whitespace noise
function pairs(text) {
  return check(text).map(t => [t.type, text.slice(t.from, t.to)]);
}
function ofType(text, type) {
  return pairs(text).filter(([t]) => t === type).map(([, s]) => s);
}
// the type of the token that contains the first occurrence of `needle`
function typeAt(text, needle, nth = 0) {
  let idx = -1;
  for (let i = 0; i <= nth; i++) idx = text.indexOf(needle, idx + 1);
  assert.notEqual(idx, -1, `needle ${needle} not found`);
  return check(text).find(t => t.from <= idx && idx < t.to).type;
}

test('empty and trivial inputs', () => {
  assert.deepEqual(tokenize(''), []);
  check(' ');
  check('\n');
  check('x');
  check('\r\n\t ');
});

test('keywords', () => {
  const kws = 'and break do else elseif end false for function goto if in local nil not or repeat return then true until while'.split(' ');
  for (const k of kws) {
    assert.deepEqual(pairs(k), [['keyword', k]], k);
    assert.equal(typeAt(`x ${k} y`, k), 'keyword', k);
  }
});

test('identifiers containing keywords are not keywords', () => {
  for (const id of ['endx', 'xend', 'end_', '_end', 'iff', 'function1', 'fo', 'nill', 'Local', 'END', 'dofile', 'in2', 'notx']) {
    assert.deepEqual(pairs(id), [['plain', id]], id);
  }
});

test('keywords adjacent to punctuation', () => {
  assert.equal(typeAt('(end)', 'end'), 'keyword');
  assert.equal(typeAt('x=function()return{}end', 'function'), 'keyword');
  assert.equal(typeAt('x=function()return{}end', 'return'), 'keyword');
  assert.equal(typeAt('x=function()return{}end', 'end'), 'keyword');
  assert.equal(typeAt('a=not b', 'not'), 'keyword');
  assert.equal(typeAt('if(x)then', 'if'), 'keyword');
  assert.equal(typeAt('if(x)then', 'then'), 'keyword');
});

test('strings with escapes', () => {
  assert.deepEqual(pairs('"abc"'), [['string', '"abc"']]);
  assert.deepEqual(pairs("'abc'"), [['string', "'abc'"]]);
  assert.deepEqual(ofType('x = "a\\"b" y', 'string'), ['"a\\"b"']);
  assert.deepEqual(ofType('"a\\\\" b "c"', 'string'), ['"a\\\\"', '"c"']);
  assert.deepEqual(ofType("'it\\'s' end", 'string'), ["'it\\'s'"]);
  assert.deepEqual(ofType('"a\\z \n  b" x', 'string'), ['"a\\z \n  b"']);
  assert.deepEqual(ofType('"a\\\nb" x', 'string'), ['"a\\\nb"']);
  assert.deepEqual(ofType('"a\\\r\nb" x', 'string'), ['"a\\\r\nb"']);
  assert.equal(typeAt('"end function 12 --x"', 'end'), 'string');
  assert.equal(typeAt('"end function 12 --x"', '12'), 'string');
  assert.equal(typeAt('"end function 12 --x"', '--'), 'string');
  assert.equal(typeAt('"a" game "b"', 'game'), 'global');
  assert.equal(typeAt("'\"' game '\"'", 'game'), 'global');
});

test('strings: keyword text inside is not a keyword, even adjacent', () => {
  const toks = pairs('local s = "if" .. \'end\'');
  assert.deepEqual(toks.filter(([t]) => t === 'keyword').map(([, s]) => s), ['local']);
  assert.deepEqual(toks.filter(([t]) => t === 'string').map(([, s]) => s), ['"if"', "'end'"]);
});

test('long strings with all bracket levels', () => {
  assert.deepEqual(ofType('x = [[abc]]', 'string'), ['[[abc]]']);
  assert.deepEqual(ofType('x = [==[abc]==]', 'string'), ['[==[abc]==]']);
  assert.deepEqual(ofType('x = [=[a]]b]==]c]=] y', 'string'), ['[=[a]]b]==]c]=]']);
  assert.deepEqual(ofType('x = [[\nline1\nline2\n]] y', 'string'), ['[[\nline1\nline2\n]]']);
  assert.deepEqual(ofType('x = [==[ ]=] ]] ]==] [[z]]', 'string'), ['[==[ ]=] ]] ]==]', '[[z]]']);
  assert.deepEqual(ofType('[[a]][[b]]', 'string'), ['[[a]]', '[[b]]']);
  assert.equal(typeAt('[[ end 123 -- ]]', 'end'), 'string');
  assert.equal(typeAt('[[ end 123 -- ]]', '123'), 'string');
  assert.equal(typeAt('[[ "quote ]] x "', 'x'), 'plain');
});

test('index expressions with brackets are not long strings', () => {
  assert.equal(typeAt('t[1] = t[ x ]', '1'), 'number');
  assert.deepEqual(ofType('a[b[1]]', 'string'), []);
  assert.deepEqual(ofType('a[=b]', 'string'), []);
  assert.equal(typeAt('a[ [[k]] ]', 'k'), 'string');
});

test('numbers', () => {
  for (const n of ['0', '12', '123456', '0x1F', '0XFF', '1.5', '.5', '5.', '1e5', '1E5', '1e-5', '1e+5', '1.5e-3', '0x.8p1', '0xA.8p-2', '3.14159', '0x0']) {
    assert.deepEqual(ofType(`x = ${n}`, 'number'), [n], n);
  }
  assert.equal(typeAt('x = 1 + 22', '22'), 'number');
  assert.equal(typeAt('f(1,2)', '1'), 'number');
  assert.equal(typeAt('f(1,2)', '2'), 'number');
});

test('numbers vs identifiers and concat', () => {
  assert.deepEqual(pairs('x1'), [['plain', 'x1']]);
  assert.deepEqual(pairs('_2'), [['plain', '_2']]);
  assert.equal(typeAt('x1 = 5', '1'), 'plain');
  // `3..2` : Lua reads this as a malformed number; we only require full coverage and that it does not throw
  check('3..2');
  check('a..b');
  assert.equal(typeAt('a .. 1', '1'), 'number');
  check('1..2');
  check('0x');
  check('1e');
  check('1e+');
  check('...');
});

test('comments', () => {
  assert.deepEqual(ofType('x = 1 -- hello "world" end\ny = 2', 'comment'), ['-- hello "world" end']);
  assert.deepEqual(ofType('--c1\n--c2\n', 'comment'), ['--c1', '--c2']);
  assert.deepEqual(ofType('x --[[ a\nb ]] y', 'comment'), ['--[[ a\nb ]]']);
  assert.deepEqual(ofType('--[==[ a ]] b ]=] c ]==] z', 'comment'), ['--[==[ a ]] b ]=] c ]==]']);
  assert.deepEqual(ofType('--[[ a ]] --[[ b ]]', 'comment'), ['--[[ a ]]', '--[[ b ]]']);
  assert.equal(typeAt('--[[ end 1 "x ]] end', 'end'), 'comment');
  assert.equal(typeAt('--[[ end 1 "x ]] end', 'end', 1), 'keyword');
  // "--[" without a second bracket, "--[=" and "--[==" without a third are plain line comments
  assert.deepEqual(ofType('--[ not long ]]\nend', 'comment'), ['--[ not long ]]']);
  assert.deepEqual(ofType('--[=x not long ]=]\nend', 'comment'), ['--[=x not long ]=]']);
  assert.deepEqual(ofType('-- [[ also a line comment\nend', 'comment'), ['-- [[ also a line comment']);
  assert.deepEqual(ofType('---[[ line comment\nend', 'comment'), ['---[[ line comment']);
  assert.equal(typeAt('---[[ line comment\nend', 'end'), 'keyword');
  assert.equal(typeAt('-- game.print\nx', 'game'), 'comment');
  // comment at EOF without newline, with CRLF
  assert.deepEqual(ofType('x -- tail', 'comment'), ['-- tail']);
  const crlf = pairs('-- a\r\nend');
  assert.equal(crlf.find(([t]) => t === 'keyword')[1], 'end');
  assert.ok(crlf.find(([t]) => t === 'comment')[1].startsWith('-- a'));
  // a single dash is not a comment
  assert.deepEqual(ofType('a - b', 'comment'), []);
  assert.deepEqual(ofType('a-b', 'comment'), []);
  assert.deepEqual(ofType('a--b', 'comment'), ['--b']);
});

test('globals', () => {
  const names = 'data game script defines settings storage global remote commands rcon prototypes mods log serpent table_size localised_print require table string math ipairs pairs type tostring tonumber select unpack error assert pcall next setmetatable getmetatable'.split(' ');
  for (const g of names) {
    assert.deepEqual(pairs(g), [['global', g]], g);
    assert.equal(typeAt(`x = ${g}`, g), 'global', g);
    assert.equal(typeAt(`(${g})`, g), 'global', g);
  }
});

test('globals are not matched as substrings or after . or :', () => {
  assert.equal(typeAt('a.table', 'table'), 'plain');
  assert.equal(typeAt('a:table()', 'table'), 'plain');
  assert.equal(typeAt('x.data.raw', 'data'), 'plain');
  assert.equal(typeAt('player.game', 'game'), 'plain');
  assert.equal(typeAt('obj:log()', 'log'), 'plain');
  assert.equal(typeAt('table.insert(t, 1)', 'table'), 'global');
  assert.equal(typeAt('table.insert(t, 1)', 'insert'), 'plain');
  assert.equal(typeAt('string.format("x")', 'string'), 'global');
  assert.equal(typeAt('math.floor(1.5)', 'math'), 'global');
  assert.equal(typeAt('mytable', 'table'), 'plain');
  assert.equal(typeAt('tables', 'tables'), 'plain');
  assert.equal(typeAt('data2', 'data2'), 'plain');
  assert.equal(typeAt('_data', '_data'), 'plain');
  assert.equal(typeAt('game_speed', 'game_speed'), 'plain');
  assert.equal(typeAt('Game', 'Game'), 'plain');
  assert.equal(typeAt('TABLE', 'TABLE'), 'plain');
  // `..` concat is not a member access
  assert.equal(typeAt('a..table', 'table'), 'global');
  assert.equal(typeAt('"x"..game', 'game'), 'global');
  // `...` vararg followed by global with a space is also not member access
  assert.equal(typeAt('f(...) game', 'game'), 'global');
});

test('global names that are also member names after a keyword-ish context', () => {
  assert.equal(typeAt('return table', 'table'), 'global');
  assert.equal(typeAt('local x = require("a")', 'require'), 'global');
  assert.equal(typeAt('for _, p in pairs(game.players) do end', 'pairs'), 'global');
  assert.equal(typeAt('for _, p in pairs(game.players) do end', 'game'), 'global');
  assert.equal(typeAt('for _, p in pairs(game.players) do end', 'players'), 'plain');
});

test('unterminated strings degrade gracefully', () => {
  // runs to end of line (or EOF); the next line is highlighted normally
  let toks = pairs('x = "abc\nend');
  assert.equal(toks.find(([t, s]) => t === 'string' && s.startsWith('"abc'))[1].replace(/\n$/, ''), '"abc');
  assert.equal(toks.find(([, s]) => s === 'end')[0], 'keyword');
  toks = pairs("x = 'abc");
  assert.deepEqual(toks.filter(([t]) => t === 'string'), [['string', "'abc"]]);
  check('"');
  check("'");
  check('"\\');
  check('"abc\\');
  assert.deepEqual(ofType('"abc\r\nend', 'string').map(s => s.replace(/\r?\n$/, '')), ['"abc']);
});

test('unterminated long strings and comments run to EOF', () => {
  assert.equal(typeAt('x = [[abc\nend', 'end'), 'string');
  assert.equal(typeAt('x = [==[abc ]] ]=]\nend', 'end'), 'string');
  assert.equal(typeAt('--[[ abc\nend', 'end'), 'comment');
  assert.equal(typeAt('--[==[ abc ]] \nend', 'end'), 'comment');
  check('[[');
  check('[==[');
  check('--[[');
  check('--[==[');
  check('[=');
  check('[==');
  check('--[=');
});

test('realistic factorio code: full coverage and expected classification', () => {
  const src = `local util = require("util")
data:extend({
  {
    type = "item", name = "my-item", stack_size = 100, weight = 1e3,
    icon = "__my-mod__/graphics/icon.png", -- comment with end
  },
})
script.on_event(defines.events.on_tick, function(event)
  for _, p in pairs(game.players) do
    if p.valid and not p.connected then
      log("x]] y")
    elseif event.tick % 60 == 0x3C then
      p.print([[hello ]] .. table.concat({1, 2}, ","))
    end
  end
end)
--[[ block
comment ]]
return { n = nil, b = true }
`;
  const toks = pairs(src);
  const kw = toks.filter(([t]) => t === 'keyword').map(([, s]) => s);
  assert.deepEqual(kw, ['local', 'function', 'for', 'in', 'do', 'if', 'and', 'not', 'then', 'elseif', 'then', 'end', 'end', 'end', 'return', 'nil', 'true']);
  const gl = toks.filter(([t]) => t === 'global').map(([, s]) => s);
  assert.deepEqual(gl, ['require', 'data', 'type', 'script', 'defines', 'pairs', 'game', 'log', 'table']);
  assert.deepEqual(toks.filter(([t]) => t === 'number').map(([, s]) => s), ['100', '1e3', '60', '0x3C', '1', '2']);
  assert.equal(toks.filter(([t]) => t === 'comment').length, 2);
});

test('crlf, tabs, unicode, bom, nul, lone surrogates: always full coverage', () => {
  check('x = 1\r\ny = "a"\r\n-- c\r\n');
  check('\t\tlocal x\t=\t1');
  check('-- привет\nx = "你好"');
  check('﻿local x');
  check('x\0y "\0" --\0');
  check('"\uD83D"');
  check('😀 = 1');
  check('\uD800');
  check('x = "😀" -- 😀');
});

test('tokens are plain objects with numeric from/to', () => {
  for (const t of tokenize('local x = 1 -- c')) {
    assert.equal(typeof t.type, 'string');
    assert.ok(Number.isInteger(t.from));
    assert.ok(Number.isInteger(t.to));
  }
});

test('is deterministic and does not keep state between calls', () => {
  const a = JSON.stringify(tokenize('--[[ x'));
  tokenize('"abc');
  tokenize('[==[');
  assert.equal(JSON.stringify(tokenize('--[[ x')), a);
  const b = JSON.stringify(tokenize('local x = 1'));
  assert.equal(JSON.stringify(tokenize('local x = 1')), b);
});

function rng(seed) {
  let s = seed >>> 0;
  return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
}

test('fuzz: random token soup (fixed seed) keeps the coverage property', () => {
  const atoms = ['function', 'end', 'if', 'then', 'else', 'local', 'return', 'nil', 'true', '(', ')', '[', ']', '{', '}', '[[', ']]', '[=[', ']=]', '[==[', ']==]',
    '--', '--[[', '--[==[', '"', "'", '\\', '\\z', '\n', '\r\n', ' ', '\t', '=', '..', '...', '::', 'x', '1', '0x', '1e-', '.5', '\0', 'é', '\uD83D', '\uDE00', '﻿',
    'game', 'table', '.', ':', 'a.table', 'data', '-', ',', '3..2'];
  const r = rng(12345);
  for (let i = 0; i < 5000; i++) {
    const n = Math.floor(r() * 60);
    let s = '';
    for (let j = 0; j < n; j++) s += atoms[Math.floor(r() * atoms.length)] + (r() < 0.3 ? ' ' : '');
    check(s);
  }
});

test('fuzz: random chars (fixed seed) keeps the coverage property', () => {
  const r = rng(987654321);
  for (let i = 0; i < 3000; i++) {
    const n = Math.floor(r() * 200);
    let s = '';
    for (let j = 0; j < n; j++) s += String.fromCharCode(r() < 0.25 ? Math.floor(r() * 65536) : Math.floor(r() * 128));
    check(s);
  }
});

test('fuzz: every prefix of realistic code is covered', () => {
  const src = `local t = { a = [==[x]==], b = "s\\"t", c = 0x1F } -- c\n--[[ m ]] function f() if x then return 1e-5 end end\n`;
  for (let i = 0; i <= src.length; i++) check(src.slice(0, i));
});

function timed(name, text, limitMs = 1500) {
  tokenize('x = 1');
  const t0 = performance.now();
  const toks = tokenize(text);
  const dt = performance.now() - t0;
  assert.ok(dt < limitMs, `${name}: ${dt.toFixed(0)} ms for ${text.length} chars`);
  // coverage check (cheap)
  let pos = 0;
  for (const t of toks) { assert.equal(t.from, pos); pos = t.to; }
  assert.equal(pos, text.length);
}

test('perf: 2 MB of realistic code', () => {
  const unit = `local function f(a, b)\n  if a and not b then return table.concat({1, 2.5, 0xFF}, ", ") end -- c\n  return "str\\"ing" .. [[long]]\nend\n`;
  timed('realistic', unit.repeat(Math.ceil(2_000_000 / unit.length)).slice(0, 2_000_000));
});

test('perf: 2 MB adversarial inputs stay linear', () => {
  const MB2 = 2_000_000;
  timed('unterminated string', '"' + 'a'.repeat(MB2));
  timed('unterminated long string', '[==[' + 'a'.repeat(MB2));
  timed('unterminated long comment', '--[[' + ']'.repeat(MB2));
  timed('many [[ openers', '[[ '.repeat(MB2 / 3));
  timed('many unterminated long comments', '--[==[ '.repeat(MB2 / 7));
  timed('many unterminated strings', '"a\n'.repeat(MB2 / 3));
  timed('increasing level openers', Array.from({ length: 1500 }, (_, i) => '[' + '='.repeat(i) + '[ ').join(''));
  timed('backslashes', '"' + '\\'.repeat(MB2));
  timed('equals', '[' + '='.repeat(MB2));
  timed('dashes', '-'.repeat(MB2));
  timed('dots', '.'.repeat(MB2));
  timed('digits', '1'.repeat(MB2));
  timed('hex', '0x' + 'f'.repeat(MB2));
  timed('exponents', '1e+'.repeat(MB2 / 3));
  timed('identifier', 'a'.repeat(MB2));
  timed('many tiny tokens', 'a '.repeat(MB2 / 2));
  timed('newlines', '\n'.repeat(MB2));
  timed('crlf', '\r\n'.repeat(MB2 / 2));
  timed('globals', 'game.table '.repeat(MB2 / 11));
});
