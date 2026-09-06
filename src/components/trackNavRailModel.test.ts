import test from 'node:test'
import assert from 'node:assert/strict'
import {
  NAV_RAIL_GROUP_SIZE,
  NAV_RAIL_MARKER_SYMBOLS,
  NAV_RAIL_MAX_MARKERS,
  NAV_RAIL_SCROLL_MAX_DURATION_MS,
  NAV_RAIL_SCROLL_MIN_DURATION_MS,
  buildNavRailMarkers,
  clampPreviewTop,
  navRailGroupScrollTop,
  resolveActiveMarkerIndex,
  resolveNavRailMarkerSymbol,
  resolveNavRailScrollDuration,
  resolveNavRailScrollFrame,
} from './trackNavRailModel.ts'

test('buildNavRailMarkers returns no markers for empty or invalid totals', () => {
  assert.deepEqual(buildNavRailMarkers(0), [])
  assert.deepEqual(buildNavRailMarkers(-12), [])
  assert.deepEqual(buildNavRailMarkers(Number.NaN), [])
})

test('buildNavRailMarkers groups every five tracks into one marker', () => {
  const markers = buildNavRailMarkers(12)

  assert.equal(markers.length, 3)
  assert.deepEqual(
    markers.map((marker) => [marker.groupIndex, marker.startTrack, marker.endTrack]),
    [
      [0, 1, 5],
      [1, 6, 10],
      [2, 11, 12],
    ],
  )
})

test('buildNavRailMarkers caps marker count and distributes groups proportionally', () => {
  const totalCount = 10_000
  const groupCount = Math.ceil(totalCount / NAV_RAIL_GROUP_SIZE)
  const markers = buildNavRailMarkers(totalCount)

  assert.equal(markers.length, NAV_RAIL_MAX_MARKERS)
  assert.equal(markers[0].groupIndex, 0)
  assert.equal(markers[0].startTrack, 1)
  assert.equal(markers[0].endTrack, NAV_RAIL_GROUP_SIZE)
  assert.equal(markers[markers.length - 1].groupIndex, groupCount - 1)
  assert.equal(markers[markers.length - 1].endTrack, totalCount)

  const groupIndexes = markers.map((marker) => marker.groupIndex)
  const strictlyIncreasing = groupIndexes.every(
    (groupIndex, index) => index === 0 || groupIndex > groupIndexes[index - 1],
  )
  assert.equal(strictlyIncreasing, true)
})

test('buildNavRailMarkers keeps a single group on one marker', () => {
  const markers = buildNavRailMarkers(3)

  assert.equal(markers.length, 1)
  assert.deepEqual(markers[0], {
    key: '0',
    groupIndex: 0,
    startTrack: 1,
    endTrack: 3,
  })
})

test('resolveActiveMarkerIndex follows the viewport group', () => {
  const markers = buildNavRailMarkers(100)
  const rowHeight = 76

  assert.equal(resolveActiveMarkerIndex(markers, 0, rowHeight), 0)
  assert.equal(resolveActiveMarkerIndex(markers, 6 * NAV_RAIL_GROUP_SIZE * rowHeight, rowHeight), 6)
  assert.equal(resolveActiveMarkerIndex(markers, 99 * NAV_RAIL_GROUP_SIZE * rowHeight, rowHeight), markers.length - 1)
  assert.equal(resolveActiveMarkerIndex([], 100, rowHeight), -1)
})

test('resolveActiveMarkerIndex snaps to the nearest capped marker', () => {
  const markers = buildNavRailMarkers(10_000)
  const lastGroup = Math.ceil(10_000 / NAV_RAIL_GROUP_SIZE) - 1
  const activeIndex = resolveActiveMarkerIndex(markers, Number.MAX_SAFE_INTEGER, 76)

  assert.equal(markers[activeIndex].groupIndex, lastGroup)
})

test('navRailGroupScrollTop targets the first row of the group', () => {
  const markers = buildNavRailMarkers(50)

  assert.equal(navRailGroupScrollTop(markers[0], 76), 0)
  assert.equal(navRailGroupScrollTop(markers[4], 76), 4 * NAV_RAIL_GROUP_SIZE * 76)
  assert.equal(navRailGroupScrollTop(markers[4], 0), 4 * NAV_RAIL_GROUP_SIZE * 76)
})

test('resolveNavRailMarkerSymbol cycles through distinct music symbols', () => {
  assert.equal(resolveNavRailMarkerSymbol(0).glyph, '♪')
  assert.equal(resolveNavRailMarkerSymbol(1).glyph, '𝄞')
  assert.equal(resolveNavRailMarkerSymbol(2).glyph, '♫')
  assert.equal(resolveNavRailMarkerSymbol(3).glyph, '♯')
  assert.equal(resolveNavRailMarkerSymbol(8).glyph, resolveNavRailMarkerSymbol(0).glyph)
  assert.equal(
    resolveNavRailMarkerSymbol(NAV_RAIL_MARKER_SYMBOLS.length * 3 + 2).glyph,
    NAV_RAIL_MARKER_SYMBOLS[2].glyph,
  )
  assert.equal(resolveNavRailMarkerSymbol(-1).glyph, NAV_RAIL_MARKER_SYMBOLS[0].glyph)

  const uniqueGlyphs = new Set(
    NAV_RAIL_MARKER_SYMBOLS.map((_, index) => resolveNavRailMarkerSymbol(index).glyph),
  )
  assert.equal(uniqueGlyphs.size, NAV_RAIL_MARKER_SYMBOLS.length)

  assert.deepEqual(NAV_RAIL_MARKER_SYMBOLS.map(symbol => symbol.icon), [
    'eighth-note', 'treble-clef', 'eighth-pair', 'sharp',
    'quarter-note', 'bass-clef', 'sixteenth-pair', 'flat',
  ])
})

test('resolveNavRailScrollDuration scales with distance and stays within bounds', () => {
  assert.equal(resolveNavRailScrollDuration(0), NAV_RAIL_SCROLL_MIN_DURATION_MS)
  assert.equal(resolveNavRailScrollDuration(Number.NaN), NAV_RAIL_SCROLL_MIN_DURATION_MS)
  assert.equal(resolveNavRailScrollDuration(1_000_000), NAV_RAIL_SCROLL_MAX_DURATION_MS)

  const shortJump = resolveNavRailScrollDuration(2_000)
  const longJump = resolveNavRailScrollDuration(15_000)

  assert.equal(shortJump > NAV_RAIL_SCROLL_MIN_DURATION_MS, true)
  assert.equal(longJump > shortJump, true)
  assert.equal(longJump < NAV_RAIL_SCROLL_MAX_DURATION_MS, true)
})

test('resolveNavRailScrollFrame eases out towards the target', () => {
  const firstFrame = resolveNavRailScrollFrame(0, 1000, 0, 400)
  assert.deepEqual(firstFrame, { scrollTop: 0, done: false })

  const midFrame = resolveNavRailScrollFrame(0, 1000, 200, 400)
  assert.equal(midFrame.done, false)
  assert.equal(midFrame.scrollTop > 500, true)
  assert.equal(midFrame.scrollTop < 1000, true)

  const lastFrame = resolveNavRailScrollFrame(0, 1000, 400, 400)
  assert.deepEqual(lastFrame, { scrollTop: 1000, done: true })

  const overtimeFrame = resolveNavRailScrollFrame(200, 1000, 900, 400)
  assert.deepEqual(overtimeFrame, { scrollTop: 1000, done: true })

  const reverseFrame = resolveNavRailScrollFrame(1000, 0, 400, 400)
  assert.deepEqual(reverseFrame, { scrollTop: 0, done: true })
})

test('scroll frames follow CSS bezier time coordinates without overshooting in either direction', () => {
  // Independently sample known parametric points and use x as elapsed time.
  for (const t of [0.1, 0.25, 0.5, 0.75, 0.9]) {
    const x = 3 * (1 - t) ** 2 * t * 0.22 + 3 * (1 - t) * t ** 2 * 0.36 + t ** 3
    const y = 3 * (1 - t) ** 2 * t + 3 * (1 - t) * t ** 2 + t ** 3
    const forward = resolveNavRailScrollFrame(0, 20_000, x * 400, 400)
    const reverse = resolveNavRailScrollFrame(20_000, 0, x * 400, 400)
    assert.ok(Math.abs(forward.scrollTop - y * 20_000) < 0.01)
    assert.ok(Math.abs(reverse.scrollTop - (1 - y) * 20_000) < 0.01)
  }

  const frames = Array.from({ length: 101 }, (_, index) =>
    resolveNavRailScrollFrame(0, 20_000, index * 4, 400).scrollTop,
  )
  assert.ok(frames.every((value, index) => value >= 0 && value <= 20_000 && (index === 0 || value >= frames[index - 1])))
  assert.deepEqual(resolveNavRailScrollFrame(200, 1000, -50, 400), { scrollTop: 200, done: false })
  assert.deepEqual(resolveNavRailScrollFrame(200, 1000, 400, Number.NaN), { scrollTop: 1000, done: true })
})

test('clampPreviewTop keeps the preview card inside the viewport', () => {
  assert.equal(clampPreviewTop(120, 200, 800), 120)
  assert.equal(clampPreviewTop(-40, 200, 800), 8)
  assert.equal(clampPreviewTop(700, 200, 800), 592)
  assert.equal(clampPreviewTop(700, 2000, 800), 8)
})
