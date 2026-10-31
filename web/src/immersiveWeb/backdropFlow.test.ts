import assert from 'node:assert/strict'
import test from 'node:test'
import { BACKDROP_CELLS, BACKDROP_FLOATS, BACKDROP_LAYERS, backdropVertices, createFlowClock } from './backdropFlow.ts'

const cells = BACKDROP_CELLS
const [ground, drift] = BACKDROP_LAYERS
const screens: Array<[number, number]> = [[1920, 1080], [3840, 2160], [1080, 2400], [900, 900]]

function mesh(layer: typeof ground, width: number, height: number, seconds: number) {
  const out = new Float32Array(BACKDROP_FLOATS)
  backdropVertices(layer, width, height, seconds, out)
  return { x: (row: number, column: number) => out[(row * (cells + 1) + column) * 2], y: (row: number, column: number) => out[(row * (cells + 1) + column) * 2 + 1] }
}

/** Fifteen minutes, in steps no period of the flow divides. */
function* moments(until = 900) {
  for (let seconds = 0; seconds < until; seconds += 0.73) yield seconds
}

function distanceToSegment(px: number, py: number, ax: number, ay: number, bx: number, by: number) {
  const dx = bx - ax
  const dy = by - ay
  const t = Math.min(1, Math.max(0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
  return Math.hypot(px - (ax + dx * t), py - (ay + dy * t))
}

test('the mesh reaches past every corner of the screen whichever way it has turned', () => {
  // The outline, walked once round and back to where it began.
  const outline: Array<[number, number]> = []
  for (let i = 0; i <= cells; i++) outline.push([0, i])
  for (let i = 1; i <= cells; i++) outline.push([i, cells])
  for (let i = cells - 1; i >= 0; i--) outline.push([cells, i])
  for (let i = cells - 1; i >= 0; i--) outline.push([i, 0])
  for (const [width, height] of screens) {
    const corner = Math.hypot(width, height) / 2
    for (const layer of BACKDROP_LAYERS) {
      for (const seconds of moments()) {
        const m = mesh(layer, width, height, seconds)
        // No stretch of the outline may come nearer the centre than the corners are.
        let nearest = Infinity
        for (let i = 1; i < outline.length; i++) {
          const [ar, ac] = outline[i - 1]
          const [br, bc] = outline[i]
          nearest = Math.min(nearest, distanceToSegment(width / 2, height / 2, m.x(ar, ac), m.y(ar, ac), m.x(br, bc), m.y(br, bc)))
        }
        assert.ok(nearest >= corner, `${width}x${height} at ${seconds}s: ${nearest} < ${corner}`)
      }
    }
  }
})

test('the mesh stretches but never folds over itself', () => {
  for (const layer of BACKDROP_LAYERS) {
    for (const seconds of moments()) {
      const m = mesh(layer, 1920, 1080, seconds)
      for (let row = 0; row < cells; row++) {
        for (let column = 0; column < cells; column++) {
          const ax = m.x(row, column + 1) - m.x(row, column)
          const ay = m.y(row, column + 1) - m.y(row, column)
          const bx = m.x(row + 1, column) - m.x(row, column)
          const by = m.y(row + 1, column) - m.y(row, column)
          assert.ok(ax * by - ay * bx > 0, `cell ${row},${column} at ${seconds}s`)
        }
      }
    }
  }
})

test('it starts with the cover the right way up', () => {
  const m = mesh(ground, 1920, 1080, 0)
  assert.ok(m.x(0, 0) < 0 && m.y(0, 0) < 0)
  assert.ok(m.x(0, cells) > 1920 && m.y(0, cells) < 0)
  assert.ok(m.x(cells, 0) < 0 && m.y(cells, 0) > 1080)
})

test('what is on screen always moves, and never fast', () => {
  const diagonal = Math.hypot(1920, 1080)
  for (const layer of BACKDROP_LAYERS) {
    for (const seconds of moments(240)) {
      const now = mesh(layer, 1920, 1080, seconds)
      const next = mesh(layer, 1920, 1080, seconds + 1)
      let fastest = 0
      for (let row = 0; row <= cells; row++) {
        for (let column = 0; column <= cells; column++) {
          const x = now.x(row, column)
          const y = now.y(row, column)
          if (x < 0 || x > 1920 || y < 0 || y > 1080) continue
          fastest = Math.max(fastest, Math.hypot(next.x(row, column) - x, next.y(row, column) - y))
        }
      }
      assert.ok(fastest > diagonal * 0.004 && fastest < diagonal * 0.1, `at ${seconds}s moves ${fastest}`)
    }
  }
})

test('the mesh is where the Android player puts it', () => {
  // The same points and the same expectations as BackdropFlowTest in OFplayer-android.
  const expectPoint = (layer: typeof ground, width: number, height: number, seconds: number, row: number, column: number, x: number, y: number) => {
    const m = mesh(layer, width, height, seconds)
    assert.ok(Math.abs(m.x(row, column) - x) < 1 && Math.abs(m.y(row, column) - y) < 1, `${m.x(row, column)}, ${m.y(row, column)}`)
  }
  expectPoint(ground, 1080, 2400, 0, 0, 0, -992.4, -417.52)
  expectPoint(ground, 1080, 2400, 12.5, 3, 11, 1790.58, 717.97)
  expectPoint(drift, 1080, 2400, 47, 9, 2, 1235.41, 2827.84)
  expectPoint(drift, 1920, 1080, 300.25, 16, 16, -124.04, -1541.33)
})

test('the flow eases into motion and comes to rest after a pause', () => {
  const clock = createFlowClock()
  for (let i = 0; i < 100; i++) assert.equal(clock.advance(0.033, true), true)
  // 3.3 seconds of playing, less what the ease in held back.
  assert.ok(clock.seconds > 2.4 && clock.seconds < 2.8, `${clock.seconds}`)

  const paused = clock.seconds
  let frames = 0
  while (clock.advance(0.033, false)) frames++
  // It carries on for a few seconds, ever slower, then stays where it is.
  assert.ok(frames > 60 && frames < 120, `${frames} frames`)
  assert.ok(clock.seconds > paused + 0.4 && clock.seconds < paused + 0.8)
  const rest = clock.seconds
  assert.equal(clock.advance(0.033, false), false)
  assert.equal(clock.seconds, rest)
})

test('a long gap between frames is not a jump', () => {
  const clock = createFlowClock()
  for (let i = 0; i < 200; i++) clock.advance(0.033, true)
  const before = clock.seconds
  clock.advance(45, true)
  assert.ok(clock.seconds - before <= 0.1)
})
