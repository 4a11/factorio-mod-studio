// Pure logic of the quick-open / command palette: fuzzy matching and parsing of the input line.
// Plain ES module with no DOM access at import time (it is also imported by `node --test`).
//
// Scoring model (higher is better, `null` = no match):
//   * the query has to be a case-insensitive subsequence of the text;
//   * among all alignments the best one is used. Adjacent matched characters dominate everything else, so a contiguous
//     substring always beats a scattered match of the same query (the other terms are bounded far below one adjacency);
//   * smaller terms break ties: a match start at the beginning of the basename, or after one of "/ - _ . space" (or at a
//     lower->upper camelCase edge), earns a bonus; small penalties for leading and inner gaps prefer compact, early matches;
//   * a text equal to the query (ignoring case) scores higher than anything else can.
// Everything is deterministic: no randomness, no state.

const ADJACENT = 1_000_000;
const EXACT = 1_000_000_000_000;
const BASE = 10;
const BASENAME_START = 30;
const BOUNDARY = 20;
const CAMEL = 12;
const LEADING = 0.05;
const LEADING_CAP = 100;
const GAP = 0.01;
const BOUNDARY_CHARS = '/-_. \\';

const lowerUnit = (ch) => {
  const lower = ch.toLowerCase();
  return lower.length === 1 ? lower : ch; // keep UTF-16 indices aligned for the few characters whose lowercase is longer
};
const lowerText = (text) => {
  let out = '';
  for (let i = 0; i < text.length; i++) out += lowerUnit(text[i]);
  return out;
};

function isSubsequence(q, s) {
  let at = 0;
  for (let i = 0; i < s.length && at < q.length; i++) if (s[i] === q[at]) at++;
  return at === q.length;
}

function bonusAt(text, j, baseStart) {
  if (j === baseStart) return BASENAME_START;
  if (j === 0 || BOUNDARY_CHARS.includes(text[j - 1])) return BOUNDARY;
  const ch = text[j];
  const prev = text[j - 1];
  if (ch !== ch.toLowerCase() && ch === ch.toUpperCase() && prev === prev.toLowerCase() && prev !== prev.toUpperCase()) return CAMEL;
  return 0;
}

/** Best alignment of `query` in `text` -> { score, positions } or null. Both are already validated as a subsequence match. */
function align(query, text) {
  const q = lowerText(query);
  const s = lowerText(text);
  const n = s.length;
  const m = q.length;
  const baseStart = text.lastIndexOf('/') + 1;

  const NEG = -Infinity;
  // f[i][j]: best score with query[i] matched at text[j]; from[i][j]: the text index used for query[i-1].
  const f = Array.from({ length: m }, () => new Float64Array(n).fill(NEG));
  const from = Array.from({ length: m }, () => new Int32Array(n).fill(-1));

  for (let j = 0; j < n; j++) {
    if (s[j] === q[0]) f[0][j] = BASE + bonusAt(text, j, baseStart) - Math.min(j, LEADING_CAP) * LEADING;
  }
  for (let i = 1; i < m; i++) {
    let bestGap = NEG; // max over k <= j-2 of f[i-1][k] + k * GAP
    let bestGapAt = -1;
    for (let j = 1; j < n; j++) {
      const k = j - 2;
      if (k >= 0 && f[i - 1][k] !== NEG) {
        const value = f[i - 1][k] + k * GAP;
        if (value > bestGap) { bestGap = value; bestGapAt = k; }
      }
      if (s[j] !== q[i]) continue;
      let best = NEG;
      let at = -1;
      if (bestGapAt >= 0) { best = bestGap - (j - 1) * GAP; at = bestGapAt; }
      if (f[i - 1][j - 1] !== NEG) {
        const adjacent = f[i - 1][j - 1] + ADJACENT;
        if (adjacent > best) { best = adjacent; at = j - 1; }
      }
      if (at >= 0) {
        f[i][j] = best + BASE + bonusAt(text, j, baseStart);
        from[i][j] = at;
      }
    }
  }

  let end = -1;
  let score = NEG;
  for (let j = 0; j < n; j++) if (f[m - 1][j] > score) { score = f[m - 1][j]; end = j; }
  if (end < 0) return null;

  const positions = new Array(m);
  for (let i = m - 1, j = end; i >= 0; i--) {
    positions[i] = j;
    j = from[i][j];
  }
  return { score, positions };
}

/** Case-insensitive subsequence score: `null` when `query` is not a subsequence of `text`, `0` for an empty query. */
export function fuzzyScore(query, text) {
  const q = String(query ?? '');
  const s = String(text ?? '');
  if (q.length === 0) return 0;
  if (q.length > s.length) return null;
  const lq = lowerText(q);
  const ls = lowerText(s);
  if (!isSubsequence(lq, ls)) return null;
  if (lq === ls) return EXACT;
  const result = align(q, s);
  return result ? result.score : null;
}

/** Ascending indices of the characters of `text` that `fuzzyScore` credits, or `null` when there is no match. */
export function fuzzyPositions(query, text) {
  const q = String(query ?? '');
  const s = String(text ?? '');
  if (q.length === 0) return [];
  if (q.length > s.length) return null;
  const lq = lowerText(q);
  const ls = lowerText(s);
  if (!isSubsequence(lq, ls)) return null;
  if (lq === ls) return Array.from({ length: s.length }, (_, i) => i);
  const result = align(q, s);
  return result ? result.positions : null;
}

/** Items with a match, best first; equal scores keep their original order. An empty query keeps everything in order. */
export function fuzzyFilter(query, items, getText = (x) => x, limit = Infinity) {
  const list = Array.from(items ?? []);
  const max = typeof limit === 'number' && limit >= 0 ? limit : Infinity;
  const q = String(query ?? '');
  if (q.length === 0) return list.slice(0, max);
  const scored = [];
  for (let index = 0; index < list.length; index++) {
    const score = fuzzyScore(q, getText(list[index]));
    if (score !== null) scored.push({ item: list[index], score, index });
  }
  scored.sort((a, b) => (b.score - a.score) || (a.index - b.index));
  return scored.slice(0, max).map((x) => x.item);
}

const DIGITS = /^\d{1,9}$/;

/**
 * Splits the palette input line.
 *  - a leading ">" (after optional spaces) selects the command list, the rest (trimmed) is the query;
 *  - otherwise it is a file query with an optional trailing ":line" or ":line:column" (positive integers, up to 9 digits).
 */
export function parsePaletteInput(raw) {
  const text = String(raw ?? '');
  const command = /^\s*>/.exec(text);
  if (command) return { mode: 'commands', query: text.slice(command[0].length).trim(), line: null, column: null };

  const trimmed = text.trim();
  const parts = trimmed.split(':');
  const positive = (value) => (DIGITS.test(value) && Number(value) >= 1 ? Number(value) : null);

  if (parts.length >= 3) {
    const line = positive(parts[parts.length - 2]);
    const column = positive(parts[parts.length - 1]);
    if (line !== null && column !== null) {
      return { mode: 'files', query: parts.slice(0, -2).join(':').trim(), line, column };
    }
  }
  if (parts.length >= 2) {
    const line = positive(parts[parts.length - 1]);
    if (line !== null) return { mode: 'files', query: parts.slice(0, -1).join(':').trim(), line, column: null };
  }
  return { mode: 'files', query: trimmed, line: null, column: null };
}
