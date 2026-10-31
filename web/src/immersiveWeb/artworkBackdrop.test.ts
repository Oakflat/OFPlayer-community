import assert from 'node:assert/strict'
import test from 'node:test'
import { createArtworkBackdropController } from './artworkBackdrop.ts'

test('backdrop waits for decode and rejects stale A to B to A results', async () => {
  const pending = new Map<string, Array<{ resolve: (ground: string) => void; reject: () => void }>>()
  const applied: Array<string | null> = []
  const controller = createArtworkBackdropController<string>(
    (source) => new Promise((resolve, reject) => {
      const entries = pending.get(source) ?? []
      entries.push({ resolve, reject })
      pending.set(source, entries)
    }),
    (ground) => applied.push(ground),
  )

  controller.update('A')
  controller.update('B')
  controller.update('A')
  pending.get('A')![0].resolve('first A')
  pending.get('B')![0].resolve('B')
  await Promise.resolve()
  assert.deepEqual(applied, [])
  pending.get('A')![1].resolve('second A')
  await Promise.resolve()
  assert.deepEqual(applied, ['second A'])

  controller.update('bad')
  assert.deepEqual(applied, ['second A']) // Keep the old ground while the next is prepared.
  pending.get('bad')![0].reject()
  await Promise.resolve()
  assert.deepEqual(applied, ['second A', null])
  controller.update(null)
  assert.deepEqual(applied, ['second A', null, null])
})

test('a cancelled controller applies nothing that was still loading', async () => {
  let finish: (ground: string) => void = () => {}
  const applied: Array<string | null> = []
  const controller = createArtworkBackdropController<string>(
    () => new Promise((resolve) => { finish = resolve }),
    (ground) => applied.push(ground),
  )
  controller.update('A')
  controller.cancel()
  finish('A')
  await Promise.resolve()
  assert.deepEqual(applied, [])
})
