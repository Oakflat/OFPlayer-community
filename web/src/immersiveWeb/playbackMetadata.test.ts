import test from 'node:test'
import assert from 'node:assert/strict'
import { readPlaybackTrackUpdate } from './playbackMetadata.ts'

const packet = (revision: number, id = 'external', title = '14 Bellhart') => ({
  revision, trackId: id,
  track: { id, displayTitle: title, artist: 'Christopher Larkin', artwork: 'data:image/jpeg;base64,/9g=' },
})

test('cloud metadata supplies immersive title and cover without altering playback clock', () => {
  assert.deepEqual(readPlaybackTrackUpdate(packet(1), 'external', 0), {
    revision: 1, title: '14 Bellhart', artist: 'Christopher Larkin', artwork: 'data:image/jpeg;base64,/9g=',
  })
})

test('successive local originals with the same native ID accept only the newer presentation', () => {
  const next = readPlaybackTrackUpdate(packet(3, 'external', '53 Silksong'), 'external', 1)!
  assert.equal(next.title, '53 Silksong')
  assert.equal(readPlaybackTrackUpdate(packet(2), 'external', next.revision), null)
  assert.equal(readPlaybackTrackUpdate(packet(3), 'external', next.revision), null)
})

test('late remote metadata cannot replace another active song', () => {
  assert.equal(readPlaybackTrackUpdate(packet(4, 'remote/cloud-b/old'), 'remote/cloud-b/new', 2), null)
  assert.equal(readPlaybackTrackUpdate(packet(4), null, 2), null)
})

test('clearing a pending presentation removes the previous title and artwork', () => {
  assert.deepEqual(readPlaybackTrackUpdate({ trackId: 'external', revision: 5, track: null }, 'external', 4),
    { revision: 5, title: '', artist: '', artwork: null })
})

test('mismatched detail IDs and malformed revisions are rejected', () => {
  assert.equal(readPlaybackTrackUpdate({ ...packet(5), track: { ...packet(5).track, id: 'other' } }, 'external', 4), null)
  for (const revision of [NaN, Infinity, 5.5, -1]) {
    assert.equal(readPlaybackTrackUpdate({ ...packet(5), revision }, 'external', 4), null)
  }
})
