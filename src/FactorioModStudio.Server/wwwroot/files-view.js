// "Files" tab of the mod page: a folder tree (or the files grouped by category) with a name filter, an "unused only" filter,
// type icons and badges (problems, role, unsaved, unused), a context menu (mouse, the row's ⋯ button, ContextMenu key, Shift+F10)
// with new file / new folder / rename / duplicate / delete / reveal / upload, drag & drop inside the tree to move, and OS files
// dropped on a folder (or the drop zone under the tree) to upload. The toolbar names the folder new files go into.
// Deleting goes to the server-side trash; the toast offers Undo.
//
// The view owns one persistent subtree (`element`) that the host re-attaches on every redraw, so the filter box keeps focus
// and the expanded folders survive live updates. Mod content (file names, roles, messages) is only ever set with
// textContent / attributes; dialogs are <dialog> elements built with el(), never prompt() / alert() or HTML strings.

import { t } from '/i18n.js';
import { EDITABLE_EXT } from '/editor-tabs.js';
import {
  buildTree, flatten, fileMatches, countFiles, validateName, parentOf, nameOf, joinPath, movedPath, isInside, copyName,
} from '/files-logic.js';

const CATEGORIES = ['script', 'locale', 'graphics', 'sound', 'data', 'text', 'other'];
const ASSET_EXT = ['png', 'jpg', 'jpeg', 'ogg', 'wav'];
const MAX_ROWS = 3000;
const TOAST_MS = 8000;
const DRAG_TYPE = 'application/x-fms-path';
const VIEW_KEY = 'fms.files.view';

const enc = encodeURIComponent;
const extOf = (name) => { const i = name.lastIndexOf('.'); return i < 0 ? '' : name.slice(i + 1).toLowerCase(); };

/**
 * @param {{ api: Function, el: Function, getModId: () => string|null, isEditable: () => boolean,
 *           openFile: (path: string, line?: number) => any, refresh: () => Promise<void>, formatSize: (n: number) => string,
 *           editorTabs: { activePath: () => string|null, dirtyPaths: () => string[], renamePath: Function, closePaths: Function },
 *           setDropOverlay?: (on: boolean) => void }} host
 */
export function createFilesView({ api, el, getModId, isEditable, openFile, refresh, formatSize, editorTabs, setDropOverlay = () => {} }) {
  const s = {
    overview: null,
    view: 'tree', // 'tree' | 'category'
    name: '',
    unusedOnly: false,
    expanded: new Map(), // modId -> Set of open folder paths
    folders: new Map(), // modId -> Set of folders created in this session (empty folders are not in the overview)
    selected: null, // path of the selected row ('' = mod root)
    busy: false,
  };
  try { if (localStorage.getItem(VIEW_KEY) === 'category') s.view = 'category'; } catch { /* storage unavailable */ }

  const base = () => `/api/mods/${enc(getModId())}`;
  const perMod = (map) => { const id = getModId(); if (!map.has(id)) map.set(id, new Set()); return map.get(id); };
  const expanded = () => perMod(s.expanded);
  const extraFolders = () => perMod(s.folders);
  const files = () => s.overview?.files ?? [];
  const filePaths = () => new Set(files().map((f) => f.path));
  const filtering = () => s.name.trim() !== '' || s.unusedOnly;

  // ------------------------------------------------------------ persistent structure

  // Toolbar: actions (editable mods) with the folder new things go into, or a read-only notice; below it the view controls.
  const root = el('div', { class: 'files-panel', 'data-testid': 'files-panel' });
  const iconButton = (testid, icon, onclick) => {
    const label = el('span');
    const button = el('button', { type: 'button', class: 'files-btn', 'data-testid': testid, onclick },
      el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: icon }), label);
    return { button, label };
  };
  const newFileBtn = iconButton('files-new-file', '+', () => newFile(targetFolder()));
  const newFolderBtn = iconButton('files-new-folder', '+', () => newFolder(targetFolder()));
  const uploadBtn = iconButton('files-upload', '↑', () => pickUpload(targetFolder()));
  const targetText = el('span', { 'data-testid': 'files-target' });
  const targetReset = el('button', { type: 'button', class: 'linklike small', 'data-testid': 'files-target-reset', onclick: () => select(null, false) });
  const actions = el('div', { class: 'files-actions' }, newFileBtn.button, newFolderBtn.button, uploadBtn.button,
    el('span', { class: 'files-target muted small' }, targetText, targetReset));
  const readOnly = el('div', { class: 'files-readonly small', 'data-testid': 'files-readonly' });

  const filter = el('input', { type: 'search', class: 'files-filter', 'data-testid': 'files-filter', autocomplete: 'off', spellcheck: 'false' });
  filter.addEventListener('input', () => { s.name = filter.value; renderBody(); });
  const unusedLabel = el('span');
  const unusedCount = el('span', { class: 'files-count' });
  const unusedBtn = el('button', { type: 'button', class: 'files-toggle', 'data-testid': 'files-unused-only', 'aria-pressed': 'false',
    onclick: () => { s.unusedOnly = !s.unusedOnly; renderBody(); } }, unusedLabel, unusedCount);
  const viewTree = el('button', { type: 'button', 'data-testid': 'files-view-tree', onclick: () => setView('tree') });
  const viewCategory = el('button', { type: 'button', 'data-testid': 'files-view-category', onclick: () => setView('category') });
  const viewSwitch = el('div', { class: 'files-switch', role: 'group' }, viewTree, viewCategory);
  const expandBtn = el('button', { type: 'button', 'data-testid': 'files-expand-all', onclick: () => toggleAll() });
  const summary = el('span', { class: 'muted small files-summary', 'data-testid': 'files-summary' });
  const viewBar = el('div', { class: 'files-viewbar' }, filter, unusedBtn, viewSwitch, expandBtn, summary);
  const toolbar = el('div', { class: 'files-toolbar' }, actions, readOnly, viewBar);
  const body = el('div', { class: 'files-body' });
  // Below the tree of an editable mod: drop target / picker for the mod root, and a one-line tip about the hidden actions.
  const dropzone = el('button', { type: 'button', class: 'files-dropzone', 'data-testid': 'files-dropzone', onclick: () => pickUpload('') });
  const tip = el('p', { class: 'muted small files-tip' });
  const uploadInput = el('input', { type: 'file', multiple: true, hidden: true, 'data-testid': 'files-upload-input' });
  let uploadTarget = '';
  uploadInput.addEventListener('change', () => {
    const picked = Array.from(uploadInput.files || []);
    uploadInput.value = '';
    if (picked.length) upload(uploadTarget, picked);
  });
  root.append(toolbar, body, uploadInput);

  const menu = el('div', { class: 'files-menu', role: 'menu', 'data-testid': 'files-menu', hidden: true });
  const toast = el('div', { class: 'files-toast', role: 'status', 'aria-live': 'polite', 'data-testid': 'files-toast', hidden: true });
  document.body.append(menu, toast);

  function setView(view) {
    s.view = view;
    try { localStorage.setItem(VIEW_KEY, view); } catch { /* storage unavailable */ }
    renderToolbar();
    renderBody();
  }

  function renderToolbar() {
    const editable = isEditable();
    actions.hidden = !editable;
    readOnly.hidden = editable;
    readOnly.textContent = t('files.readOnlyHint');
    newFileBtn.label.textContent = t('files.new');
    newFolderBtn.label.textContent = t('files.newFolder');
    uploadBtn.label.textContent = t('files.upload');
    targetReset.textContent = t('files.targetReset');
    filter.setAttribute('placeholder', t('files.filter'));
    filter.setAttribute('aria-label', t('files.filter'));
    unusedLabel.textContent = t('files.unusedOnly');
    unusedBtn.title = t('files.badge.unusedTitle');
    viewSwitch.setAttribute('aria-label', t('files.viewLabel'));
    viewTree.textContent = t('files.viewTree');
    viewCategory.textContent = t('files.viewCategory');
    viewTree.setAttribute('aria-pressed', s.view === 'tree' ? 'true' : 'false');
    viewCategory.setAttribute('aria-pressed', s.view === 'category' ? 'true' : 'false');
    dropzone.textContent = t('files.dropzone');
    tip.textContent = t('files.tip');
  }

  /** Toolbar parts that depend on the file list or the selection. */
  function renderStatus(shown, folderPaths) {
    // Unused files matter only where they can be removed; for the game's own mods the engine uses many files Lua never names.
    if (!isEditable()) s.unusedOnly = false;
    const unused = isEditable() ? files().filter((f) => f.unused === true).length : 0;
    unusedBtn.setAttribute('aria-pressed', s.unusedOnly ? 'true' : 'false');
    unusedBtn.hidden = unused === 0 && !s.unusedOnly;
    unusedCount.textContent = String(unused);

    s.allFolders = folderPaths;
    const allOpen = folderPaths.length > 0 && folderPaths.every((p) => expanded().has(p));
    expandBtn.hidden = s.view !== 'tree' || filtering() || folderPaths.length === 0;
    expandBtn.textContent = t(allOpen ? 'files.collapseAll' : 'files.expandAll');
    expandBtn.dataset.mode = allOpen ? 'collapse' : 'expand';

    summary.textContent = filtering()
      ? `${t('count.file', { n: shown })} / ${files().length}`
      : t('count.file', { n: files().length });
    renderTarget();
  }

  function renderTarget() {
    const folder = targetFolder();
    targetText.textContent = t('files.target', { folder: folder ? `${folder}/` : t('files.targetRoot') });
    targetReset.hidden = !folder;
  }

  function toggleAll() {
    const set = expanded();
    if (expandBtn.dataset.mode === 'collapse') set.clear();
    else for (const path of s.allFolders || []) set.add(path);
    renderBody();
  }

  // ------------------------------------------------------------ rows

  function issueCounts() {
    const map = new Map();
    for (const issue of s.overview?.issues ?? []) {
      if (!issue.file) continue;
      const entry = map.get(issue.file) || { error: 0, warning: 0 };
      if (issue.severity === 'error') entry.error++; else if (issue.severity === 'warning') entry.warning++;
      map.set(issue.file, entry);
    }
    return map;
  }

  function badges(node, ctx) {
    const out = [];
    if (node.kind === 'folder') {
      const n = countFiles(node);
      out.push(el('span', { class: 'muted small files-size', title: t('count.file', { n }), text: String(n) }));
      return out;
    }
    const f = node.file;
    const issues = ctx.issues.get(node.path);
    if (issues?.error) out.push(el('span', { class: 'badge error', 'data-testid': 'files-badge-errors', title: t('files.badge.errors'), text: String(issues.error) }));
    if (issues?.warning) out.push(el('span', { class: 'badge warning', 'data-testid': 'files-badge-warnings', title: t('files.badge.warnings'), text: String(issues.warning) }));
    if (ctx.dirty.has(node.path)) out.push(el('span', { class: 'files-dirty', 'data-testid': 'files-badge-unsaved', title: t('files.badge.unsaved'), 'aria-label': t('files.badge.unsaved'), text: '●' }));
    if (f.unused === true && ctx.editable) out.push(el('span', { class: 'badge files-unused', 'data-testid': 'files-badge-unused', title: t('files.badge.unusedTitle'), text: t('files.badge.unused') }));
    if (f.role) out.push(el('span', { class: 'chip files-role', 'data-testid': 'files-badge-role', title: f.role, text: t('files.badge.role') }));
    out.push(el('span', { class: 'muted small files-size', text: formatSize(f.size) }));
    return out;
  }

  /** Icon class of a row: folders, images and sounds have their own shape; other files are coloured by category. */
  function iconClass(node) {
    if (node.kind === 'folder') return 'fi-folder';
    const category = node.file?.category ?? 'other';
    if (category === 'graphics') return 'fi-image';
    if (category === 'sound') return 'fi-sound';
    return `fi-file cat-${category}`;
  }

  function rowEl(node, depth, ctx, label = node.name) {
    const folder = node.kind === 'folder';
    const open = folder && (filtering() || expanded().has(node.path));
    const selected = node.path === s.selected;
    const row = el('div', {
      class: ['files-row', folder ? 'folder' : 'file', node.path === ctx.active && 'active'].filter(Boolean).join(' '),
      role: 'treeitem', 'data-testid': 'files-row', 'data-path': node.path, 'data-kind': node.kind,
      'aria-level': String(depth + 1), 'aria-selected': selected ? 'true' : 'false', 'aria-expanded': folder ? (open ? 'true' : 'false') : null,
      'aria-current': node.path === ctx.active ? 'true' : null,
      tabindex: selected ? '0' : '-1', draggable: ctx.editable ? 'true' : null, title: node.path,
    },
    el('span', { class: 'files-twisty', 'aria-hidden': 'true', text: folder ? (open ? '▾' : '▸') : '' }),
    el('span', { class: `files-icon ${iconClass(node)}`, 'aria-hidden': 'true' }),
    el('span', { class: 'files-name', text: label }),
    el('span', { class: 'files-badges' }, badges(node, ctx)),
    // Visible on hover / selection: the same menu as a right-click, for people who never right-click.
    el('button', { type: 'button', class: 'files-more', tabindex: '-1', 'data-testid': 'files-row-menu',
      'aria-label': t('files.actionsFor', { name: node.name }), title: t('files.actionsFor', { name: node.name }), text: '⋯' }));
    row.style.setProperty('--depth', String(depth)); // CSSOM, allowed under the strict CSP
    return row;
  }

  function renderBody() {
    const hadFocus = root.contains(document.activeElement) && document.activeElement?.classList.contains('files-row');
    const ctx = {
      issues: issueCounts(),
      dirty: new Set(editorTabs.dirtyPaths()),
      active: editorTabs.activePath(),
      editable: isEditable(),
    };

    const empty = () => el('p', { class: 'muted files-empty', 'data-testid': 'files-empty', text: t(filtering() ? 'files.noMatch' : 'files.empty') });
    const footer = ctx.editable ? [dropzone, tip] : [];

    if (s.view === 'category') {
      const groups = {};
      let shown = 0;
      for (const f of files()) if (fileMatches(f, s)) { (groups[f.category] ||= []).push(f); shown++; }
      const sections = CATEGORIES.filter((c) => groups[c]).map((category) => {
        const list = groups[category];
        return el('details', { open: category === 'script' || filtering() },
          el('summary', {}, t(`category.${category === 'other' ? 'misc' : category}`), el('span', { class: 'muted', text: ` (${list.length})` })),
          el('div', { class: 'files-tree', role: 'tree', 'aria-label': t(`category.${category === 'other' ? 'misc' : category}`) },
            list.slice(0, MAX_ROWS).map((f) => rowEl({ kind: 'file', name: nameOf(f.path), path: f.path, file: f }, 0, { ...ctx, editable: false }, f.path))),
          list.length > MAX_ROWS && el('div', { class: 'muted small', text: t('files.moreNotShown', { n: list.length - MAX_ROWS }) }));
      });
      body.replaceChildren(...(sections.length ? sections : [empty()]), ...footer);
      wireTree();
      renderStatus(shown, []);
      return;
    }

    const tree = buildTree(files(), [...extraFolders()]);
    const rows = flatten(tree, { name: s.name, unusedOnly: s.unusedOnly, expanded: expanded() });
    const treeEl = el('div', { class: 'files-tree', role: 'tree', 'data-testid': 'files-tree', 'aria-label': t('files.treeLabel') },
      rows.slice(0, MAX_ROWS).map(({ node, depth }) => rowEl(node, depth, ctx)));
    if (!rows.some((r) => r.node.path === s.selected) && rows.length) {
      const first = treeEl.querySelector('.files-row');
      if (first) first.tabIndex = 0; // keep the tree reachable with Tab
    }
    // replaceChildren() turns null into the text "null", so the optional note is spread in only when it exists.
    body.replaceChildren(rows.length ? treeEl : empty(),
      ...(rows.length > MAX_ROWS ? [el('div', { class: 'muted small', text: t('files.moreNotShown', { n: rows.length - MAX_ROWS }) })] : []),
      ...footer);
    wireTree();

    const folderPaths = [];
    const collect = (node) => { for (const child of node.children) if (child.kind === 'folder') { folderPaths.push(child.path); collect(child); } };
    collect(tree);
    renderStatus(rows.filter((r) => r.node.kind === 'file').length, folderPaths);
    if (hadFocus) requestAnimationFrame(() => focusRow(s.selected));
  }

  const rowOf = (target) => target?.closest?.('.files-row') ?? null;
  const visibleRows = () => Array.from(body.querySelectorAll('.files-row'));
  const nodeOfRow = (row) => (row ? { path: row.dataset.path, kind: row.dataset.kind } : { path: '', kind: 'folder' });

  function focusRow(path) {
    const row = visibleRows().find((r) => r.dataset.path === path);
    if (!row) return;
    select(path, false);
    row.focus();
    row.scrollIntoView?.({ block: 'nearest' });
  }

  function select(path, redraw = true) {
    s.selected = path;
    const rows = visibleRows();
    for (const row of rows) {
      const on = row.dataset.path === path;
      row.setAttribute('aria-selected', on ? 'true' : 'false');
      row.tabIndex = on ? 0 : -1;
    }
    if (rows.length && !rows.some((row) => row.tabIndex === 0)) rows[0].tabIndex = 0; // keep the tree reachable with Tab
    if (redraw) renderBody(); else renderTarget();
  }

  function toggle(path, open) {
    const set = expanded();
    const now = open ?? !set.has(path);
    if (now) set.add(path); else set.delete(path);
    renderBody();
  }

  function activateRow(row) {
    const node = nodeOfRow(row);
    select(node.path, false);
    if (node.kind === 'folder') { if (!filtering()) toggle(node.path); }
    else openFile(node.path);
  }

  /** The folder new things go into: the selected folder, the folder of the selected file, or the mod root. */
  function targetFolder() {
    const row = visibleRows().find((r) => r.dataset.path === s.selected);
    if (!row) return '';
    return row.dataset.kind === 'folder' ? row.dataset.path : parentOf(row.dataset.path);
  }

  // ------------------------------------------------------------ tree events (delegated; the body is persistent)

  let wired = false;
  function wireTree() {
    if (wired) return;
    wired = true;

    body.addEventListener('click', (e) => {
      const row = rowOf(e.target);
      const more = e.target.closest?.('.files-more');
      if (row && more) {
        select(row.dataset.path, false);
        const box = more.getBoundingClientRect();
        openMenu(nodeOfRow(row), box.left, box.bottom, row);
      } else if (row) activateRow(row);
      else if (e.target === body || e.target.classList?.contains('files-tree')) select(null, false); // empty space: back to the mod root
    });

    body.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const row = rowOf(e.target);
      if (row) select(row.dataset.path, false);
      openMenu(nodeOfRow(row), e.clientX, e.clientY, row);
    });

    body.addEventListener('keydown', (e) => {
      const row = rowOf(e.target);
      if (!row) return;
      const rows = visibleRows();
      const index = rows.indexOf(row);
      const node = nodeOfRow(row);
      const open = row.getAttribute('aria-expanded') === 'true';
      const move = (i) => { const next = rows[Math.max(0, Math.min(rows.length - 1, i))]; if (next) focusRow(next.dataset.path); };

      if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
        e.preventDefault();
        const box = row.getBoundingClientRect();
        openMenu(node, box.left + 24, box.bottom, row);
      } else if (e.key === 'ArrowDown') { e.preventDefault(); move(index + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); move(index - 1); }
      else if (e.key === 'Home') { e.preventDefault(); move(0); }
      else if (e.key === 'End') { e.preventDefault(); move(rows.length - 1); }
      else if (e.key === 'ArrowRight' && node.kind === 'folder') {
        e.preventDefault();
        if (!open && !filtering()) { toggle(node.path, true); requestAnimationFrame(() => focusRow(node.path)); } else move(index + 1);
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault();
        if (node.kind === 'folder' && open && !filtering()) { toggle(node.path, false); requestAnimationFrame(() => focusRow(node.path)); }
        else { const parent = parentOf(node.path); if (parent && node.path.includes('/')) focusRow(parent); }
      } else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activateRow(row); if (node.kind === 'folder') requestAnimationFrame(() => focusRow(node.path)); }
      else if (e.key === 'F2' && isEditable()) { e.preventDefault(); rename(node); }
      else if (e.key === 'Delete' && isEditable()) { e.preventDefault(); remove(node); }
    });

    // Drag inside the tree moves; OS files dropped on a folder are uploaded. Neither may reach the page-wide mod import handler.
    let dropRow = null;
    const setDropRow = (row) => {
      if (dropRow === row) return;
      dropRow?.classList.remove('drop-target');
      dropRow = row;
      dropRow?.classList.add('drop-target');
    };
    const types = (e) => Array.from(e.dataTransfer?.types || []);
    // The "By type" view shows no folders, so everything dropped there goes to the mod root (the drop zone lights up).
    const dropFolder = (row) => (!row || s.view !== 'tree' ? '' : row.dataset.kind === 'folder' ? row.dataset.path : parentOf(row.dataset.path));
    const folderRow = (row) => (row && row.dataset.kind === 'file' ? visibleRows().find((r) => r.dataset.path === parentOf(row.dataset.path)) ?? null : row);
    const highlightFor = (row) => (row && s.view === 'tree' ? folderRow(row) : null) ?? (dropzone.isConnected ? dropzone : null);

    body.addEventListener('dragstart', (e) => {
      const row = rowOf(e.target);
      if (!row || !isEditable() || s.view !== 'tree') { e.preventDefault(); return; }
      e.dataTransfer.setData(DRAG_TYPE, row.dataset.path);
      e.dataTransfer.setData('text/plain', row.dataset.path);
      e.dataTransfer.effectAllowed = 'move';
    });
    const over = (e) => {
      const internal = types(e).includes(DRAG_TYPE);
      const external = types(e).includes('Files');
      if (!internal && !external) return;
      e.preventDefault();
      e.stopPropagation();
      setDropOverlay(false);
      if (!isEditable()) { e.dataTransfer.dropEffect = 'none'; setDropRow(null); return; }
      e.dataTransfer.dropEffect = internal ? 'move' : 'copy';
      setDropRow(highlightFor(rowOf(e.target)));
    };
    body.addEventListener('dragenter', over);
    body.addEventListener('dragover', over);
    body.addEventListener('dragleave', (e) => {
      if (types(e).includes(DRAG_TYPE) || types(e).includes('Files')) e.stopPropagation();
      if (!body.contains(e.relatedTarget)) setDropRow(null);
    });
    body.addEventListener('dragend', () => setDropRow(null));
    body.addEventListener('drop', (e) => {
      const internal = types(e).includes(DRAG_TYPE);
      const external = types(e).includes('Files');
      if (!internal && !external) return;
      e.preventDefault();
      e.stopPropagation();
      setDropOverlay(false);
      setDropRow(null);
      if (!isEditable()) return;
      const folder = dropFolder(rowOf(e.target));
      if (internal) { move(e.dataTransfer.getData(DRAG_TYPE), folder); return; }

      // Only plain files; dropped folders are skipped (their entries would need a recursive walk and a new folder layout).
      const picked = [];
      let folders = 0;
      for (const item of Array.from(e.dataTransfer.items || [])) {
        if (item.kind !== 'file') continue;
        if (item.webkitGetAsEntry?.()?.isDirectory) { folders++; continue; }
        const file = item.getAsFile();
        if (file) picked.push(file);
      }
      if (!picked.length && !folders) picked.push(...Array.from(e.dataTransfer.files || []));
      upload(folder, picked, folders);
    });
  }

  // ------------------------------------------------------------ context menu

  let menuReturn = null;
  function closeMenu(restoreFocus = true) {
    if (menu.hidden) return;
    menu.hidden = true;
    menu.replaceChildren();
    if (restoreFocus && menuReturn?.isConnected) menuReturn.focus();
    menuReturn = null;
  }

  function openMenu(node, x, y, row) {
    const editable = isEditable();
    const isRoot = node.path === '';
    const folder = node.kind === 'folder';
    const items = [];
    const add = (key, run, testid) => items.push(el('button', { type: 'button', role: 'menuitem', tabindex: '-1', 'data-testid': `files-menu-${testid}`, text: t(key), onclick: () => { closeMenu(false); run(); } }));

    if (!folder) add('files.menu.open', () => openFile(node.path), 'open');
    if (editable && folder) {
      add('files.menu.newFile', () => newFile(node.path), 'new-file');
      add('files.menu.newFolder', () => newFolder(node.path), 'new-folder');
      add('files.menu.upload', () => pickUpload(node.path), 'upload');
    }
    if (editable && !isRoot) add('files.menu.rename', () => rename(node), 'rename');
    if (editable && !folder) add('files.menu.duplicate', () => duplicate(node), 'duplicate');
    if (editable && !isRoot) add('files.menu.delete', () => remove(node), 'delete');
    add('files.menu.reveal', () => reveal(node), 'reveal');

    menuReturn = row ?? document.activeElement;
    menu.replaceChildren(...items);
    menu.hidden = false;
    const width = menu.offsetWidth || 200;
    const height = menu.offsetHeight || items.length * 30;
    menu.style.setProperty('left', `${Math.max(4, Math.min(x, window.innerWidth - width - 4))}px`);
    menu.style.setProperty('top', `${Math.max(4, Math.min(y, window.innerHeight - height - 4))}px`);
    items[0]?.focus();
  }

  menu.addEventListener('keydown', (e) => {
    const items = Array.from(menu.querySelectorAll('[role="menuitem"]'));
    const index = items.indexOf(document.activeElement);
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); e.stopPropagation(); closeMenu(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); items[(index + 1) % items.length]?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); items[(index - 1 + items.length) % items.length]?.focus(); }
    else if (e.key === 'Home') { e.preventDefault(); items[0]?.focus(); }
    else if (e.key === 'End') { e.preventDefault(); items[items.length - 1]?.focus(); }
  });
  document.addEventListener('pointerdown', (e) => { if (!menu.hidden && !menu.contains(e.target)) closeMenu(false); }, true);
  window.addEventListener('blur', () => closeMenu(false));
  window.addEventListener('resize', () => closeMenu(false));
  document.addEventListener('scroll', () => closeMenu(false), true);

  // ------------------------------------------------------------ dialogs

  /** Asks for a name. `check(name)` returns an i18n key for a problem or null. Resolves to the name or null (cancelled). */
  function askName({ title, value = '', check, okKey = 'files.dialog.ok', placeholder = null, help = null }) {
    return new Promise((resolve) => {
      const input = el('input', { type: 'text', 'data-testid': 'files-dialog-input', value, placeholder, autocomplete: 'off', spellcheck: 'false', 'aria-label': title });
      const problem = el('p', { class: 'files-dialog-error small', role: 'alert', 'data-testid': 'files-dialog-error' });
      const ok = el('button', { type: 'submit', class: 'primary', 'data-testid': 'files-dialog-ok', text: t(okKey) });
      const cancel = el('button', { type: 'button', 'data-testid': 'files-dialog-cancel', text: t('files.dialog.cancel') });
      const dialog = el('dialog', { class: 'files-dialog', 'data-testid': 'files-dialog', 'aria-label': title },
        el('form', {}, el('h2', { text: title }), input, help && el('p', { class: 'muted small files-dialog-help', text: help }), problem,
          el('div', { class: 'nm-actions' }, cancel, ok)));
      let result = null;
      const validate = () => { const key = check(input.value.trim()); problem.textContent = key ? t(key) : ''; ok.disabled = !!key; return !key; };
      input.addEventListener('input', validate);
      dialog.querySelector('form').addEventListener('submit', (e) => { e.preventDefault(); if (validate()) { result = input.value.trim(); dialog.close(); } });
      cancel.addEventListener('click', () => dialog.close());
      dialog.addEventListener('close', () => { dialog.remove(); resolve(result); });
      document.body.append(dialog);
      dialog.showModal();
      if (value) validate(); else ok.disabled = true;
      input.focus();
      const dot = value.lastIndexOf('.');
      input.setSelectionRange(0, dot > 0 ? dot : value.length);
    });
  }

  /** Yes/no question in a <dialog>. Resolves to true when confirmed. */
  function confirmDialog({ title, message, okKey, danger = false }) {
    return new Promise((resolve) => {
      const ok = el('button', { type: 'submit', class: danger ? 'primary danger' : 'primary', 'data-testid': 'files-dialog-ok', text: t(okKey) });
      const cancel = el('button', { type: 'button', 'data-testid': 'files-dialog-cancel', text: t('files.dialog.cancel') });
      const dialog = el('dialog', { class: 'files-dialog', 'data-testid': 'files-dialog', 'aria-label': title },
        el('form', {}, el('h2', { text: title }), el('p', { text: message }), el('div', { class: 'nm-actions' }, cancel, ok)));
      let result = false;
      dialog.querySelector('form').addEventListener('submit', (e) => { e.preventDefault(); result = true; dialog.close(); });
      cancel.addEventListener('click', () => dialog.close());
      dialog.addEventListener('close', () => { dialog.remove(); resolve(result); });
      document.body.append(dialog);
      dialog.showModal();
      cancel.focus();
    });
  }

  // ------------------------------------------------------------ toast

  let toastTimer = 0;
  function showToast(text, actions = [], bad = false) {
    clearTimeout(toastTimer);
    toast.classList.toggle('bad', bad);
    toast.replaceChildren(el('span', { text }), ...actions,
      el('button', { type: 'button', class: 'linklike', 'aria-label': t('files.toast.dismiss'), text: '×', onclick: hideToast }));
    toast.hidden = false;
    toastTimer = setTimeout(hideToast, TOAST_MS);
  }
  function hideToast() { clearTimeout(toastTimer); toast.hidden = true; toast.replaceChildren(); }
  // A file or folder another program has open (Windows refuses to move it) gets a message that says what to do.
  const fail = (key, error) => showToast(error?.body?.code === 'in-use' ? t('files.inUse') : t(key, { message: error?.message ?? String(error) }), [], true);

  // ------------------------------------------------------------ operations

  const nameCheck = (folder, { extensions = null, except = null } = {}) => (name) => {
    const problem = validateName(name);
    if (problem) return problem;
    if (extensions && !extensions.includes(extOf(name))) return 'files.badExtension';
    const path = joinPath(folder, name);
    if (path !== except && (filePaths().has(path) || extraFolders().has(path))) return 'files.exists';
    return null;
  };

  async function after(selectPath) {
    if (selectPath !== undefined) s.selected = selectPath;
    await refresh();
    renderBody();
  }

  async function newFile(folder) {
    const name = await askName({
      title: t('files.dialog.newFile', { folder: folder || '/' }), check: nameCheck(folder, { extensions: EDITABLE_EXT }), okKey: 'files.dialog.create',
      placeholder: t('files.placeholderFile'), help: t('files.badExtension'),
    });
    if (!name) return;
    const path = joinPath(folder, name);
    try {
      await api(`${base()}/file?path=${enc(path)}`, { method: 'PUT', headers: { 'Content-Type': 'text/plain; charset=utf-8', 'If-None-Match': '*' }, body: '' });
    } catch (error) { fail(error.status === 428 ? 'files.exists' : 'files.createFailed', error); return; }
    if (folder) expanded().add(folder);
    await after(path);
    openFile(path);
  }

  async function newFolder(folder) {
    const name = await askName({ title: t('files.dialog.newFolder', { folder: folder || '/' }), check: nameCheck(folder), okKey: 'files.dialog.create', placeholder: t('files.placeholderFolder') });
    if (!name) return;
    const path = joinPath(folder, name);
    try { await api(`${base()}/fs/mkdir?path=${enc(path)}`, { method: 'POST' }); }
    catch (error) { fail(error.status === 409 ? 'files.exists' : 'files.folderFailed', error); return; }
    extraFolders().add(path);
    if (folder) expanded().add(folder);
    expanded().add(path);
    await after(path);
  }

  /** Keeps per-session folder state and editor tabs in step with a move or rename. */
  function followMove(from, to) {
    for (const set of [extraFolders(), expanded()]) {
      for (const path of [...set]) {
        const next = movedPath(path, from, to);
        if (next !== null) { set.delete(path); set.add(next); }
      }
    }
    editorTabs.renamePath(from, to);
  }

  async function moveTo(from, to) {
    try { await api(`${base()}/fs/move`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ from, to }) }); }
    catch (error) { fail(error.status === 409 ? 'files.exists' : 'files.moveFailed', error); return false; }
    followMove(from, to);
    const parent = parentOf(to);
    if (parent) expanded().add(parent);
    await after(to);
    return true;
  }

  async function rename(node) {
    if (!node.path) return;
    const folder = parentOf(node.path);
    const current = nameOf(node.path);
    const name = await askName({
      title: t('files.dialog.rename', { name: current }), value: current, okKey: 'files.dialog.renameOk',
      check: nameCheck(folder, { except: node.path }),
    });
    if (!name || name === current) return;
    await moveTo(node.path, joinPath(folder, name));
  }

  async function move(from, folder) {
    if (!from) return;
    const to = joinPath(folder, nameOf(from));
    if (to === from || isInside(folder, from)) return;
    await moveTo(from, to);
  }

  async function duplicate(node) {
    const folder = parentOf(node.path);
    const name = await askName({ title: t('files.dialog.duplicate', { name: nameOf(node.path) }), value: copyName(node.path, filePaths()), check: nameCheck(folder), okKey: 'files.dialog.duplicateOk' });
    if (!name) return;
    const to = joinPath(folder, name);
    try { await api(`${base()}/fs/copy`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ from: node.path, to }) }); }
    catch (error) { fail(error.status === 409 ? 'files.exists' : 'files.copyFailed', error); return; }
    await after(to);
  }

  async function remove(node) {
    if (!node.path) return;
    const dirty = editorTabs.dirtyPaths().filter((p) => isInside(p, node.path));
    const folder = node.kind === 'folder';
    const message = [
      t(folder ? 'files.confirmDeleteFolder' : 'files.confirmDeleteFile', { path: node.path }),
      dirty.length ? t('files.confirmDeleteDirty') : '',
    ].filter(Boolean).join(' ');
    const yes = await confirmDialog({ title: t('files.dialog.delete'), message, okKey: 'files.dialog.deleteOk', danger: true });
    if (!yes) return;

    const modId = getModId();
    let trashId;
    try { ({ trashId } = await api(`${base()}/file?path=${enc(node.path)}`, { method: 'DELETE' })); }
    catch (error) { fail('files.deleteFailed', error); return; }
    editorTabs.closePaths(node.path);
    for (const path of [...extraFolders()]) if (isInside(path, node.path)) extraFolders().delete(path);
    if (s.selected && isInside(s.selected, node.path)) s.selected = parentOf(node.path) || null;
    await after();
    showToast(t('files.deleted', { name: nameOf(node.path) }), [
      el('button', { type: 'button', 'data-testid': 'files-undo', text: t('files.undo'), onclick: () => undo(modId, trashId, node) }),
    ]);
  }

  async function undo(modId, trashId, node) {
    hideToast();
    if (modId !== getModId()) return;
    try { await api(`${base()}/trash/${enc(trashId)}/restore`, { method: 'POST' }); }
    catch (error) { fail(error.status === 409 ? 'files.restoreOccupied' : 'files.restoreFailed', error); return; }
    if (node.kind === 'folder') extraFolders().add(node.path);
    await after(node.path);
    showToast(t('files.restored', { name: nameOf(node.path) }));
  }

  async function reveal(node) {
    const query = node.path ? `?path=${enc(node.path)}` : '';
    try { await api(`${base()}/reveal${query}`, { method: 'POST' }); }
    catch (error) { fail('files.revealFailed', error); }
  }

  function pickUpload(folder) {
    uploadTarget = folder;
    uploadInput.click();
  }

  /** Uploads OS files into `folder`: text files through PUT /file, images and sounds through PUT /asset. Existing files are skipped. */
  async function upload(folder, list, skippedFolders = 0) {
    if (!isEditable() || s.busy) return;
    s.busy = true;
    let done = 0;
    const problems = [];
    try {
      for (const file of list) {
        const ext = extOf(file.name);
        const problem = validateName(file.name);
        const path = joinPath(folder, file.name);
        if (problem || (!EDITABLE_EXT.includes(ext) && !ASSET_EXT.includes(ext))) { problems.push(t('files.uploadSkipped', { name: file.name })); continue; }
        showToast(t('files.uploading', { done, total: list.length }));
        try {
          const endpoint = EDITABLE_EXT.includes(ext) ? 'file' : 'asset';
          const type = endpoint === 'file' ? 'text/plain; charset=utf-8' : 'application/octet-stream';
          await api(`${base()}/${endpoint}?path=${enc(path)}`, { method: 'PUT', headers: { 'Content-Type': type, 'If-None-Match': '*' }, body: file });
          done++;
        } catch (error) {
          problems.push(error.status === 428 ? t('files.uploadExists', { name: file.name }) : `${file.name}: ${error.message}`);
        }
      }
    } finally {
      s.busy = false;
    }
    if (skippedFolders) problems.push(t('files.uploadFolders', { count: skippedFolders }));
    if (folder) expanded().add(folder);
    if (done) await after();
    showToast([t('files.uploaded', { done, total: list.length }), ...problems].join(' · '), [], problems.length > 0);
  }

  // ------------------------------------------------------------ host API

  return {
    element: root,

    /** Draws the tab for the overview `o` and returns the persistent element. */
    render(o) {
      s.overview = o;
      if (filter.value !== s.name) filter.value = s.name;
      renderToolbar();
      renderBody();
      return root;
    },

    /** Editor tabs changed (opened, switched, dirty flag): redraw highlights and unsaved badges if the tab is visible. */
    onTabsChanged() {
      if (root.isConnected) renderBody();
    },

    /** The page switched to another mod. */
    reset() {
      s.selected = null;
      closeMenu(false);
      hideToast();
    },
  };
}
