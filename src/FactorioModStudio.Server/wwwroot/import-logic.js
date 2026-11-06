// Pure logic of the pre-check that runs before a picked or dropped folder is uploaded: which mods the folder holds, what
// their info.json says and which of them can be added. Mirrors the server's import rules (ModImporter) so problems show
// up before thousands of files are sent. No DOM access, so it runs under Node for tests.

export const MAX_IMPORT_FILES = 20000;
export const MAX_ZIP_BYTES = 200 * 1024 * 1024;

const NAME = /^[A-Za-z0-9_-]{1,100}$/;
const VERSION = /^\d{1,9}(\.\d{1,9}){1,2}$/;
const RESERVED = new Set(['CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$',
  ...Array.from({ length: 9 }, (_, i) => `COM${i + 1}`), ...Array.from({ length: 9 }, (_, i) => `LPT${i + 1}`)]);
// Factorio's own zip names: <name>_<version>.zip
const ZIP_NAME = /^(.+)_(\d{1,9}(?:\.\d{1,9}){1,2})\.zip$/i;

const isZip = (path) => /\.zip$/i.test(path);

/**
 * Sorts the files of a folder (`{ path, ... }`, '/'-separated and relative to the folder) into mods.
 * - `layout: 'single'`: the folder itself is a mod (info.json at its top); `mods` holds that one mod.
 * - `layout: 'many'`: a folder of mods, like Factorio's own mods folder: every sub-folder with an info.json directly inside
 *   is a mod, every top-level .zip a packed mod.
 * - `layout: 'none'`: neither.
 * Mods are `{ folder, files }` with file paths relative to the mod; `zips` are the top-level .zip entries; `others` the names
 * of top-level folders without an info.json. Loose top-level files (mod-list.json, mod-settings.dat...) are ignored.
 */
export function findMods(entries) {
  if (entries.some((e) => e.path === 'info.json')) return { layout: 'single', mods: [{ folder: '', files: entries }], zips: [], others: [] };

  const folders = new Map();
  const zips = [];
  for (const entry of entries) {
    const cut = entry.path.indexOf('/');
    if (cut < 0) { if (isZip(entry.path)) zips.push(entry); continue; }
    const top = entry.path.slice(0, cut);
    if (!folders.has(top)) folders.set(top, []);
    folders.get(top).push({ ...entry, path: entry.path.slice(cut + 1) });
  }

  const mods = [];
  const others = [];
  for (const [folder, files] of folders) {
    if (files.some((f) => f.path === 'info.json')) mods.push({ folder, files });
    else others.push(folder);
  }
  const byName = (a, b) => a.localeCompare(b, 'en', { sensitivity: 'base', numeric: true });
  mods.sort((a, b) => byName(a.folder, b.folder));
  zips.sort((a, b) => byName(a.path, b.path));
  others.sort(byName);
  return { layout: mods.length || zips.length ? 'many' : 'none', mods, zips, others };
}

/**
 * Reads an info.json text the way the server does. Returns `{ name, version, title }` or `{ problem }` with the key of a
 * translated reason: 'import.check.infoJson', 'import.check.infoName' or 'import.check.infoVersion'.
 */
export function readInfo(text) {
  let info;
  try { info = JSON.parse(String(text).replace(/^﻿/, '')); } catch { return { problem: 'import.check.infoJson' }; }
  if (!info || typeof info !== 'object' || Array.isArray(info)) return { problem: 'import.check.infoJson' };
  const { name, version, title } = info;
  if (typeof name !== 'string' || !NAME.test(name) || RESERVED.has(name.toUpperCase())) return { problem: 'import.check.infoName' };
  if (typeof version !== 'string' || !VERSION.test(version)) return { problem: 'import.check.infoVersion' };
  return { name, version, title: typeof title === 'string' ? title : '' };
}

/** The name and version a packed mod's file name promises (`<name>_<version>.zip`), or null. */
export function zipIdentity(fileName) {
  const m = ZIP_NAME.exec(fileName);
  return m && NAME.test(m[1]) ? { name: m[1], version: m[2] } : null;
}

/** Compares "1.2.10" and "1.2.9" numerically; missing parts count as 0. */
export function compareVersions(a, b) {
  const pa = String(a).split('.').map(Number);
  const pb = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d < 0 ? -1 : 1;
  }
  return 0;
}

/**
 * Decides which candidates can be added. A candidate is `{ kind: 'folder' | 'zip', name?, version?, problem?, files?, size? }`:
 * folders carry what readInfo found plus their usable file count, zips their size and the identity from their file name.
 * `existing` holds the names of the mods already in the workspace (compared without case, like the server).
 * Returns the candidates in the same order with `status` ('ok' or 'blocked') and `reason` (a translation key) plus
 * `params` for it. When one name appears more than once, the newest version is kept.
 */
export function assess(candidates, existing) {
  const taken = new Set([...existing].map((n) => n.toLowerCase()));
  const newest = new Map();
  for (const c of candidates) {
    if (c.problem || !c.name) continue;
    const key = c.name.toLowerCase();
    const best = newest.get(key);
    if (!best || compareVersions(c.version, best.version) > 0) newest.set(key, c);
  }

  return candidates.map((c) => {
    const blocked = (reason, params = {}) => ({ ...c, status: 'blocked', reason, params });
    if (c.problem) return blocked(c.problem);
    if (c.kind === 'folder' && !c.files) return blocked('import.check.noFiles');
    if (c.kind === 'folder' && c.files > MAX_IMPORT_FILES) return blocked('import.check.tooMany', { max: MAX_IMPORT_FILES });
    if (c.kind === 'zip' && c.size > MAX_ZIP_BYTES) return blocked('import.check.tooLarge', { max: MAX_ZIP_BYTES / 1024 / 1024 });
    if (c.name && taken.has(c.name.toLowerCase())) return blocked('import.check.exists');
    if (c.name && newest.get(c.name.toLowerCase()) !== c) return blocked('import.check.duplicate', { version: newest.get(c.name.toLowerCase()).version });
    return { ...c, status: 'ok', reason: c.kind === 'zip' && !c.name ? 'import.check.zipUnknown' : 'import.check.ready', params: {} };
  });
}
