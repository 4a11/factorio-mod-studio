// Placeholder rules for Factorio locale strings. Pure string functions, no DOM access, never throw.
// This is a port of FactorioModStudio.Core/LocalePlaceholders.cs: both must find the same tokens and report the same
// normalised Missing/Unexpected lists, so change them together.
//
// Tokens (matched left to right, non-overlapping):
//   __plural_for_parameter_N_{...}__   (normalised to __plural_for_parameter_N__; the contents are translated)
//   __1__, __ITEM__name__, __CONTROL__name__, __ALT_CONTROL__1__name__, __CONTROL_STYLE_BEGIN__, ...
//   [color=red], [/color], [item=iron-plate], [img=item/iron-plate]
//   the two characters backslash + n

const BARE_KEYWORDS = ['CONTROL_STYLE_BEGIN', 'CONTROL_STYLE_END', 'REMARK_COLOR_BEGIN', 'REMARK_COLOR_END'];
const ARGUMENT_KEYWORDS = ['ENTITY', 'ITEM', 'FLUID', 'RECIPE', 'TECHNOLOGY', 'TILE', 'CONTROL'];
const ALT_CONTROL = 'ALT_CONTROL';
const PLURAL_PREFIX = '__plural_for_parameter_';

// The grammar is scanned by hand instead of with a regular expression: unterminated input such as thousands of "[a=" or
// "__ITEM__" must stay linear, which a backtracking engine cannot promise. The C# side implements exactly the same scanner.

const isDigit = (c) => c >= '0' && c <= '9';
const isLower = (c) => c >= 'a' && c <= 'z';
const isArgument = (c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c === '-' || c === '_' || c === '.' || c === '/';
const underscores = (text, at) => text[at] === '_' && text[at + 1] === '_';

// Index just past the first "__" at or after `from` that is preceded by at least one argument character, or -1.
function argumentEnd(text, from) {
  let k = from;
  if (k >= text.length || !isArgument(text[k])) return -1;
  k++;
  for (;;) {
    if (underscores(text, k)) return k + 2;
    if (k >= text.length || !isArgument(text[k])) return -1;
    k++;
  }
}

// name = [a-z][a-z-]*; returns the index after it, or -1.
function readName(text, p) {
  if (p >= text.length || !isLower(text[p])) return -1;
  p++;
  while (p < text.length && (isLower(text[p]) || text[p] === '-')) p++;
  return p;
}

// Returns { length, normalised } of the token starting at i (length 0: none).
function scan(text) {
  const result = [];
  if (typeof text !== 'string' || text.length === 0) return result;

  // Searches only move forward, so remembering the last answer keeps the whole scan linear.
  let braceFrom = -1; let brace = -1;
  let closeFrom = -1; let close = -1;
  const nextBrace = (from) => {
    if (braceFrom >= 0 && from >= braceFrom && (brace < 0 || brace >= from)) return brace;
    braceFrom = from;
    brace = text.indexOf('}', from);
    return brace;
  };
  const nextClose = (from) => {
    if (closeFrom >= 0 && from >= closeFrom && (close < 0 || close >= from)) return close;
    closeFrom = from;
    close = -1;
    for (let k = from; k < text.length; k++) {
      const c = text[k];
      if (c === ']' || c === '\r' || c === '\n') { close = k; break; }
    }
    return close;
  };

  const underscoreToken = (i) => {
    if (text.startsWith(PLURAL_PREFIX, i)) {
      const digits = i + PLURAL_PREFIX.length;
      let p = digits;
      while (p < text.length && isDigit(text[p])) p++;
      if (p > digits && text[p] === '_' && text[p + 1] === '{') {
        const end = nextBrace(p + 2);
        if (end >= 0 && underscores(text, end + 1)) {
          return { length: end + 3 - i, normalised: `${PLURAL_PREFIX}${text.slice(digits, p)}__` };
        }
      }
    }

    const a = i + 2;
    let d = a;
    while (d < text.length && isDigit(text[d])) d++;
    if (d > a && underscores(text, d)) return { length: d + 2 - i };

    for (const keyword of BARE_KEYWORDS) {
      if (text.startsWith(keyword, a) && underscores(text, a + keyword.length)) return { length: a + keyword.length + 2 - i };
    }
    for (const keyword of ARGUMENT_KEYWORDS) {
      if (!text.startsWith(keyword, a) || !underscores(text, a + keyword.length)) continue;
      const end = argumentEnd(text, a + keyword.length + 2);
      if (end >= 0) return { length: end - i };
    }
    if (text.startsWith(ALT_CONTROL, a) && underscores(text, a + ALT_CONTROL.length)) {
      const b = a + ALT_CONTROL.length + 2;
      let n = b;
      while (n < text.length && isDigit(text[n])) n++;
      if (n > b && underscores(text, n)) {
        const end = argumentEnd(text, n + 2);
        if (end >= 0) return { length: end - i };
      }
      const single = argumentEnd(text, b);
      if (single >= 0) return { length: single - i };
    }
    return null;
  };

  const tagToken = (i) => {
    let p = i + 1;
    if (text[p] === '/') {
      p = readName(text, p + 1);
      return p >= 0 && text[p] === ']' ? { length: p + 1 - i } : null;
    }
    p = readName(text, p);
    if (p < 0) return null;
    if (text[p] === ']') return { length: p + 1 - i };
    if (text[p] === '=') {
      const end = nextClose(p + 1);
      return end >= 0 && text[end] === ']' ? { length: end + 1 - i } : null;
    }
    return null;
  };

  let i = 0;
  while (i < text.length) {
    const c = text[i];
    let token = null;
    if (c === '_' && text[i + 1] === '_') token = underscoreToken(i);
    else if (c === '[') token = tagToken(i);
    else if (c === '\\' && text[i + 1] === 'n') token = { length: 2 };

    if (token) {
      result.push({ raw: text.slice(i, i + token.length), normalised: token.normalised ?? text.slice(i, i + token.length) });
      i += token.length;
    } else {
      i++;
    }
  }
  return result;
}

/** Tokens in order of appearance (duplicates kept), exactly as written in the text. */
export function extract(text) {
  return scan(text).map((token) => token.raw);
}

function normalised(text) {
  return scan(text).map((token) => token.normalised);
}

// Tokens of `have` that `other` contains fewer times, grouped in order of first appearance.
function deficit(have, other) {
  const counts = new Map();
  const order = [];
  for (const token of have) {
    if (counts.has(token)) counts.set(token, counts.get(token) + 1);
    else { counts.set(token, 1); order.push(token); }
  }
  const otherCounts = new Map();
  for (const token of other) otherCounts.set(token, (otherCounts.get(token) || 0) + 1);

  const result = [];
  for (const token of order) {
    const missing = counts.get(token) - (otherCounts.get(token) || 0);
    for (let i = 0; i < missing; i++) result.push(token);
  }
  return result;
}

/**
 * Compares the tokens of a source string with its translation (multiset, normalised).
 * missing: in the source but not (often enough) in the target; unexpected: the other way round.
 */
export function compare(source, target) {
  const from = normalised(source);
  const to = normalised(target);
  const missing = deficit(from, to);
  const unexpected = deficit(to, from);
  return { missing, unexpected, isEmpty: missing.length === 0 && unexpected.length === 0 };
}

// Aliases so callers can pick the more descriptive name.
export const extractPlaceholders = extract;
export const comparePlaceholders = compare;
