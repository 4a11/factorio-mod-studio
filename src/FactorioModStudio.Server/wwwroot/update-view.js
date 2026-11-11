// The studio's own updates. At the bottom of the sidebar a version chip ("v0.3.0 · Releases", a dot when a newer version is out,
// a progress ring while one downloads) opens the "About and updates" dialog: the version, the channel (Releases: 0.3.0, 0.4.0 ...;
// Previews: also 0.3.1, 0.3.2 ...), how this copy was installed, the last check with "Check now", and a newer version with its
// notes from CHANGELOG.md. A copy unpacked from a release archive downloads it in the background (a bar with MB and speed, Cancel),
// swaps the files while running and restarts (the window) or asks to be started again (the browser); a .NET tool shows its update
// command; a build from source only says the version is out. A small banner above the chip keeps the state in view while the
// dialog is closed. Nothing is downloaded unasked; the server asks GitHub at most every six hours unless "Check now" is pressed.

import { t, formatNumber, getLanguage } from '/i18n.js';
import { chipState, percentOf, formatBytes, addSample, parseNotes } from '/update-logic.js';

/**
 * @param {{ el: Function, api: Function, notice: HTMLElement, chip: HTMLButtonElement, hasUnsaved?: () => boolean }} host
 */
export function createUpdateView({ el, api, notice, chip, hasUnsaved = () => false }) {
  let s = null;
  let poll = null;
  let busy = null; // null | 'checking' | 'installing' | 'cancelling' | 'restarting' | 'channel'
  let actionError = '';
  let speed = null;
  const samples = [];

  const bytes = (n) => formatBytes(n, formatNumber);

  // ------------------------------------------------------------ server

  async function load(force = false) {
    if (force) { busy = 'checking'; render(); }
    try { s = await api('/api/update' + (force ? '?force=true' : '')); actionError = ''; }
    catch (error) { if (force) actionError = error.message; else s = s ?? null; }
    finally { if (force) busy = null; }
    render();
    watch();
  }

  async function post(url, body, kind) {
    busy = kind;
    actionError = '';
    render();
    try {
      const answer = await api(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });
      if (answer && typeof answer === 'object' && 'current' in answer) s = answer;
    } catch (error) {
      actionError = error.message;
    } finally {
      if (kind !== 'restarting' || actionError) busy = null;
    }
    render();
    watch();
  }

  function watch() {
    if (poll || s?.install?.state !== 'downloading') return;
    samples.length = 0;
    speed = null;
    poll = setInterval(async () => {
      try { s = await api('/api/update'); } catch { /* keep the last state; the next poll tries again */ }
      if (s?.install?.state === 'downloading') speed = addSample(samples, s.install.received || 0, Date.now());
      else { clearInterval(poll); poll = null; speed = null; }
      render();
    }, 1000);
  }

  const install = () => post('/api/update/install', null, 'installing');
  const cancel = () => post('/api/update/cancel', null, 'cancelling');
  const setChannel = (channel) => post('/api/update/channel', { channel }, 'channel');

  function restart() {
    if (hasUnsaved() && !confirm(t('update.unsavedConfirm'))) return;
    post('/api/update/restart', null, 'restarting');
  }

  // ------------------------------------------------------------ pieces

  const versionText = (v) => 'v' + v;
  const channelName = (c) => t(c === 'preview' ? 'update.channel.preview' : 'update.channel.stable');
  const when = (iso) => {
    try { return new Intl.DateTimeFormat(getLanguage(), { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso)); }
    catch { return new Date(iso).toLocaleString(); }
  };
  const day = (iso) => {
    try { return new Intl.DateTimeFormat(getLanguage(), { dateStyle: 'long' }).format(new Date(iso)); }
    catch { return new Date(iso).toLocaleDateString(); }
  };

  function progressParts(inst) {
    const percent = percentOf(inst);
    const bar = el('progress', { class: 'update-progress', 'data-testid': 'update-progress', max: '100', 'aria-label': t('update.downloadingShort', { version: inst.version }) });
    if (percent !== null) bar.value = percent; // no value: the bar shows "busy" while the size is unknown
    const amount = inst.total ? t('update.amount', { received: bytes(inst.received), total: bytes(inst.total) }) : bytes(inst.received);
    const rate = speed ? t('update.speed', { speed: bytes(speed) }) : '';
    const detail = [percent !== null ? formatNumber(percent) + '%' : '', amount, rate].filter(Boolean).join(' · ');
    return { bar, detail };
  }

  function errorLine(text) {
    return text && el('p', { class: 'update-error', role: 'alert', 'data-testid': 'update-error', text });
  }

  // ------------------------------------------------------------ chip and banner (always visible)

  function renderChip() {
    const state = chipState(s);
    chip.dataset.state = state;
    chip.disabled = !s;
    const inst = s?.install;
    const percent = state === 'downloading' ? percentOf(inst) : null;
    const ring = el('span', { class: 'version-chip-mark', 'aria-hidden': 'true' });
    if (percent !== null) ring.style.setProperty('--p', String(percent)); // CSSOM: allowed under the strict CSP
    chip.replaceChildren(...[ // replaceChildren would print null as text: filter first
      el('span', { class: 'version-chip-version', text: s ? versionText(s.current) : '…' }),
      s && el('span', { class: 'version-chip-channel', text: channelName(s.channel) }),
      state !== 'current' ? ring : null].filter(Boolean));
    const tip = !s ? '' : state === 'available' ? t('update.chip.available', { version: s.latest.version })
      : state === 'downloading' ? t('update.downloadingShort', { version: inst.version }) + (percent !== null ? ` ${formatNumber(percent)}%` : '')
      : state === 'installed' ? t('update.installed', { version: inst.version })
      : state === 'failed' ? t('update.chip.failed')
      : t('update.chip.current', { version: s.current });
    chip.title = tip;
    chip.setAttribute('aria-label', tip ? `${t('update.dialog.title')}: ${tip}` : t('update.dialog.title'));
  }

  function renderNotice() {
    const state = chipState(s);
    const parts = [];
    const details = () => el('button', { type: 'button', class: 'update-link', 'data-testid': 'update-details', text: t('update.details'), onclick: open });
    if (state === 'available' && !dismissed(s.latest.version)) {
      parts.push(el('span', { class: 'update-text', text: t(s.latest.preview ? 'update.availablePreview' : 'update.available', { version: s.latest.version }) }),
        details(),
        el('button', { type: 'button', class: 'update-dismiss', 'aria-label': t('update.dismiss'), title: t('update.dismiss'), text: '×', onclick: () => { dismiss(s.latest.version); render(); } }));
    } else if (state === 'downloading') {
      const { bar, detail } = progressParts(s.install);
      parts.push(el('span', { class: 'update-text', text: t('update.downloadingShort', { version: s.install.version }) }), bar,
        el('span', { class: 'muted small update-detail', text: detail }), details());
    } else if (state === 'installed') {
      parts.push(el('span', { class: 'update-text', text: t('update.installed', { version: s.install.version }) }));
      if (s.canRestart) parts.push(el('button', { type: 'button', class: 'primary', 'data-testid': 'update-restart', disabled: busy === 'restarting', onclick: restart,
        text: t(busy === 'restarting' ? 'update.restarting' : 'update.restartNow') }));
      else parts.push(el('span', { class: 'muted small', text: t('update.restartManually') }));
    } else if (state === 'failed') {
      parts.push(el('span', { class: 'update-text', text: t(s.install.state === 'cancelled' ? 'update.cancelledShort' : 'update.failedShort') }), details());
    }
    notice.replaceChildren(...parts.filter(Boolean));
    notice.hidden = parts.length === 0;
  }

  // "×" on the banner hides it for that version only (per browser); the chip keeps its dot.
  const DISMISS_KEY = 'fms.update.dismissed';
  function dismissed(version) { try { return localStorage.getItem(DISMISS_KEY) === version; } catch { return false; } }
  function dismiss(version) { try { localStorage.setItem(DISMISS_KEY, version); } catch { /* private window: shown again next time */ } }

  // ------------------------------------------------------------ dialog

  const dialog = el('dialog', { class: 'build-path-dialog update-dialog', 'data-testid': 'update-dialog', 'aria-labelledby': 'update-heading' });
  document.body.append(dialog);
  dialog.addEventListener('close', () => { if (!s) return; render(); });

  function open() {
    renderDialog();
    if (!dialog.open) dialog.showModal();
    if (!s) load();
  }

  function renderNotes(markdown) {
    const blocks = parseNotes(markdown);
    if (!blocks.length) return null;
    const inline = (parts) => parts.map((p) =>
      p.strong !== undefined ? el('strong', { text: p.strong })
        : p.em !== undefined ? el('em', { text: p.em })
          : p.code !== undefined ? el('code', { text: p.code })
            : p.link !== undefined ? el('a', { href: p.href, target: '_blank', rel: 'noopener noreferrer', text: p.link })
              : document.createTextNode(p.text));
    const nodes = [];
    let list = null;
    for (const b of blocks) {
      if (b.type === 'item') {
        if (!list) { list = el('ul'); nodes.push(list); }
        list.append(el('li', {}, ...inline(b.inline)));
        continue;
      }
      list = null;
      nodes.push(b.type === 'heading' ? el(b.level <= 2 ? 'h4' : 'h5', {}, ...inline(b.inline)) : el('p', {}, ...inline(b.inline)));
    }
    return el('div', { class: 'update-notes', 'data-testid': 'update-notes', tabindex: '0', 'aria-label': t('update.notes') }, ...nodes);
  }

  function renderDialog() {
    const parts = [el('h2', { id: 'update-heading', text: t('update.dialog.title') })];
    if (!s) {
      parts.push(el('p', { class: 'muted', text: t('update.loading') }), errorLine(actionError));
    } else {
      // Version, channel, how this copy is installed.
      const channelSelect = el('select', { 'data-testid': 'update-channel', 'aria-label': t('update.channel.label'), disabled: !!busy,
        onchange: (e) => setChannel(e.target.value) },
      el('option', { value: 'stable', text: t('update.channel.stable') }),
      el('option', { value: 'preview', text: t('update.channel.preview') }));
      channelSelect.value = s.channel;
      parts.push(el('div', { class: 'update-facts' },
        el('span', { class: 'muted', text: t('update.version') }), el('strong', { 'data-testid': 'update-current', text: versionText(s.current) }),
        el('label', { class: 'muted', text: t('update.channel.label') }), el('span', { class: 'update-channel-cell' }, channelSelect,
          el('span', { class: 'muted small', text: t(s.channel === 'preview' ? 'update.channel.previewHint' : 'update.channel.stableHint') })),
        el('span', { class: 'muted', text: t('update.kind.label') }), el('span', { 'data-testid': 'update-kind', text: t(s.kind === 'other' ? 'update.kind.source' : `update.kind.${s.kind}`) })));

      // Last check and "Check now".
      const checked = s.checkedAt ? t('update.checkedAt', { time: when(s.checkedAt) }) : t('update.neverChecked');
      parts.push(el('div', { class: 'update-check-row' },
        el('span', { class: 'muted small', 'data-testid': 'update-checked', text: busy === 'checking' ? t('update.checking') : checked }),
        el('button', { type: 'button', 'data-testid': 'update-check', disabled: !!busy || s.install?.state === 'downloading', onclick: () => load(true), text: t('update.checkNow') })));
      if (s.error) parts.push(errorLine(t('update.checkFailed', { error: s.error })));

      const inst = s.install;
      const state = chipState(s);
      if (state === 'downloading') {
        const { bar, detail } = progressParts(inst);
        parts.push(el('section', { class: 'update-box', 'data-state': 'downloading' },
          el('h3', { text: t('update.downloadingShort', { version: inst.version }) }), bar,
          el('div', { class: 'update-row' }, el('span', { class: 'muted small', 'data-testid': 'update-detail', text: detail }),
            el('button', { type: 'button', 'data-testid': 'update-cancel', disabled: busy === 'cancelling', onclick: cancel, text: t('update.cancel') })),
          el('p', { class: 'muted small', text: t('update.background') })));
      } else if (state === 'installed') {
        parts.push(el('section', { class: 'update-box', 'data-state': 'installed' },
          el('h3', { text: t('update.installed', { version: inst.version }) }),
          el('p', { class: 'muted small', text: t(s.canRestart ? 'update.installedHint' : 'update.restartManually') }),
          s.canRestart && el('div', { class: 'update-row' },
            el('button', { type: 'button', class: 'primary', 'data-testid': 'update-restart-now', disabled: busy === 'restarting', onclick: restart,
              text: t(busy === 'restarting' ? 'update.restarting' : 'update.restartNow') }),
            el('button', { type: 'button', 'data-testid': 'update-later', onclick: () => dialog.close(), text: t('update.later') }))));
      } else if (s.latest) {
        const latest = s.latest;
        const head = el('div', { class: 'update-latest-head' },
          el('h3', { text: t(latest.preview ? 'update.availablePreview' : 'update.available', { version: latest.version }) }),
          latest.published && el('span', { class: 'muted small', text: day(latest.published) }),
          latest.url && el('a', { href: latest.url, target: '_blank', rel: 'noopener noreferrer', text: t('update.onGithub') }));
        const action = s.kind === 'archive'
          ? el('div', { class: 'update-row' },
            el('button', { type: 'button', class: 'primary', 'data-testid': 'update-install', disabled: !!busy, onclick: install,
              text: t(inst && (inst.state === 'failed' || inst.state === 'cancelled') ? 'update.retry' : 'update.install') }),
            el('span', { class: 'muted small', text: t('update.installHint') }))
          : s.kind === 'tool' && s.command
            ? el('div', { class: 'update-row' }, el('span', { class: 'muted small', text: t('update.toolHint') }), el('code', { class: 'update-command', text: s.command }))
            : el('p', { class: 'muted small', text: t('update.manualHint') });
        // A failed download (some networks block GitHub's download host) gets the way round it: the archive from the release page.
        const outcome = inst?.state === 'failed'
          ? el('div', { class: 'update-failed' }, errorLine(t('update.failed', { error: inst.error || '' })),
            latest.url && el('a', { href: latest.url, target: '_blank', rel: 'noopener noreferrer', 'data-testid': 'update-manual', text: t('update.manualDownload') }))
          : inst?.state === 'cancelled' ? el('p', { class: 'muted small', text: t('update.cancelled') }) : null;
        parts.push(el('section', { class: 'update-box', 'data-state': 'available' }, head, outcome, action, renderNotes(latest.notes)));
      } else if (!s.error) {
        parts.push(el('p', { class: 'update-uptodate', 'data-testid': 'update-uptodate', text: t('update.upToDate', { channel: channelName(s.channel) }) }));
      }
      parts.push(errorLine(actionError));
    }
    parts.push(el('div', { class: 'nm-actions build-path-actions' }, el('button', { type: 'button', 'data-testid': 'update-close', onclick: () => dialog.close(), text: t('update.close') })));

    // Keep the notes' scroll position across the once-a-second redraws of a download.
    const scroller = dialog.querySelector('.update-notes');
    const top = scroller ? scroller.scrollTop : 0;
    dialog.replaceChildren(...parts.filter(Boolean));
    const again = dialog.querySelector('.update-notes');
    if (again) again.scrollTop = top;
  }

  function render() {
    renderChip();
    renderNotice();
    if (dialog.open) renderDialog();
  }

  chip.addEventListener('click', open);

  return { load, open, retranslate: render };
}
