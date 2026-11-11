// Colours of building blocks without the DOM: "#rrggbb" <-> hue, the colour a hue is written as, and the icon preview URL.
// The studio recolours icons by hue only (IconRecolor.cs keeps each pixel's brightness), so a hue is all a colour needs.

const SATURATION = 0.7;

const clamp01 = (x) => Math.min(1, Math.max(0, x));
const hex2 = (x) => Math.round(clamp01(x) * 255).toString(16).padStart(2, '0');

/** "#rrggbb" -> { r, g, b } (0..1), or null. */
export function parseHex(text) {
  const m = /^#?([0-9a-f]{6})$/i.exec(String(text ?? '').trim());
  if (!m) return null;
  const v = parseInt(m[1], 16);
  return { r: (v >> 16 & 255) / 255, g: (v >> 8 & 255) / 255, b: (v & 255) / 255 };
}

/** The hue (0..359) of "#rrggbb"; 0 for grey or an unreadable colour. */
export function hueOf(hex) {
  const c = parseHex(hex);
  if (!c) return 0;
  const max = Math.max(c.r, c.g, c.b);
  const d = max - Math.min(c.r, c.g, c.b);
  if (d === 0) return 0;
  let h;
  if (max === c.r) h = 60 * (((c.g - c.b) / d) % 6);
  else if (max === c.g) h = 60 * ((c.b - c.r) / d + 2);
  else h = 60 * ((c.r - c.g) / d + 4);
  return Math.round((h + 360) % 360) % 360;
}

/** "#rrggbb" of a hue at the blocks' saturation and full brightness. */
export function colourOfHue(hue) {
  const h = ((Number(hue) % 360) + 360) % 360;
  const c = SATURATION;
  const x = c * (1 - Math.abs((h / 60) % 2 - 1));
  const m = 1 - c;
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return `#${hex2(r + m)}${hex2(g + m)}${hex2(b + m)}`;
}

/** True when two colours are the same hue (what the slider can tell apart). */
export const sameHue = (a, b) => Math.abs(hueOf(a) - hueOf(b)) % 360 <= 1 || Math.abs(hueOf(a) - hueOf(b)) >= 359;

/** URL of a block's icon recoloured to `colour` (suffix "" is the main prototype's icon). */
export const iconUrl = (blockId, suffix, colour) =>
  `/api/content/blocks/${encodeURIComponent(blockId)}/icon?suffix=${encodeURIComponent(suffix)}&color=${encodeURIComponent(colour)}`;
