// Light / dark / auto colour theme. The mode is remembered by ui-state.js; /theme-init.js applies it before first paint,
// this module keeps it in sync afterwards (select control, palette commands, changes of the system preference).
// The resolved theme ("light" or "dark") is `document.documentElement.dataset.theme`, the chosen mode is `dataset.themeMode`.

export const THEME_MODES = ['auto', 'light', 'dark'];

export function createTheme({ uiState, onChange = () => {} }) {
  let query = null;
  try { query = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null; } catch { query = null; }
  let mode = THEME_MODES.includes(uiState.get().theme) ? uiState.get().theme : 'auto';

  const resolved = () => (mode === 'dark' || (mode === 'auto' && !!query?.matches) ? 'dark' : 'light');

  function apply() {
    const root = document.documentElement;
    root.dataset.theme = resolved();
    root.dataset.themeMode = mode;
    onChange(mode);
  }

  const follow = () => { if (mode === 'auto') apply(); };
  if (query?.addEventListener) query.addEventListener('change', follow);
  else if (query?.addListener) query.addListener(follow);

  return {
    getMode: () => mode,
    getResolved: resolved,
    setMode(next) {
      if (!THEME_MODES.includes(next)) return;
      mode = next;
      uiState.setTheme(next);
      apply();
    },
    apply,
  };
}
