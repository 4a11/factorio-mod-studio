// Applies the remembered colour theme before the stylesheet is used, so there is no flash of the wrong theme.
// Classic script (no module, no inline code). It must never throw: storage may be missing, blocked or hold garbage.
(function () {
  try {
    var mode = 'auto';
    try {
      var raw = window.localStorage.getItem('fms.ui.v1');
      var saved = raw ? JSON.parse(raw) : null;
      if (saved && typeof saved === 'object' && (saved.theme === 'light' || saved.theme === 'dark' || saved.theme === 'auto')) mode = saved.theme;
    } catch (ignore) { /* keep auto */ }
    var dark = mode === 'dark';
    if (mode === 'auto') {
      try { dark = !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches); } catch (ignore) { dark = false; }
    }
    var root = document.documentElement;
    root.dataset.theme = dark ? 'dark' : 'light';
    root.dataset.themeMode = mode;
  } catch (ignore) { /* the stylesheet falls back to prefers-color-scheme */ }
})();
