import assert from 'node:assert/strict'
import test from 'node:test'
import { BACKDROP_SIDE, emptyBackdropField, mixBackdropFields, renderBackdropField } from './backdropField.ts'

const side = BACKDROP_SIDE

function cover(pixel: (x: number, y: number) => [number, number, number, number?]) {
  const pixels = new Uint8ClampedArray(side * side * 4)
  for (let y = 0; y < side; y++) {
    for (let x = 0; x < side; x++) {
      const [r, g, b, a = 255] = pixel(x, y)
      pixels.set([r, g, b, a], (y * side + x) * 4)
    }
  }
  return pixels
}

const at = (field: Uint8ClampedArray, x: number, y: number) => [...field.subarray((y * side + x) * 4, (y * side + x) * 4 + 3)]
const light = ([r, g, b]: number[]) => 0.2126 * r + 0.7152 * g + 0.0722 * b
const near = (actual: number[], expected: number[]) =>
  assert.ok(actual.every((value, i) => Math.abs(value - expected[i]) <= 1), `${actual} is not ${expected}`)

test('without a cover the ground is the base under its scrim', () => {
  // #0b0b0f under rgba(18, 22, 26, 0.58).
  assert.deepEqual([...emptyBackdropField().subarray(0, 4)], [15, 17, 21, 255])
  // A cover that is entirely see-through is no cover.
  near(at(renderBackdropField(cover(() => [255, 0, 0, 0])), 5, 5), [15, 17, 21])
})

test('a cover of one colour gives that colour, richer and darkened', () => {
  const ground = renderBackdropField(cover(() => [200, 40, 40]))
  // saturate(1.2) takes (200, 40, 40) to (225, 33, 33); the scrim leaves 42% of it.
  near(at(ground, 0, 0), [105, 27, 29])
  assert.deepEqual(at(ground, 77, 31), at(ground, 0, 0))
})

test('a hard edge is spread wide and smooth', () => {
  const ground = renderBackdropField(cover((x) => (x < side / 2 ? [0, 0, 0] : [255, 255, 255])))
  const row = Array.from({ length: side }, (_, x) => light(at(ground, x, 0)))
  assert.ok(row.every((value, x) => x === 0 || value >= row[x - 1]))
  const low = row[0]
  const high = row[side - 1]
  const rise = row.filter((value) => value > low + (high - low) * 0.1 && value < low + (high - low) * 0.9).length
  // About 2.6 sigma, and sigma is 4% of the side: nothing of the edge is left but a slope.
  assert.ok(rise >= 9 && rise <= 18, `edge spread over ${rise} pixels`)
})

test('a white cover still leaves the ground dark enough for white text', () => {
  const white = light(at(renderBackdropField(cover(() => [255, 255, 255])), 0, 0))
  // The scrim alone would leave about 120.
  assert.ok(white >= 88 && white <= 100, `${white}`)
  assert.ok(light(at(renderBackdropField(cover(() => [128, 128, 128])), 0, 0)) < white)
})

test('the ground matches the Android player pixel for pixel', () => {
  // The same cover and the same expectations as BackdropFieldTest in OFplayer-android.
  const ground = renderBackdropField(cover((x, y) => [2 * x, 2 * y, x + y]))
  near(at(ground, 0, 0), [12, 14, 17])
  near(at(ground, 64, 64), [64, 67, 69])
  near(at(ground, 127, 127), [97, 99, 101])
  near(at(ground, 100, 20), [105, 26, 69])
  near(at(ground, 10, 110), [10, 109, 61])
})

test('mixing runs from one ground to the other', () => {
  const from = cover(() => [16, 32, 48])
  const to = cover(() => [80, 96, 112])
  const into = new Uint8ClampedArray(from.length)
  mixBackdropFields(from, to, 0, into)
  assert.deepEqual(into, from)
  mixBackdropFields(from, to, 1, into)
  assert.deepEqual(into, to)
  mixBackdropFields(from, to, 0.5, into)
  assert.deepEqual([...into.subarray(0, 4)], [48, 64, 80, 255])
})
