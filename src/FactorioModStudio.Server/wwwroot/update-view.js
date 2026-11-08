// The studio's own updates, at the bottom of the sidebar: the running version with the update channel (stable: releases such as
// 0.3.0; preview: also the versions between them, 0.3.1, 0.3.2 ...), and a notice when a newer version of the channel is out.
// A copy unpacked from a release archive installs it with one button and restarts (the window) or asks to be started again (the
// browser); a .NET tool shows its update command; anything else only says that the version is out. Nothing is downloaded unasked.
// The server asks GitHub at most every six hours; without a network the notice simply stays hidden.

import { t, formatNumber } from '/i18n.js';

/**
 * @param {{ el: Function, api: Function, notice: HTMLElement, version: HTMLElement, channel: HTMLSelectElement }} host
 */
export function createUpdateView({ el, api, notice, version, channel }) {
  let s = null;
  let poll = null;
  let busy = false;

  async function load(force = false) {
    try { s = await api('/api/update' + (force ? '?force=true' : '')); } catch { s = null; }
    render();
  }

  async function post(url, body) {
    busy = true;
    render();
    try {
      const answer = await api(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });
      if (answer) s = answer;
    } catch (error) {
      s = { ...(s || {}), install: { ...(s?.install || {}), state: 'failed', error: error.message } };
    } finally {
      busy = false;
    }
    render();
  }

  function watch() {
    if (poll || s?.install?.state !== 'downloading') return;
    poll = setInterval(async () => {
      await load();
      if (s?.install?.state !== 'downloading') { clearInterval(poll); poll = null; }
    }, 1000);
  }

  function renderChannel() {
    channel.replaceChildren(
      el('option', { value: 'stable', text: t('update.channel.stable') }),
      el('option', { value: 'preview', text: t('update.channel.preview') }));
    channel.title = t('update.channel.label');
    channel.setAttribute('aria-label', t('update.channel.label'));
    if (s) channel.value = s.channel;
    channel.disabled = !s || busy;
  }

  function render() {
    renderChannel();
    version.textContent = s ? 'v' + s.current : '';
    version.title = s ? t('update.current', { version: s.current }) : '';

    const install = s?.install;
    const parts = [];
    if (install?.state === 'downloading') {
      const percent = install.total ? formatNumber(Math.floor(install.received * 100 / install.total)) + '%' : '';
      parts.push(el('span', { class: 'update-text', text: t('update.downloading', { version: install.version, progress: percent }) }));
      watch();
    } else if (install?.state === 'succeeded') {
      parts.push(el('span', { class: 'update-text', text: t('update.installed', { version: install.version }) }));
      if (s.canRestart) parts.push(el('button', { type: 'button', 'data-testid': 'update-restart', text: t('update.restart'), disabled: busy, onclick: () => post('/api/update/restart') }));
      else parts.push(el('span', { class: 'muted small', text: t('update.restartManually') }));
    } else if (s?.latest) {
      const latest = s.latest;
      parts.push(el('span', { class: 'update-text', text: t(latest.preview ? 'update.availablePreview' : 'update.available', { version: latest.version }) }));
      if (latest.url) parts.push(el('a', { href: latest.url, target: '_blank', rel: 'noopener', text: t('update.whatsNew') }));
      if (s.kind === 'archive') {
        parts.push(el('button', { type: 'button', class: 'update-install', 'data-testid': 'update-install', text: t('update.install'), disabled: busy,
          onclick: async () => { await post('/api/update/install'); watch(); } }));
      } else if (s.kind === 'tool' && s.command) {
        parts.push(el('span', { class: 'muted small', text: t('update.toolHint') }), el('code', { class: 'update-command', text: s.command }));
      } else {
        parts.push(el('span', { class: 'muted small', text: t('update.manualHint') }));
      }
      if (install?.state === 'failed') parts.push(el('span', { class: 'update-error', role: 'alert', text: t('update.failed', { error: install.error || '' }) }));
    }

    notice.replaceChildren(...parts);
    notice.hidden = parts.length === 0;
  }

  channel.addEventListener('change', () => post('/api/update/channel', { channel: channel.value }));

  return { load, retranslate: render };
}
