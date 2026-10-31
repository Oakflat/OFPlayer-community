type FrameLoopState = { visible: boolean; playing: boolean; reducedMotion: boolean }

/** One animation chain for the visible player; a warm hidden page needs no frames. */
export function createImmersiveFrameLoop(options: {
  requestFrame: (callback: (timestamp: number) => void) => number
  cancelFrame: (id: number) => void
  updateClock: () => void
  /** Paints the background. True while it has more to paint whether or not anything plays. */
  drawBackground?: (timestamp: number) => boolean | void
}) {
  let state: FrameLoopState = { visible: false, playing: false, reducedMotion: false }
  let frame: number | null = null
  let generation = 0
  let lastBackgroundAt = -Infinity
  // A background coming to rest after a pause, or mixing in a new cover, keeps the chain alive.
  let backgroundBusy = false
  const animatesBackground = () => options.drawBackground !== undefined && !state.reducedMotion
  const running = () => state.visible && (state.playing || (backgroundBusy && animatesBackground()))

  function schedule() {
    if (frame !== null || !running()) return
    const scheduledGeneration = generation
    frame = options.requestFrame((timestamp) => {
      // Also reject a callback already dispatched when the page was hidden.
      if (scheduledGeneration !== generation) return
      frame = null
      if (!running()) return
      if (state.playing) options.updateClock()
      if (animatesBackground() && timestamp - lastBackgroundAt >= 33) {
        lastBackgroundAt = timestamp
        backgroundBusy = options.drawBackground!(timestamp) === true
      }
      schedule()
    })
  }

  return {
    setState(next: FrameLoopState) {
      state = next
      if (running()) {
        schedule()
      } else {
        generation++
        if (frame !== null) options.cancelFrame(frame)
        frame = null
        lastBackgroundAt = -Infinity
        backgroundBusy = false
      }
    },
    /** The background has something new to paint; frames run until it says it is done. */
    wake() {
      if (!state.visible || !animatesBackground()) return
      backgroundBusy = true
      schedule()
    },
    stop() {
      this.setState({ ...state, visible: false })
    },
  }
}
