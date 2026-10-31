import { emptyBackdropField, mixBackdropFields } from './backdropField.ts'
import { createFlowClock } from './backdropFlow.ts'

/** Whatever paints a ground at a moment of the flow: the canvas renderer, or a recorder in tests. */
export type BackdropPainter = {
  /** The picture to paint from now on; null is the bare ground. */
  setField(pixels: Uint8ClampedArray | null): void
  draw(seconds: number): void
}

const CROSSFADE_MS = 900

/**
 * What is on the ground now and how far its flow has run. A change is mixed in the field's own
 * pixels: both pictures lie on the same mesh, so that is exactly a crossfade on screen, the
 * ground is still painted once, and a change that arrives mid-fade starts from whatever shows.
 */
export function createBackdrop(painter: BackdropPainter) {
  const empty = emptyBackdropField()
  const pixels = emptyBackdropField()
  let target: Uint8ClampedArray = empty
  let from: Uint8ClampedArray | null = null
  let fadeStart: number | null = null
  let lastFrame: number | null = null
  const clock = createFlowClock()

  const present = () => painter.setField(from === null && target === empty ? null : pixels)
  const paint = () => painter.draw(clock.seconds)

  function settle() {
    from = null
    fadeStart = null
    pixels.set(target)
    present()
    paint()
  }

  present()

  return {
    /**
     * A new ground, or none. Animated, it is mixed in over the coming frames; otherwise, as
     * when nothing is being shown or motion is reduced, it is simply there.
     */
    show(field: Uint8ClampedArray | null, animate: boolean) {
      const next = field ?? empty
      if (next !== target) {
        target = next
        if (animate) {
          from = pixels.slice()
          fadeStart = null
        }
      }
      if (!animate) settle()
    },
    /** Ends a change at once: for when the frames that would have carried it stop coming. */
    settle,
    /** One frame of the player's loop. True while the ground still has somewhere to go. */
    frame(timestamp: number, flowing: boolean): boolean {
      const elapsed = lastFrame === null ? 0 : (timestamp - lastFrame) / 1000
      lastFrame = timestamp
      if (from) {
        fadeStart ??= timestamp
        const fraction = (timestamp - fadeStart) / CROSSFADE_MS
        if (fraction < 1) {
          mixBackdropFields(from, target, fraction, pixels)
        } else {
          from = null
          fadeStart = null
          pixels.set(target)
        }
        present()
      }
      const moving = clock.advance(elapsed, flowing)
      paint()
      return from !== null || moving
    },
    /** Paints what there is with no time passing: a resize, a reveal. */
    draw: paint,
  }
}
