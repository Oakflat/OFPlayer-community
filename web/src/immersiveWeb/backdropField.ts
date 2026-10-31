// The ground the immersive player stands on: the cover itself, blurred until only its colours
// and where they sit are left, a little more saturated, under a dark scrim. It is worked out
// once per cover on a small square of pixels that the screen then stretches.
//
// The Android player draws the same ground from the same numbers
// (OFplayer-android: ui/player/immersive/BackdropField.kt). Change both together; the tests on
// each side check the same sample values.

/** Side of the square the cover is reduced to. Blurred this far, nothing finer survives. */
export const BACKDROP_SIDE = 128

/** What a transparent cover, or none, stands on. */
const BASE = [11, 11, 15] as const

// A 60px blur on a cover as wide as the window, as a share of the cover's side.
const BLUR_SHARE = 0.04
const SATURATION = 1.2
const SCRIM = [18, 22, 26] as const
const SCRIM_ALPHA = 0.58

// Titles and lyrics sit directly on this, in white. Under the scrim alone a white cover is
// still mid-grey, so the brightest part is eased down: nothing below the knee changes.
const KNEE = 88
const ROLLOFF = 0.35

/** Scrim, then the knee. Writes three channels and an opaque alpha at [at]. */
function finish(r: number, g: number, b: number, into: Uint8ClampedArray, at: number) {
  let red = r * (1 - SCRIM_ALPHA) + SCRIM[0] * SCRIM_ALPHA
  let green = g * (1 - SCRIM_ALPHA) + SCRIM[1] * SCRIM_ALPHA
  let blue = b * (1 - SCRIM_ALPHA) + SCRIM[2] * SCRIM_ALPHA
  const light = 0.2126 * red + 0.7152 * green + 0.0722 * blue
  if (light > KNEE) {
    const scale = (KNEE + (light - KNEE) * ROLLOFF) / light
    red *= scale
    green *= scale
    blue *= scale
  }
  into[at] = red
  into[at + 1] = green
  into[at + 2] = blue
  into[at + 3] = 255
}

/** The ground with no cover on it, as RGBA pixels. */
export function emptyBackdropField(): Uint8ClampedArray<ArrayBuffer> {
  const field = new Uint8ClampedArray(BACKDROP_SIDE * BACKDROP_SIDE * 4)
  for (let at = 0; at < field.length; at += 4) finish(BASE[0], BASE[1], BASE[2], field, at)
  return field
}

function gaussian(sigma: number): Float32Array {
  const radius = Math.ceil(sigma * 3)
  const kernel = new Float32Array(radius * 2 + 1)
  let sum = 0
  for (let i = 0; i < kernel.length; i++) {
    kernel[i] = Math.exp(-0.5 * ((i - radius) / sigma) ** 2)
    sum += kernel[i]
  }
  return kernel.map((weight) => weight / sum)
}

/** One pass along lines [step] apart per pixel; past either end a line repeats its last pixel. */
function blur(source: Float32Array, target: Float32Array, kernel: Float32Array, step: number, lineStep: number) {
  const radius = (kernel.length - 1) / 2
  for (let line = 0; line < BACKDROP_SIDE; line++) {
    const start = line * lineStep
    for (let at = 0; at < BACKDROP_SIDE; at++) {
      let sum = 0
      for (let k = 0; k < kernel.length; k++) {
        const from = Math.min(BACKDROP_SIDE - 1, Math.max(0, at + k - radius))
        sum += source[start + from * step] * kernel[k]
      }
      target[start + at * step] = sum
    }
  }
}

/** [cover] is BACKDROP_SIDE × BACKDROP_SIDE RGBA pixels, alpha not premultiplied; so is the result. */
export function renderBackdropField(cover: Uint8ClampedArray): Uint8ClampedArray<ArrayBuffer> {
  const count = BACKDROP_SIDE * BACKDROP_SIDE
  if (cover.length !== count * 4) throw new Error(`cover must be ${BACKDROP_SIDE} x ${BACKDROP_SIDE}`)
  const channels = [new Float32Array(count), new Float32Array(count), new Float32Array(count)]
  for (let i = 0; i < count; i++) {
    const alpha = cover[i * 4 + 3] / 255
    for (let c = 0; c < 3; c++) channels[c][i] = cover[i * 4 + c] * alpha + BASE[c] * (1 - alpha)
  }
  const kernel = gaussian(BLUR_SHARE * BACKDROP_SIDE)
  const scratch = new Float32Array(count)
  for (const channel of channels) {
    blur(channel, scratch, kernel, 1, BACKDROP_SIDE)
    blur(scratch, channel, kernel, BACKDROP_SIDE, 1)
  }
  const field = new Uint8ClampedArray(count * 4)
  const clamp = (value: number) => Math.min(255, Math.max(0, value))
  for (let i = 0; i < count; i++) {
    // Blur, then saturate, as a CSS `blur() saturate()` would, on gamma-encoded values.
    const r = channels[0][i]
    const g = channels[1][i]
    const b = channels[2][i]
    const grey = 0.213 * r + 0.715 * g + 0.072 * b
    finish(
      clamp(grey + (r - grey) * SATURATION),
      clamp(grey + (g - grey) * SATURATION),
      clamp(grey + (b - grey) * SATURATION),
      field,
      i * 4,
    )
  }
  return field
}

/** Writes into [into] the picture [fraction] of the way from [from] to [to]. */
export function mixBackdropFields(from: Uint8ClampedArray, to: Uint8ClampedArray, fraction: number, into: Uint8ClampedArray) {
  const t = Math.round(Math.min(1, Math.max(0, fraction)) * 256)
  const s = 256 - t
  for (let i = 0; i < into.length; i++) into[i] = (from[i] * s + to[i] * t) >> 8
}
