// Pure helpers for the studio's own updates (update-view.js): what the version chip shows, download sizes and speed, and the
// release notes (GitHub's Markdown, from CHANGELOG.md) cut into blocks the view turns into DOM nodes. No DOM here, so the node
// tests can run it.

/** What the version chip in the sidebar shows: 'downloading' | 'installed' | 'failed' | 'available' | 'current'. */
export function chipState(s) {
  const state = s?.install?.state;
  if (state === 'downloading') return 'downloading';
  if (state === 'succeeded') return 'installed';
  if (s?.latest && (state === 'failed' || state === 'cancelled')) return 'failed';
  if (s?.latest) return 'available';
  return 'current';
}

/** Whole percent of a download, or null while the size is unknown. Never 100 before the last byte. */
export function percentOf(install) {
  if (!install || !(install.total > 0)) return null;
  return Math.min(100, Math.floor((install.received * 100) / install.total));
}

/** "12.3 MB", "640 KB", "18 B"; one decimal from 10 KB up to 100 of a unit. */
export function formatBytes(bytes, format = (n, o) => n.toLocaleString(undefined, o)) {
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = Math.max(0, Number(bytes) || 0);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  const digits = unit === 0 || value >= 100 ? 0 : 1;
  return `${format(value, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${units[unit]}`;
}

/**
 * Download speed from the polls: keeps the samples of the last few seconds and returns bytes per second, or null until two
 * samples are at least half a second apart. `samples` is the array the view keeps; it is changed in place.
 */
export function addSample(samples, received, now, window = 4000) {
  if (samples.length && received < samples[samples.length - 1].received) samples.length = 0; // a new download started
  samples.push({ received, at: now });
  while (samples.length > 2 && now - samples[0].at > window) samples.shift();
  const first = samples[0];
  const span = now - first.at;
  return span >= 500 ? ((received - first.received) * 1000) / span : null;
}

/**
 * Release notes as blocks: { type: 'heading', level, inline } | { type: 'item', inline } | { type: 'para', inline }.
 * Bullets may continue on indented lines; blank lines end a paragraph. `inline` is a list of
 * { text } | { strong } | { em } | { code } | { link, href } (only http(s) links; others stay plain text).
 */
export function parseNotes(markdown) {
  const blocks = [];
  let current = null;
  const flush = () => { if (current) blocks.push({ type: current.type, level: current.level, inline: parseInline(current.text.trim()) }); current = null; };
  for (const raw of String(markdown ?? '').replace(/\r\n?/g, '\n').split('\n')) {
    const line = raw.replace(/\s+$/, '');
    if (line.trim() === '') { flush(); continue; }
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) { flush(); blocks.push({ type: 'heading', level: heading[1].length, inline: parseInline(heading[2].trim()) }); continue; }
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);
    if (bullet && !/^\s{4,}/.test(line)) { flush(); current = { type: 'item', text: bullet[1] }; continue; }
    if (current) { current.text += ' ' + line.trim(); continue; }
    current = { type: 'para', text: line.trim() };
  }
  flush();
  return blocks;
}

/** **strong**, *em* / _em_, `code` and [text](https://...) inside one line of notes. */
export function parseInline(text) {
  const out = [];
  const pattern = /\*\*([^*]+)\*\*|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)|\*([^*\s][^*]*)\*|(?<![\w])_([^_]+)_(?![\w])/g;
  let last = 0;
  let m;
  while ((m = pattern.exec(text))) {
    if (m.index > last) out.push({ text: text.slice(last, m.index) });
    if (m[1] !== undefined) out.push({ strong: m[1] });
    else if (m[2] !== undefined) out.push({ code: m[2] });
    else if (m[3] !== undefined) out.push(/^https?:\/\//i.test(m[4]) ? { link: m[3], href: m[4] } : { text: m[3] });
    else out.push({ em: m[5] ?? m[6] });
    last = pattern.lastIndex;
  }
  if (last < text.length) out.push({ text: text.slice(last) });
  return out;
}
