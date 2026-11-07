// Persistent mod header: title / version / target Factorio on the left, Validate / Pack / Smoke test / Run (+ Cancel while a job
// of this mod runs), the resolved Factorio installation, the state of the latest job and the problem counter on the right.
// It is visible on every tab of the mod page. The buttons drive the same job controller as the Build tab (build-view.js), so both
// views always show the same job; the enable / disable rules are the Build tab's rules.

import { t } from '/i18n.js';
import { formatElapsed } from '/bottom-panel.js';

const KINDS = ['validate', 'pack', 'smoke', 'run'];

/**
 * @param {{ el: Function, mount: HTMLElement, buildView: object, onAddContent: () => void,
 *           onFactorio: () => void, onJob: () => void, onProblems: () => void }} host
 */
export function createActionBar({ el, mount, buildView, onFactorio, onJob, onProblems, onAddContent }) {
  const s = { overview: null, tick: 0 };

  const title = el('h2', { class: 'ab-title', 'data-testid': 'action-title' });
  const version = el('span', { class: 'ab-version', 'data-testid': 'action-version' });
  const target = el('span', { class: 'ab-target muted small', 'data-testid': 'action-target' });
  const heading = el('div', { class: 'ab-heading' }, title, version, target);

  const buttons = {};
  for (const kind of KINDS) {
    buttons[kind] = el('button', { type: 'button', 'data-testid': `action-${kind}`, onclick: () => buildView.start(kind, 'actionbar') });
  }
  const cancel = el('button', { type: 'button', class: 'ab-cancel', 'data-testid': 'action-cancel', onclick: () => buildView.cancel() });
  cancel.hidden = true;
  const addContent = el('button', { type: 'button', class: 'ab-add', 'data-testid': 'action-add-content', onclick: () => onAddContent?.() },
    el('span', { class: 'glyph', 'aria-hidden': 'true', text: '+' }), el('span', { class: 'ab-add-label' }));
  const actions = el('div', { class: 'ab-actions', role: 'group' }, addContent, buttons.validate, buttons.pack, buttons.smoke, buttons.run, cancel);

  const factorio = el('button', { type: 'button', class: 'ab-badge', 'data-testid': 'action-factorio', 'data-mode': 'none', onclick: onFactorio });
  const job = el('button', { type: 'button', class: 'ab-job', 'data-testid': 'action-job', 'data-state': 'idle', onclick: onJob });
  const problems = el('button', { type: 'button', class: 'ab-problems', 'data-testid': 'action-problems', 'data-errors': '0', 'data-warnings': '0', onclick: onProblems });
  const status = el('div', { class: 'ab-status' }, factorio, job, problems);

  mount.append(heading, actions, status);

  function counts() {
    const issues = Array.isArray(s.overview?.issues) ? s.overview.issues : [];
    return { errors: issues.filter((i) => i.severity === 'error').length, warnings: issues.filter((i) => i.severity === 'warning').length };
  }

  function render() {
    const overview = s.overview;
    mount.hidden = !overview;
    if (!overview) { stopTick(); return; }

    const info = overview.info || {};
    title.textContent = info.title || info.name || t('mod.unnamed');
    version.textContent = info.version ? `v${info.version}` : '';
    version.hidden = !info.version;
    target.textContent = info.factorioVersion ? t('ui.action.target', { version: info.factorioVersion }) : '';
    target.hidden = !info.factorioVersion;

    const st = buildView.getState();
    const busy = st.busy;
    const set = (node, disabled, reasonKey) => {
      node.disabled = disabled;
      if (disabled && reasonKey) node.setAttribute('title', t(reasonKey)); else node.removeAttribute('title');
    };
    for (const kind of KINDS) buttons[kind].textContent = t(`build.${kind}`);
    addContent.querySelector('.ab-add-label').textContent = t('content.open');
    set(addContent, !st.editable, !st.editable ? 'build.readonlyShort' : null);
    if (st.editable) addContent.setAttribute('title', t('content.openTitle'));
    cancel.textContent = t('build.cancel');
    set(buttons.validate, busy);
    set(buttons.pack, busy || !st.editable, !st.editable ? 'build.readonlyShort' : null);
    set(buttons.smoke, busy || !st.editable || !st.factorioFound, !st.editable ? 'build.readonlyShort' : !st.factorioFound ? 'build.env.notFound' : null);
    set(buttons.run, busy || !st.editable || !st.factorioFound, !st.editable ? 'build.readonlyShort' : !st.factorioFound ? 'build.env.notFound' : null);
    cancel.hidden = !st.running;
    cancel.disabled = st.cancelling;

    factorio.dataset.mode = st.mode;
    factorio.textContent = st.factorioFound ? t('ui.action.factorio', { version: st.factorioVersion || '?' }) : st.envReady ? t('ui.action.factorioNone') : t('ui.action.factorioChecking');
    factorio.setAttribute('title', t('ui.action.factorioTitle'));

    const latest = st.latest;
    if (!latest) {
      job.dataset.state = 'idle';
      job.textContent = t('ui.job.idle');
      stopTick();
    } else {
      const shown = latest.state === 'queued' ? 'running' : latest.state;
      job.dataset.state = shown;
      const startedAt = Date.parse(latest.startedAt);
      const elapsed = shown === 'running' && Number.isFinite(startedAt) ? ` ${formatElapsed(Date.now() - startedAt)}` : '';
      const kind = t(`build.${latest.kind}`);
      job.textContent = t('ui.job.chip', { kind: kind === `build.${latest.kind}` ? latest.kind : kind, state: t(`build.state.${latest.state}`) }) + elapsed;
      if (shown === 'running') startTick(); else stopTick();
    }
    job.setAttribute('title', t('ui.job.title'));

    const { errors, warnings } = counts();
    problems.dataset.errors = String(errors);
    problems.dataset.warnings = String(warnings);
    problems.dataset.level = errors ? 'error' : warnings ? 'warning' : 'ok';
    problems.textContent = `${t('ui.action.errors', { n: errors })} · ${t('ui.action.warnings', { n: warnings })}`;
    problems.setAttribute('title', t('ui.action.problemsTitle'));
    mount.setAttribute('aria-label', t('ui.action.label'));
    actions.setAttribute('aria-label', t('ui.action.actions'));
  }

  function startTick() { if (!s.tick) s.tick = setInterval(render, 1000); }
  function stopTick() { if (s.tick) { clearInterval(s.tick); s.tick = 0; } }

  buildView.onChange(render);

  return {
    /** `overview` of the open mod, or null when none is open. */
    setOverview(overview) { s.overview = overview; render(); },
    render,
    retranslate: render,
  };
}
