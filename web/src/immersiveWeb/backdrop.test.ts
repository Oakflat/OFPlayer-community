import assert from 'node:assert/strict'
import test from 'node:test'
import { createBackdrop } from './backdrop.ts'
import { BACKDROP_SIDE, emptyBackdropField } from './backdropField.ts'

const flat = (r: number, g: number, b: number) => {
  const pixels = new Uint8ClampedArray(BACKDROP_SIDE * BACKDROP_SIDE * 4)
  for (let i = 0; i < pixels.length; i += 4) pixels.set([r, g, b, 255], i)
  return pixels
}

function fixture() {
  // What the painter was last given, as a colour, and every moment it was asked to paint.
  let shown: number[] | null = null
  const draws: number[] = []
  const backdrop = createBackdrop({
    setField(pixels) { shown = pixels && [...pixels.subarray(0, 3)] },
    draw(seconds) { draws.push(seconds) },
  })
  return { backdrop, draws, get shown() { return shown } }
}

const bare = [...emptyBackdropField().subarray(0, 3)]

test('it begins as the bare ground, which needs no picture', () => {
  const f = fixture()
  assert.equal(f.shown, null)
  assert.deepEqual(f.draws, [])
})

test('a ground shown without animation is simply there', () => {
  const f = fixture()
  f.backdrop.show(flat(90, 60, 30), false)
  assert.deepEqual(f.shown, [90, 60, 30])
  assert.deepEqual(f.draws, [0])
  assert.equal(f.backdrop.frame(1000, false), false)
  f.backdrop.show(null, false)
  assert.equal(f.shown, null)
})

test('an animated change is mixed in over 900 ms from its first frame', () => {
  const f = fixture()
  f.backdrop.show(flat(15 + 64, 17 + 64, 21 + 64), true)
  // Nothing changes until a frame carries it.
  assert.equal(f.shown, null)
  assert.equal(f.backdrop.frame(5000, false), true)
  assert.deepEqual(f.shown, bare)
  assert.equal(f.backdrop.frame(5450, false), true)
  assert.deepEqual(f.shown, [15 + 32, 17 + 32, 21 + 32])
  assert.equal(f.backdrop.frame(5899, false), true)
  assert.equal(f.backdrop.frame(5900, false), false)
  assert.deepEqual(f.shown, [15 + 64, 17 + 64, 21 + 64])
  assert.equal(f.draws.length, 4)
})

test('a change that arrives mid-fade starts from whatever shows', () => {
  const f = fixture()
  f.backdrop.show(flat(200, 0, 0), false)
  f.backdrop.show(flat(0, 200, 0), true)
  f.backdrop.frame(0, false)
  f.backdrop.frame(450, false)
  assert.deepEqual(f.shown, [100, 100, 0])
  f.backdrop.show(flat(0, 0, 200), true)
  assert.equal(f.backdrop.frame(500, false), true)
  assert.deepEqual(f.shown, [100, 100, 0])
  f.backdrop.frame(950, false)
  assert.deepEqual(f.shown, [50, 50, 100])
  assert.equal(f.backdrop.frame(1400, false), false)
  assert.deepEqual(f.shown, [0, 0, 200])
})

test('losing the cover fades to the bare ground', () => {
  const f = fixture()
  f.backdrop.show(flat(15 + 128, 17, 21), false)
  f.backdrop.show(null, true)
  f.backdrop.frame(0, false)
  f.backdrop.frame(450, false)
  assert.deepEqual(f.shown, [15 + 64, 17, 21])
  assert.equal(f.backdrop.frame(900, false), false)
  assert.equal(f.shown, null)
})

test('settling ends a fade where it was going', () => {
  const f = fixture()
  f.backdrop.show(flat(10, 20, 30), true)
  f.backdrop.frame(0, false)
  f.backdrop.frame(100, false)
  f.backdrop.settle()
  assert.deepEqual(f.shown, [10, 20, 30])
  assert.equal(f.backdrop.frame(200, false), false)
})

test('showing the same ground again starts nothing', () => {
  const f = fixture()
  const ground = flat(10, 20, 30)
  f.backdrop.show(ground, false)
  f.backdrop.show(ground, true)
  assert.equal(f.backdrop.frame(0, false), false)
  assert.deepEqual(f.shown, [10, 20, 30])
})

test('the flow runs while music plays and comes to rest after it stops', () => {
  const f = fixture()
  f.backdrop.show(flat(10, 20, 30), false)
  let at = 0
  for (; at < 3000; at += 33) assert.equal(f.backdrop.frame(at, true), true)
  const playing = f.draws.at(-1)!
  assert.ok(playing > 2 && playing < 3, `${playing}`)

  let coasting = 0
  while (f.backdrop.frame(at, false)) {
    at += 33
    coasting++
  }
  assert.ok(coasting > 30 && coasting < 150, `${coasting} frames`)
  const rest = f.draws.at(-1)!
  assert.ok(rest > playing)
  // A still ground stays where it stopped, however long until the next frame.
  f.backdrop.frame(at + 60_000, false)
  f.backdrop.draw()
  assert.equal(f.draws.at(-1), rest)
  assert.ok(f.draws.every((seconds, i) => i === 0 || seconds >= f.draws[i - 1]))
})
