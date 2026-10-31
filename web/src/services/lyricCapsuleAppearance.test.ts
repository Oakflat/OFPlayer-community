import assert from 'node:assert/strict'
import test from 'node:test'
import {
  CAPSULE_BRAND_ACCENT,
  DEFAULT_CAPSULE_APPEARANCE,
  capsuleEchoSample,
  capsuleMetrics,
  capsuleOnAccent,
  capsuleLineWidth,
  capsulePalette,
  normalizeCapsuleAppearance,
  pickArtworkAccent,
  resolveCapsuleAccent,
} from './lyricCapsuleAppearance.ts'

test('capsule appearance falls back field by field, like the host', () => {
  assert.deepEqual(normalizeCapsuleAppearance(null), DEFAULT_CAPSULE_APPEARANCE)
  assert.deepEqual(normalizeCapsuleAppearance('dark'), DEFAULT_CAPSULE_APPEARANCE)
  const mixed = normalizeCapsuleAppearance({ theme: 'neon', accent: 'teal', opacity: '80', spin: 'no', wave: 'spectrum', whenPaused: 'mini' })
  assert.equal(mixed.theme, 'dark')
  assert.equal(mixed.accent, 'teal')
  assert.equal(mixed.opacity, DEFAULT_CAPSULE_APPEARANCE.opacity)
  assert.equal(mixed.spin, true)
  assert.equal(mixed.wave, 'spectrum')
  assert.equal(mixed.whenPaused, 'mini')
  assert.equal(normalizeCapsuleAppearance({ opacity: 12 }).opacity, 60)
  assert.equal(normalizeCapsuleAppearance({ opacity: 77.6 }).opacity, 78)
})

test('row height follows font size and the second line', () => {
  assert.equal(capsuleMetrics(DEFAULT_CAPSULE_APPEARANCE).rowHeight, 48)
  assert.equal(capsuleMetrics({ ...DEFAULT_CAPSULE_APPEARANCE, fontSize: 'large' }).rowHeight, 54)
  assert.equal(capsuleMetrics({ ...DEFAULT_CAPSULE_APPEARANCE, secondLine: 'none', fontSize: 'large' }).rowHeight, 44)
  assert.equal(capsuleMetrics({ ...DEFAULT_CAPSULE_APPEARANCE, wave: 'off' }).waveWidth, 0)
})

test('width follows the line being sung, in 8px steps within bounds', () => {
  const long = capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '晚风把路灯一盏盏点亮')
  const short = capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '短句')
  assert.equal(long % 8, 0)
  assert.ok(long > 300 && long < 540, String(long))
  assert.ok(short < long, 'a short line gives a narrower capsule')
  assert.ok(capsuleLineWidth({ ...DEFAULT_CAPSULE_APPEARANCE, wave: 'off' }, '晚风把路灯一盏盏点亮') < long)
  assert.equal(capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, 'a'), 224)
  assert.equal(capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '长'.repeat(80)), 540)
  assert.equal(capsuleLineWidth({ ...DEFAULT_CAPSULE_APPEARANCE, width: 'fixed' }, '短句'), 440)
  for (const secondLine of ['meta', 'translation', 'next', 'none'] as const)
    assert.equal(capsuleLineWidth({ ...DEFAULT_CAPSULE_APPEARANCE, secondLine }, '短句'), short)
})

test('actual font geometry drives width, including short to medium lyric changes', () => {
  const short = capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '短句', 30)
  const medium = capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '天色慢慢暗下来', 105)
  assert.ok(medium > short, 'common short lines no longer share the old 300px floor')
  assert.ok(capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, 'WWWWWWWWWWWWWWWWWW', 260) >
    capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, 'iiiiiiiiiiiiiiiiii', 65), 'equal-length lyrics need different rendered widths')
  assert.equal(capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, 'oversized', 5000), 540)
  for (const invalid of [Number.NaN, Number.POSITIVE_INFINITY, -10])
    assert.equal(capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '晚风把路灯一盏盏点亮', invalid),
      capsuleLineWidth(DEFAULT_CAPSULE_APPEARANCE, '晚风把路灯一盏盏点亮'))
})

test('a profile saved with the old per-song width follows the lines again', () => {
  assert.equal(normalizeCapsuleAppearance({ width: 'song' }).width, 'line')
  assert.equal(normalizeCapsuleAppearance({ width: 'fixed' }).width, 'fixed')
})

function solid(r: number, g: number, b: number, count = 64): number[] {
  return Array.from({ length: count }, () => [r, g, b, 255]).flat()
}

test('cover accent picks the dominant vivid colour and ignores grey covers', () => {
  const coral = pickArtworkAccent([...solid(230, 110, 90, 48), ...solid(40, 40, 40, 16)])
  assert.ok(coral, 'a coral cover yields an accent')
  const [r, g, b] = [1, 3, 5].map((offset) => Number.parseInt(coral.slice(offset, offset + 2), 16))
  assert.ok(r > g && g > b, coral)
  assert.equal(pickArtworkAccent(solid(128, 128, 128)), null)
  assert.equal(pickArtworkAccent(solid(8, 6, 10)), null)
  assert.equal(pickArtworkAccent([]), null)
  assert.equal(pickArtworkAccent(solid(230, 110, 90).map((value, index) => (index % 4 === 3 ? 0 : value))), null)
})

test('accent and palette fall back to the brand look without a cover colour', () => {
  assert.equal(resolveCapsuleAccent(DEFAULT_CAPSULE_APPEARANCE, null), CAPSULE_BRAND_ACCENT)
  assert.equal(resolveCapsuleAccent(DEFAULT_CAPSULE_APPEARANCE, '#ff8f73'), '#ff8f73')
  assert.equal(resolveCapsuleAccent({ ...DEFAULT_CAPSULE_APPEARANCE, accent: 'teal' }, '#ff8f73'), '#2ec4a6')
  assert.equal(capsulePalette('dark', DEFAULT_CAPSULE_APPEARANCE.opacity, null).background, 'rgba(0, 0, 0, 1)')
  assert.deepEqual(capsulePalette('cover', 92, null), capsulePalette('dark', 92, null))
  assert.notEqual(capsulePalette('cover', 92, '#ff8f73').background, capsulePalette('dark', 92, null).background)
  assert.equal(capsulePalette('light', 70, null).background, 'rgba(250, 250, 252, 0.7)')
  assert.equal(capsulePalette('light', 70, null).tone, 'light')
  assert.equal(capsuleOnAccent('#f2b03d'), '#141414')
  assert.equal(capsuleOnAccent('#2a8bfd'), '#ffffff')
})

test('echo samples stay low when paused and grow with loudness', () => {
  assert.equal(capsuleEchoSample([1, 1, 1, 1, 1, 1, 1, 1], false), 0.04)
  assert.ok(capsuleEchoSample([0.6, 0.6, 0.6, 0.6, 0.6, 0.6, 0.6, 0.6], true) > capsuleEchoSample([0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1], true))
  assert.equal(capsuleEchoSample([1, 1, 1, 1, 1, 1, 1, 1], true), 1)
})
