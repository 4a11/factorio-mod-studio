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

export function eventAt(text, offset) {
  const token = tokenize(text).find(t => t.from <= offset && offset < t.to);
  if (!token || token.type === 'comment' || token.type === 'string') return null;
  const word = text.slice(0, offset).match(/[A-Za-z0-9_]*$/)[0];
  const receiver = text.slice(0, offset - word.length);
  if (!/(?:^|[^.\w:])defines\.events\.$/.test(receiver)) return null;
  return word + text.slice(offset).match(/^[A-Za-z0-9_]*/)[0] || null;
}

export function onEventArgument(text, caret) {
  const prefix = text.slice(0, caret);
  const tokens = tokenize(prefix);
  const stack = [];
  const closing = { ')': '(', ']': '[', '}': '{' };
  for (const token of tokens) {
    if (token.type === 'comment' || token.type === 'string') continue;
    if (token.type === 'keyword' && prefix.slice(token.from, token.to) === 'function' && stack.at(-1)?.onEvent) {
      // While editing an inline callback, its statements are not on_event arguments.
      stack.at(-1).callback = true;
    }
    for (let i = token.from; i < token.to; i++) {
      const ch = prefix[i];
      if ('([{'.includes(ch)) {
        const onEvent = ch === '(' && /(?:^|[^.\w:])script\.on_event\s*$/.test(prefix.slice(Math.max(0, i - 64), i));
        stack.push({ ch, onEvent, index: 0, callback: false });
      } else if (closing[ch]) {
        if (stack.at(-1)?.ch === closing[ch]) stack.pop();
        else stack.length = 0;
      } else if (ch === ',' && stack.at(-1)?.onEvent && !stack.at(-1).callback) {
        stack.at(-1).index++;
      }
    }
  }
  const frame = stack.findLast(entry => entry.onEvent);
  return frame && !frame.callback ? frame.index : null;
}
