// "Resources" tab of the mod page: a browser for the mod's graphics and sounds. One kind at a time (segmented switch with
// counts), a name filter, "unused only" (the same static check as the Files tab), preview sizes, and one collapsible section per
// folder with its own row limit. Image tiles show the pixel size once loaded and which prototypes use the file as their icon;
// every file can be opened, and its Lua path (__mod__/path) copied. Sounds share one player, so only one plays at a time.
// Locale files are not listed here: the Localization tab covers them. Mod content is only ever set as text.

import { t } from '/i18n.js';
import { parentOf, nameOf } from '/files-logic.js';

const MAX_TILES = 120; // per folder section until "Show all"
const SIZES = ['s', 'm', 'l'];

// One player for the whole tab: starting a sound stops the previous one, and it survives the redraws of live updates.
const player = typeof Audio === 'undefined' ? null : new Audio();
let playing = null; // url of the sound loaded in the player
const playerListeners = new Set();
if (player) for (const event of ['play', 'pause', 'ended']) player.addEventListener(event, () => { for (const fn of playerListeners) fn(); });

/** Stops the sound, e.g. when the page switches to another mod. */
export function stopResourceSound() {
  if (!player) return;
  player.pause();
  playing = null;
}

/**
 * Renders the tab. `state` keeps resKind ('graphics' | 'sound'), resFilter, resUnusedOnly, resSize ('s' | 'm' | 'l'),
 * resExpanded (a Set of opened folders; folders start collapsed) and resAllRows (folders shown without the limit) across redraws.
 * @param {{ el: Function, state: object, openFile: Function, fileUrl: Function, formatSize: (n: number) => string }} host
 */
export function renderResources(o, { el, state, openFile, fileUrl, formatSize }) {
  state.resKind ||= 'graphics';
  state.resSize ||= 'm';
  state.resExpanded ||= new Set();
  state.resAllRows ||= new Set();
  playerListeners.clear();

  const byKind = { graphics: o.files.filter((f) => f.category === 'graphics'), sound: o.files.filter((f) => f.category === 'sound') };
  const list = byKind[state.resKind];
  const unusedTotal = list.filter((f) => f.unused === true).length;
  if (!unusedTotal) state.resUnusedOnly = false;
  const luaPath = (path) => (o.info?.name ? `__${o.info.name}__/${path}` : path);

  // Prototypes whose icon is this file ("used as the icon of ...").
  const iconOf = new Map();
  for (const p of o.prototypes) if (p.icon) { if (!iconOf.has(p.icon)) iconOf.set(p.icon, []); iconOf.get(p.icon).push(p); }

  // ------------------------------------------------------------ toolbar

  const kindSwitch = el('div', { class: 'files-switch', role: 'group', 'aria-label': t('resources.kindAria'), 'data-testid': 'res-kind' },
    [['graphics', 'resources.kindGraphics', 'fi-image'], ['sound', 'resources.kindSounds', 'fi-sound']].map(([kind, key, icon]) =>
      el('button', { type: 'button', class: 'files-btn', 'data-kind': kind, 'aria-pressed': state.resKind === kind ? 'true' : 'false',
        onclick: () => { if (state.resKind !== kind) { state.resKind = kind; state.resFilter = ''; rerender(); } } },
      el('span', { class: `fi-icon ${icon}`, 'aria-hidden': 'true' }), el('span', { text: t(key) }),
      el('span', { class: 'files-count', text: String(byKind[kind].length) }))));

  const search = el('input', { type: 'search', class: 'proto-filter', placeholder: t('files.filter'), 'aria-label': t('files.filter'), value: state.resFilter || '',
    oninput: (e) => { state.resFilter = e.target.value; refresh(); } });
  const unusedBtn = unusedTotal > 0 && el('button', { type: 'button', class: 'files-toggle', 'data-testid': 'res-unused-only', title: t('files.badge.unusedTitle'),
    onclick: () => { state.resUnusedOnly = !state.resUnusedOnly; refresh(); } },
  el('span', { text: t('files.unusedOnly') }), el('span', { class: 'files-count', text: String(unusedTotal) }));
  const sizeSwitch = state.resKind === 'graphics' && el('div', { class: 'files-switch res-size', role: 'group', 'aria-label': t('resources.sizeAria') },
    SIZES.map((size) => el('button', { type: 'button', 'data-size': size, 'aria-label': t(`resources.size.${size}`), title: t(`resources.size.${size}`),
      onclick: () => { state.resSize = size; refresh(); } }, el('span', { class: `res-size-glyph ${size}`, 'aria-hidden': 'true' }))));
  const count = el('span', { class: 'muted small files-summary', 'data-testid': 'res-count' });
  const content = el('div', { class: 'res-content' });

  const root = el('div', { class: 'res-panel' },
    el('div', { class: 'files-viewbar proto-bar' }, kindSwitch, search, unusedBtn, sizeSwitch, count), content);

  function rerender() { root.replaceWith(renderResources(o, { el, state, openFile, fileUrl, formatSize })); }

  // ------------------------------------------------------------ items

  const copyButton = (path) => {
    const button = el('button', { type: 'button', class: 'res-copy', 'data-testid': 'res-copy', title: t('resources.copyPath', { path: luaPath(path) }),
      'aria-label': t('resources.copyPath', { path: luaPath(path) }), text: '⧉' });
    button.addEventListener('click', async (e) => {
      e.stopPropagation();
      try { await navigator.clipboard.writeText(luaPath(path)); button.textContent = '✓'; button.classList.add('done'); }
      catch { button.textContent = '✕'; }
      setTimeout(() => { button.textContent = '⧉'; button.classList.remove('done'); }, 1200);
    });
    return button;
  };

  const unusedBadge = (f) => f.unused === true && el('span', { class: 'badge files-unused', title: t('files.badge.unusedTitle'), text: t('files.badge.unused') });

  const tile = (f) => {
    const dims = el('span', { class: 'res-dims muted' });
    const img = el('img', { loading: 'lazy', alt: '', src: fileUrl(f.path) });
    img.addEventListener('load', () => { dims.textContent = `${img.naturalWidth}×${img.naturalHeight}`; });
    const users = iconOf.get(f.path) || [];
    return el('div', { class: 'res-tile', 'data-testid': 'res-tile', 'data-path': f.path },
      el('button', { type: 'button', class: 'res-preview', title: t('readiness.action.open', { file: f.path }), onclick: () => openFile(f.path) }, img),
      el('span', { class: 'res-name mono', title: f.path, text: nameOf(f.path) }),
      el('span', { class: 'res-meta small' }, dims, el('span', { class: 'muted', text: formatSize(f.size) }), unusedBadge(f)),
      users.length > 0 && el('span', { class: 'res-users' }, users.slice(0, 3).map((p) => el('button', {
        type: 'button', class: 'chip res-user mono', title: t('resources.iconOf', { name: `${p.type}/${p.name}` }), onclick: () => openFile(p.file, p.line), text: p.name })),
      users.length > 3 && el('span', { class: 'muted small', text: `+${users.length - 3}` })),
      copyButton(f.path));
  };

  const soundRow = (f) => {
    const url = fileUrl(f.path);
    const duration = el('span', { class: 'muted small res-duration' });
    const play = el('button', { type: 'button', class: 'res-play', 'data-testid': 'res-play' });
    const sync = () => {
      const on = playing === url && player && !player.paused;
      play.textContent = on ? '❚❚' : '▶';
      play.setAttribute('aria-label', t(on ? 'resources.pause' : 'resources.play', { name: nameOf(f.path) }));
      play.classList.toggle('on', !!on);
    };
    play.addEventListener('click', () => {
      if (!player) return;
      if (playing === url && !player.paused) { player.pause(); return; }
      if (playing !== url) { player.src = url; playing = url; }
      player.play().catch(() => {});
    });
    // The length comes from the file's metadata, read without downloading the whole sound.
    const probe = el('audio', { preload: 'metadata', src: url, hidden: true });
    probe.addEventListener('loadedmetadata', () => {
      const s = Math.round(probe.duration);
      if (Number.isFinite(s)) duration.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
    });
    playerListeners.add(sync);
    sync();
    return el('div', { class: 'res-sound', 'data-testid': 'res-sound', 'data-path': f.path },
      play,
      el('span', { class: 'fi-icon fi-sound', 'aria-hidden': 'true' }),
      el('button', { type: 'button', class: 'res-sound-name mono', title: t('readiness.action.open', { file: f.path }), onclick: () => openFile(f.path), text: nameOf(f.path) }),
      unusedBadge(f), duration, el('span', { class: 'muted small files-size', text: formatSize(f.size) }), copyButton(f.path), probe);
  };

  // ------------------------------------------------------------ content

  function refresh() {
    const filter = (state.resFilter || '').trim().toLowerCase();
    const shown = list.filter((f) => (!filter || nameOf(f.path).toLowerCase().includes(filter)) && (!state.resUnusedOnly || f.unused === true));
    if (unusedBtn) unusedBtn.setAttribute('aria-pressed', state.resUnusedOnly ? 'true' : 'false');
    if (sizeSwitch) for (const button of sizeSwitch.children) button.setAttribute('aria-pressed', button.dataset.size === state.resSize ? 'true' : 'false');
    count.textContent = shown.length === list.length ? t('count.file', { n: list.length }) : `${t('count.file', { n: shown.length })} / ${list.length}`;

    if (!list.length) {
      content.replaceChildren(el('p', { class: 'muted ov-empty', text: t(state.resKind === 'graphics' ? 'resources.noImages' : 'resources.noSounds') }));
      return;
    }
    if (!shown.length) {
      content.replaceChildren(el('p', { class: 'muted ov-empty', text: t('files.noMatch') }));
      return;
    }

    const folders = new Map();
    for (const f of shown) { const folder = parentOf(f.path); if (!folders.has(folder)) folders.set(folder, []); folders.get(folder).push(f); }
    const ordered = [...folders].sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
    // Folders start collapsed; a filter opens every section that has matches, and a single folder is always open.
    const forced = !!filter || state.resUnusedOnly || ordered.length === 1;
    const key = (folder) => `${state.resKind}:${folder}`;

    content.replaceChildren(...ordered.map(([folder, files]) => {
      files.sort((a, b) => (a.path < b.path ? -1 : 1));
      const all = state.resAllRows.has(key(folder));
      const visible = all ? files : files.slice(0, MAX_TILES);
      const items = state.resKind === 'graphics'
        ? el('div', { class: `res-grid size-${state.resSize}` }, visible.map(tile))
        : el('div', { class: 'res-sounds' }, visible.map(soundRow));
      const section = el('details', { class: 'proto-family res-folder', 'data-folder': folder, open: state.resExpanded.has(key(folder)) || forced },
        el('summary', {},
          el('span', { class: 'fi-icon fi-folder', 'aria-hidden': 'true' }),
          el('span', { class: 'mono', text: folder ? `${folder}/` : '/' }),
          el('span', { class: 'ov-count', text: String(files.length) })),
        items,
        files.length > MAX_TILES && !all && el('div', { class: 'proto-more-rows' },
          el('span', { class: 'muted small', text: t('prototypes.showing', { shown: MAX_TILES, total: files.length }) }),
          el('button', { type: 'button', class: 'linklike small', onclick: () => { state.resAllRows.add(key(folder)); refresh(); }, text: t('prototypes.showAll') })));
      section.addEventListener('toggle', () => {
        // Opening forced by a filter is not the user's choice and must not be remembered.
        if (!section.open) state.resExpanded.delete(key(folder));
        else if (!forced) state.resExpanded.add(key(folder));
      });
      return section;
    }));
  }

  refresh();
  return root;
}
