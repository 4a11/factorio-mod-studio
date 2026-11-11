// "Problems" tab of the mod page: the validator's issues with a text filter, severity toggles, grouping by rule / by file / a flat
// list, collapsible groups, "copy list" for bug reports, and a per-row menu (the ⋯ button, right click, the ContextMenu key or
// Shift+F10): open the file, copy the message, show only this rule. Clicking a row (or Enter) opens the file at the line.
//
// Like the Files tab, the view owns one persistent subtree (`element`) that the host re-attaches on every redraw, so the filter keeps
// focus and the collapsed groups survive live updates. Messages, codes and paths come from mods and are only set as text.

import { t } from '/i18n.js';
import { SEVERITIES, GROUPINGS, countBySeverity, filterIssues, groupIssues, issuesAsText } from '/problems-logic.js';

const GROUPING_KEY = 'fms.problems.grouping';
const GROUP_LIMIT = 20; // rows shown per group before "Show N more"
const LIST_LIMIT = 200; // rows of the flat list before "Show N more"
const TOAST_MS = 4000;

/**
 * @param {{ el: Function, getModId: () => string|null, openFile: (path: string, line?: number) => any }} host
 */
export function createProblemsView({ el, getModId, openFile }) {
  const s = {
    issues: [],
    text: '',
    hidden: new Set(), // severities switched off
    grouping: 'rule',
    collapsed: new Map(), // modId -> Set of "grouping:key" that are folded
    showAll: new Set(), // "grouping:key" whose rows are all shown
    lastMod: null,
  };
  try { const g = localStorage.getItem(GROUPING_KEY); if (GROUPINGS.includes(g)) s.grouping = g; } catch { /* storage unavailable */ }

  const collapsed = () => {
    const id = getModId();
    if (!s.collapsed.has(id)) s.collapsed.set(id, new Set());
    return s.collapsed.get(id);
  };
  const groupId = (key) => `${s.grouping}:${key}`;
  const filtering = () => s.text.trim() !== '' || s.hidden.size > 0;

  // ------------------------------------------------------------ persistent structure

  const root = el('div', { class: 'problems-panel', 'data-testid': 'problems-panel' });
  const copyLabel = el('span');
  const copyBtn = el('button', { type: 'button', class: 'files-btn', 'data-testid': 'problems-copy', onclick: () => copyList() },
    el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: '⧉' }), copyLabel);
  const actions = el('div', { class: 'files-actions' }, copyBtn);

  const filter = el('input', { type: 'search', class: 'files-filter', 'data-testid': 'problems-filter', autocomplete: 'off', spellcheck: 'false' });
  filter.addEventListener('input', () => { s.text = filter.value; renderBody(); });
  const toggles = Object.fromEntries(SEVERITIES.map((severity) => {
    const label = el('span');
    const count = el('span', { class: 'files-count' });
    const button = el('button', { type: 'button', class: `files-toggle problems-toggle ${severity}`, 'data-testid': `problems-toggle-${severity}`, 'aria-pressed': 'true',
      onclick: () => { if (s.hidden.has(severity)) s.hidden.delete(severity); else s.hidden.add(severity); renderBody(); } },
    el('span', { class: `problems-dot ${severity}`, 'aria-hidden': 'true' }), label, count);
    return [severity, { button, label, count }];
  }));
  const switchButtons = Object.fromEntries(GROUPINGS.map((g) => [g, el('button', { type: 'button', 'data-testid': `problems-group-${g}`, onclick: () => setGrouping(g) })]));
  const groupSwitch = el('div', { class: 'files-switch', role: 'group' }, ...GROUPINGS.map((g) => switchButtons[g]));
  const foldBtn = el('button', { type: 'button', 'data-testid': 'problems-fold-all', onclick: () => foldAll() });
  const summary = el('span', { class: 'muted small files-summary', 'data-testid': 'problems-summary' });
  const viewBar = el('div', { class: 'files-viewbar' }, filter, ...SEVERITIES.map((sev) => toggles[sev].button), groupSwitch, foldBtn, summary);
  const toolbar = el('div', { class: 'files-toolbar' }, actions, viewBar);
  const body = el('div', { class: 'problems-body' });
  const tip = el('p', { class: 'muted small files-tip' });
  root.append(toolbar, body, tip);

  const menu = el('div', { class: 'files-menu', role: 'menu', 'data-testid': 'problems-menu', hidden: true });
  const toast = el('div', { class: 'files-toast', role: 'status', 'aria-live': 'polite', 'data-testid': 'problems-toast', hidden: true });
  document.body.append(menu, toast);
  let toastTimer = 0;
  let menuReturn = null;

  function setGrouping(grouping) {
    s.grouping = grouping;
    try { localStorage.setItem(GROUPING_KEY, grouping); } catch { /* storage unavailable */ }
    renderBody();
  }

  function showToast(text, bad = false) {
    clearTimeout(toastTimer);
    toast.textContent = text;
    toast.classList.toggle('bad', bad);
    toast.hidden = false;
    toastTimer = setTimeout(() => { toast.hidden = true; }, TOAST_MS);
  }

  async function copyText(text, done) {
    try {
      await navigator.clipboard.writeText(text);
      showToast(done);
    } catch {
      showToast(t('problems.copyFailed'), true);
    }
  }

  function copyList() {
    const shown = filterIssues(s.issues, { text: s.text, hidden: s.hidden });
    if (shown.length) copyText(issuesAsText(shown), t('problems.copied', { n: shown.length }));
  }

  function foldAll() {
    const set = collapsed();
    const keys = groupIssues(filterIssues(s.issues, { text: s.text, hidden: s.hidden }), s.grouping).map((g) => groupId(g.key));
    if (foldBtn.dataset.mode === 'collapse') keys.forEach((k) => set.add(k));
    else keys.forEach((k) => set.delete(k));
    renderBody();
  }

  function clearFilters() {
    s.text = '';
    filter.value = '';
    s.hidden.clear();
    renderBody();
    filter.focus();
  }

  // ------------------------------------------------------------ rendering

  function renderToolbar() {
    copyLabel.textContent = t('problems.copy');
    filter.setAttribute('placeholder', t('problems.filter'));
    filter.setAttribute('aria-label', t('problems.filter'));
    groupSwitch.setAttribute('aria-label', t('problems.groupLabel'));
    for (const g of GROUPINGS) {
      switchButtons[g].textContent = t(`problems.group.${g}`);
      switchButtons[g].setAttribute('aria-pressed', s.grouping === g ? 'true' : 'false');
    }
    tip.textContent = t('problems.tip');
  }

  function renderBody() {
    renderToolbar();
    const counts = countBySeverity(s.issues);
    for (const severity of SEVERITIES) {
      const { button, label, count } = toggles[severity];
      label.textContent = t(`problems.sev.${severity}`);
      count.textContent = String(counts[severity]);
      button.setAttribute('aria-pressed', s.hidden.has(severity) ? 'false' : 'true');
      button.hidden = counts[severity] === 0 && !s.hidden.has(severity); // counters hidden when zero
    }
    const shown = filterIssues(s.issues, { text: s.text, hidden: s.hidden });
    const groups = groupIssues(shown, s.grouping);
    summary.textContent = filtering()
      ? `${t('count.problem', { n: shown.length })} / ${s.issues.length}`
      : t('count.problem', { n: s.issues.length });
    copyBtn.disabled = shown.length === 0;
    actions.hidden = s.issues.length === 0;
    viewBar.hidden = s.issues.length === 0;
    tip.hidden = s.issues.length === 0;

    const grouped = s.grouping !== 'list';
    const allOpen = groups.every((g) => !collapsed().has(groupId(g.key)));
    foldBtn.hidden = !grouped || groups.length < 2;
    foldBtn.dataset.mode = allOpen ? 'collapse' : 'expand';
    foldBtn.textContent = t(allOpen ? 'files.collapseAll' : 'files.expandAll');

    if (!s.issues.length) {
      body.replaceChildren(el('div', { class: 'files-empty problems-empty', 'data-testid': 'problems-empty' },
        el('div', { class: 'problems-ok', text: t('problems.none') }),
        el('p', { class: 'muted small', text: t('problems.noneHint') })));
      return;
    }
    if (!shown.length) {
      body.replaceChildren(el('div', { class: 'files-empty', 'data-testid': 'problems-no-match' },
        el('span', { text: t('problems.noMatch') + ' ' }),
        el('button', { type: 'button', class: 'linklike', 'data-testid': 'problems-clear', onclick: clearFilters, text: t('problems.clear') })));
      return;
    }
    const list = el('div', { class: `problems-list${grouped ? '' : ' flat'}`, 'data-testid': 'problems-list', role: 'list' });
    for (const group of groups) {
      if (grouped) list.append(groupHeader(group));
      if (grouped && collapsed().has(groupId(group.key))) continue;
      const limit = s.showAll.has(groupId(group.key)) ? Infinity : grouped ? GROUP_LIMIT : LIST_LIMIT;
      for (const issue of group.items.slice(0, limit)) list.append(row(issue));
      if (group.items.length > limit) {
        const rest = group.items.length - limit;
        list.append(el('button', { type: 'button', class: 'linklike small problems-more-rows', 'data-testid': 'problems-show-more',
          onclick: () => { s.showAll.add(groupId(group.key)); renderBody(); }, text: t('problems.showMore', { n: rest }) }));
      }
    }
    body.replaceChildren(list);
  }

  function groupHeader(group) {
    const open = !collapsed().has(groupId(group.key));
    const title = s.grouping === 'rule'
      ? [el('span', { class: 'mono problems-code', text: group.key || '—' }), el('span', { class: 'muted problems-sample', text: group.items[0].message ?? '' })]
      : [el('span', { class: 'mono problems-file', text: group.key || t('problems.noFile') })];
    const counts = countBySeverity(group.items);
    return el('button', {
      type: 'button', class: 'problems-group', 'data-testid': 'problems-group', 'data-key': group.key, 'aria-expanded': open ? 'true' : 'false',
      onclick: () => { const set = collapsed(); if (open) set.add(groupId(group.key)); else set.delete(groupId(group.key)); renderBody(); },
    },
    el('span', { class: 'problems-chevron', 'aria-hidden': 'true', text: open ? '▾' : '▸' }),
    el('span', { class: `problems-dot ${group.severity}`, 'aria-hidden': 'true' }),
    ...title,
    el('span', { class: 'problems-group-counts' },
      ...SEVERITIES.filter((sev) => counts[sev] > 0).map((sev) => el('span', { class: `files-count problems-count ${sev}`, title: t(`problems.sev.${sev}`), text: String(counts[sev]) }))));
  }

  function row(issue) {
    const file = typeof issue.file === 'string' ? issue.file : '';
    const line = Number.isInteger(issue.line) && issue.line > 0 ? issue.line : 0;
    const where = s.grouping === 'file' ? (line ? `:${line}` : '') : (file ? `${file}${line ? `:${line}` : ''}` : '');
    const more = el('button', { type: 'button', class: 'files-more', tabindex: '-1', 'data-testid': 'problems-row-menu', 'aria-label': t('problems.menuLabel'), text: '⋯' });
    const node = el('div', {
      class: `problems-row${file ? '' : ' no-file'}`, role: 'listitem', tabindex: '0', 'data-testid': 'problem-item',
      'data-severity': issue.severity, 'data-code': issue.code ?? '', 'data-file': file, 'data-line': line ? String(line) : '',
      title: file ? t('problems.openTitle', { where: `${file}${line ? `:${line}` : ''}` }) : null,
    },
    el('span', { class: `badge ${issue.severity}`, text: t(`severity.${issue.severity}`) }),
    s.grouping !== 'rule' && el('span', { class: 'mono problems-code', text: issue.code ?? '' }),
    el('span', { class: 'problems-message', text: issue.message ?? '' }),
    where && el('span', { class: 'mono problems-where', text: where }),
    more);
    const open = () => { if (file) openFile(file, line || undefined); };
    node.addEventListener('click', (e) => {
      if (e.target.closest('.files-more')) { const r = more.getBoundingClientRect(); openMenu(issue, r.left, r.bottom, node); return; }
      open();
    });
    node.addEventListener('contextmenu', (e) => { e.preventDefault(); openMenu(issue, e.clientX, e.clientY, node); });
    node.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
      else if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) { e.preventDefault(); const r = node.getBoundingClientRect(); openMenu(issue, r.left + 24, r.bottom, node); }
      else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const rows = Array.from(body.querySelectorAll('.problems-row'));
        rows[rows.indexOf(node) + (e.key === 'ArrowDown' ? 1 : -1)]?.focus();
      }
    });
    return node;
  }

  // ------------------------------------------------------------ row menu

  function closeMenu(restoreFocus = true) {
    if (menu.hidden) return;
    menu.hidden = true;
    menu.replaceChildren();
    if (restoreFocus && menuReturn?.isConnected) menuReturn.focus();
    menuReturn = null;
  }

  function openMenu(issue, x, y, node) {
    const items = [];
    const add = (text, run, testid) => items.push(el('button', { type: 'button', role: 'menuitem', tabindex: '-1', 'data-testid': `problems-menu-${testid}`, text, onclick: () => { closeMenu(false); run(); } }));
    const file = typeof issue.file === 'string' ? issue.file : '';
    const line = Number.isInteger(issue.line) && issue.line > 0 ? issue.line : 0;
    if (file) add(t('problems.menu.open'), () => openFile(file, line || undefined), 'open');
    add(t('problems.menu.copy'), () => copyText(issuesAsText([issue]), t('problems.copied', { n: 1 })), 'copy');
    if (issue.code) {
      add(t('problems.menu.onlyRule', { code: issue.code }), () => { s.text = issue.code; filter.value = issue.code; s.hidden.clear(); renderBody(); }, 'only-rule');
    }
    if (file) add(t('problems.menu.onlyFile'), () => { s.text = file; filter.value = file; s.hidden.clear(); renderBody(); }, 'only-file');
    menuReturn = node;
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

  return {
    element: root,
    /** Shows the issues of the current mod; another mod starts with the filters cleared. */
    show(overview) {
      const id = getModId();
      if (id !== s.lastMod) {
        s.lastMod = id;
        s.text = '';
        filter.value = '';
        s.hidden.clear();
        s.showAll.clear();
      }
      s.issues = Array.isArray(overview?.issues) ? overview.issues : [];
      renderBody();
      return root;
    },
  };
}
