// Lua tokenizer for the editor's highlight layer. Pure string -> tokens, no DOM access, linear time, never throws.
// tokenize(text) returns [{ type, from, to }] sorted, non-overlapping and covering the whole text without gaps.
// Types: keyword, string, number, comment, global, plain.

const KEYWORDS = new Set([
  'and', 'break', 'do', 'else', 'elseif', 'end', 'false', 'for', 'function', 'goto', 'if', 'in', 'local', 'nil',
  'not', 'or', 'repeat', 'return', 'then', 'true', 'until', 'while',
]);

const GLOBALS = new Set([
  'data', 'game', 'script', 'defines', 'settings', 'storage', 'global', 'remote', 'commands', 'rcon', 'prototypes',
  'mods', 'log', 'serpent', 'table_size', 'localised_print', 'require', 'table', 'string', 'math', 'ipairs', 'pairs',
  'type', 'tostring', 'tonumber', 'select', 'unpack', 'error', 'assert', 'pcall', 'next', 'setmetatable', 'getmetatable',
]);

const isIdentStart = (c) => (c >= 97 && c <= 122) || (c >= 65 && c <= 90) || c === 95 || c > 127;
const isDigit = (c) => c >= 48 && c <= 57;
const isIdentPart = (c) => isIdentStart(c) || isDigit(c);

// At s[i] === '[': level of a long bracket opening here, or -1. `after` (via the returned pair) is the index past it.
function longOpen(s, i) {
  let j = i + 1;
  while (j < s.length && s.charCodeAt(j) === 61) j++;
  return j < s.length && s.charCodeAt(j) === 91 ? [j - i - 1, j + 1] : null;
}

function longEnd(s, from, level) {
  const at = s.indexOf(']' + '='.repeat(level) + ']', from);
  return at < 0 ? s.length : at + level + 2;
}

function skipNumber(s, i) {
  const n = s.length;
  const hex = s.charCodeAt(i) === 48 && i + 1 < n && (s[i + 1] === 'x' || s[i + 1] === 'X');
  if (hex) i += 2;
  while (i < n) {
    const c = s.charCodeAt(i);
    if (isIdentPart(c)) {
      const exponent = hex ? (c === 112 || c === 80) : (c === 101 || c === 69);
      i++;
      if (exponent && i < n && (s[i] === '+' || s[i] === '-')) i++;
    } else if (c === 46 && s.charCodeAt(i + 1) !== 46) {
      i++;
    } else break;
  }
  return i;
}

export function tokenize(text) {
  const s = typeof text === 'string' ? text : String(text ?? '');
  const n = s.length;
  const tokens = [];
  let i = 0;
  let plainStart = -1; // start of the current run of plain characters

  const flushPlain = (end) => {
    if (plainStart >= 0 && end > plainStart) tokens.push({ type: 'plain', from: plainStart, to: end });
    plainStart = -1;
  };
  const push = (type, from, to) => {
    flushPlain(from);
    tokens.push({ type, from, to });
  };

  while (i < n) {
    const c = s.charCodeAt(i);

    // comments
    if (c === 45 && s.charCodeAt(i + 1) === 45) {
      let end;
      const open = s.charCodeAt(i + 2) === 91 ? longOpen(s, i + 2) : null;
      if (open) end = longEnd(s, open[1], open[0]);
      else { end = i + 2; while (end < n && s.charCodeAt(end) !== 10) end++; }
      push('comment', i, end);
      i = end;
      continue;
    }

    // quoted strings; an unterminated one stops at the end of its line
    if (c === 34 || c === 39) {
      let j = i + 1;
      while (j < n) {
        const d = s.charCodeAt(j);
        if (d === 92) {
          if (s.charCodeAt(j + 1) === 122) { // \z skips following whitespace, including line breaks
            j += 2;
            while (j < n) { const w = s.charCodeAt(j); if (w === 32 || (w >= 9 && w <= 13)) j++; else break; }
            continue;
          }
          j += (s.charCodeAt(j + 1) === 13 && s.charCodeAt(j + 2) === 10) ? 3 : 2;
          continue;
        }
        if (d === c) { j++; break; }
        if (d === 10 || d === 13) break;
        j++;
      }
      if (j > n) j = n;
      push('string', i, j);
      i = j;
      continue;
    }

    // long strings
    if (c === 91) {
      const open = longOpen(s, i);
      if (open) {
        const end = longEnd(s, open[1], open[0]);
        push('string', i, end);
        i = end;
        continue;
      }
    }

    // numbers
    if (isDigit(c) || (c === 46 && isDigit(s.charCodeAt(i + 1)))) {
      const end = skipNumber(s, i);
      push('number', i, end);
      i = end;
      continue;
    }

    // names
    if (isIdentStart(c)) {
      let j = i + 1;
      while (j < n && isIdentPart(s.charCodeAt(j))) j++;
      const word = s.slice(i, j);
      if (KEYWORDS.has(word)) push('keyword', i, j);
      else if (GLOBALS.has(word)) {
        const prev = i > 0 ? s.charCodeAt(i - 1) : 0;
        const prev2 = i > 1 ? s.charCodeAt(i - 2) : 0;
        const member = (prev === 46 && prev2 !== 46) || (prev === 58 && prev2 !== 58); // t.log, obj:type, but not "a"..log
        if (member) { if (plainStart < 0) plainStart = i; } else push('global', i, j);
      } else if (plainStart < 0) plainStart = i;
      i = j;
      continue;
    }

    if (plainStart < 0) plainStart = i;
    i++;
  }
  flushPlain(n);
  return tokens;
}
