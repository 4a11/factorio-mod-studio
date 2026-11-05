// "Prototypes" tab of the mod page. Two lists: the prototypes the mod defines (data:extend) and the existing prototypes it
// touches through data.raw. Either list is shown grouped into families (items, recipes, technologies, entities, ...) with one
// collapsible section per family, or by name: one row per object, since an item, its recipe and its entity usually share a name.
// Family chips (and, for the chosen family, type chips) with counts filter the list; "No localised name" keeps the prototypes the validator reported with FMS042.
// Recipe rows also show what they craft (ingredients → results, category, time), and the name filter matches those item names too,
// so "iron-plate" finds the recipes that use or make it. (This replaced the former Recipes tab.)
// Mod content (names, display names, paths) is only ever set as text.

import { t, formatNumber } from '/i18n.js';

const MAX_ROWS = 200; // rows per family section (and of the by-name list) until "Show all"
const TYPE_CHIPS = 10; // types shown in the second chip row before "+N"

const fmtNum = (n) => (n === null || n === undefined ? '?' : formatNumber(n, { maximumFractionDigits: 3, useGrouping: false }));

const ITEM_TYPES = new Set([
  'item', 'ammo', 'armor', 'blueprint', 'blueprint-book', 'capsule', 'copy-paste-tool', 'deconstruction-item', 'gun',
  'item-with-entity-data', 'item-with-inventory', 'item-with-label', 'item-with-tags', 'module', 'rail-planner', 'repair-tool',
  'selection-tool', 'spidertron-remote', 'tool', 'upgrade-item', 'space-platform-starter-pack',
]);
const OTHER_TYPES = new Set([
  'item-group', 'item-subgroup', 'custom-input', 'shortcut', 'sprite', 'font', 'gui-style', 'virtual-signal', 'damage-type',
  'autoplace-control', 'noise-layer', 'noise-expression', 'noise-function', 'map-settings', 'map-gen-presets', 'tips-and-tricks-item',
  'tips-and-tricks-item-category', 'tutorial', 'sound', 'ambient-sound', 'mouse-cursor', 'utility-constants', 'utility-sounds',
  'utility-sprites', 'equipment-grid', 'trivial-smoke', 'collision-layer', 'airborne-pollutant', 'surface-property', 'impact-category',
]);

/** Families in display order: [id, icon class, does a prototype type belong to it]. "entities" takes everything not matched before it. */
const FAMILIES = [
  ['items', 'fi-item', (type) => ITEM_TYPES.has(type)],
  ['recipes', 'fi-recipe', (type) => type === 'recipe'],
  ['technologies', 'fi-tech', (type) => type === 'technology'],
  ['fluids', 'fi-drop', (type) => type === 'fluid'],
  ['equipment', 'fi-grid', (type) => type.endsWith('-equipment')],
  ['tiles', 'fi-tile', (type) => type === 'tile'],
  ['settings', 'fi-sliders', (type) => type.endsWith('-setting')],
  ['misc', 'fi-file', (type) => OTHER_TYPES.has(type) || type.endsWith('-category') || type.endsWith('-achievement')],
  ['entities', 'fi-cube', () => true],
];
const FAMILY_ORDER = ['items', 'recipes', 'technologies', 'entities', 'fluids', 'equipment', 'tiles', 'settings', 'misc'];

/** The family a prototype type belongs to: { id, icon }. */
export function familyOf(type) {
  const [id, icon] = FAMILIES.find(([, , test]) => test(type));
  return { id, icon };
}

const byName = (a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
const byType = (a, b) => (a.type < b.type ? -1 : a.type > b.type ? 1 : 0);

/** Prototypes grouped by family in display order: [{ id, icon, items }], each sorted by type, then name. */
export function groupByFamily(list) {
  const groups = new Map();
  for (const p of list) {
    const family = familyOf(p.type);
    if (!groups.has(family.id)) groups.set(family.id, { ...family, items: [] });
    groups.get(family.id).items.push(p);
  }
  for (const group of groups.values()) group.items.sort((a, b) => byType(a, b) || byName(a, b));
  return FAMILY_ORDER.filter((id) => groups.has(id)).map((id) => groups.get(id));
}

/** One entry per name: [{ name, items }] sorted by name; items in family order (item before recipe before entity ...). */
export function groupByName(list) {
  const groups = new Map();
  for (const p of list) {
    if (!groups.has(p.name)) groups.set(p.name, { name: p.name, items: [] });
    groups.get(p.name).items.push(p);
  }
  const rank = (p) => FAMILY_ORDER.indexOf(familyOf(p.type).id);
  for (const group of groups.values()) group.items.sort((a, b) => rank(a) - rank(b) || byType(a, b));
  return [...groups.values()].sort(byName);
}

/** Types with their counts, in family order and then by count: [[type, n]]. */
export function typeCounts(list) {
  const counts = new Map();
  for (const p of list) counts.set(p.type, (counts.get(p.type) || 0) + 1);
  const rank = (type) => FAMILY_ORDER.indexOf(familyOf(type).id);
  return [...counts].sort((a, b) => rank(a[0]) - rank(b[0]) || b[1] - a[1] || (a[0] < b[0] ? -1 : 1));
}

/**
 * Renders the tab. `state` keeps protoMode ('defined' | 'touched'), protoFamily, protoType, protoFilter, protoView ('type' | 'name'),
 * protoMissingOnly, protoTypesExpanded, protoCollapsed (a Set of collapsed family ids) and protoAllRows (sections shown without
 * the row limit) across redraws.
 * @param {{ el: Function, state: object, openFile: Function, fileUrl: Function, rerender: () => void }} host
 */
export function renderPrototypes(o, { el, state, openFile, fileUrl, rerender }) {
  const touched = state.protoMode === 'touched';
  const list = touched ? o.touched : o.prototypes;
  state.protoView ||= 'type';
  state.protoCollapsed ||= new Set();
  const types = typeCounts(list);
  if (state.protoType && !types.some(([type]) => type === state.protoType)) state.protoType = '';
  // A type chosen from elsewhere (the Overview's touched chips) selects its family too.
  if (state.protoType) state.protoFamily = familyOf(state.protoType).id;
  if (state.protoFamily && !list.some((p) => familyOf(p.type).id === state.protoFamily)) state.protoFamily = '';

  // FMS042 is reported at the line of the prototype: the same rules as the validator, including its exceptions.
  const noName = new Map();
  if (!touched) for (const issue of o.issues) if (issue.code === 'FMS042' && issue.file) noName.set(`${issue.file}:${issue.line}`, issue.message);
  const missing = (p) => noName.get(`${p.file}:${p.line}`);
  // What a recipe prototype crafts (the overview's recipe list, by name); none in the touched list.
  const recipes = new Map(touched ? [] : o.recipes.map((r) => [r.name, r]));
  const recipeOf = (p) => (p.type === 'recipe' ? recipes.get(p.name) : undefined);
  const missingCount = touched ? 0 : list.filter(missing).length;
  if (!missingCount) state.protoMissingOnly = false;

  // ------------------------------------------------------------ toolbar

  const setMode = (mode) => { if (state.protoMode !== mode) { state.protoMode = mode; state.protoType = ''; state.protoFamily = ''; rerender(); } };
  const modeSwitch = (o.touched.length > 0 || touched) && el('div', { class: 'segmented', role: 'group', 'aria-label': t('prototypes.modeAria'), 'data-testid': 'proto-mode' },
    [['defined', 'prototypes.modeDefined'], ['touched', 'prototypes.modeTouched']].map(([mode, key]) =>
      el('button', { type: 'button', class: 'toggle', 'data-mode': mode, 'aria-pressed': (touched ? mode === 'touched' : mode === 'defined') ? 'true' : 'false',
        onclick: () => setMode(mode), text: t(key) })));
  const head = el('div', { class: 'proto-head' }, modeSwitch,
    el('span', { class: 'muted small', text: t(touched ? 'prototypes.explainTouched' : 'prototypes.explainDefined') }));

  if (!list.length) {
    return el('div', { class: 'proto-panel' }, head,
      el('p', { class: 'muted ov-empty', text: t(touched ? 'prototypes.touchedEmpty' : 'prototypes.empty') }));
  }

  const search = el('input', { type: 'search', class: 'proto-filter', placeholder: t('prototypes.filter'), 'aria-label': t('prototypes.filterAria'), value: state.protoFilter,
    oninput: (e) => { state.protoFilter = e.target.value; refresh(); } });
  const missingBtn = missingCount > 0 && el('button', { type: 'button', class: 'files-toggle', 'data-testid': 'proto-missing-only', title: t('prototypes.noNameTitle'),
    onclick: () => { state.protoMissingOnly = !state.protoMissingOnly; refresh(); } },
  el('span', { text: t('prototypes.missingOnly') }), el('span', { class: 'files-count', text: String(missingCount) }));
  const viewButtons = [['type', 'prototypes.viewType'], ['name', 'prototypes.viewName']].map(([view, key]) =>
    el('button', { type: 'button', 'data-testid': `proto-view-${view}`, onclick: () => { state.protoView = view; refresh(); }, text: t(key) }));
  const count = el('span', { class: 'muted small files-summary', 'data-testid': 'proto-count' });
  // Two chip rows: the families (at most nine, whatever the size of the mod), then the types of the chosen family.
  const chips = el('div', { class: 'proto-types', role: 'group', 'aria-label': t('prototypes.familyAria'), 'data-testid': 'proto-family' });
  const typeChips = el('div', { class: 'proto-types proto-subtypes', role: 'group', 'aria-label': t('prototypes.typeAria'), 'data-testid': 'proto-type' });
  const content = el('div', { class: 'proto-content' });

  // ------------------------------------------------------------ rows

  const icon = (p) => (!touched && p.icon
    ? el('img', { class: 'thumb proto-thumb', loading: 'lazy', alt: '', src: fileUrl(p.icon) })
    : el('span', { class: `fi-icon ${familyOf(p.type).icon}`, 'aria-hidden': 'true' }));

  const itemChip = (item) => el('span', { class: 'chip proto-item' + (item.type === 'fluid' ? ' accent' : ''), title: item.type,
    text: `${item.amount === null || item.amount === undefined ? '' : `${fmtNum(item.amount)}× `}${item.name}` });

  // "2× iron-plate → 1× my-chest · crafting · 0.5 s"
  const recipeLine = (r) => el('span', { class: 'proto-recipe', 'data-testid': 'proto-recipe' },
    r.ingredients.length ? r.ingredients.map(itemChip) : el('span', { class: 'muted small', text: t('recipes.noIngredients') }),
    el('span', { class: 'proto-arrow', 'aria-hidden': 'true', text: '→' }),
    r.results.length ? r.results.map(itemChip) : el('span', { class: 'muted small', text: t('recipes.noResults') }),
    (r.category || (r.energyRequired !== null && r.energyRequired !== undefined)) && el('span', { class: 'muted small',
      text: ['', r.category, r.energyRequired !== null && r.energyRequired !== undefined && t('recipes.seconds', { value: fmtNum(r.energyRequired) })].filter((x) => x !== false && x !== null && x !== undefined).join(' · ') }));

  const nameBlock = (p, recipe = recipeOf(p)) => el('span', { class: 'proto-names' },
    el('span', { class: 'mono proto-name', text: p.name }),
    p.displayName
      ? el('span', { class: 'muted small', text: p.displayName })
      : missing(p) && el('span', { class: 'badge warning proto-noname', 'data-testid': 'proto-noname', title: missing(p), text: t('prototypes.noName') }),
    recipe && recipeLine(recipe));

  const where = (p) => el('span', { class: 'muted small mono proto-where', text: `${p.file}:${p.line}` });

  const typeRow = (p) => el('button', { type: 'button', class: 'proto-row', 'data-testid': 'proto-row', title: t('readiness.action.open', { file: `${p.file}:${p.line}` }), onclick: () => openFile(p.file, p.line) },
    icon(p), nameBlock(p), el('span', { class: 'chip proto-type mono', text: p.type }), where(p),
    el('span', { class: 'ov-row-go', 'aria-hidden': 'true', text: '›' }));

  // By name: the row opens the first definition; every type chip opens its own.
  const nameRow = (group) => {
    const first = group.items[0];
    const display = group.items.find((p) => p.displayName) ?? first;
    return el('div', { class: 'proto-row by-name', role: 'button', tabindex: '0', 'data-testid': 'proto-row',
      onclick: () => openFile(first.file, first.line),
      onkeydown: (e) => { if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openFile(first.file, first.line); } } },
    icon(group.items.find((p) => p.icon) ?? first), nameBlock({ ...display, name: group.name }, group.items.map(recipeOf).find(Boolean)),
    el('span', { class: 'proto-type-chips' }, group.items.map((p) => el('button', {
      type: 'button', class: 'chip proto-type mono', title: `${p.file}:${p.line}`,
      onclick: (e) => { e.stopPropagation(); openFile(p.file, p.line); }, text: p.type }))),
    el('span', { class: 'ov-row-go', 'aria-hidden': 'true', text: '›' }));
  };

  // ------------------------------------------------------------ content

  function refresh() {
    const filter = state.protoFilter.trim().toLowerCase();
    const recipeMatches = (r) => !!r && [...r.ingredients, ...r.results].some((i) => i.name.toLowerCase().includes(filter));
    const matchesText = (p) => !filter || p.name.toLowerCase().includes(filter) || (p.displayName || '').toLowerCase().includes(filter) || recipeMatches(recipeOf(p));
    const base = list.filter((p) => matchesText(p) && (!state.protoMissingOnly || missing(p)));
    const shown = base.filter((p) => (!state.protoFamily || familyOf(p.type).id === state.protoFamily) && (!state.protoType || p.type === state.protoType));

    // Chips count what the text filters leave, so they say where the matches are.
    const chip = ({ mono, ...attrs }, iconClass, label, n, onclick) => el('button', {
      type: 'button', class: 'chip proto-chip', onclick, ...attrs, disabled: !n && attrs['aria-pressed'] !== 'true' ? true : null,
    }, iconClass && el('span', { class: `fi-icon ${iconClass}`, 'aria-hidden': 'true' }), el('span', { class: mono ? 'mono' : null, text: label }),
    el('span', { class: 'ov-count', text: String(n) }));
    const pick = (family, type) => {
      if (family !== state.protoFamily) state.protoTypesExpanded = false;
      state.protoFamily = family;
      state.protoType = type;
      refresh();
    };

    const families = groupByFamily(base);
    const familyCount = (id) => families.find((f) => f.id === id)?.items.length ?? 0;
    const allFamilies = groupByFamily(list);
    chips.replaceChildren(
      chip({ 'aria-pressed': state.protoFamily ? 'false' : 'true' }, null, t('prototypes.all'), base.length, () => pick('', '')),
      ...allFamilies.map((f) => chip({ 'data-family': f.id, 'aria-pressed': state.protoFamily === f.id ? 'true' : 'false' },
        f.icon, t(`prototypes.family.${f.id}`), familyCount(f.id), () => pick(state.protoFamily === f.id ? '' : f.id, ''))));

    const familyTypes = state.protoFamily ? types.filter(([type]) => familyOf(type).id === state.protoFamily) : [];
    typeChips.hidden = familyTypes.length < 2;
    if (!typeChips.hidden) {
      const counts = new Map(typeCounts(base));
      const visible = state.protoTypesExpanded || familyTypes.length <= TYPE_CHIPS + 1
        ? familyTypes
        : familyTypes.filter(([type], i) => i < TYPE_CHIPS || type === state.protoType);
      const hiddenCount = familyTypes.length - visible.length;
      // No "all types" chip: the family chip above already is that; a second click on the chosen type clears it.
      typeChips.replaceChildren(
        ...visible.map(([type]) => chip({ 'data-type': type, mono: true, 'aria-pressed': state.protoType === type ? 'true' : 'false' },
          null, type, counts.get(type) || 0, () => pick(state.protoFamily, state.protoType === type ? '' : type))),
        hiddenCount > 0 && el('button', { type: 'button', class: 'chip proto-chip proto-more', 'data-testid': 'proto-more-types', title: t('prototypes.moreTypes', { n: hiddenCount }),
          onclick: () => { state.protoTypesExpanded = true; refresh(); }, text: `+${hiddenCount}` }),
        state.protoTypesExpanded && familyTypes.length > TYPE_CHIPS + 1 && el('button', { type: 'button', class: 'linklike small', 'data-testid': 'proto-fewer-types',
          onclick: () => { state.protoTypesExpanded = false; refresh(); }, text: t('prototypes.fewerTypes') }));
    }

    if (missingBtn) missingBtn.setAttribute('aria-pressed', state.protoMissingOnly ? 'true' : 'false');
    for (const button of viewButtons) button.setAttribute('aria-pressed', button.dataset.testid === `proto-view-${state.protoView}` ? 'true' : 'false');
    count.textContent = shown.length === list.length ? t('count.prototype', { n: list.length }) : `${t('count.prototype', { n: shown.length })} / ${list.length}`;

    if (!shown.length) {
      content.replaceChildren(el('p', { class: 'muted ov-empty', 'data-testid': 'proto-empty', text: t('prototypes.noMatch') }));
      return;
    }

    // Each section (and the by-name list) has its own row limit, so a big family never starves the ones after it.
    state.protoAllRows ||= new Set();
    const limited = (key, items) => (state.protoAllRows.has(key) ? items : items.slice(0, MAX_ROWS));
    const more = (key, total) => total > MAX_ROWS && !state.protoAllRows.has(key) && el('div', { class: 'proto-more-rows' },
      el('span', { class: 'muted small', text: t('prototypes.showing', { shown: MAX_ROWS, total }) }),
      el('button', { type: 'button', class: 'linklike small', 'data-testid': 'proto-show-all', onclick: () => { state.protoAllRows.add(key); refresh(); }, text: t('prototypes.showAll') }));

    if (state.protoView === 'name') {
      const groups = groupByName(shown);
      content.replaceChildren(el('div', { class: 'ov-list proto-list' }, limited('name', groups).map(nameRow), more('name', groups.length)));
      return;
    }

    const forced = !!filter || !!state.protoFamily;
    const sections = groupByFamily(shown).map((family) => {
      const breakdown = typeCounts(family.items);
      const section = el('details', { class: 'proto-family', 'data-family': family.id, open: !state.protoCollapsed.has(family.id) || forced },
        el('summary', {},
          el('span', { class: `fi-icon ${family.icon}`, 'aria-hidden': 'true' }),
          el('span', { text: t(`prototypes.family.${family.id}`) }),
          el('span', { class: 'ov-count', text: String(family.items.length) }),
          breakdown.length > 1 && el('span', { class: 'muted small proto-breakdown', text: breakdown.map(([type, n]) => `${type} ${n}`).join(' · ') })),
        el('div', { class: 'proto-list' }, limited(family.id, family.items).map(typeRow), more(family.id, family.items.length)));
      // While a filter or a chosen family forces sections open, opening is not the user's choice and must not forget a collapse.
      section.addEventListener('toggle', () => {
        if (!section.open) state.protoCollapsed.add(family.id);
        else if (!forced) state.protoCollapsed.delete(family.id);
      });
      return section;
    });
    content.replaceChildren(...sections);
  }

  refresh();
  return el('div', { class: 'proto-panel' }, head,
    el('div', { class: 'files-viewbar proto-bar' }, search, missingBtn, el('div', { class: 'files-switch', role: 'group', 'aria-label': t('files.viewLabel') }, viewButtons), count),
    chips, typeChips, content);
}
