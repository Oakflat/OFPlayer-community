// How the ground moves. The picture is laid on a square mesh larger than the screen, and the
// mesh turns slowly while its points swell and ripple, so the colours stretch and slide like
// something poured. It is drawn twice: the second layer turns the other way, part see-through,
// so where the two cross the colours keep mixing differently and the motion never reads as one
// picture turning.
//
// Everything is a function of the seconds the flow has run. No period divides another, so it
// does not visibly repeat, and at zero the first layer is the cover the right way up.
//
// The Android player moves its ground by the same numbers
// (OFplayer-android: ui/player/immersive/BackdropFlow.kt). Change both together; the tests on
// each side check the same sample points.

/** Cells along each side of the mesh. Fine enough that its straight edges never show. */
export const BACKDROP_CELLS = 16
export const BACKDROP_FLOATS = (BACKDROP_CELLS + 1) * (BACKDROP_CELLS + 1) * 2

export type BackdropLayer = {
  /** Seconds for one full turn; negative turns the other way. */
  turnSeconds: number
  startTurn: number
  /** Size against the smallest mesh that still covers the screen. */
  scale: number
  phase: number
  alpha: number
}

export const BACKDROP_LAYERS: readonly BackdropLayer[] = [
  { turnSeconds: 120, startTurn: 0, scale: 1, phase: 0, alpha: 1 },
  { turnSeconds: -80, startTurn: 0.31, scale: 1.25, phase: 2.4, alpha: 0.4 },
]

// Shares of the mesh's half side. A point never leaves its place by more than
// (SWELL + RIPPLE) * sqrt(2), the mesh never shrinks below 1 - BREATH, and its centre never
// wanders further than WANDER * sqrt(2): 0.97 - 0.156 - 0.042 leaves the mesh reaching past
// COVER of its half side in every direction, whichever way it is turned.
const SWELL = 0.075
const RIPPLE = 0.035
const BREATH = 0.03
const WANDER = 0.03
const COVER = 0.77

const TAU = Math.PI * 2
const PI = Math.PI

/** Half the side of [layer]'s mesh for a [width] × [height] screen. */
export function backdropReach(layer: BackdropLayer, width: number, height: number): number {
  return Math.hypot(width, height) / 2 / COVER * layer.scale
}

/** Fills [out] with the x, y of each mesh point, row by row. */
export function backdropVertices(layer: BackdropLayer, width: number, height: number, seconds: number, out: Float32Array) {
  const reach = backdropReach(layer, width, height)
  const phase = layer.phase
  const breath = 1 + BREATH * Math.sin(TAU * seconds / 41 + phase)
  const angle = TAU * (layer.startTurn + seconds / layer.turnSeconds)
  const cosine = Math.cos(angle)
  const sine = Math.sin(angle)
  const centerX = width / 2 + reach * WANDER * Math.sin(TAU * seconds / 37 + phase)
  const centerY = height / 2 + reach * WANDER * Math.cos(TAU * seconds / 29 + phase)
  const swellX = TAU * seconds / 19 + phase
  const swellY = TAU * seconds / 23 + phase * 0.6
  const rippleX = -TAU * seconds / 13 + phase * 1.7
  const rippleY = TAU * seconds / 17 + phase * 2.3
  let i = 0
  for (let row = 0; row <= BACKDROP_CELLS; row++) {
    const v = row * 2 / BACKDROP_CELLS - 1
    for (let column = 0; column <= BACKDROP_CELLS; column++) {
      const u = column * 2 / BACKDROP_CELLS - 1
      const x = u * breath +
        SWELL * Math.sin(PI * (0.9 * v + 0.6 * u) + swellX) +
        RIPPLE * Math.sin(PI * (1.7 * v - 0.4 * u) + rippleX)
      const y = v * breath +
        SWELL * Math.cos(PI * (0.8 * u - 0.5 * v) + swellY) +
        RIPPLE * Math.sin(PI * (1.5 * u + 0.7 * v) + rippleY)
      out[i++] = centerX + (x * cosine - y * sine) * reach
      out[i++] = centerY + (x * sine + y * cosine) * reach
    }
  }
}

const EASE_SECONDS = 0.7
const REST = 0.01

/**
 * The seconds the flow has run. Starting and stopping ease in and out, so pausing lets the
 * ground come to rest instead of freezing it.
 */
export function createFlowClock() {
  let seconds = 0
  let pace = 0
  return {
    get seconds() { return seconds },
    /** Moves on by [elapsed] seconds of real time; false once the ground is at rest. */
    advance(elapsed: number, moving: boolean): boolean {
      // A frame after a long gap, such as a hidden window coming back, is not a jump.
      const step = Math.min(Math.max(elapsed, 0), 0.1)
      pace += ((moving ? 1 : 0) - pace) * (1 - Math.exp(-step / EASE_SECONDS))
      if (!moving && pace <= REST) pace = 0
      seconds += step * pace
      return moving || pace > 0
    },
  }
}
