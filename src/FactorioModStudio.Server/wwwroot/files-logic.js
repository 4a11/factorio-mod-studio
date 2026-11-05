// Pure logic of the Files tab: the folder tree built from the overview's file list, filtering, ordering, flattening into visible
// rows and file-name validation. No DOM access, so it runs under Node for tests.

const RESERVED = new Set(['CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$',
  ...Array.from({ length: 9 }, (_, i) => `COM${i + 1}`), ...Array.from({ length: 9 }, (_, i) => `LPT${i + 1}`)]);
const FORBIDDEN_CHARS = /[<>:"|?*\u0000-\u001f]/;
const MAX_NAME = 255;

const collator = typeof Intl !== 'undefined' ? new Intl.Collator('en', { numeric: true, sensitivity: 'base' }) : null;
const compareNames = (a, b) => {
  const c = collator ? collator.compare(a, b) : a.toLowerCase().localeCompare(b.toLowerCase());
  return c !== 0 ? c : a < b ? -1 : a > b ? 1 : 0;
};

const folderNode = (name, path) => ({ kind: 'folder', name, path, children: [] });

/**
 * Builds the folder tree from overview file entries (`{ path, size, category, role, unused }`, '/'-separated paths).
 * `folders` adds folders that hold no files yet (created in this session). Returns the root folder node
 * `{ kind: 'folder', name: '', path: '', children }`; file nodes are `{ kind: 'file', name, path, file }` where `file` is the entry.
 * Children are sorted with {@link sort}.
 */
export function buildTree(files, folders = []) {
  const root = folderNode('', '');
  const index = new Map([['', root]]);

  const ensureFolder = (path) => {
    if (index.has(path)) return index.get(path);
    const cut = path.lastIndexOf('/');
    const parent = ensureFolder(cut < 0 ? '' : path.slice(0, cut));
    const node = folderNode(path.slice(cut + 1), path);
    parent.children.push(node);
    index.set(path, node);
    return node;
  };

  for (const folder of folders || []) {
    const clean = String(folder).split('/').filter(Boolean).join('/');
    if (clean) ensureFolder(clean);
  }
  for (const file of files || []) {
    if (!file || typeof file.path !== 'string' || !file.path) continue;
    const cut = file.path.lastIndexOf('/');
    const parent = ensureFolder(cut < 0 ? '' : file.path.slice(0, cut));
    parent.children.push({ kind: 'file', name: file.path.slice(cut + 1), path: file.path, file });
  }

  const order = (node) => { node.children = sort(node.children); for (const child of node.children) if (child.kind === 'folder') order(child); };
  order(root);
  return root;
}

/** Folders first, then files; names in natural, case-insensitive order (ties broken by exact name). Returns a new array. */
export function sort(nodes) {
  return [...(nodes || [])].sort((a, b) => {
    if (a.kind !== b.kind) return a.kind === 'folder' ? -1 : 1;
    return compareNames(a.name, b.name);
  });
}
export const sortNodes = sort;

/** `filterTree(tree, { name, unusedOnly })` or `filterTree(tree, name, unusedOnly)`. */
function readFilter(filter, unusedOnly) {
  if (typeof filter === 'string') return { name: filter, unusedOnly: !!unusedOnly };
  return { name: filter?.name ?? '', unusedOnly: !!(filter?.unusedOnly ?? unusedOnly), expanded: filter?.expanded };
}

const isActive = (f) => f.name.trim() !== '' || f.unusedOnly;

/** Does a file entry pass the filter? The name filter is a case-insensitive substring of the file name. */
export function fileMatches(file, filter, unusedOnly) {
  const f = readFilter(filter, unusedOnly);
  const query = f.name.trim().toLowerCase();
  const name = file.path.slice(file.path.lastIndexOf('/') + 1).toLowerCase();
  if (query && !name.includes(query)) return false;
  if (f.unusedOnly && file.unused !== true) return false;
  return true;
}

/**
 * A copy of the tree that keeps only matching files and the folders leading to them. Without an active filter the tree is
 * returned unchanged (empty folders included); with one, folders without a matching file are dropped.
 */
export function filterTree(tree, filter, unusedOnly) {
  const f = readFilter(filter, unusedOnly);
  if (!tree || !isActive(f)) return tree;

  const walk = (node) => {
    const children = [];
    for (const child of node.children) {
      if (child.kind === 'file') { if (fileMatches(child.file, f)) children.push(child); }
      else { const kept = walk(child); if (kept.children.length) children.push(kept); }
    }
    return { ...node, children };
  };
  return walk(tree);
}

/**
 * The visible rows of the tree, depth first: `[{ node, depth }]` (the root itself is not a row; its children have depth 0).
 * Folders are descended into when their path is in `expanded` (a Set or an array); while a filter is active every folder is open.
 * Accepts `flatten(tree, { name, unusedOnly, expanded })` or `flatten(tree, expanded)`.
 */
export function flatten(tree, options = {}) {
  if (!tree) return [];
  const opts = options instanceof Set || Array.isArray(options) ? { expanded: options } : (options || {});
  const f = readFilter(opts);
  const filtering = isActive(f);
  const expanded = opts.expanded instanceof Set ? opts.expanded : new Set(opts.expanded || []);
  const source = filterTree(tree, f);

  const rows = [];
  const walk = (node, depth) => {
    for (const child of node.children) {
      rows.push({ node: child, depth });
      if (child.kind === 'folder' && (filtering || expanded.has(child.path))) walk(child, depth + 1);
    }
  };
  walk(source, 0);
  return rows;
}

/** Number of files in a (sub)tree. */
export function countFiles(node) {
  if (!node) return 0;
  if (node.kind === 'file') return 1;
  return node.children.reduce((n, child) => n + countFiles(child), 0);
}

/**
 * Checks one file or folder name (a single path segment). Returns null when the name is fine, otherwise the i18n key of the
 * problem: empty, separator ('/' or '\'), dots ('.' or '..'), leading dot (hidden files are never written), reserved Windows
 * device names (also with an extension: "con.txt"), forbidden characters <>:"|?* and control characters, a trailing dot or space
 * (Windows drops them) and names longer than 255 characters.
 */
export function validateName(name) {
  if (typeof name !== 'string' || name.trim() === '') return 'files.name.empty';
  if (name.includes('/') || name.includes('\\')) return 'files.name.separator';
  if (name === '.' || name === '..' || name.includes('..')) return 'files.name.dots';
  if (name.startsWith('.')) return 'files.name.leadingDot';
  if (FORBIDDEN_CHARS.test(name)) return 'files.name.chars';
  const stem = name.split('.')[0].trim().toUpperCase();
  if (RESERVED.has(stem)) return 'files.name.reserved';
  if (/[. ]$/.test(name) || /^\s/.test(name)) return 'files.name.trailing';
  if (name.length > MAX_NAME) return 'files.name.long';
  return null;
}

/** "a/b/c.lua" -> "a/b"; "c.lua" -> "". */
export const parentOf = (path) => { const cut = path.lastIndexOf('/'); return cut < 0 ? '' : path.slice(0, cut); };

/** "a/b/c.lua" -> "c.lua". */
export const nameOf = (path) => path.slice(path.lastIndexOf('/') + 1);

/** Joins a folder ('' = mod root) and a name. */
export const joinPath = (folder, name) => (folder ? `${folder}/${name}` : name);

/** Where a path ends up when `from` (a file or a folder) is moved to `to`; null when it is not affected. */
export function movedPath(path, from, to) {
  if (path === from) return to;
  if (path.startsWith(`${from}/`)) return to + path.slice(from.length);
  return null;
}

/** True when `path` is `folder` itself or lies inside it. */
export const isInside = (path, folder) => path === folder || path.startsWith(`${folder}/`);

/** A free "name-copy.ext" / "name-copy-2.ext" next to `path`, given the set of existing paths. */
export function copyName(path, existing) {
  const folder = parentOf(path);
  const name = nameOf(path);
  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : '';
  const taken = existing instanceof Set ? existing : new Set(existing || []);
  for (let i = 1; i < 1000; i++) {
    const candidate = `${stem}-copy${i === 1 ? '' : `-${i}`}${ext}`;
    if (!taken.has(joinPath(folder, candidate))) return candidate;
  }
  return `${stem}-copy-${Date.now()}${ext}`;
}
