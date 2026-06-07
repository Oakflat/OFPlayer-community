import test from 'node:test'
import assert from 'node:assert/strict'
import {
  createBrowserCatalogController,
  isBrowserCollectionRef,
  type BrowserCatalogLibraryStore,
} from './browserCatalogController.ts'

function createController({
  tracks = [] as Record<string, unknown>[],
  expectedTrackCount,
  catalogComplete = true,
  artworkMode = 'album-covers',
  hydrate = null as (() => Promise<unknown>) | null,
}: {
  tracks?: Record<string, unknown>[]
  expectedTrackCount?: number
  catalogComplete?: boolean
  artworkMode?: string
  hydrate?: (() => Promise<unknown>) | null
} = {}) {
  const resolvedTrackCount = expectedTrackCount ?? tracks.length
  const readiness = {
    value: {
      status: 'ready' as string,
      libraryId: null as string | null,
      collectionRef: null as string | null,
      reason: '',
      error: null as string | null,
    },
  }
  const activeLibrary = { value: 'library-1' }
  const activeCollection = { value: 'view:albums' }
  const storedTracks = [...tracks]
  const libraryStore = {
    catalogTrackListComplete: { value: catalogComplete },
    catalogTrackArtworkMode: { value: artworkMode },
    getTracksForLibrary: () => storedTracks,
    hydrate:
      hydrate ??
      (async () => {
        libraryStore.catalogTrackListComplete.value = true
        libraryStore.catalogTrackArtworkMode.value = 'album-covers'
        return { libraries: [{}], playlists: [{}], tracks: storedTracks }
      }),
  } as unknown as BrowserCatalogLibraryStore
  const watchCalls: { source: unknown; callback: unknown; options: unknown }[] = []
  const stopCalls: string[] = []
  const controller = createBrowserCatalogController({
    watch: (source: unknown, callback: unknown, options: unknown) => {
      watchCalls.push({ source, callback, options })
      return () => stopCalls.push('stopped')
    },
    readiness,
    libraryStore,
    preferencesStore: {
      activeLibrary,
      activeCollection,
    },
    navigationSummary: {
      value: {
        libraryTrackCounts: {
          'library-1': resolvedTrackCount,
        },
      },
    },
    navigationSummaryRevision: { value: 'rev-1' },
    isBootstrapReady: { value: true },
    catalogRevision: { value: 7 },
    captureRendererResourceSample: () => ({}) as Record<string, unknown>,
    recordStartupRendererStep: () => {},
    logDiagnosticsInfo: async () => {},
    logDiagnosticsWarn: async () => {},
    now: () => 100,
  })

  return { activeCollection, controller, libraryStore, readiness, stopCalls, storedTracks, watchCalls }
}

test('isBrowserCollectionRef recognizes browser-only smart collections', () => {
  assert.equal(isBrowserCollectionRef('view:albums'), true)
  assert.equal(isBrowserCollectionRef('view:artists'), true)
  assert.equal(isBrowserCollectionRef('view:all-plays'), false)
})

test('browser catalog controller reports readiness from catalog completeness and artwork mode', () => {
  assert.equal(
    createController({ catalogComplete: false }).controller.needsFullCatalogForLibrary('library-1'),
    true,
  )
  assert.equal(
    createController({ tracks: [{}], expectedTrackCount: 2 }).controller.needsFullCatalogForLibrary('library-1'),
    true,
  )
  assert.equal(
    createController({ tracks: [{}, {}], expectedTrackCount: 2, artworkMode: 'none' }).controller.needsFullCatalogForLibrary('library-1'),
    true,
  )
  assert.equal(
    createController({ tracks: [{}, {}], expectedTrackCount: 2 }).controller.needsFullCatalogForLibrary('library-1'),
    false,
  )
})

test('browser catalog controller prepares browser collections by hydrating full catalog', async () => {
  const { controller, readiness } = createController({
    catalogComplete: false,
    tracks: [{ id: 'a' }, { id: 'b' }],
    expectedTrackCount: 2,
  })

  const ready = await controller.prepareSelection({ reason: 'test' })

  assert.equal(ready, true)
  assert.equal(readiness.value.status, 'ready')
  assert.equal(readiness.value.libraryId, 'library-1')
  assert.equal(readiness.value.collectionRef, 'view:albums')
  assert.equal(readiness.value.reason, 'test')
  assert.equal(controller.getActiveCollectionDataStatus(), 'ready')
})

test('browser catalog controller marks readiness error when hydration fails', async () => {
  const { controller, readiness } = createController({
    catalogComplete: false,
    hydrate: async () => {
      throw new Error('offline')
    },
  })

  const ready = await controller.prepareSelection({ reason: 'test-failure' })

  assert.equal(ready, false)
  assert.equal(readiness.value.status, 'error')
  assert.equal(readiness.value.error, 'browser-catalog-hydration-failed')
  assert.equal(controller.getActiveCollectionDataError(), 'browser-catalog-hydration-failed')
})

test('browser catalog watcher hydrates only after bootstrap is ready and can be stopped', () => {
  const { controller, stopCalls, watchCalls } = createController()

  const stop = controller.startWatcher({ immediate: true })
  assert.equal(watchCalls.length, 1)
  assert.equal((watchCalls[0].options as Record<string, unknown>).flush, 'post')
  assert.equal(typeof stop, 'function')

  controller.stopWatcher()
  assert.deepEqual(stopCalls, ['stopped'])
})
