import test from 'node:test'
import assert from 'node:assert/strict'
import {
  createPlaybackOrderSignature,
  createShuffledPlaybackOrder,
  normalizePlaybackOrderTrackIds,
  PLAYBACK_ORDER_SEPARATOR,
  resolvePlaybackOrderTrackId,
} from './playbackOrder.ts'

test('normalizePlaybackOrderTrackIds trims, deduplicates, and drops empty ids', () => {
  assert.deepEqual(
    normalizePlaybackOrderTrackIds([' a ', 'b', '', null, 'a', 42, '42']),
    ['a', 'b', '42'],
  )
  assert.deepEqual(normalizePlaybackOrderTrackIds('a' as unknown as unknown[]), [])
})

test('createPlaybackOrderSignature uses the normalized queue identity', () => {
  assert.equal(
    createPlaybackOrderSignature([' a ', 'b', 'a']),
    ['a', 'b'].join(PLAYBACK_ORDER_SEPARATOR),
  )
})

test('createShuffledPlaybackOrder keeps the anchor first when it exists', () => {
  const randomValues = [0.1, 0.7, 0.2]
  const random = () => randomValues.shift() ?? 0

  assert.deepEqual(
    createShuffledPlaybackOrder(['a', 'b', 'c', 'd'], 'c', random),
    ['c', 'd', 'b', 'a'],
  )
})

test('createShuffledPlaybackOrder omits a missing anchor', () => {
  assert.deepEqual(
    createShuffledPlaybackOrder(['a', 'b', 'c'], 'x', () => 0),
    ['b', 'c', 'a'],
  )
})

test('resolvePlaybackOrderTrackId advances, wraps, and respects repeat modes', () => {
  const queueTrackIds = ['a', 'b', 'c']

  assert.equal(resolvePlaybackOrderTrackId({ queueTrackIds, currentTrackId: 'a', step: 1 }), 'b')
  assert.equal(
    resolvePlaybackOrderTrackId({
      queueTrackIds,
      currentTrackId: 'c',
      repeatMode: 'none',
      step: 1,
    }),
    null,
  )
  assert.equal(
    resolvePlaybackOrderTrackId({
      queueTrackIds,
      currentTrackId: 'c',
      repeatMode: 'all',
      step: 1,
    }),
    'a',
  )
  assert.equal(
    resolvePlaybackOrderTrackId({
      queueTrackIds,
      currentTrackId: 'b',
      repeatMode: 'one',
      reason: 'ended',
      step: 1,
    }),
    'b',
  )
})

test('resolvePlaybackOrderTrackId falls back to the first item for empty or missing selection', () => {
  assert.equal(resolvePlaybackOrderTrackId({ queueTrackIds: [] }), null)
  assert.equal(resolvePlaybackOrderTrackId({ queueTrackIds: ['a', 'b'] }), 'a')
  assert.equal(
    resolvePlaybackOrderTrackId({ queueTrackIds: ['a', 'b'], currentTrackId: 'missing' }),
    'a',
  )
})
