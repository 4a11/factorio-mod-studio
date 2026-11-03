// Quick open (files of the open mod) and command palette in one modal dialog.
// The pure logic (fuzzy matching, parsing of "path:line:col" and the ">" prefix) lives in palette-logic.js.
// Everything shown is built with DOM APIs; file names and mod names are untrusted text and only go through textContent.

import { t } from '/i18n.js';
import { fuzzyFilter, fuzzyPositions, parsePaletteInput } from '/palette-logic.js';

const MAX_ITEMS = 50;
const PAGE = 8;

/**
 * @param {{ el: Function,
 *           getFiles: () => string[]|null, getRecent: () => string[], getMods: () => Array<{id: string, name: string, version?: string}>,
 *           getCommands: () => Array<{id: string, kind?: string, title: string, hint?: string, run: () => void}>,
 *           openFile: (path: string, line?: number, column?: number) => void, openMod: (id: string) => void,
 *           shortcutText: (keys: string) => string }} host
 */
export function createPalette({ el, getFiles, getRecent, getMods, getCommands, openFile, openMod, shortcutText }) {
  const input = el('input', {
    type: 'text', class: 'palette-input', 'data-testid': 'palette-input', role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'palette-list',
    'aria-autocomplete': 'list', 'aria-activedescendant': '', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off',
  });
  const list = el('ul', { class: 'palette-list', id: 'palette-list', 'data-testid': 'palette-list', role: 'listbox' });
  const hint = el('p', { class: 'palette-hint small muted', 'data-testid': 'palette-hint' });
  const dialog = el('dialog', { class: 'palette', 'data-testid': 'palette', 'aria-modal': 'true' }, input, list, hint);
  dialog.setAttribute('aria-label', t('ui.palette.title'));
  document.body.append(dialog);

  const s = { items: [], active: 0, returnFocus: null, ready: false };

  // ------------------------------------------------------------ building the item list

  function filesItems(parsed) {
    const files = getFiles();
    if (files === null) { // no mod open: offer the mods instead
      return fuzzyFilter(parsed.query, getMods(), (m) => m.name, MAX_ITEMS).map((m) => ({
        kind: 'mod', id: m.id, text: m.name, detail: m.version ? `v${m.version}` : '', run: () => openMod(m.id),
      }));
    }
    let ordered = files;
    if (parsed.query === '') {
      const known = new Set(files);
      const recent = getRecent().filter((p) => known.has(p));
      const recentSet = new Set(recent);
      ordered = [...recent, ...files.filter((p) => !recentSet.has(p))];
    }
    return fuzzyFilter(parsed.query, ordered, (p) => p, MAX_ITEMS).map((path) => ({
      kind: 'file', id: path, text: path, detail: '', run: () => openFile(path, parsed.line ?? undefined, parsed.column ?? undefined),
    }));
  }

  function commandItems(parsed) {
    return fuzzyFilter(parsed.query, getCommands(), (c) => c.title, MAX_ITEMS).map((c) => ({
      kind: c.kind === 'mod' ? 'mod' : 'command', id: c.id, text: c.title, detail: c.hint ?? '', run: c.run,
    }));
  }

  function compute() {
    const parsed = parsePaletteInput(input.value);
    return { parsed, items: parsed.mode === 'commands' ? commandItems(parsed) : filesItems(parsed) };
  }

  // ------------------------------------------------------------ rendering

  function textNodes(text, query) {
    const positions = query ? fuzzyPositions(query, text) : null;
    if (!positions || positions.length === 0) return [text];
    const out = [];
    let at = 0;
    let i = 0;
    while (i < positions.length) {
      let j = i;
      while (j + 1 < positions.length && positions[j + 1] === positions[j] + 1) j++;
      if (positions[i] > at) out.push(text.slice(at, positions[i]));
      out.push(el('mark', { text: text.slice(positions[i], positions[j] + 1) }));
      at = positions[j] + 1;
      i = j + 1;
    }
    if (at < text.length) out.push(text.slice(at));
    return out;
  }

  function render() {
    const { parsed, items } = compute();
    s.items = items;
    s.active = Math.min(s.active, Math.max(0, items.length - 1));
    list.replaceChildren(...(items.length === 0
      ? [el('li', { class: 'palette-empty muted', 'data-testid': 'palette-empty', role: 'presentation', text: t('ui.palette.empty') })]
      : items.map((item, index) => {
        const row = el('li', {
          class: 'palette-item', id: `palette-item-${index}`, role: 'option', 'data-testid': 'palette-item', 'data-kind': item.kind, 'data-id': item.id,
          'aria-selected': index === s.active ? 'true' : 'false',
        },
        el('span', { class: 'palette-text' }, ...textNodes(item.text, parsed.query)),
        item.detail ? el('span', { class: 'palette-detail muted small', text: item.detail }) : null);
        row.addEventListener('mousemove', () => { if (s.active !== index) { s.active = index; markActive(false); } });
        row.addEventListener('click', () => run(index));
        return row;
      })));
    input.setAttribute('aria-activedescendant', items.length ? `palette-item-${s.active}` : '');
    input.setAttribute('aria-label', parsed.mode === 'commands' ? t('ui.palette.commandsLabel') : t('ui.palette.filesLabel'));
    input.setAttribute('placeholder', t('ui.palette.placeholder'));
    hint.textContent = t('ui.palette.hint', { open: shortcutText('Ctrl+P'), commands: shortcutText('Ctrl+Shift+P') });
  }

  function markActive(scroll = true) {
    const rows = list.querySelectorAll('.palette-item');
    rows.forEach((row, index) => row.setAttribute('aria-selected', index === s.active ? 'true' : 'false'));
    input.setAttribute('aria-activedescendant', rows.length ? `palette-item-${s.active}` : '');
    if (scroll) rows[s.active]?.scrollIntoView({ block: 'nearest' });
  }

  function move(to) {
    if (!s.items.length) return;
    s.active = Math.max(0, Math.min(s.items.length - 1, to));
    markActive();
  }

  // ------------------------------------------------------------ open / close / run

  function close() {
    if (dialog.open) dialog.close();
  }

  function run(index = s.active) {
    const item = s.items[index];
    if (!item) return;
    close();
    // The dialog closes first (focus goes back), then the action runs and may move the focus again.
    setTimeout(() => { try { item.run(); } catch { /* an action failing must not break the palette */ } }, 0);
  }

  function open(initial) {
    if (!dialog.open) {
      s.returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      dialog.setAttribute('aria-label', t('ui.palette.title'));
      dialog.showModal();
      dialog.dataset.modalOpen = 'true';
    }
    input.value = initial;
    s.active = 0;
    render();
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }

  dialog.addEventListener('close', () => {
    delete dialog.dataset.modalOpen;
    const target = s.returnFocus;
    s.returnFocus = null;
    const active = document.activeElement;
    if (target?.isConnected && (!active || active === document.body || active === dialog)) target.focus();
  });
  dialog.addEventListener('click', (e) => { if (e.target === dialog) close(); }); // backdrop click

  input.addEventListener('input', () => { s.active = 0; render(); });
  input.addEventListener('keydown', (e) => {
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); move(s.active + 1 >= s.items.length ? 0 : s.active + 1); break;
      case 'ArrowUp': e.preventDefault(); move(s.active - 1 < 0 ? s.items.length - 1 : s.active - 1); break;
      case 'Home': e.preventDefault(); move(0); break;
      case 'End': e.preventDefault(); move(s.items.length - 1); break;
      case 'PageDown': e.preventDefault(); move(s.active + PAGE); break;
      case 'PageUp': e.preventDefault(); move(s.active - PAGE); break;
      case 'Enter': e.preventDefault(); run(); break;
      default: break;
    }
  });

  // ------------------------------------------------------------ global chords

  document.addEventListener('keydown', (e) => {
    const mod = e.ctrlKey || e.metaKey;
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    let initial = null;
    if (mod && !e.altKey && key === 'p') initial = e.shiftKey ? '>' : '';
    else if (mod && !e.altKey && !e.shiftKey && key === 'k') initial = '>';
    else if (!mod && !e.altKey && !e.shiftKey && key === 'F1') initial = '>';
    if (initial === null) return;
    // Another modal dialog (new mod, installations) keeps its keys; the palette itself may switch mode.
    const other = [...document.querySelectorAll('dialog[open]')].some((d) => d !== dialog);
    if (other) return;
    e.preventDefault();
    e.stopPropagation();
    open(initial);
  }, true);

  return {
    open,
    openFiles: () => open(''),
    openCommands: () => open('>'),
    close,
    isOpen: () => dialog.open,
    retranslate() { dialog.setAttribute('aria-label', t('ui.palette.title')); if (dialog.open) render(); },
  };
}
