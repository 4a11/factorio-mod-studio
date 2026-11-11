// Tests for wwwroot/colour-logic.js: the hue of a block colour, the colour a hue is written as, and the preview URL.
import test from 'node:test';
import assert from 'node:assert/strict';

const { parseHex, hueOf, colourOfHue, sameHue, iconUrl } = await import('../../src/FactorioModStudio.Server/wwwroot/colour-logic.js');

test('parseHex reads #rrggbb and rrggbb, nothing else', () => {
  assert.deepEqual(parseHex('#ff0000'), { r: 1, g: 0, b: 0 });
  assert.deepEqual(parseHex(' 00FF00 '), { r: 0, g: 1, b: 0 });
  for (const bad of [null, '', '#fff', 'red', '#gg0000']) assert.equal(parseHex(bad), null);
});

test('hueOf gives the hue, 0 for grey', () => {
  assert.equal(hueOf('#ff0000'), 0);
  assert.equal(hueOf('#00ff00'), 120);
  assert.equal(hueOf('#0000ff'), 240);
  assert.equal(hueOf('#4cccff'), 197); // the item block's { r = 0.3, g = 0.8, b = 1.0 }
  assert.equal(hueOf('#808080'), 0);
});

test('colourOfHue keeps the hue it is given, at the blocks saturation', () => {
  for (let h = 0; h < 360; h += 15) assert.ok(Math.abs(hueOf(colourOfHue(h)) - h) <= 1, `hue ${h}`);
  assert.equal(colourOfHue(0), '#ff4d4d');
  assert.equal(colourOfHue(360), colourOfHue(0));
});

test('sameHue compares what the slider can tell apart', () => {
  assert.ok(sameHue('#ff0000', '#ff4d4d'));
  assert.ok(sameHue('#ff0001', '#ff0100'));
  assert.ok(!sameHue('#ff0000', '#00ff00'));
});

test('iconUrl names the block, the icon and the colour', () => {
  assert.equal(iconUrl('gun', '-ammo', '#ff0000'), '/api/content/blocks/gun/icon?suffix=-ammo&color=%23ff0000');
});
