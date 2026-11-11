// "Build" tab of the mod page: validate, pack, smoke-test and run a mod from the browser, with a live log.
//
// The browser only chooses a kind and a few numbers; the server decides which Factorio is started and with which arguments.
// Like the localisation view, this one owns a persistent DOM subtree (`element`) and keeps its state outside the DOM, so the host
// page can detach and re-attach it (it redraws the whole mod page on live updates) without losing the log.
// Everything that comes from the server or from a mod (log lines, messages, file names) is only set with textContent / attributes.
//
// "Install a missing Factorio": when no installation matches the mod's factorio_version, a button installs the headless server
// (Factorio without graphics: enough for smoke tests, not for playing) through /api/factorio/install; the installations dialog
// offers any branch. One install runs at a time for the whole studio, so its state lives outside the per-mod state.
// The full game (with graphics) can be installed the same way with a factorio.com account that owns Factorio: the dialog saves a
// username and token (the token is sent to the studio once and never comes back) or uses the account the game is signed in to.

import { t, getLanguage } from '/i18n.js';

const POLL_MS = 500;
const INSTALL_POLL_MS = 700;
const MAX_POLL_DELAY_MS = 5000;
const MAX_LOG_LINES = 5000;
const MAX_TICKS = 216000;
const enc = encodeURIComponent;

const KINDS = ['validate', 'pack', 'smoke', 'run'];
const ACTIVE = new Set(['queued', 'running']);
const SOURCES = new Set(['ui', 'env', 'config', 'auto']);
const MODES = new Set(['auto', 'manual', 'none']);
const STORE_PREFIX = 'fms.install.';

// The manual choice is a per-viewer convenience: storage may be missing or blocked, everything works without it.
const rememberedChoice = (modId) => { try { return localStorage.getItem(STORE_PREFIX + modId) || null; } catch { return null; } };
/** The installation chosen by hand in the Build tab for a mod, or null (automatic); other tabs that start the game use it too. */
export const chosenInstallation = (modId) => rememberedChoice(modId);
const rememberChoice = (modId, id) => {
  try { if (id) localStorage.setItem(STORE_PREFIX + modId, id); else localStorage.removeItem(STORE_PREFIX + modId); } catch { /* not essential */ }
};
const isActive = (entry) => !!entry && ACTIVE.has(entry.snap.state);

// CSI sequences (colours, cursor), OSC sequences (titles), other two-byte escapes, then stray control characters.
const ANSI = /\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|\u001b[@-Z\\-_]/g;
const CONTROL = /[\u0000-\u0008\u000b-\u001f\u007f]/g;
export const cleanLine = (text) => String(text).replace(ANSI, '').replace(CONTROL, '');

/**
 * @param {{ api: (url: string, options?: object) => Promise<any>,
 *           el: (tag: string, props?: object, ...children: any[]) => HTMLElement,
 *           openFile: (path: string, line?: number) => void,
 *           modName: () => string | null }} host
 */
export function createBuildView({ api, el, openFile, modName }) {
  const s = {
    modId: null,
    editable: false,
    env: null, // { found, path, error, version, source, resolution } | 'checking' | 'failed'
    installs: [], // candidate installations offered in the picker
    manualId: null, // installation chosen by hand for this mod (null = automatic)
    history: [], // job snapshots without lines, newest first
    entries: new Map(), // jobId -> { snap, lines: [{n, text}], lastN, loaded, trimmed }
    shownId: null,
    runningId: null, // the active job of this mod that is being polled
    message: null, // { key, params } | { text } | null
    starting: false,
    cancelling: false,
    pollTimer: 0,
    pollFailures: 0,
    seq: 0, // bumped when the mod changes: answers for an older mod are dropped
    stick: true, // keep the log scrolled to the end
    renderedId: null,
    renderedN: 0,
    historySig: '',
    historyForce: false,
  };

  // Other views (action bar, output panel, command palette) follow this view's state instead of polling the jobs API themselves.
  const listeners = new Set();
  const startedListeners = new Set();
  let notifyQueued = false;
  const notify = () => {
    if (notifyQueued) return;
    notifyQueued = true;
    queueMicrotask(() => {
      notifyQueued = false;
      for (const listener of [...listeners]) { try { listener(); } catch { /* a broken listener must not stop the others */ } }
    });
  };

  const labels = []; // [node, key]
  const refs = {};
  const label = (node, key) => { labels.push([node, key]); node.textContent = t(key); return node; };
  // A dump (the Prototypes tab's 'In the game' mode) has no button here but shows up in the history like the others.
const kindLabel = (kind) => (KINDS.includes(kind) || kind === 'dump' ? t(`build.${kind}`) : String(kind));
  const shown = () => (s.shownId ? s.entries.get(s.shownId) ?? null : null);
  const activeNow = () => !!s.runningId && isActive(s.entries.get(s.runningId));

  // ------------------------------------------------------------ static structure

  const root = el('div', { class: 'build-panel', 'data-testid': 'build-panel' });

  refs.env = el('div', { class: 'note build-env', 'data-testid': 'build-env', 'data-found': 'false', role: 'status' });
  refs.readonly = label(el('div', { class: 'note', 'data-testid': 'build-readonly', role: 'note' }), 'build.readonly');

  refs.ticks = el('input', { type: 'number', id: 'build-ticks', 'data-testid': 'build-ticks', min: '1', max: String(MAX_TICKS), step: '1', value: '600', inputmode: 'numeric' });
  refs.seed = el('input', { type: 'number', id: 'build-seed', 'data-testid': 'build-seed', step: '1', value: '1', inputmode: 'numeric' });
  refs.force = el('input', { type: 'checkbox', id: 'build-force', 'data-testid': 'build-force' });
  const field = (id, key, input) => el('div', { class: 'build-field' }, label(el('label', { for: id }), key), input);
  refs.options = el('div', { class: 'build-options' },
    field('build-ticks', 'build.ticks', refs.ticks),
    field('build-seed', 'build.seed', refs.seed),
    el('div', { class: 'build-field' }, refs.force, label(el('label', { for: 'build-force' }), 'build.force')));

  const button = (kind, extra = {}) => {
    const node = el('button', { type: 'button', 'data-testid': `build-${kind}`, ...extra });
    label(node, `build.${kind}`);
    return node;
  };
  refs.validate = button('validate');
  refs.pack = button('pack');
  refs.smoke = button('smoke');
  refs.run = button('run');
  refs.cancel = button('cancel');
  refs.cancel.hidden = true;
  refs.validate.addEventListener('click', () => start('validate'));
  refs.pack.addEventListener('click', () => start('pack'));
  refs.smoke.addEventListener('click', () => start('smoke'));
  refs.run.addEventListener('click', () => start('run'));
  refs.cancel.addEventListener('click', cancel);
  refs.actions = el('div', { class: 'build-actions' }, refs.validate, refs.pack, refs.smoke, refs.run, refs.cancel);

  refs.state = el('span', { class: 'build-state', 'data-testid': 'build-state', 'data-state': 'idle', role: 'status' });
  refs.message = el('span', { class: 'build-message', 'data-testid': 'build-message', role: 'alert' });
  refs.statusRow = el('div', { class: 'build-status' }, refs.state, refs.message);

  refs.logText = document.createTextNode('');
  refs.log = el('pre', { class: 'build-log', 'data-testid': 'build-log', tabindex: '0', role: 'log' }, refs.logText);
  refs.log.addEventListener('scroll', () => { s.stick = refs.log.scrollTop + refs.log.clientHeight >= refs.log.scrollHeight - 12; });
  refs.truncated = label(el('p', { class: 'muted small' }), 'build.log.truncated');
  refs.truncated.hidden = true;

  refs.result = el('div', { class: 'build-result', 'data-testid': 'build-result' });
  refs.historyHeading = label(el('h3', { class: 'section' }), 'build.history.heading');
  refs.history = el('ul', { class: 'build-history', 'data-testid': 'build-history' });

  // Installation picker: "automatic" follows the mod's factorio_version, any other entry is a manual choice for the next jobs.
  refs.resolution = el('div', { class: 'note build-resolution', 'data-testid': 'build-resolution', 'data-mode': 'none', role: 'status' });
  refs.resolution.hidden = true;
  refs.installSelect = el('select', { id: 'build-install-select', 'data-testid': 'build-install-select' });
  refs.installManage = label(el('button', { type: 'button', 'data-testid': 'build-install-manage' }), 'build.install.manage');
  refs.installHint = label(el('p', { class: 'small build-install-hint', 'data-testid': 'build-install-hint' }), 'build.install.noneHint');
  refs.installHint.hidden = true;
  // Install a missing version: under the hint (for the mod's own version) and in the installations dialog (any branch).
  refs.getBox = el('div', { class: 'build-get', 'data-testid': 'build-get', role: 'status' });
  refs.getBox.hidden = true;
  refs.getSelect = el('select', { id: 'build-get-select', 'data-testid': 'build-get-select' });
  refs.getDialogBox = el('div', { class: 'build-get build-get-dialog', 'data-testid': 'build-get-dialog', role: 'status' });
  refs.picker = el('div', { class: 'build-install-picker' },
    label(el('label', { for: 'build-install-select' }), 'build.install.label'), refs.installSelect, refs.installManage);

  // Installations dialog: the list, "add" (a typed / pasted path or the built-in folder browser: the server lists directories only).
  refs.pathInput = el('input', { type: 'text', id: 'build-path-input', 'data-testid': 'build-path-input', maxlength: '1024', spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', autocorrect: 'off' });
  refs.pathInput.setAttribute('placeholder', t('build.path.placeholder'));
  refs.pathBrowse = label(el('button', { type: 'button', 'data-testid': 'build-path-browse', 'aria-expanded': 'false', 'aria-controls': 'build-path-browser' }), 'build.path.browse');
  refs.pathUp = label(el('button', { type: 'button', 'data-testid': 'build-path-up' }), 'build.path.up');
  refs.pathCurrent = el('span', { class: 'mono build-path-current', 'data-testid': 'build-path-current' });
  refs.pathList = el('ul', { class: 'build-path-list', 'data-testid': 'build-path-list' });
  refs.pathStatus = el('p', { class: 'muted small build-path-status', role: 'status' });
  refs.pathBrowser = el('div', { class: 'build-path-browser', id: 'build-path-browser', 'data-testid': 'build-path-browser' },
    el('div', { class: 'build-path-nav' }, refs.pathUp, refs.pathCurrent), refs.pathList, refs.pathStatus);
  refs.pathBrowser.hidden = true;
  refs.pathError = el('p', { class: 'build-path-error', 'data-testid': 'build-path-error', role: 'alert' });
  refs.pathHint = label(el('p', { class: 'muted small' }), 'build.path.hint');

  refs.instList = el('ul', { class: 'build-install-list', 'data-testid': 'build-install-list' });
  refs.instRescan = label(el('button', { type: 'button', 'data-testid': 'build-install-rescan' }), 'build.install.rescan');
  refs.instAdd = label(el('button', { type: 'button', 'data-testid': 'build-install-add', 'aria-expanded': 'false', 'aria-controls': 'build-install-add-form' }), 'build.install.add');
  refs.instLabel = el('input', { type: 'text', id: 'build-install-label', 'data-testid': 'build-install-label', maxlength: '80', autocomplete: 'off' });
  refs.instConfirm = label(el('button', { type: 'submit', class: 'primary', 'data-testid': 'build-install-add-confirm' }), 'build.install.addConfirm');
  refs.instAddCancel = label(el('button', { type: 'button', 'data-testid': 'build-install-add-cancel' }), 'build.install.addCancel');
  refs.instClose = label(el('button', { type: 'button', 'data-testid': 'build-install-close' }), 'build.install.close');
  refs.addForm = el('form', { id: 'build-install-add-form', class: 'build-install-add', novalidate: true, autocomplete: 'off' },
    label(el('label', { for: 'build-path-input' }), 'build.path.label'),
    el('div', { class: 'build-path-row' }, refs.pathInput, refs.pathBrowse),
    refs.pathHint, refs.pathBrowser,
    label(el('label', { for: 'build-install-label' }), 'build.install.labelField'), refs.instLabel,
    el('div', { class: 'nm-actions build-path-actions' }, refs.instAddCancel, refs.instConfirm));
  refs.addForm.hidden = true;
  refs.installDialog = el('dialog', { class: 'build-path-dialog build-install-dialog', 'data-testid': 'build-install-dialog', 'aria-labelledby': 'build-install-heading' },
    label(el('h2', { id: 'build-install-heading' }), 'build.install.title'),
    refs.instList,
    el('div', { class: 'nm-actions build-path-actions' }, refs.instRescan, refs.instAdd),
    refs.addForm, refs.pathError,
    label(el('h3', { class: 'section build-get-heading' }), 'build.get.heading'),
    refs.getDialogBox,
    el('div', { class: 'nm-actions build-path-actions' }, refs.instClose));

  root.append(refs.env, refs.resolution, refs.picker, refs.installHint, refs.getBox, refs.readonly, refs.options, refs.actions, refs.statusRow, refs.log, refs.truncated, refs.result, refs.historyHeading, refs.history, refs.installDialog);

  // ------------------------------------------------------------ rendering

  function setMessage(key, params) { s.message = key ? { key, params } : null; renderStatus(); }
  function setMessageText(text) { s.message = text ? { text } : null; renderStatus(); }

  const unknownVersion = () => t('build.install.unknownVersion');
  const installText = (install) => `${install.version || unknownVersion()} - ${install.label || install.path}`;

  function renderPicker() {
    const env = s.env && typeof s.env === 'object' ? s.env : null;
    const modVersion = env?.resolution?.modVersion ?? null;
    const signature = JSON.stringify([s.installs.map((i) => [i.id, i.version, i.label, i.path]), modVersion, s.manualId, getLanguage()]);
    if (signature === refs.installSelect.dataset.sig) return;
    refs.installSelect.dataset.sig = signature;
    const auto = el('option', { value: '', text: modVersion ? t('build.install.auto', { version: modVersion }) : t('build.install.autoAny') });
    const options = s.installs.map((install) => el('option', { value: install.id, 'data-version': install.version ?? '', text: installText(install) }));
    refs.installSelect.replaceChildren(auto, ...options);
    refs.installSelect.value = s.manualId && s.installs.some((i) => i.id === s.manualId) ? s.manualId : '';
  }

  function renderEnv() {
    const env = s.env;
    const found = !!env && typeof env === 'object' && env.found === true;
    refs.env.dataset.found = found ? 'true' : 'false';
    renderPicker();
    renderResolution();
    if (env === 'checking' || env === null) { // nothing to report yet: hidden, and no data-found until the answer is in
      delete refs.env.dataset.found;
      refs.env.hidden = true;
      refs.env.replaceChildren(t('build.env.checking'));
      return;
    }
    refs.env.hidden = false;
    if (found) {
      const sourceKey = SOURCES.has(env.source) ? `build.path.source.${env.source}` : null;
      refs.env.replaceChildren(
        el('span', { text: t('build.env.found') + ' ' }),
        el('strong', { 'data-testid': 'build-env-version', text: env.version || unknownVersion() }),
        ' ',
        el('span', { class: 'mono', text: env.path }),
        ' ',
        el('span', { class: 'muted small', 'data-testid': 'build-env-source', 'data-source': env.source ?? '', text: sourceKey ? `(${t(sourceKey)})` : '' }));
      renderGet();
      return;
    }
    refs.env.replaceChildren(el('strong', { text: t('build.env.notFound') }), el('p', { class: 'small', text: t('build.env.hint') }));
    renderGet();
  }

  function renderResolution() {
    const resolution = s.env && typeof s.env === 'object' ? s.env.resolution : null;
    const mode = resolution && MODES.has(resolution.mode) ? resolution.mode : null;
    refs.resolution.hidden = !mode;
    refs.installHint.hidden = mode !== 'none';
    if (!mode) { refs.resolution.dataset.mode = 'none'; refs.resolution.replaceChildren(); return; }
    refs.resolution.dataset.mode = mode;
    refs.resolution.replaceChildren(
      el('strong', { text: t(`build.install.mode.${mode}`) + ': ' }),
      el('span', { 'data-testid': 'build-resolution-reason', text: resolution.reason || '' }));
  }

  function renderStatus() {
    const entry = shown();
    const state = entry ? entry.snap.state : 'idle';
    refs.state.dataset.state = state;
    refs.state.textContent = t(`build.state.${state}`);
    let text = '';
    if (s.message) text = s.message.text ?? t(s.message.key, s.message.params);
    else if (entry && entry.snap.state === 'failed' && entry.snap.error) text = entry.snap.error;
    refs.message.textContent = text;
    refs.message.hidden = text === '';
    notify();
  }

  function renderButtons() {
    const busy = activeNow() || s.starting;
    const factorio = !!s.env && typeof s.env === 'object' && s.env.found === true;
    refs.readonly.hidden = s.editable;

    const setDisabled = (node, disabled, reasonKey) => {
      node.disabled = disabled;
      if (disabled && reasonKey) node.setAttribute('title', t(reasonKey)); else node.removeAttribute('title');
    };
    setDisabled(refs.validate, busy);
    setDisabled(refs.pack, busy || !s.editable, !s.editable ? 'build.readonlyShort' : null);
    setDisabled(refs.smoke, busy || !s.editable || !factorio, !s.editable ? 'build.readonlyShort' : !factorio ? 'build.env.notFound' : null);
    setDisabled(refs.run, busy || !s.editable || !factorio, !s.editable ? 'build.readonlyShort' : !factorio ? 'build.env.notFound' : null);
    refs.cancel.hidden = !activeNow();
    refs.cancel.disabled = s.cancelling;
    notify();
  }

  function appendLog(chunk) {
    if (chunk) refs.logText.appendData(chunk);
  }

  function renderLog(forceFull = false) {
    const entry = shown();
    if (!entry) {
      refs.logText.data = '';
      s.renderedId = null;
      s.renderedN = 0;
      refs.truncated.hidden = true;
      return;
    }

    const changed = s.renderedId !== entry.snap.id;
    if (changed) s.stick = true;
    const top = refs.log.scrollTop;

    if (forceFull || changed || entry.trimmed) {
      refs.logText.data = entry.lines.map((l) => l.text).join('\n');
      entry.trimmed = false;
      s.renderedId = entry.snap.id;
      s.renderedN = entry.lastN;
    } else if (entry.lastN > s.renderedN) {
      const fresh = entry.lines.filter((l) => l.n > s.renderedN);
      appendLog((refs.logText.length > 0 ? '\n' : '') + fresh.map((l) => l.text).join('\n'));
      s.renderedN = entry.lastN;
    }
    refs.truncated.hidden = !(entry.snap.truncated || entry.lines.length >= MAX_LOG_LINES);
    if (s.stick) refs.log.scrollTop = refs.log.scrollHeight;
    else refs.log.scrollTop = top;
    notify();
  }

  const issueRow = (issue, options = {}) => {
    const file = typeof issue.file === 'string' && issue.file ? issue.file : null;
    const line = Number.isInteger(issue.line) ? issue.line : null;
    const open = file && options.canOpen !== false;
    const where = file ? (line ? `${file}:${line}` : file) : null;
    const row = el('li', {
      class: 'build-issue' + (open ? ' clickable' : ''),
      'data-testid': 'build-issue',
      'data-severity': issue.severity,
      'data-code': issue.code ?? '',
      onclick: open ? () => openFile(file, line ?? undefined) : null,
    },
    el('span', { class: `badge ${issue.severity}`, text: t(`severity.${issue.severity}`) === `severity.${issue.severity}` ? issue.severity : t(`severity.${issue.severity}`) }),
    issue.code ? el('span', { class: 'mono', text: issue.code }) : null,
    el('span', { class: 'build-issue-text', text: issue.message }),
    where ? (open ? el('button', { class: 'link mono', type: 'button', text: where }) : el('span', { class: 'muted mono', text: where })) : null);
    return row;
  };

  function renderResult() {
    const entry = shown();
    refs.result.replaceChildren();
    if (!entry || ACTIVE.has(entry.snap.state)) return;
    const { snap } = entry;
    const result = snap.result;
    const nodes = [];

    if (snap.state === 'cancelled') nodes.push(el('p', { class: 'muted', text: t('build.result.cancelled') }));

    if (result && snap.kind === 'validate') {
      nodes.push(el('p', { class: 'build-verdict', 'data-ok': result.errors === 0 ? 'true' : 'false', text: t('build.result.validate', { errors: result.errors, warnings: result.warnings, infos: result.infos }) }));
      nodes.push(issueList(result.issues));
    } else if (result && snap.kind === 'pack') {
      if (result.success) {
        nodes.push(el('p', { class: 'build-verdict', 'data-ok': 'true', text: t('build.result.packed', { archive: result.archive, files: result.fileCount, size: sizeText(result.bytes) }) }));
        if (result.downloadUrl) {
          nodes.push(el('p', {}, el('a', { class: 'link', 'data-testid': 'build-download', href: result.downloadUrl, download: result.archive || '', text: t('build.result.download', { archive: result.archive }) })));
        }
      } else {
        nodes.push(el('p', { class: 'build-verdict', 'data-ok': 'false', text: t('build.result.notPacked') }));
      }
      nodes.push(issueList(result.issues));
    } else if (result && snap.kind === 'smoke') {
      nodes.push(el('p', {
        class: 'build-verdict', 'data-testid': 'build-verdict', 'data-ok': result.passed ? 'true' : 'false',
        text: result.passed ? t('build.result.smokePassed') : t('build.result.smokeFailed', { reason: result.failureReason || '' }),
      }));
      nodes.push(...gameLog(result));
    } else if (result && snap.kind === 'run') {
      nodes.push(el('p', { class: 'build-verdict', 'data-ok': result.exitCode === 0 ? 'true' : 'false', 'data-testid': 'build-exit-code', text: t('build.result.runExit', { code: result.exitCode }) }));
      if (Array.isArray(result.diagnostics)) nodes.push(...gameLog(result));
    } else if (result && snap.kind === 'dump') {
      nodes.push(el('p', { class: 'build-verdict', 'data-ok': snap.state === 'succeeded' ? 'true' : 'false', 'data-testid': 'build-dump',
        text: snap.state === 'succeeded' ? t('build.result.dump', { added: result.added, changed: result.changed, removed: result.removed }) : snap.error || '' }));
      if (Array.isArray(result.diagnostics) && result.diagnostics.length) nodes.push(...gameLog({ ...result, warningCount: 0 }));
    }

    refs.result.replaceChildren(...nodes);
  }

  /** What the game's log said (smoke test output, or factorio-current.log after a run): the counts and every message. */
  function gameLog(result) {
    const rows = gameIssuesOf(result).map((issue) => issueRow(issue));
    return [
      el('p', { class: 'muted small', text: t('build.result.smokeCounts', { errors: result.errorCount ?? 0, warnings: result.warningCount ?? 0 }) }),
      ...(rows.length ? [el('ul', { class: 'build-issues' }, rows)] : []),
    ];
  }

  /** Log diagnostics as issues; a location is kept only when it is a file of this mod (it can be opened). */
  function gameIssuesOf(result) {
    const own = modName();
    return (Array.isArray(result?.diagnostics) ? result.diagnostics : []).map((d) => ({
      severity: d.severity, code: d.category || 'game', message: d.message,
      file: d.file && d.mod && d.mod === own ? d.file : null, line: d.line,
    }));
  }

  function issueList(issues) {
    if (!Array.isArray(issues) || issues.length === 0) return el('p', { class: 'muted', text: t('build.result.noIssues') });
    return el('ul', { class: 'build-issues' }, issues.map((issue) => issueRow(issue)));
  }

  function sizeText(bytes) {
    if (!Number.isFinite(bytes)) return '?';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
  }

  function renderHistory() {
    const signature = s.history.map((job) => `${job.id}:${(s.entries.get(job.id) ?? job).snap?.state ?? job.state}`).join('|') + `#${s.shownId}`;
    if (signature === s.historySig && !s.historyForce) return;
    notify();
    s.historySig = signature;
    s.historyForce = false;
    if (!s.history.length) { refs.history.replaceChildren(el('li', { class: 'muted', text: t('build.history.empty') })); return; }
    refs.history.replaceChildren(...s.history.map((job) => {
      const entry = s.entries.get(job.id);
      const state = entry ? entry.snap.state : job.state;
      let time = job.startedAt;
      try { time = new Date(job.startedAt).toLocaleTimeString(getLanguage()); } catch { /* keep the ISO text */ }
      return el('li', {
        class: 'build-history-item' + (job.id === s.shownId ? ' current' : ''),
        'data-testid': 'build-history-item', 'data-job-id': job.id, 'data-kind': job.kind, 'data-state': state,
        'aria-current': job.id === s.shownId ? 'true' : null,
        onclick: () => select(job.id),
      }, el('button', { type: 'button', class: 'build-history-button' },
        el('span', { class: 'build-history-kind', text: kindLabel(job.kind) }),
        el('span', { class: `build-history-state ${state}`, text: t(`build.state.${state}`) }),
        el('span', { class: 'muted small', text: time })));
    }));
  }

  function renderAll() {
    s.historyForce = true;
    renderEnv();
    renderGet();
    renderButtons();
    renderStatus();
    renderLog(true);
    renderResult();
    renderHistory();
  }

  // ------------------------------------------------------------ data

  function remember(snap) {
    let entry = s.entries.get(snap.id);
    if (!entry) {
      entry = { snap, lines: [], lastN: 0, loaded: false, trimmed: false };
      s.entries.set(snap.id, entry);
    }
    const { lines, ...rest } = snap;
    entry.snap = { ...entry.snap, ...rest };
    if (Array.isArray(lines)) {
      entry.loaded = true;
      for (const line of lines) {
        if (line.n <= entry.lastN) continue;
        entry.lines.push({ n: line.n, text: cleanLine(line.text) });
        entry.lastN = line.n;
      }
      if (entry.lines.length > MAX_LOG_LINES) {
        entry.lines.splice(0, entry.lines.length - MAX_LOG_LINES);
        entry.trimmed = true;
      }
    }
    return entry;
  }

  // ------------------------------------------------------------ Factorio location dialog

  const pathState = { pending: false, seq: 0, dir: null, parent: null, separator: '/' };

  /** Typed or pasted: trim, and drop one pair of quotes ("C:\Games\Factorio" copied from Explorer). */
  const normalizePath = (value) => {
    let text = String(value ?? '').trim();
    if (text.length >= 2 && ((text[0] === '"' && text.at(-1) === '"') || (text[0] === "'" && text.at(-1) === "'"))) text = text.slice(1, -1).trim();
    return text;
  };

  const joinPath = (dir, name, separator) => (dir === '' ? name : dir.endsWith('/') || dir.endsWith('\\') ? dir + name : dir + separator + name);
  const parentOfText = (text) => {
    const cut = Math.max(text.lastIndexOf('/'), text.lastIndexOf('\\'));
    return cut <= 0 ? '' : text.slice(0, cut + (/^[A-Za-z]:$/.test(text.slice(0, cut)) ? 1 : 0));
  };

  function renderPathButtons() {
    refs.instConfirm.disabled = pathState.pending || normalizePath(refs.pathInput.value) === '';
    refs.instRescan.disabled = pathState.pending;
    refs.pathUp.disabled = pathState.pending || pathState.dir === null || pathState.dir === '';
  }

  function setPathError(text) {
    refs.pathError.textContent = text || '';
    refs.pathError.hidden = !text;
  }

  function renderListing(listing) {
    pathState.dir = listing.path;
    pathState.parent = typeof listing.parent === 'string' ? listing.parent : '';  // a drive / filesystem root: Up leads to the roots view
    pathState.separator = listing.separator === '\\' ? '\\' : '/';
    refs.pathCurrent.textContent = listing.path === '' ? t('build.path.roots') : listing.path;
    refs.pathCurrent.dataset.path = listing.path;
    const rows = [];
    for (const entry of Array.isArray(listing.entries) ? listing.entries : []) {
      if (!entry || typeof entry.name !== 'string' || (entry.type !== 'dir' && entry.type !== 'file')) continue;
      const row = el('button', {
        type: 'button', class: 'build-path-entry', 'data-testid': 'build-path-entry',
        'data-name': entry.name, 'data-type': entry.type, 'data-factorio': entry.factorio === true ? 'true' : 'false',
        title: entry.type === 'dir' ? t('build.path.dir') : t('build.path.exe'),
      });
      row.append(el('span', { class: 'build-path-entry-name', text: entry.name }));
      row.addEventListener('click', () => chooseEntry(entry));
      rows.push(el('li', {}, row));
    }
    refs.pathList.replaceChildren(...rows);
    refs.pathList.scrollTop = 0;
    refs.pathStatus.textContent = listing.truncated ? t('build.path.truncated') : rows.length === 0 ? t('build.path.empty') : '';
    renderPathButtons();
  }

  /** @returns {Promise<boolean>} whether the listing was shown. With `quiet`, a failure is not reported (used to find a start folder). */
  async function listDir(path, quiet = false) {
    const seq = ++pathState.seq;
    pathState.pending = true;
    refs.pathStatus.textContent = t('build.path.loading');
    renderPathButtons();
    try {
      const listing = await api(`/api/fs/list?path=${enc(path)}`);
      if (seq !== pathState.seq) return false;
      pathState.pending = false;
      setPathError('');
      renderListing(listing);
      return true;
    } catch (error) {
      if (seq !== pathState.seq) return false;
      pathState.pending = false;
      refs.pathStatus.textContent = '';
      if (!quiet) setPathError(t('build.path.listError', { message: error.message }));
      renderPathButtons();
      return false;
    }
  }

  function chooseEntry(entry) {
    if (pathState.pending || pathState.dir === null) return;
    const full = joinPath(pathState.dir, entry.name, pathState.separator);
    if (entry.type === 'dir') {
      refs.pathInput.value = full;
      listDir(full);
    } else if (entry.factorio === true) {
      refs.pathInput.value = full;
      setPathError('');
      for (const row of refs.pathList.children) row.firstChild.classList.toggle('selected', row.firstChild.dataset.name === entry.name);
      renderPathButtons();
    }
  }

  async function openBrowser() {
    refs.pathBrowser.hidden = false;
    refs.pathBrowse.setAttribute('aria-expanded', 'true');
    const typed = normalizePath(refs.pathInput.value);
    // Start in the typed folder, else the folder that holds it (a typed executable), else the list of drives / roots.
    if (typed !== '' && (await listDir(typed, true))) return;
    if (typed !== '' && (await listDir(parentOfText(typed), true))) return;
    await listDir('');
  }

  function closeBrowser() {
    refs.pathBrowser.hidden = true;
    refs.pathBrowse.setAttribute('aria-expanded', 'false');
    pathState.seq++;
    pathState.pending = false;
  }

  // ------------------------------------------------------------ installations dialog and picker

  const inst = { list: [], invalid: [], loaded: false };

  function installRow(item, invalid) {
    const sources = invalid ? ['ui'] : (Array.isArray(item.sources) ? item.sources.filter((x) => SOURCES.has(x)) : []);
    const name = item.label || null;
    const row = el('li', {
      class: 'build-install-item' + (invalid ? ' invalid' : ''), 'data-testid': 'build-install-row',
      'data-id': item.id ?? '', 'data-version': invalid ? '' : (item.version ?? ''), 'data-source': sources.join(' '),
      'data-invalid': invalid ? 'true' : null,
    });
    const head = el('div', { class: 'build-install-head' },
      el('strong', { text: invalid ? t('build.install.missing') : (item.version || unknownVersion()) }),
      name ? el('span', { class: 'build-install-name', text: name }) : null,
      ...sources.map((source) => el('span', { class: 'badge', 'data-source': source, text: t(`build.path.source.${source}`) })),
      !invalid && item.headless === true ? el('span', { class: 'badge build-get-badge', title: t('build.get.why'), text: t('build.get.headlessBadge') }) : null,
      !invalid && item.managedGame === true ? el('span', { class: 'badge build-get-badge', text: t('build.game.badge') }) : null);
    const body = el('div', { class: 'build-install-body' }, head, el('div', { class: 'mono build-install-path', text: item.path }),
      invalid && item.error ? el('div', { class: 'small build-install-problem', text: item.error }) : null);
    const managed = !invalid && (item.headless === true || item.managedGame === true);
    const removable = invalid || item.removable === true || managed;
    const remove = removable
      ? el('button', { type: 'button', class: 'small', 'data-testid': 'build-install-remove', 'aria-label': t('build.install.removeLabel', { name: name || item.path }), text: t('build.install.remove') })
      : null;
    if (remove) remove.addEventListener('click', () => (managed ? removeManaged(item) : removeInstall(item)));
    row.append(body, ...(remove ? [remove] : []));
    return row;
  }

  function renderInstallList() {
    const rows = [...inst.list.map((item) => installRow(item, false)), ...inst.invalid.map((item) => installRow(item, true))];
    refs.instList.replaceChildren(...(rows.length ? rows : [el('li', { class: 'muted', text: inst.loaded ? t('build.install.empty') : t('build.path.loading') })]));
  }

  /** Takes the list from an API answer: refreshes the dialog, the picker and the environment line. */
  function applyInstalls(result) {
    inst.list = Array.isArray(result?.installations) ? result.installations : [];
    inst.invalid = Array.isArray(result?.invalid) ? result.invalid : [];
    inst.loaded = true;
    s.installs = inst.list;
    renderInstallList();
  }

  function dropUnknownChoice() {
    if (s.manualId && !s.installs.some((i) => i.id === s.manualId)) {
      s.manualId = null;
      if (s.modId) rememberChoice(s.modId, null);
    }
  }

  async function changed(result) {
    applyInstalls(result);
    dropUnknownChoice();
    await refreshEnv(true);
  }

  async function installCall(url, options, failKey) {
    if (pathState.pending) return;
    pathState.seq++;
    pathState.pending = true;
    setPathError('');
    renderPathButtons();
    try {
      const result = await api(url, options);
      pathState.pending = false;
      renderPathButtons();
      return result;
    } catch (error) {
      pathState.pending = false;
      setPathError(t(failKey, { message: error.message }));
      renderPathButtons();
      return null;
    }
  }

  async function removeInstall(item) {
    const result = await installCall(`/api/factorio/installations/${enc(item.id)}`, { method: 'DELETE' }, 'build.install.removeError');
    if (result) await changed(result);
  }

  async function rescan() {
    refs.instRescan.disabled = true;
    const result = await installCall('/api/factorio/installations/rescan', { method: 'POST' }, 'build.install.loadError');
    if (result) await changed(result);
  }

  function showAddForm(visible) {
    refs.addForm.hidden = !visible;
    refs.instAdd.setAttribute('aria-expanded', visible ? 'true' : 'false');
    if (!visible) closeBrowser();
    if (visible) {
      refs.pathInput.value = '';
      refs.instLabel.value = '';
      pathState.dir = null;
      pathState.parent = null;
      setPathError('');
      renderPathButtons();
      refs.pathInput.focus();
    }
  }

  async function openInstallDialog() {
    if (refs.installDialog.open) return;
    setPathError('');
    showAddForm(false);
    inst.loaded = s.installs.length > 0;
    inst.list = s.installs;
    renderInstallList();
    refs.installDialog.showModal();
    refs.instClose.focus();
    loadAvailable();
    try {
      applyInstalls(await api('/api/factorio/installations'));
    } catch (error) {
      setPathError(t('build.install.loadError', { message: error.message }));
    }
  }

  async function submitAdd() {
    const path = normalizePath(refs.pathInput.value);
    if (path === '') return;
    const label = refs.instLabel.value.trim();
    const result = await installCall('/api/factorio/installations', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path, label: label || null }),
    }, 'build.path.error');
    if (!result) return;
    showAddForm(false);
    await changed(result);
  }

  refs.installManage.addEventListener('click', openInstallDialog);
  refs.instAdd.addEventListener('click', () => showAddForm(refs.addForm.hidden));
  refs.instAddCancel.addEventListener('click', () => showAddForm(false));
  refs.instRescan.addEventListener('click', rescan);
  refs.instClose.addEventListener('click', () => refs.installDialog.close());
  refs.pathInput.addEventListener('input', () => { setPathError(''); renderPathButtons(); });
  refs.pathBrowse.addEventListener('click', () => { if (refs.pathBrowser.hidden) openBrowser(); else closeBrowser(); });
  refs.pathUp.addEventListener('click', () => {
    if (pathState.parent === null || pathState.pending) return;
    refs.pathInput.value = pathState.parent;
    listDir(pathState.parent);
  });
  refs.addForm.addEventListener('submit', (event) => {
    event.preventDefault();
    submitAdd();
  });
  refs.installDialog.addEventListener('close', () => { pathState.seq++; pathState.pending = false; showAddForm(false); });
  refs.installSelect.addEventListener('change', () => {
    s.manualId = refs.installSelect.value || null;
    if (s.modId) rememberChoice(s.modId, s.manualId);
    refreshEnv(true);
  });

  // ------------------------------------------------------------ install a missing Factorio (headless server)

  const get = { available: null, availableError: null, loading: false, install: null, timer: 0, startError: null };
  const installing = () => !!get.install && (get.install.state === 'downloading' || get.install.state === 'unpacking');
  /** "2.0" from the mod's factorio_version, or null. */
  const wantedBranch = () => {
    const match = /^\s*(\d+)\.(\d+)/.exec(s.env?.resolution?.modVersion || '');
    return match ? `${match[1]}.${match[2]}` : null;
  };
  /** What to install for this mod: the branch, or the exact release its "base = / <= x" dependency pins (decided by the server). */
  const wantedSpec = (branch) => {
    const spec = s.env?.resolution?.installSpec;
    return typeof spec === 'string' && branch && spec.startsWith(`${branch}.`) ? spec : branch;
  };

  async function loadAvailable(force = false) {
    if (get.loading || (get.available && !force)) return;
    get.loading = true;
    get.availableError = null;
    renderGet();
    try { get.available = await api('/api/factorio/available'); } catch (error) { get.availableError = error.message; }
    get.loading = false;
    renderGet();
  }

  async function startInstall(spec, game = false) {
    if (installing()) return;
    get.startError = null;
    try {
      const answer = await api('/api/factorio/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: spec, game }) });
      get.install = answer?.install ?? null;
    } catch (error) {
      if (error.status === 409 && error.body?.install) get.install = error.body.install; // another install is running: follow it
      else get.startError = error.message;
    }
    renderGet();
    if (installing()) pollInstall();
  }

  async function pollInstall() {
    clearTimeout(get.timer);
    const before = get.install?.state ?? null;
    let answer;
    try { answer = await api('/api/factorio/install'); } catch { get.timer = setTimeout(pollInstall, INSTALL_POLL_MS * 3); return; }
    get.install = answer?.install ?? null;
    if (installing()) get.timer = setTimeout(pollInstall, INSTALL_POLL_MS);
    else if (get.install?.state === 'succeeded' && (before === 'downloading' || before === 'unpacking')) await installedNow();
    renderGet();
  }

  /** A new installation exists: scan again, so the environment line, the picker, the buttons and the dialog list follow. */
  async function installedNow() {
    get.available = null;
    try { applyInstalls(await api('/api/factorio/installations/rescan', { method: 'POST' })); } catch { /* refreshEnv reports problems */ }
    await refreshEnv(true);
    if (refs.installDialog.open) loadAvailable();
  }

  async function cancelInstall() {
    try { await api('/api/factorio/install/cancel', { method: 'POST' }); } catch { /* it may have just finished */ }
    pollInstall();
  }

  async function removeManaged(item) {
    if (!item.version) return;
    const query = item.managedGame === true ? '?game=true' : '';
    const result = await installCall(`/api/factorio/install/${enc(item.version)}${query}`, { method: 'DELETE' }, 'build.get.removeError');
    if (!result) return;
    get.available = null;
    const list = await installCall('/api/factorio/installations/rescan', { method: 'POST' }, 'build.install.loadError');
    if (list) await changed(list);
    loadAvailable();
  }

  // ------------------------------------------------------------ the factorio.com account for the full game

  const account = () => get.available?.game?.account ?? null;
  const gameProblem = () => get.available?.game?.platformProblem ?? null;
  const hasAccount = () => !!account()?.source;
  const acc = { busy: false, error: null };
  const accUser = el('input', { type: 'text', id: 'build-account-user', 'data-testid': 'build-account-user', maxlength: '100', autocomplete: 'off', spellcheck: 'false' });
  const accToken = el('input', { type: 'password', id: 'build-account-token', 'data-testid': 'build-account-token', maxlength: '128', autocomplete: 'off', spellcheck: 'false' });

  async function saveAccount(body) {
    if (acc.busy) return;
    acc.busy = true;
    acc.error = null;
    renderGet();
    try {
      const answer = await api('/api/factorio/account', { method: body ? 'PUT' : 'DELETE', headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
      if (get.available?.game) get.available.game.account = answer?.account ?? null;
      accToken.value = ''; // the token is not kept in the page once it is saved
    } catch (error) {
      acc.error = error.message;
    }
    acc.busy = false;
    renderGet();
  }

  function accountSourceText(a) {
    return a.source === 'env' ? t('build.game.sourceEnv') : a.source === 'game' ? t('build.game.sourceGame') : t('build.game.sourceStudio');
  }

  /** The account line, or the form that sets one, in the installations dialog. */
  function accountBox() {
    const a = account();
    if (!a) return [];
    if (a.source) {
      return [el('div', { class: 'build-get-row', 'data-testid': 'build-account' },
        el('span', { text: t('build.game.account', { name: a.username, source: accountSourceText(a) }) }),
        a.source !== 'env' && el('button', { type: 'button', class: 'small', 'data-testid': 'build-account-forget', disabled: acc.busy ? true : null,
          onclick: () => saveAccount(null), text: t('build.game.forget') })),
        acc.error && el('p', { class: 'small build-get-error', text: acc.error })];
    }
    const submit = (event) => {
      event.preventDefault();
      if (accUser.value.trim() && accToken.value.trim()) saveAccount({ username: accUser.value.trim(), token: accToken.value.trim() });
    };
    return [
      el('p', { class: 'muted small', text: t('build.game.needAccount') }),
      a.gameUsername && el('div', { class: 'build-get-row' },
        el('button', { type: 'button', class: 'files-btn', 'data-testid': 'build-account-use-game', disabled: acc.busy ? true : null,
          onclick: () => saveAccount({ useGame: true }), text: t('build.game.useGame', { name: a.gameUsername }) })),
      el('form', { class: 'build-account-form', 'data-testid': 'build-account-form', novalidate: true, autocomplete: 'off', onsubmit: submit },
        el('label', { for: 'build-account-user', text: t('build.game.username') }), accUser,
        el('label', { for: 'build-account-token', text: t('build.game.token') }), accToken,
        el('button', { type: 'submit', class: 'small', 'data-testid': 'build-account-save', disabled: acc.busy ? true : null, text: t('build.game.save') })),
      el('p', { class: 'muted small', text: t('build.game.tokenHint') }),
      acc.error && el('p', { class: 'small build-get-error', text: acc.error }),
    ];
  }

  /** Progress, result or error of the current install, for both places it is shown. */
  function installView() {
    const i = get.install;
    if (!i) return null;
    const percent = i.total ? Math.min(100, Math.floor((i.received * 100) / i.total)) : null;
    const text = i.state === 'downloading'
      ? (percent !== null ? t('build.get.downloading', { version: i.version, percent }) : t('build.get.downloadingSize', { version: i.version, mb: Math.floor(i.received / (1024 * 1024)) }))
      : i.state === 'unpacking' ? t('build.get.unpacking', { version: i.version })
        : i.state === 'succeeded' ? t(i.alreadyInstalled ? 'build.get.already' : 'build.get.done', { version: i.version })
          : i.state === 'cancelled' ? t('build.get.cancelled', { version: i.version })
            : t('build.get.failed', { version: i.version, message: i.error || '' });
    const fill = el('span', { class: 'build-get-fill' });
    fill.style.setProperty('width', `${i.state === 'downloading' ? (percent ?? 0) : 100}%`); // CSSOM, allowed under the strict CSP
    const cancel = installing() && el('button', { type: 'button', class: 'small', 'data-testid': 'build-get-cancel', onclick: cancelInstall, text: t('build.get.cancel') });
    return el('div', { class: `build-get-progress ${i.state}`, 'data-state': i.state },
      el('div', { class: 'build-get-row' }, el('span', { 'data-testid': 'build-get-text', text }), cancel),
      installing() && el('div', { class: `build-get-bar${percent === null || i.state === 'unpacking' ? ' busy' : ''}`, 'aria-hidden': 'true' }, fill));
  }

  /** The two install boxes: under the hint (the mod's own version is missing) and in the installations dialog. */
  function renderGet() {
    const branch = wantedBranch();
    const missing = !!s.env && typeof s.env === 'object' && s.env.resolution?.mode === 'none' && !!branch;
    const forThisMod = get.install && branch && get.install.version.startsWith(`${branch}.`);
    refs.getBox.hidden = !(missing || installing() || (forThisMod && get.install.state !== 'succeeded'));
    if (!refs.getBox.hidden && !get.available && !get.loading && !get.availableError) loadAvailable();
    refs.getBox.replaceChildren(...mainBox(branch).filter(Boolean)); // replaceChildren would print null / false as text
    if (refs.installDialog.open) refs.getDialogBox.replaceChildren(...dialogBox().filter(Boolean));
  }

  function availabilityProblem() {
    if (get.loading && !get.available) return [el('p', { class: 'muted small', text: t('build.get.loading') })];
    if (get.availableError) {
      return [el('p', { class: 'small build-get-error', text: t('build.get.loadError', { message: get.availableError }) }),
        el('button', { type: 'button', class: 'small', onclick: () => loadAvailable(true), text: t('build.get.retry') })];
    }
    if (get.available?.platformProblem) {
      return [el('p', { class: 'small', 'data-testid': 'build-get-unavailable' },
        el('strong', { text: t('build.get.unavailable') + ' ' }), el('span', { class: 'muted', text: get.available.platformProblem }))];
    }
    return null;
  }

  function mainBox(branch) {
    const progress = installView();
    const spec = wantedSpec(branch);
    if (installing() || (progress && get.install.state !== 'succeeded')) {
      return [progress, !installing() && branch ? el('button', { type: 'button', class: 'small', onclick: () => startInstall(spec), text: t('build.get.retry') }) : null];
    }
    const problem = availabilityProblem();
    const gameOk = !!get.available && !gameProblem();
    if (problem && !gameOk) return problem;
    if (!get.available || !branch) return [];
    const release = (get.available.branches || []).find((b) => b.branch === branch);
    if (!release) return [el('p', { class: 'small', text: t('build.get.noRelease', { branch }) })];
    const version = spec === branch ? release.newest : spec;
    const headlessOk = !get.available.platformProblem;
    return [
      el('div', { class: 'build-get-row' },
        headlessOk && el('button', { type: 'button', class: 'files-btn', 'data-testid': 'build-get-install', onclick: () => startInstall(spec) },
          el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: '↓' }), el('span', { text: t('build.get.install', { version }) })),
        gameOk && hasAccount() && el('button', { type: 'button', class: 'files-btn', 'data-testid': 'build-get-install-game', onclick: () => startInstall(spec, true) },
          el('span', { class: 'files-btn-icon', 'aria-hidden': 'true', text: '↓' }), el('span', { text: t('build.game.install', { version }) })),
        gameOk && !hasAccount() && el('button', { type: 'button', class: 'files-btn', 'data-testid': 'build-get-account', onclick: openInstallDialog, text: t('build.game.setUp') })),
      el('p', { class: 'muted small', text: headlessOk ? t('build.get.why') : t('build.game.why') }),
      get.startError && el('p', { class: 'small build-get-error', text: t('build.get.startError', { message: get.startError }) }),
    ];
  }

  function dialogBox() {
    const problem = availabilityProblem();
    const progress = installView();
    const gameOk = !!get.available && !gameProblem();
    if (problem && !gameOk) return [...problem, gameProblem() && el('p', { class: 'muted small', text: gameProblem() }), progress];
    if (!get.available) return [progress];
    const headlessOk = !get.available.platformProblem;
    const games = new Set(get.available.game?.installed || []);
    const installed = new Set(get.available.installed || []);
    const branches = get.available.branches || [];
    const previous = refs.getSelect.value;
    refs.getSelect.replaceChildren(...branches.map((b) => {
      const marks = [b.newest === get.available.stable && t('build.get.stable'), b.newest === get.available.experimental && t('build.get.experimental'),
        installed.has(b.newest) && t('build.get.installedMark'), games.has(b.newest) && t('build.game.installedMark')].filter(Boolean);
      return el('option', { value: b.branch, text: `${b.branch}: ${b.newest}${marks.length ? ` (${marks.join(', ')})` : ''}` });
    }));
    const branch = wantedBranch();
    refs.getSelect.value = branches.some((b) => b.branch === previous) ? previous : branches.some((b) => b.branch === branch) ? branch : (branches[0]?.branch ?? '');
    return [
      el('p', { class: 'muted small', text: headlessOk ? t('build.get.why') : get.available.platformProblem }),
      el('div', { class: 'build-get-row' },
        el('label', { for: 'build-get-select', text: t('build.get.branchLabel') }), refs.getSelect,
        headlessOk && el('button', { type: 'button', class: 'primary', 'data-testid': 'build-get-install-selected', disabled: installing() || !branches.length ? true : null,
          onclick: () => startInstall(refs.getSelect.value), text: t('build.get.installSelected') }),
        gameOk && el('button', { type: 'button', class: headlessOk ? null : 'primary', 'data-testid': 'build-get-install-game-selected',
          disabled: installing() || !branches.length || !hasAccount() ? true : null,
          onclick: () => startInstall(refs.getSelect.value, true), text: t('build.game.installSelected') })),
      get.startError && el('p', { class: 'small build-get-error', text: t('build.get.startError', { message: get.startError }) }),
      progress,
      gameOk && el('h4', { class: 'build-get-subheading', text: t('build.game.heading') }),
      gameOk && el('p', { class: 'muted small', text: t('build.game.why') }),
      ...(gameOk ? accountBox() : [gameProblem() && el('p', { class: 'muted small', text: gameProblem() })]),
    ];
  }

  /** Loads the candidate installations and how the mod's installation is chosen. */
  async function refreshEnv(keepCurrent = false) {
    const seq = s.seq;
    const modId = s.modId;
    try {
      const list = await api('/api/factorio/installations');
      if (seq !== s.seq) return;
      s.installs = Array.isArray(list?.installations) ? list.installations : [];
      dropUnknownChoice();
    } catch { /* the environment call below reports the problem */ }
    try {
      const query = modId ? `?modId=${enc(modId)}${s.manualId ? `&installation=${enc(s.manualId)}` : ''}` : '';
      const result = await api(`/api/environment${query}`);
      if (seq !== s.seq) return;
      s.env = { ...(result?.factorio ?? { found: false, path: null, error: null }), resolution: result?.resolution ?? null };
    } catch (error) {
      if (seq !== s.seq) return;
      if (!keepCurrent || !s.env || typeof s.env !== 'object') s.env = { found: false, path: null, error: error.message, resolution: null };
    }
    renderEnv();
    renderButtons();
  }

  async function refreshHistory() {
    const seq = s.seq;
    const modId = s.modId;
    let list;
    try { list = await api(`/api/jobs?modId=${enc(modId)}`); } catch { return; }
    if (seq !== s.seq || !Array.isArray(list)) return;
    s.history = list;
    for (const job of list) {
      const known = s.entries.get(job.id);
      // The job being polled is updated by the poll only: it also has to fetch the rest of its log before it counts as finished.
      if (known && job.id !== s.runningId) known.snap = { ...known.snap, ...job };
    }
    renderHistory();
    return list;
  }

  /** Re-attaches to a job that is still running after a reload (or after a "busy" answer). */
  async function attachActive(list) {
    const active = (list || s.history).find((job) => ACTIVE.has(job.state));
    if (!active) return;
    if (s.runningId !== active.id) {
      s.runningId = active.id;
      s.entries.delete(active.id);
    }
    if (!s.shownId || !isActive(s.entries.get(s.shownId))) s.shownId = active.id;
    const entry = s.entries.get(active.id) ?? remember({ ...active, lines: [] });
    entry.loaded = false;
    renderAll();
    schedulePoll(0);
  }

  function schedulePoll(delay = POLL_MS) {
    clearTimeout(s.pollTimer);
    s.pollTimer = 0;
    if (!activeNow()) return;
    s.pollTimer = setTimeout(poll, delay);
  }

  async function poll() {
    s.pollTimer = 0;
    if (!s.runningId) return;
    const id = s.runningId;
    const seq = s.seq;
    const entry = s.entries.get(id);
    try {
      const snap = await api(`/api/jobs/${enc(id)}?after=${entry ? entry.lastN : 0}`);
      if (seq !== s.seq || s.runningId !== id) return;
      s.pollFailures = 0;
      const before = entry ? entry.snap.state : null;
      const updated = remember(snap);
      if (s.message?.key === 'build.error.poll') s.message = null;
      if (!ACTIVE.has(updated.snap.state)) {
        s.runningId = null;
        s.cancelling = false;
        await refreshHistory();
        if (seq !== s.seq) return;
      }
      if (s.shownId === id) {
        renderStatus();
        renderLog();
        if (before !== updated.snap.state) renderResult();
      }
      renderButtons();
      renderHistory();
    } catch (error) {
      if (seq !== s.seq) return;
      if (error?.status === 404) { // evicted or unknown: nothing more to follow
        s.runningId = null;
        setMessageText(error.message);
        await refreshHistory();
        renderButtons();
        return;
      }
      s.pollFailures += 1;
      setMessage('build.error.poll', { message: error?.message ?? '' });
    }
    if (seq === s.seq) schedulePoll(s.pollFailures ? Math.min(POLL_MS * 2 ** s.pollFailures, MAX_POLL_DELAY_MS) : POLL_MS);
  }

  async function select(id) {
    if (s.shownId === id) return;
    const seq = s.seq;
    s.shownId = id;
    s.stick = true;
    s.message = null;
    const entry = s.entries.get(id);
    renderAll();
    if (entry && entry.loaded) return;
    try {
      const snap = await api(`/api/jobs/${enc(id)}?after=0`);
      if (seq !== s.seq) return;
      const known = s.entries.get(id);
      if (known) { known.lines = []; known.lastN = 0; }
      remember(snap);
      if (s.shownId === id) { s.renderedId = null; renderStatus(); renderLog(true); renderResult(); }
      renderHistory();
    } catch (error) {
      if (seq !== s.seq) return;
      setMessageText(error.message);
    }
  }

  function inputs(kind) {
    const body = { kind };
    if (kind === 'smoke') {
      const ticksText = refs.ticks.value.trim();
      const ticks = Number(ticksText);
      if (!/^\d+$/.test(ticksText) || !Number.isSafeInteger(ticks) || ticks < 1 || ticks > MAX_TICKS) return { error: 'build.error.ticks' };
      const seedText = refs.seed.value.trim();
      if (!/^-?\d+$/.test(seedText) || !Number.isSafeInteger(Number(seedText)) || Math.abs(Number(seedText)) > 2147483647) return { error: 'build.error.seed' };
      body.ticks = ticks;
      body.seed = Number(seedText);
    }
    if (kind === 'pack') body.force = refs.force.checked;
    return { body };
  }

  async function start(kind, origin = 'build') {
    if (s.starting || activeNow() || !s.modId) return;
    const { body, error } = inputs(kind);
    if (error) { setMessage(error); return; }
    if ((kind === 'smoke' || kind === 'run') && s.manualId) body.installation = s.manualId;

    const seq = s.seq;
    s.starting = true;
    s.message = null;
    renderButtons();
    renderStatus();
    try {
      const snap = await api(`/api/mods/${enc(s.modId)}/jobs`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      if (seq !== s.seq) return;
      s.entries.delete(snap.id);
      remember(snap);
      s.shownId = snap.id;
      s.runningId = ACTIVE.has(snap.state) ? snap.id : null;
      s.stick = true;
      s.starting = false;
      renderAll();
      for (const listener of [...startedListeners]) { try { listener({ kind: snap.kind, id: snap.id, origin }); } catch { /* ignore */ } }
      await refreshHistory();
      if (seq !== s.seq) return;
      if (s.runningId) schedulePoll(0);
      else if (s.shownId === snap.id) { renderLog(true); renderResult(); }
    } catch (failure) {
      if (seq !== s.seq) return;
      s.starting = false;
      if (failure?.status === 409) {
        setMessage('build.busy');
        const list = await refreshHistory();
        if (seq === s.seq && list) await attachActive(list);
      } else if (failure?.status === 403 && /token/i.test(failure.message || '')) {
        setMessage('common.sessionExpired');
      } else if (failure?.status === 400 && body.installation) {
        setMessage('build.error.request', { message: failure?.message ?? '' });
        dropUnknownChoice();
        refreshEnv(true);
      } else {
        setMessage('build.error.request', { message: failure?.message ?? '' });
      }
      renderButtons();
    }
  }

  async function cancel() {
    const id = s.runningId;
    if (!id || s.cancelling) return;
    const seq = s.seq;
    s.cancelling = true;
    renderButtons();
    try {
      const snap = await api(`/api/jobs/${enc(id)}/cancel`, { method: 'POST' });
      if (seq !== s.seq) return;
      remember(snap);
    } catch (error) {
      if (seq !== s.seq) return;
      if (error?.status !== 409) setMessageText(error.message);
      try { remember(await api(`/api/jobs/${enc(id)}?after=${s.entries.get(id)?.lastN ?? 0}`)); } catch { /* the poll will report it */ }
    }
    s.cancelling = false;
    const entry = s.entries.get(id);
    if (entry && !ACTIVE.has(entry.snap.state)) s.runningId = null;
    await refreshHistory();
    if (seq !== s.seq) return;
    if (s.shownId === id) { renderStatus(); renderLog(); renderResult(); }
    renderButtons();
    renderHistory();
    schedulePoll();
  }

  function reset() {
    s.seq += 1;
    clearTimeout(s.pollTimer);
    Object.assign(s, {
      modId: null, editable: false, env: null, installs: [], manualId: null, history: [], shownId: null, runningId: null, message: null,
      starting: false, cancelling: false, pollTimer: 0, pollFailures: 0, stick: true, renderedId: null, renderedN: 0, historySig: '',
    });
    s.entries.clear();
    refs.logText.data = '';
    renderAll();
  }

  /** What the other views need to know, as plain data. `latest` is the running job, else the newest job of the mod. */
  function getState() {
    const env = s.env && typeof s.env === 'object' ? s.env : null;
    const running = s.runningId ? s.entries.get(s.runningId)?.snap ?? null : null;
    const newest = s.history[0] ? { ...s.history[0], ...(s.entries.get(s.history[0].id)?.snap ?? {}) } : null;
    const latest = running ?? newest;
    return {
      modId: s.modId,
      editable: s.editable,
      busy: activeNow() || s.starting,
      running: activeNow(),
      cancelling: s.cancelling,
      factorioFound: !!env && env.found === true,
      factorioVersion: env?.version ?? null,
      envReady: !!env,
      mode: env?.resolution && MODES.has(env.resolution.mode) ? env.resolution.mode : 'none',
      latest: latest ? { id: latest.id, kind: latest.kind, state: latest.state, startedAt: latest.startedAt, finishedAt: latest.finishedAt ?? null } : null,
      shownId: s.shownId,
    };
  }

  /** Log lines of a job this view knows, or null. */
  function getLog(id) {
    const entry = id ? s.entries.get(id) : null;
    return entry ? { lines: entry.lines.map((l) => l.text), lastN: entry.lastN, truncated: !!(entry.snap.truncated || entry.lines.length >= MAX_LOG_LINES), loaded: entry.loaded } : null;
  }

  pollInstall();

  return {
    element: root,
    reset,
    start,
    cancel,
    getState,
    getLog,
    /** Problems the game reported in the newest smoke test or run of this mod (empty when it has none or is still running). */
    gameIssues() {
      const newest = s.history[0] ? s.entries.get(s.history[0].id)?.snap ?? s.history[0] : null;
      return newest && (newest.kind === 'smoke' || newest.kind === 'run') ? gameIssuesOf(newest.result) : [];
    },
    onChange(listener) { listeners.add(listener); return () => listeners.delete(listener); },
    onStarted(listener) { startedListeners.add(listener); return () => startedListeners.delete(listener); },

    /**
     * Makes this view follow `modId` without the Build tab being on screen: loads the environment and the job history and
     * re-attaches to a job that is still running. Polling continues while the tab is not shown.
     */
    activate(modId, editable) {
      if (modId !== s.modId) {
        reset();
        s.modId = modId;
        s.manualId = rememberedChoice(modId);
        s.env = 'checking';
        s.editable = !!editable;
        renderAll();
        const seq = s.seq;
        refreshEnv();
        refreshHistory().then((list) => { if (list && seq === s.seq && !activeNow()) return attachActive(list); return null; });
        return;
      }
      s.editable = !!editable;
      renderButtons();
    },

    /**
     * Called whenever the host draws the tab. A fresh opening (the element was detached, or another mod) reloads the
     * environment and the history; a redraw caused by a live update only refreshes what is on screen.
     */
    show(modId, editable) {
      const fresh = modId !== s.modId || !root.isConnected;
      if (modId !== s.modId) {
        reset();
        s.modId = modId;
        s.manualId = rememberedChoice(modId);
        s.env = 'checking';
      }
      s.editable = !!editable;
      // A redraw of the page detaches this subtree; a dialog that was open then comes back non-modal: make it modal again.
      if (refs.installDialog.open && !refs.installDialog.matches(':modal') && root.isConnected) { refs.installDialog.removeAttribute('open'); refs.installDialog.showModal(); }
      renderAll();
      if (fresh) {
        const seq = s.seq;
        refreshEnv();
        refreshHistory().then((list) => { if (list && seq === s.seq && !activeNow()) return attachActive(list); return null; });
      }
      if (activeNow()) schedulePoll(0);
    },

    /** The interface language changed: redraw all translated text in place. */
    retranslate() {
      for (const [node, key] of labels) node.textContent = t(key);
      refs.pathInput.setAttribute('placeholder', t('build.path.placeholder'));
      renderInstallList();
      renderAll();
    },

    /** Reloads the job list and re-attaches to a running job (used when another view may have started one). */
    async resync() {
      const seq = s.seq;
      const list = await refreshHistory();
      if (list && seq === s.seq && !activeNow()) await attachActive(list);
    },
  };
}
