import test from 'node:test'
import assert from 'node:assert/strict'
import {
  buildArtworkAlbumKey,
  canHydrateRemoteTrackMetadata,
  clampPercent,
  createIdleScanProgress,
  createPlaybackSourceOverride,
  createRemotePlaybackMetadataPatch,
  createRemoteTrackReadiness,
  hasCompleteRemoteMetadata,
  isExternalLibrary,
  isExternalTrack,
  isTransientPlaybackSource,
  normalizeArtworkUrl,
  normalizeScanCount,
  resolveBackendRevisions,
  resolveLyricsDialogPath,
  resolveScanMode,
  sanitizeTrackArtwork,
} from './appStateHelpers.ts'

const oversizedArtwork = `data:image/png;base64,${'a'.repeat(768 * 1024 + 1)}`

test('resolveBackendRevisions normalizes missing and invalid revision fields', () => {
  assert.deepEqual(resolveBackendRevisions(), {
    catalog: 0,
    navigation: 0,
    history: 0,
    preferences: 0,
    session: 0,
  })
  assert.deepEqual(
    resolveBackendRevisions({
      revisions: {
        catalog: 3,
        navigation: -1,
        history: 2.5,
        preferences: 0,
        session: 8,
      },
    }),
    {
      catalog: 3,
      navigation: 0,
      history: 0,
      preferences: 0,
      session: 8,
    },
  )
})

test('scan progress helpers clamp numeric input and resolve modes', () => {
  assert.equal(createIdleScanProgress().phase, 'idle')
  assert.equal(clampPercent(101.8), 100)
  assert.equal(clampPercent(-1), 0)
  assert.equal(normalizeScanCount(3.6), 4)
  assert.equal(normalizeScanCount(Number.NaN), 0)
  assert.equal(resolveScanMode({ source: 'watch', interactive: true }), 'watch')
  assert.equal(resolveScanMode({ interactive: true }), 'manual')
  assert.equal(resolveScanMode(), 'auto')
})

test('artwork helpers trim safe artwork and strip oversized embedded data', () => {
  assert.equal(normalizeArtworkUrl('  asset://cover.png  '), 'asset://cover.png')
  assert.equal(normalizeArtworkUrl(oversizedArtwork), '')
  assert.deepEqual(sanitizeTrackArtwork({ id: 't1', artwork: oversizedArtwork }), {
    id: 't1',
    artwork: '',
  })
  assert.equal(buildArtworkAlbumKey({ albumArtist: ' Artist ', album: ' Album ' }), 'artist::album')
  assert.equal(buildArtworkAlbumKey({ artist: ' Artist ', album: ' Album ' }), 'artist::album')
  assert.equal(buildArtworkAlbumKey({ artist: ' Artist ' }), '')
})

test('remote metadata patch copies resolved values and mirrors size fields', () => {
  assert.deepEqual(
    createRemotePlaybackMetadataPatch(
      { id: 't1', title: 'Old', artwork: oversizedArtwork },
      { title: 'New', duration: 12, fileSize: 42 },
    ),
    {
      artwork: '',
      title: 'New',
      duration: 12,
      fileSize: 42,
      size: 42,
    },
  )
})

test('remote source helpers classify libraries, tracks, and transient playback overrides', () => {
  const connectionSource = { connectionId: 'c1', provider: 'subsonic' }
  const transientSource = { connectionId: 'c1', kind: 'external-temp', path: 'cache.mp3' }

  assert.equal(isExternalLibrary({ source: { kind: 'external', connectionId: 'c1' } }), true)
  assert.equal(isExternalTrack({ source: connectionSource }), true)
  assert.equal(canHydrateRemoteTrackMetadata({ source: connectionSource }), true)
  assert.equal(canHydrateRemoteTrackMetadata({ source: { provider: 'webdav' } }), false)
  assert.equal(isTransientPlaybackSource(transientSource), true)
  assert.deepEqual(
    createPlaybackSourceOverride({ source: connectionSource }, { source: transientSource }),
    transientSource,
  )
  assert.equal(createPlaybackSourceOverride({ source: {} }, { source: transientSource }), null)
})

test('remote readiness combines metadata, artwork, and playback source state', () => {
  assert.equal(hasCompleteRemoteMetadata({ metadataVersion: 3, duration: 1 }), true)
  assert.deepEqual(createRemoteTrackReadiness({ id: 'local' }), {
    isRemote: false,
    provider: '',
    isPreparing: false,
    metadataReady: true,
    artworkReady: true,
    playbackReady: true,
  })
  assert.deepEqual(
    createRemoteTrackReadiness(
      {
        id: 'remote',
        duration: 30,
        artwork: 'asset://cover.png',
        source: { connectionId: 'c1', provider: 'subsonic', kind: 'external-url', url: 'http://x' },
      },
      { active: true, trackId: 'remote' },
    ),
    {
      isRemote: true,
      provider: 'subsonic',
      isPreparing: true,
      metadataReady: true,
      artworkReady: true,
      playbackReady: true,
    },
  )
})

test('resolveLyricsDialogPath prefers explicit binding, then source origin/path', () => {
  assert.equal(resolveLyricsDialogPath(null), '')
  assert.equal(resolveLyricsDialogPath({ lyricsPath: 'lyrics.lrc' }), 'lyrics.lrc')
  assert.equal(resolveLyricsDialogPath({ source: { originPath: 'origin.mp3', path: 'cache.mp3' } }), 'origin.mp3')
  assert.equal(resolveLyricsDialogPath({ source: { path: 'cache.mp3' } }), 'cache.mp3')
})
