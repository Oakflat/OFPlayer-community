import assert from 'node:assert/strict'
import test from 'node:test'
import { createImmersiveFrameLoop } from './frameLoop.ts'

function fixture() {
  let nextId = 0
  let clocks = 0
  const backgrounds: number[] = []
  const callbacks = new Map<number, (timestamp: number) => void>()
  const background = { busy: false }
  const loop = createImmersiveFrameLoop({
    requestFrame(callback) { callbacks.set(++nextId, callback); return nextId },
    cancelFrame(id) { callbacks.delete(id) },
    updateClock() { clocks++ },
    drawBackground(timestamp) { backgrounds.push(timestamp); return background.busy },
  })
  return {
    loop, callbacks, backgrounds, background,
    get clocks() { return clocks },
    tick(timestamp: number) {
      const pending = [...callbacks.values()]
      callbacks.clear()
      for (const callback of pending) callback(timestamp)
    },
  }
}

const playing = { visible: true, playing: true, reducedMotion: false }

test('hidden prewarm and paused player schedule no animation work', () => {
  const f = fixture()
  f.loop.setState({ ...playing, visible: false })
  f.tick(100)
  f.loop.setState({ ...playing, playing: false })
  f.tick(200)
  assert.equal(f.callbacks.size, 0)
  assert.equal(f.clocks, 0)
  assert.deepEqual(f.backgrounds, [])
})

test('visible playback shares one frame chain and caps background work at 30 Hz', () => {
  const f = fixture()
  f.loop.setState(playing)
  f.loop.setState(playing)
  assert.equal(f.callbacks.size, 1)
  for (const at of [0, 16, 32, 48, 64, 80, 96]) f.tick(at)
  assert.equal(f.clocks, 7)
  assert.deepEqual(f.backgrounds, [0, 48, 96])
  assert.equal(f.callbacks.size, 1)
})

test('hide cancels work; a late old callback cannot fork the resumed chain', () => {
  const f = fixture()
  f.loop.setState(playing)
  const oldCallback = [...f.callbacks.values()][0]
  f.loop.setState({ ...playing, visible: false })
  assert.equal(f.callbacks.size, 0)
  f.loop.setState(playing)
  oldCallback(50)
  assert.equal(f.clocks, 0)
  assert.equal(f.callbacks.size, 1)
  f.tick(100)
  assert.equal(f.clocks, 1)
  assert.deepEqual(f.backgrounds, [100])
  f.loop.stop()
  f.tick(120)
  assert.equal(f.clocks, 1)
})

test('reduced motion retains lyric timing without background animation', () => {
  const f = fixture()
  f.loop.setState({ ...playing, reducedMotion: true })
  f.tick(0)
  f.tick(16)
  assert.equal(f.clocks, 2)
  assert.deepEqual(f.backgrounds, [])
  f.loop.setState(playing)
  f.tick(32)
  assert.deepEqual(f.backgrounds, [32])
  f.loop.setState({ ...playing, playing: false })
  assert.equal(f.callbacks.size, 0)
})

test('after a pause the background is drawn until it has come to rest', () => {
  const f = fixture()
  f.background.busy = true
  f.loop.setState(playing)
  f.tick(0)
  f.loop.setState({ ...playing, playing: false })
  assert.equal(f.callbacks.size, 1)
  f.tick(40)
  f.tick(80)
  assert.equal(f.clocks, 1) // The lyric clock stopped with the music.
  assert.deepEqual(f.backgrounds, [0, 40, 80])
  f.background.busy = false
  f.tick(120)
  assert.deepEqual(f.backgrounds, [0, 40, 80, 120])
  assert.equal(f.callbacks.size, 0)
})

test('a new cover wakes a still player for as long as it takes to fade in', () => {
  const f = fixture()
  f.loop.setState({ ...playing, playing: false })
  f.background.busy = true
  f.loop.wake()
  f.loop.wake()
  assert.equal(f.callbacks.size, 1)
  f.tick(0)
  f.tick(40)
  f.background.busy = false
  f.tick(80)
  assert.deepEqual(f.backgrounds, [0, 40, 80])
  assert.equal(f.clocks, 0)
  assert.equal(f.callbacks.size, 0)
})

test('nothing wakes a hidden page or one with motion reduced', () => {
  const f = fixture()
  f.background.busy = true
  f.loop.setState({ ...playing, visible: false })
  f.loop.wake()
  assert.equal(f.callbacks.size, 0)
  // Nor does that wake wait for the page to be shown.
  f.loop.setState({ ...playing, playing: false })
  assert.equal(f.callbacks.size, 0)
  f.loop.setState({ visible: true, playing: false, reducedMotion: true })
  f.loop.wake()
  assert.equal(f.callbacks.size, 0)
})

test('hiding the page ends a background that was still busy', () => {
  const f = fixture()
  f.background.busy = true
  f.loop.setState(playing)
  f.tick(0)
  f.loop.setState({ ...playing, playing: false })
  f.loop.setState({ visible: false, playing: false, reducedMotion: false })
  assert.equal(f.callbacks.size, 0)
  f.loop.setState({ ...playing, playing: false })
  assert.equal(f.callbacks.size, 0)
})

test('static artwork backdrop keeps playback clock without a background renderer', () => {
  const callbacks = new Map<number, (timestamp: number) => void>()
  let nextId = 0
  let clocks = 0
  const loop = createImmersiveFrameLoop({
    requestFrame(callback) { callbacks.set(++nextId, callback); return nextId },
    cancelFrame(id) { callbacks.delete(id) },
    updateClock() { clocks++ },
  })
  loop.setState(playing)
  for (const at of [0, 16, 32]) {
    const callback = [...callbacks.values()][0]
    callbacks.clear()
    callback(at)
  }
  assert.equal(clocks, 3)
  loop.setState({ ...playing, visible: false })
  assert.equal(callbacks.size, 0)
})
