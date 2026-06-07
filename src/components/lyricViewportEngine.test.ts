import test from 'node:test'
import assert from 'node:assert/strict'
import {
  LYRIC_NO_ACTIVE_DISTANCE,
  LYRIC_SCROLL_SETTLE_THRESHOLD_PX,
  LYRIC_WHEEL_LINE_DELTA_PX,
  clampLyricScrollTarget,
  normalizeLyricWheelDelta,
  resolveLyricFocusIndex,
  resolveLyricLineDistance,
  resolveLyricScrollDamping,
  resolveLyricScrollFrameDelta,
  resolveLyricScrollStep,
  resolveLyricScrollTarget,
  resolveLyricStageStyle,
} from './lyricViewportEngine.ts'

test('resolveLyricLineDistance reports stable relative lyric state', () => {
  assert.equal(resolveLyricLineDistance(4, 2), 2)
  assert.equal(resolveLyricLineDistance(1, 2), -1)
  assert.equal(resolveLyricLineDistance(0, -1), LYRIC_NO_ACTIVE_DISTANCE)
})

test('resolveLyricFocusIndex falls back to the pending lyric before the first active line', () => {
  assert.equal(resolveLyricFocusIndex(2, 3), 2)
  assert.equal(resolveLyricFocusIndex(-1, 0), 0)
  assert.equal(resolveLyricFocusIndex(-1, -1), -1)
})

test('resolveLyricScrollTarget centers line at the configured alignment point', () => {
  assert.equal(
    resolveLyricScrollTarget({
      alignPosition: 0.4,
      containerHeight: 500,
      lineHeight: 80,
      lineOffsetTop: 360,
    }),
    200,
  )
})

test('resolveLyricStageStyle renders viewport offset as a compositor transform', () => {
  assert.deepEqual(resolveLyricStageStyle(42), {
    transform: 'translate3d(0, -42px, 0)',
  })
  assert.deepEqual(resolveLyricStageStyle(Number.NaN), {
    transform: 'translate3d(0, 0px, 0)',
  })
})

test('clampLyricScrollTarget keeps native scroll goals inside the container', () => {
  const metrics = { clientHeight: 400, scrollHeight: 1000 }

  assert.equal(clampLyricScrollTarget(metrics, -10), 0)
  assert.equal(clampLyricScrollTarget(metrics, 250), 250)
  assert.equal(clampLyricScrollTarget(metrics, 700), 600)
})

test('normalizeLyricWheelDelta converts browser wheel modes into pixels', () => {
  assert.equal(normalizeLyricWheelDelta(2, 0, 500), 2)
  assert.equal(normalizeLyricWheelDelta(2, 1, 500), 2 * LYRIC_WHEEL_LINE_DELTA_PX)
  assert.equal(normalizeLyricWheelDelta(2, 2, 500), 1000)
  assert.equal(normalizeLyricWheelDelta(Number.NaN, 0, 500), 0)
})

test('resolveLyricScrollFrameDelta caps long frames and seeds the first frame', () => {
  assert.equal(resolveLyricScrollFrameDelta(1000, 0), 1000 / 60)
  assert.equal(resolveLyricScrollFrameDelta(1100, 1000), 50)
  assert.equal(resolveLyricScrollFrameDelta(1016, 1000), 16)
})

test('resolveLyricScrollDamping is stronger while lyrics are playing', () => {
  const playingDamping = resolveLyricScrollDamping(1016, 1000, true)
  const idleDamping = resolveLyricScrollDamping(1016, 1000, false)

  assert.ok(playingDamping > idleDamping)
})

test('resolveLyricScrollStep settles tiny differences exactly at the goal', () => {
  const nextFrame = resolveLyricScrollStep({
    currentScrollTop: 100,
    goal: 100 + LYRIC_SCROLL_SETTLE_THRESHOLD_PX / 2,
    lastFrameAt: 1000,
    playing: true,
    timestamp: 1016,
  })

  assert.equal(nextFrame.done, true)
  assert.equal(nextFrame.scrollTop, 100 + LYRIC_SCROLL_SETTLE_THRESHOLD_PX / 2)
})

test('resolveLyricScrollStep moves toward the goal without overshooting', () => {
  const nextFrame = resolveLyricScrollStep({
    currentScrollTop: 100,
    goal: 200,
    lastFrameAt: 1000,
    playing: true,
    timestamp: 1016,
  })

  assert.equal(nextFrame.done, false)
  assert.ok(nextFrame.scrollTop > 100)
  assert.ok(nextFrame.scrollTop < 200)
})
