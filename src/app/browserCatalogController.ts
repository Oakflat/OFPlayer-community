import {
  BROWSER_CATALOG_ARTWORK_MODE,
  BROWSER_CATALOG_READY_ARTWORK_MODES,
  type NavigationSummary,
  nowMs,
} from './appStateHelpers.ts'

const BROWSER_COLLECTION_REFS = new Set(['view:albums', 'view:artists'])

type Ref<T = unknown> = { value: T }
type StopWatcher = () => void
type WatchFn = (
  source: () => string,
  callback: () => void,
  options?: { immediate?: boolean; flush?: 'pre' | 'post' | 'sync' },
) => StopWatcher
type ResourceSample = Record<string, unknown> | null

export type BrowserCatalogReadinessStatus = 'ready' | 'preparing' | 'error'

export type BrowserCatalogReadiness = {
  status: BrowserCatalogReadinessStatus
  libraryId: string | null
  collectionRef: string | null
  reason: string
  error: string | null
}

export type BrowserCatalogLibraryStore = {
  catalogTrackListComplete: Ref<boolean>
  catalogTrackArtworkMode: Ref<string>
  getTracksForLibrary: (libraryId: string) => unknown[]
  hydrate: (
    libraryId: string | null,
    options: { revision: unknown; trackArtworkMode: string; trackListComplete: boolean },
  ) => Promise<{ libraries?: unknown[]; playlists?: unknown[]; tracks?: Array<{ artwork?: unknown }> } | null>
}

export type BrowserCatalogControllerOptions = {
  watch: WatchFn
  readiness: Ref
  libraryStore: BrowserCatalogLibraryStore
  preferencesStore: {
    activeLibrary: Ref<string | null>
    activeCollection: Ref<string | null>
  }
  navigationSummary: Ref<Partial<NavigationSummary> | null | undefined>
  navigationSummaryRevision: Ref<unknown>
  isBootstrapReady: Ref<boolean>
  catalogRevision: Ref<unknown>
  captureRendererResourceSample: () => ResourceSample
  recordStartupRendererStep: (key: string, startedAt: number, resourceStart?: ResourceSample) => unknown
  logDiagnosticsInfo: (scope: string, category: string, event: string, payload: Record<string, unknown>) => unknown
  logDiagnosticsWarn: (scope: string, category: string, event: string, payload: Record<string, unknown>) => unknown
  now?: () => number
}

export function isBrowserCollectionRef(collectionRef: unknown): collectionRef is string {
  return typeof collectionRef === 'string' && BROWSER_COLLECTION_REFS.has(collectionRef)
}

export function createBrowserCatalogController({
  watch,
  readiness,
  libraryStore,
  preferencesStore,
  navigationSummary,
  navigationSummaryRevision,
  isBootstrapReady,
  catalogRevision,
  captureRendererResourceSample,
  recordStartupRendererStep,
  logDiagnosticsInfo,
  logDiagnosticsWarn,
  now = nowMs,
}: BrowserCatalogControllerOptions) {
  let fullCatalogHydrationPromise: Promise<Awaited<ReturnType<BrowserCatalogLibraryStore['hydrate']>> | boolean | null> | null = null
  let preparationRequestId = 0
  let stopWatcher: StopWatcher | null = null

  function needsFullCatalogForLibrary(libraryId: string | null | undefined): boolean {
    if (!libraryId) {
      return false
    }

    const expectedTrackCount = navigationSummary.value?.libraryTrackCounts?.[libraryId] ?? 0
    const loadedTrackCount = libraryStore.getTracksForLibrary(libraryId).length

    if (!libraryStore.catalogTrackListComplete.value) {
      return true
    }

    if (expectedTrackCount > 0 && loadedTrackCount < expectedTrackCount) {
      return true
    }

    return !BROWSER_CATALOG_READY_ARTWORK_MODES.has(libraryStore.catalogTrackArtworkMode.value)
  }

  function needsFullCatalogForActiveLibrary(): boolean {
    return needsFullCatalogForLibrary(preferencesStore.activeLibrary.value)
  }

  function getActiveCollectionDataReady(): boolean {
    const activeCollection = preferencesStore.activeCollection.value

    if (!isBrowserCollectionRef(activeCollection)) {
      return true
    }

    return !needsFullCatalogForLibrary(preferencesStore.activeLibrary.value)
  }

  function getActiveCollectionDataStatus(): BrowserCatalogReadinessStatus {
    const activeCollection = preferencesStore.activeCollection.value

    if (!isBrowserCollectionRef(activeCollection)) {
      return 'ready'
    }

    const activeLibraryId = preferencesStore.activeLibrary.value
    const currentReadiness = readiness.value as BrowserCatalogReadiness
    const matchesActiveTarget =
      currentReadiness.libraryId === activeLibraryId &&
      currentReadiness.collectionRef === activeCollection

    if (matchesActiveTarget && currentReadiness.status === 'error') {
      return 'error'
    }

    if (!getActiveCollectionDataReady() || (matchesActiveTarget && currentReadiness.status === 'preparing')) {
      return 'preparing'
    }

    return 'ready'
  }

  function getActiveCollectionDataError(): string | null {
    const activeCollection = preferencesStore.activeCollection.value
    const activeLibraryId = preferencesStore.activeLibrary.value
    const currentReadiness = readiness.value as BrowserCatalogReadiness

    if (
      currentReadiness.status !== 'error' ||
      currentReadiness.libraryId !== activeLibraryId ||
      currentReadiness.collectionRef !== activeCollection
    ) {
      return null
    }

    return currentReadiness.error
  }

  function buildHydrationKey(): string {
    const activeLibraryId = preferencesStore.activeLibrary.value ?? ''
    const activeCollection = preferencesStore.activeCollection.value ?? ''
    const expectedTrackCount = activeLibraryId
      ? (navigationSummary.value?.libraryTrackCounts?.[activeLibraryId] ?? 0)
      : 0
    const loadedTrackCount = activeLibraryId
      ? libraryStore.getTracksForLibrary(activeLibraryId).length
      : 0

    return [
      isBootstrapReady.value ? 'ready' : 'booting',
      activeLibraryId,
      activeCollection,
      navigationSummaryRevision.value,
      expectedTrackCount,
      loadedTrackCount,
      libraryStore.catalogTrackListComplete.value ? 'complete' : 'partial',
      libraryStore.catalogTrackArtworkMode.value,
    ].join('|')
  }

  function setReadiness(status: BrowserCatalogReadinessStatus, {
    libraryId = preferencesStore.activeLibrary.value,
    collectionRef = preferencesStore.activeCollection.value,
    reason = '',
    error = null,
  }: Partial<Omit<BrowserCatalogReadiness, 'status'>> = {}): void {
    readiness.value = {
      status,
      libraryId: libraryId ?? null,
      collectionRef: collectionRef ?? null,
      reason,
      error,
    }
  }

  function runHydration({ reason = 'browser-view' }: { reason?: string } = {}) {
    if (fullCatalogHydrationPromise) {
      return fullCatalogHydrationPromise
    }

    const hydrateStartedAt = now()
    const hydrateResourceStart = captureRendererResourceSample()
    const revision = catalogRevision.value
    const trackArtworkMode = BROWSER_CATALOG_ARTWORK_MODE
    fullCatalogHydrationPromise = libraryStore
      .hydrate(null, {
        revision,
        trackArtworkMode,
        trackListComplete: true,
      })
      .then((snapshot) => {
        recordStartupRendererStep('browserCatalogHydrate', hydrateStartedAt, hydrateResourceStart)
        void logDiagnosticsInfo('[OFPlayer bootstrap]', 'startup', 'browser_catalog_hydrated', {
          reason,
          trackArtworkMode,
          totalMs: Math.round(now() - hydrateStartedAt),
          libraryCount: snapshot?.libraries?.length ?? 0,
          playlistCount: snapshot?.playlists?.length ?? 0,
          trackCount: snapshot?.tracks?.length ?? 0,
          artworkTrackCount: (snapshot?.tracks ?? []).filter((track) => Boolean(track?.artwork)).length,
        })
        return snapshot
      })
      .catch((error: unknown) => {
        void logDiagnosticsWarn('[OFPlayer bootstrap]', 'startup', 'browser_catalog_hydrate_failed', {
          reason,
          error,
        })
        return null
      })
      .finally(() => {
        fullCatalogHydrationPromise = null
      })

    return fullCatalogHydrationPromise
  }

  async function prepareSelection({
    libraryId = preferencesStore.activeLibrary.value,
    collectionRef = preferencesStore.activeCollection.value,
    reason = 'browser-view',
  }: { libraryId?: string | null; collectionRef?: string | null; reason?: string } = {}): Promise<boolean> {
    if (!isBrowserCollectionRef(collectionRef)) {
      return true
    }

    if (!libraryId) {
      return false
    }

    const requestId = ++preparationRequestId

    if (!needsFullCatalogForLibrary(libraryId)) {
      setReadiness('ready', { libraryId, collectionRef, reason })
      return true
    }

    setReadiness('preparing', { libraryId, collectionRef, reason })

    const snapshot = await runHydration({ reason })
    const ready = Boolean(snapshot) && !needsFullCatalogForLibrary(libraryId)

    if (requestId !== preparationRequestId) {
      return ready
    }

    setReadiness(ready ? 'ready' : 'error', {
      libraryId,
      collectionRef,
      reason,
      error: ready ? null : 'browser-catalog-hydration-failed',
    })

    return ready
  }

  function hydrateFullCatalogForBrowserView({ reason = 'browser-view' }: { reason?: string } = {}) {
    if (!isBrowserCollectionRef(preferencesStore.activeCollection.value)) {
      return Promise.resolve(null)
    }

    if (!needsFullCatalogForActiveLibrary()) {
      return Promise.resolve(null)
    }

    return prepareSelection({ reason })
  }

  function startWatcher({ immediate = false }: { immediate?: boolean } = {}) {
    if (stopWatcher) {
      return stopWatcher
    }

    stopWatcher = watch(
      buildHydrationKey,
      () => {
        if (!isBootstrapReady.value) {
          return
        }

        void hydrateFullCatalogForBrowserView({ reason: 'browser-catalog-watch' })
      },
      { immediate, flush: 'post' },
    )

    return stopWatcher
  }

  function stopHydrationWatcher(): void {
    if (typeof stopWatcher !== 'function') {
      return
    }

    stopWatcher()
    stopWatcher = null
  }

  return {
    getActiveCollectionDataError,
    getActiveCollectionDataReady,
    getActiveCollectionDataStatus,
    hydrateFullCatalogForBrowserView,
    isBrowserCollectionRef,
    needsFullCatalogForLibrary,
    prepareSelection,
    runHydration,
    setReadiness,
    startWatcher,
    stopWatcher: stopHydrationWatcher,
  }
}
