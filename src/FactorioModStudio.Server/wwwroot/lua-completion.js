import { tokenize } from './lua-highlight.js';

// First pass: simple global names and members of defines.events in control.lua.
export function completionAt(text, caret) {
  const token = tokenize(text).find(t => t.from < caret && caret <= t.to);
  if (token && (token.type === 'comment' || token.type === 'string')) return null;
  const before = text.slice(0, caret);
  const prefix = before.match(/[A-Za-z0-9_]*$/)[0];
  const start = caret - prefix.length;
  const receiver = before.slice(0, start);
  const events = /(?:^|[^.\w:])defines\.events\.$/.test(receiver);
  if (!events && (/[.:]$/.test(receiver) || /^[0-9]/.test(prefix))) return null;
  return { group: events ? 'events' : 'globals', prefix, start, end: caret + text.slice(caret).match(/^[A-Za-z0-9_]*/)[0].length };
}

export function completionNames(api, context) {
  const names = api?.[context.group];
  return Array.isArray(names) ? names.filter(name => name.startsWith(context.prefix) && name !== context.prefix).slice(0, 12) : [];
}
