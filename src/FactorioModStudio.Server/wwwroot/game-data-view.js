// "In the game" mode of the Prototypes tab: what the game really has with the mod. A "dump" job (BuildJobs) starts Factorio with
// --dump-data twice, without and with the mod, and saves the difference; this view starts that job, follows it, and lists what
// the mod adds, changes and removes. A row opens the values: for a copy "original → this mod", for a changed prototype
// "before → after", and all values of the prototype as the game has them.

import { t, formatNumber } from '/i18n.js';

const POLL_MS = 1000;
const MAX_ROWS = 300;
const CHANGES = ['added', 'changed', 'removed'];

/**
 * @param {{ api: Function, el: Function, openFile: (path: string, line?: number) => void, fileUrl: (path: string) => string,
 *   installation: (modId: string) => string | null, openBlock: (file: string) => void }} host
 */
export function createGameDataView({ api, el, openFile, fileUrl, installation, openBlock }) {
  const enc = encodeURIComponent;
  const root = el('div', { class: 'gd-panel', 'data-testid': 'gamedata' });
  /** Per mod: { data, loaded, overview, job: { id, lastN, line } | null, error, busy, filter, search, open: Set, items: Map, showAll } */
  const mods = new Map();
  let current = null; // { modId, editable, overview }
  let pollTimer = 0;

  const stateOf = (modId) => {
    if (!mods.has(modId)) mods.set(modId, { data: null, loaded: false, job: null, error: null, busy: false, filter: 'all', search: '', open: new Set(), items: new Map(), showAll: false });
    return mods.get(modId);
  };
  const keyOf = (i) => `${i.type}/${i.name}`;

  // ------------------------------------------------------------ data and jobs

  async function load(modId) {
    const s = stateOf(modId);
    const before = s.data?.createdUtc;
    try { s.data = await api(`/api/mods/${enc(modId)}/gamedata`); }
    catch (error) { s.data = null; s.error = error.message; }
    s.loaded = true;
    if (s.data?.createdUtc !== before) { s.items.clear(); s.open.clear(); } // a new dump: the opened values are of the old one
    // A dump started earlier (or from another window) is followed too.
    if (!s.job) {
      try {
        const jobs = await api(`/api/jobs?modId=${enc(modId)}`);
        const running = (jobs || []).find((j) => j.kind === 'dump' && (j.state === 'running' || j.state === 'queued'));
        if (running) { s.job = { id: running.id, lastN: 0, line: '' }; schedulePoll(); }
      } catch { /* the list is optional */ }
    }
    if (current?.modId === modId) render();
  }

  async function start() {
    const modId = current.modId;
    const s = stateOf(modId);
    s.busy = true; s.error = null;
    render();
    const body = { kind: 'dump' };
    const chosen = installation(modId);
    if (chosen) body.installation = chosen;
    try {
      const job = await api(`/api/mods/${enc(modId)}/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      s.job = { id: job.id, lastN: 0, line: '' };
      schedulePoll();
    } catch (error) {
      s.error = error.status === 409 ? t('gamedata.busy') : error.message;
    } finally {
      s.busy = false;
    }
    render();
  }

  async function cancel() {
    const s = stateOf(current.modId);
    if (!s.job) return;
    try { await api(`/api/jobs/${enc(s.job.id)}/cancel`, { method: 'POST' }); } catch { /* it may have just finished */ }
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    pollTimer = setTimeout(poll, POLL_MS);
  }

  async function poll() {
    let pending = false;
    for (const [modId, s] of mods) {
      if (!s.job) continue;
      try {
        const snap = await api(`/api/jobs/${enc(s.job.id)}?after=${s.job.lastN}`);
        for (const line of snap.lines || []) { s.job.lastN = line.n; if (line.text.trim()) s.job.line = line.text; }
        if (snap.state === 'running' || snap.state === 'queued') { pending = true; continue; }
        s.job = null;
        if (snap.state === 'failed') s.error = failureText(snap);
        await load(modId);
      } catch {
        s.job = null; // the job is gone (the studio restarted): show what is saved
        await load(modId);
      }
    }
    if (current) render();
    if (pending) schedulePoll();
  }

  function failureText(snap) {
    const first = snap.result?.diagnostics?.find((d) => d.severity === 'error');
    const detail = first ? [first.message, ...(first.details || [])].join(' ').replace(/\s+/g, ' ').trim() : '';
    return t('gamedata.failed', { error: [snap.error, detail].filter(Boolean).join(' — ') });
  }

  async function toggle(item) {
    const s = stateOf(current.modId);
    const key = keyOf(item);
    if (s.open.has(key)) { s.open.delete(key); render(); return; }
    s.open.add(key);
    render();
    if (!s.items.has(key)) {
      try { s.items.set(key, await api(`/api/mods/${enc(current.modId)}/gamedata/item?type=${enc(item.type)}&name=${enc(item.name)}`)); }
      catch (error) { s.items.set(key, { error: error.message }); }
      render();
    }
  }

  // ------------------------------------------------------------ rendering

  const short = (value) => {
    if (value === null || value === undefined) return t('gamedata.none');
    const text = typeof value === 'string' ? JSON.stringify(value) : JSON.stringify(value);
    return text.length > 140 ? text.slice(0, 139) + '…' : text;
  };

  function iconFor(item) {
    const o = current.overview;
    const own = o.prototypes.find((p) => p.name === item.name && p.icon);
    return own
      ? el('img', { class: 'thumb proto-thumb', loading: 'lazy', alt: '', src: fileUrl(own.icon) })
      : el('span', { class: 'proto-thumb gd-dot', 'data-change': item.change, 'aria-hidden': 'true' });
  }

  function details(item, s) {
    const loaded = s.items.get(keyOf(item));
    if (!loaded) return el('div', { class: 'gd-details muted small', text: t('gamedata.loadingItem') });
    if (loaded.error) return el('div', { class: 'gd-details gd-error small', text: loaded.error });

    const copy = loaded.change === 'added' && loaded.copyOf;
    const definition = current.overview.prototypes.find((p) => p.type === item.type && p.name === item.name);
    const table = loaded.fields.length > 0 && el('table', { class: 'gd-fields', 'data-testid': 'gamedata-fields' },
      el('thead', {}, el('tr', {},
        el('th', { text: t('gamedata.colPath') }),
        el('th', { text: t(copy ? 'gamedata.colOriginal' : 'gamedata.colBefore') }),
        el('th', { text: t(copy ? 'gamedata.colMod' : 'gamedata.colAfter') }))),
      el('tbody', {}, loaded.fields.map((f) => el('tr', {},
        el('td', { class: 'mono', text: f.path || '·' }),
        el('td', { class: 'mono gd-before', title: JSON.stringify(f.before), text: short(f.before) }),
        el('td', { class: 'mono gd-after', title: JSON.stringify(f.after), text: short(f.after) })))));

    return el('div', { class: 'gd-details', 'data-testid': 'gamedata-details' },
      copy && el('p', { class: 'muted small', text: t('gamedata.copyExplain', { name: loaded.copyOf }) }),
      table,
      loaded.truncated && el('p', { class: 'muted small', text: t('gamedata.truncated') }),
      loaded.value && el('details', { class: 'gd-values' },
        el('summary', { text: t('gamedata.allValues') }),
        el('pre', { class: 'mono gd-json', text: JSON.stringify(loaded.value, null, 2) })),
      definition && el('span', { class: 'gd-links' },
        el('button', { type: 'button', class: 'linklike small', 'data-testid': 'gamedata-open-code',
          onclick: () => openFile(definition.file, definition.line), text: t('gamedata.openCode', { file: `${definition.file}:${definition.line}` }) }),
        (current.overview.blocks || []).some((b) => b.file === definition.file) && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'gamedata-open-block',
          onclick: () => openBlock(definition.file), text: t('blockprops.edit') })));
  }

  function row(item, s) {
    const open = s.open.has(keyOf(item));
    const note = item.change === 'removed' ? t('gamedata.removedNote')
      : item.copyOf ? `${t('prototypes.copyOf', { name: item.copyOf })} · ${t('gamedata.differs', { n: formatNumber(item.fields) })}`
      : item.change === 'changed' ? t('gamedata.changedFields', { n: formatNumber(item.fields) })
      : '';
    return el('div', { class: 'gd-item' + (open ? ' open' : '') },
      el('button', { type: 'button', class: 'proto-row', 'aria-expanded': open ? 'true' : 'false', 'data-testid': 'gamedata-row', onclick: () => toggle(item) },
        iconFor(item),
        el('span', { class: 'proto-names' },
          el('span', { class: 'mono proto-name', text: item.name }),
          note && el('span', { class: 'muted small', text: note })),
        el('span', { class: 'badge gd-change', 'data-change': item.change, text: t(`gamedata.filter.${item.change}`) }),
        el('span', { class: 'chip proto-type mono', title: item.type, text: item.type }),
        el('span', { class: 'ov-row-go', 'aria-hidden': 'true', text: open ? '▾' : '›' })),
      open && details(item, s));
  }

  function render() {
    if (!current) return;
    const { modId, editable } = current;
    const s = stateOf(modId);
    const data = s.data;
    const running = !!s.job;

    const actions = [];
    if (running) {
      actions.push(el('span', { class: 'gd-running', role: 'status', 'data-testid': 'gamedata-running' },
        el('span', { class: 'spinner', 'aria-hidden': 'true' }),
        el('span', { text: t('gamedata.running') }),
        s.job.line && el('span', { class: 'muted small mono gd-line', text: s.job.line })),
      el('button', { type: 'button', 'data-testid': 'gamedata-cancel', onclick: cancel, text: t('gamedata.cancel') }));
    } else if (editable) {
      actions.push(el('button', { type: 'button', class: data?.exists ? '' : 'primary', disabled: s.busy, 'data-testid': 'gamedata-load', onclick: start,
        text: t(data?.exists ? 'gamedata.refresh' : 'gamedata.load') }));
    }

    const status = data?.exists && el('div', { class: 'gd-status' },
      el('span', { class: 'muted small', 'data-testid': 'gamedata-summary', title: t('gamedata.mods', { mods: (data.mods || []).join(', ') }),
        text: t('gamedata.summary', { version: data.gameVersion || '?', date: new Date(data.createdUtc).toLocaleString() }) }),
      data.stale && el('span', { class: 'badge warning', 'data-testid': 'gamedata-stale', title: t('gamedata.stale'), text: t('gamedata.staleShort') }));

    const parts = [el('div', { class: 'gd-bar' }, status, el('span', { class: 'gd-actions' }, ...actions))];
    if (!editable && !data?.exists) parts.push(el('p', { class: 'muted small', text: t('gamedata.readOnly') }));
    if (s.error) parts.push(el('div', { class: 'gd-error', role: 'alert', 'data-testid': 'gamedata-error', text: s.error }));

    if (!s.loaded) parts.push(el('p', { class: 'muted small', text: t('gamedata.loadingItem') }));
    else if (!data?.exists) {
      // The intro promises a load; a read-only mod only gets the note above that it cannot be loaded.
      if (!running && editable) parts.push(el('div', { class: 'gd-intro' }, el('h3', { text: t('gamedata.introTitle') }), el('p', { class: 'muted', text: t('gamedata.intro') })));
    } else {
      parts.push(...list(data, s));
    }
    root.replaceChildren(...parts.filter(Boolean));
  }

  function list(data, s) {
    const query = s.search.trim().toLowerCase();
    const byChange = (c) => data.items.filter((i) => i.change === c).length;
    const filter = el('div', { class: 'segmented', role: 'group', 'aria-label': t('gamedata.filterAria') },
      ['all', ...CHANGES].map((c) => el('button', {
        type: 'button', class: 'toggle', 'aria-pressed': s.filter === c ? 'true' : 'false', 'data-filter': c,
        onclick: () => { s.filter = c; s.showAll = false; render(); },
      }, el('span', { text: t(`gamedata.filter.${c}`) }), el('span', { class: 'files-count', text: formatNumber(c === 'all' ? data.items.length : byChange(c)) }))));
    const search = el('input', { type: 'search', class: 'proto-filter', placeholder: t('gamedata.search'), 'aria-label': t('gamedata.search'), value: s.search,
      oninput: (e) => {
        s.search = e.target.value; s.showAll = false;
        const at = e.target.selectionStart;
        render();
        const again = root.querySelector('.proto-filter');
        again?.focus(); again?.setSelectionRange(at, at);
      } });

    const shown = data.items.filter((i) => (s.filter === 'all' || i.change === s.filter)
      && (!query || i.name.toLowerCase().includes(query) || i.type.toLowerCase().includes(query) || (i.copyOf || '').toLowerCase().includes(query)));
    const visible = s.showAll ? shown : shown.slice(0, MAX_ROWS);

    if (!data.items.length) return [el('p', { class: 'muted ov-empty', text: t('gamedata.empty') })];
    return [
      el('div', { class: 'files-toolbar proto-bar gd-filter' }, filter, search),
      shown.length
        ? el('div', { class: 'gd-list', 'data-testid': 'gamedata-list' }, visible.map((i) => row(i, s)))
        : el('p', { class: 'muted ov-empty', text: t('prototypes.noMatch') }),
      shown.length > visible.length && el('div', { class: 'proto-more-rows' },
        el('span', { class: 'muted small', text: t('prototypes.showing', { shown: visible.length, total: shown.length }) }),
        el('button', { type: 'button', class: 'linklike small', onclick: () => { s.showAll = true; render(); }, text: t('prototypes.showAll') })),
    ];
  }

  return {
    /** The view for the mod open in the Prototypes tab; loads what is saved the first time a mod is shown. */
    show(overview, modId, editable) {
      current = { modId, editable, overview };
      const s = stateOf(modId);
      // A new overview means the mod's files changed (or the mod was opened again): ask whether the saved data is outdated.
      if (!s.loaded || s.overview !== overview) { s.overview = overview; load(modId); }
      render();
      return root;
    },
  };
}
