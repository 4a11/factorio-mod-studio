// Context menu of a mod in the sidebar: a right click on its row, the context-menu key, or the "⋯" button shown on hover.
// Open, pin, show in the file manager and, for mods in the workspace folder, delete: the mod goes into the studio's trash
// (workspace/.fmstudio/trash, kept 7 days) and the toast offers Undo. Uses the look of the Files tab menu, dialog and toast.

import { t } from '/i18n.js';

const TOAST_MS = 8000;

/**
 * @param {{ el: Function, api: Function, isPinned: (id: string) => boolean, togglePin: (id: string) => void,
 *   open: (mod: object) => void, hasUnsaved: (mod: object) => boolean, refresh: () => Promise<void> }} host
 */
export function createModMenu({ el, api, isPinned, togglePin, open, hasUnsaved, refresh }) {
  const enc = encodeURIComponent;
  const menu = el('div', { class: 'files-menu', role: 'menu', 'data-testid': 'mod-menu', hidden: true });
  const toast = el('div', { class: 'files-toast', role: 'status', 'aria-live': 'polite', 'data-testid': 'mod-toast', hidden: true });
  document.body.append(menu, toast);
  let returnFocus = null;

  // ------------------------------------------------------------ menu

  function close(focusBack = true) {
    if (menu.hidden) return;
    menu.hidden = true;
    menu.replaceChildren();
    if (focusBack) returnFocus?.focus();
    returnFocus = null;
  }

  /** Opens the menu of `mod` at (x, y); `from` gets the focus back when it closes. */
  function show(mod, x, y, from) {
    const items = [];
    const add = (key, run, testid, danger = false) => items.push(el('button', {
      type: 'button', role: 'menuitem', tabindex: '-1', class: danger ? 'danger' : null, 'data-testid': `mod-menu-${testid}`,
      text: t(key), onclick: () => { close(false); run(); } }));

    add('sidebar.menu.open', () => open(mod), 'open');
    add(isPinned(mod.id) ? 'sidebar.unpin' : 'sidebar.pin', () => togglePin(mod.id), 'pin');
    add('files.menu.reveal', () => reveal(mod), 'reveal');
    if (mod.source === 'workspace') add('sidebar.menu.delete', () => remove(mod), 'delete', true);

    menu.replaceChildren(...items);
    menu.setAttribute('aria-label', mod.name);
    returnFocus = from;
    menu.hidden = false;
    const box = menu.getBoundingClientRect();
    menu.style.setProperty('left', `${Math.round(Math.max(4, Math.min(x, innerWidth - box.width - 4)))}px`);
    menu.style.setProperty('top', `${Math.round(Math.max(4, Math.min(y, innerHeight - box.height - 4)))}px`);
    items[0].focus();
  }

  menu.addEventListener('keydown', (e) => {
    const items = [...menu.querySelectorAll('[role="menuitem"]')];
    const i = items.indexOf(document.activeElement);
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); close(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus(); }
    else if (e.key === 'Home' || e.key === 'End') { e.preventDefault(); items[e.key === 'Home' ? 0 : items.length - 1]?.focus(); }
  });
  document.addEventListener('pointerdown', (e) => { if (!menu.hidden && !menu.contains(e.target)) close(false); });
  window.addEventListener('resize', () => close(false));
  window.addEventListener('blur', () => close(false));

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

  /** Yes/no question in a <dialog>, as in the Files tab. Resolves to true when confirmed. */
  function confirmDialog({ title, message, okKey }) {
    return new Promise((resolve) => {
      const ok = el('button', { type: 'submit', class: 'primary danger', 'data-testid': 'mod-dialog-ok', text: t(okKey) });
      const cancel = el('button', { type: 'button', 'data-testid': 'mod-dialog-cancel', text: t('files.dialog.cancel') });
      const dialog = el('dialog', { class: 'files-dialog', 'data-testid': 'mod-dialog', 'aria-label': title },
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

  // ------------------------------------------------------------ actions

  async function reveal(mod) {
    try { await api(`/api/mods/${enc(mod.id)}/reveal`, { method: 'POST' }); }
    catch (error) { showToast(t('files.revealFailed', { message: error.message }), [], true); }
  }

  async function remove(mod) {
    const message = [
      t(mod.kind === 'zip' ? 'sidebar.confirmDeleteZip' : 'sidebar.confirmDelete', { name: mod.name }),
      hasUnsaved(mod) ? t('sidebar.confirmDeleteUnsaved') : '',
    ].filter(Boolean).join(' ');
    if (!await confirmDialog({ title: t('sidebar.dialog.delete', { name: mod.name }), message, okKey: 'files.dialog.deleteOk' })) return;

    let trashId;
    try { ({ trashId } = await api(`/api/mods/${enc(mod.id)}`, { method: 'DELETE' })); }
    catch (error) {
      // Windows keeps a folder that a program has open (or uses as its current folder) from being moved: say what to do.
      showToast(error.body?.code === 'in-use' ? t('sidebar.deleteInUse', { name: mod.name }) : t('files.deleteFailed', { message: error.message }), [], true);
      return;
    }
    // The id follows the folder, so after Undo the mod has it again; the pin comes back with it.
    const wasPinned = isPinned(mod.id);
    if (wasPinned) togglePin(mod.id);
    await refresh();
    showToast(t('sidebar.deleted', { name: mod.name }), [
      el('button', { type: 'button', 'data-testid': 'mod-undo', text: t('files.undo'), onclick: () => undo(mod, trashId, wasPinned) }),
    ]);
  }

  async function undo(mod, trashId, wasPinned) {
    hideToast();
    try { await api(`/api/workspace/trash/${enc(trashId)}/restore`, { method: 'POST' }); }
    catch (error) {
      showToast(error.body?.code === 'in-use' ? t('files.inUse') : error.status === 409 ? t('sidebar.restoreOccupied') : t('sidebar.restoreFailed', { message: error.message }), [], true);
      return;
    }
    if (wasPinned && !isPinned(mod.id)) togglePin(mod.id);
    await refresh();
    showToast(t('sidebar.restored', { name: mod.name }));
  }

  return { show, close };
}
