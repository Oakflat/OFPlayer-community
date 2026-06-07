import test from 'node:test'
import assert from 'node:assert/strict'
import { createRemoteMetadataHydrator, type RemoteMetadataLibraryStore } from './remoteMetadataHydrator.ts'

function createLibraryStore(tracks: Record<string, unknown>[]) {
  const trackMap = new Map(tracks.map((track) => [track.id as string, track]))
  const updates: { trackId: string; patch: Record<string, unknown> }[] = []

  return {
    updates,
    getLibraryById: (libraryId: string) => ({
      id: libraryId,
      source: { kind: 'external', connectionId: 'connection-1' },
    }),
    getTracksForLibrary: () => tracks,
    getTrackById: (trackId: string) => trackMap.get(trackId) ?? null,
    updateTrackMetadata: async (trackId: string, patch: Record<string, unknown>) => {
      updates.push({ trackId, patch })
      const nextTrack = { ...trackMap.get(trackId), ...patch }
      trackMap.set(trackId, nextTrack)
      return nextTrack
    },
  }
}

function createHydrator({
  tracks = [] as Record<string, unknown>[],
  resolveTrackMetadata = async () => ({ duration: 120 }),
  logs = [] as unknown[][],
}: {
  tracks?: Record<string, unknown>[]
  resolveTrackMetadata?: (track: Record<string, unknown>) => Promise<Record<string, unknown>>
  logs?: unknown[][]
} = {}) {
  const scheduled: { callback: () => void; delayMs: number }[] = []
  const libraryStore = createLibraryStore(tracks)
  const hydrator = createRemoteMetadataHydrator({
    libraryStore: libraryStore as unknown as RemoteMetadataLibraryStore,
    externalLibraryService: { resolveTrackMetadata },
    getActiveLibraryId: () => 'library-1',
    isPlaying: () => false,
    isDisposed: () => false,
    getLifecycleToken: () => 1,
    isLifecycleCurrent: () => true,
    logDiagnosticsInfo: async (...args: unknown[]) => {
      logs.push(args)
    },
    startDelayMs: 10,
    idleDelayMs: 20,
    playingDelayMs: 30,
    now: () => 100,
    wait: async () => {},
    setTimeoutFn: (callback: () => void, delayMs: number) => {
      const timer = { callback, delayMs }
      scheduled.push(timer)
      return timer as unknown as ReturnType<typeof setTimeout>
    },
    clearTimeoutFn: () => {},
  })

  return { hydrator, libraryStore, logs, scheduled }
}

test('remote metadata hydrator queues only eligible external tracks', () => {
  const { hydrator, scheduled } = createHydrator({
    tracks: [
      { id: 'remote-1', metadataVersion: 0, source: { connectionId: 'c1', provider: 'subsonic' } },
      { id: 'remote-webdav', metadataVersion: 0, source: { connectionId: 'c1', provider: 'webdav' } },
      { id: 'remote-complete', metadataVersion: 3, duration: 1, source: { connectionId: 'c1', provider: 'subsonic' } },
      { id: 'local' },
    ],
  })

  assert.equal(hydrator.enqueueLibrary('library-1'), 1)
  assert.equal(hydrator.enqueueLibrary('library-1'), 0)
  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].delayMs, 10)
})

test('remote metadata hydrator drains queued tracks and logs a summary', async () => {
  const logs: unknown[][] = []
  const { hydrator, libraryStore } = createHydrator({
    logs,
    tracks: [
      { id: 'remote-1', metadataVersion: 0, source: { connectionId: 'c1', provider: 'subsonic' } },
    ],
    resolveTrackMetadata: async (track: Record<string, unknown>) => ({ title: `Hydrated ${track.id}`, duration: 99 }),
  })

  assert.equal(hydrator.enqueueActiveLibrary(), 1)
  await hydrator.drain()

  assert.deepEqual(libraryStore.updates, [
    {
      trackId: 'remote-1',
      patch: { title: 'Hydrated remote-1', duration: 99 },
    },
  ])
  assert.equal(logs.length, 1)
  assert.equal(logs[0][2], 'background_hydrate')
  assert.equal((logs[0][3] as Record<string, unknown>).processed, 1)
  assert.equal((logs[0][3] as Record<string, unknown>).hydrated, 1)
})

test('remote metadata hydrator can retry tracks that previously failed', async () => {
  let shouldFail = true
  const { hydrator, libraryStore } = createHydrator({
    tracks: [
      { id: 'remote-1', metadataVersion: 0, source: { connectionId: 'c1', provider: 'subsonic' } },
    ],
    resolveTrackMetadata: async () => {
      if (shouldFail) {
        throw new Error('offline')
      }
      return { duration: 44 }
    },
  })

  assert.equal(hydrator.enqueueLibrary('library-1'), 1)
  await hydrator.drain()
  assert.deepEqual(libraryStore.updates, [])

  shouldFail = false
  assert.equal(hydrator.enqueueLibrary('library-1'), 0)
  assert.equal(hydrator.enqueueLibrary('library-1', { retryFailed: true }), 1)
  await hydrator.drain()
  assert.deepEqual(libraryStore.updates, [{ trackId: 'remote-1', patch: { duration: 44 } }])
})
