// How the properties of a building block are shown: labels for the fields, values of the game data, the value a reset goes back
// to. Used by the Blocks tab (blocks-view.js).

import { t, formatNumber } from '/i18n.js';

/** "attack_parameters.range" -> its translated label, or "Range" made from the last part of the path. */
export function fieldLabel(path) {
  const last = String(path).split('.').pop();
  const key = `blockprop.${last}`;
  const label = t(key);
  if (label !== key) return label;
  const words = last.replace(/_/g, ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** A value of the game data for display: numbers rounded to 6 significant digits, strings as they are. */
export function shownValue(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') return formatNumber(Number(value.toPrecision(6)), { maximumFractionDigits: 6 });
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

/** The text an input gets back when the property is reset: the original for a value, 1 for a factor; null when not known. */
export function resetText(property) {
  if (property.kind === 'multiplier' || property.kind === 'damage') return '1';
  const original = property.original;
  if (original === null || original === undefined || typeof original === 'object') return null;
  return typeof original === 'number' ? String(Number(original.toPrecision(12))) : String(original);
}
