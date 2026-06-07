import { computed, inject, ref, watch, type App, type InjectionKey } from 'vue'
import { createDataService } from '../services/data'
import { createLibraryService } from '../services/libraryService'
import { createPlaylistService } from '../services/playlistService'
import { createTrackService } from '../services/trackService'
import { createExternalLibraryService } from '../services/externalLibraryService'
import { createFileImportService } from '../services/fileImportService'
import { createDesktopStorageService } from '../services/desktopStorageService'
import { createLyricsService } from '../services/lyricsService'
import { createNavigationQueryService } from '../services/navigationQueryService'
import { createSystemMediaService } from '../services/systemMediaService'
import { createAppUpdateService, UPDATE_CHECK_DAILY_MS } from '../services/appUpdateService'
import { createLicenseService } from '../services/licenseService'
import {
  hasDiagnosticsReportEndpoint,
  uploadDiagnosticsReport,
} from '../services/diagnosticsReportService'
import {
  trackPause,
  trackPlay,
  trackSeek,
  trackSkipNext,
  trackSkipPrev,
  trackTelemetryConsent,
} from '../services/telemetryService'
import {
  logDiagnosticsError,
  logDiagnosticsInfo,
  logDiagnosticsWarn,
} from '../services/diagnosticsLogger'
import { formatCommandError } from '../services/errorNormalizer'
import {
  buildRendererResourceProfile,
  buildRendererStepProfile,
  captureRendererResourceSample,
} from '../services/diagnosticsProfiler'
import { createLibraryStore } from '../stores/libraryStore'
import { clearPersistedStartupState, createPreferencesStore } from '../stores/preferencesStore'
import { createPlayerStore } from '../stores/playerStore'
import { createSessionStore } from '../stores/sessionStore'
import { REPEAT_MODES } from '../models/preferences'
import {
  LICENSE_FEATURES,
  createFeatureLimitError,
  createFeatureLimitSnapshot,
} from '../models/license'
import {
  buildArtworkAlbumKey,
  createEmptyNavigationSummary,
  createIdleRemotePlaybackStatus,
  createIdleScanProgress,
  createPlaybackSourceOverride,
  createRemotePlaybackMetadataPatch,
  createRemoteTrackReadiness,
  hasRemotePlaybackData,
  isExternalTrack,
  isTransientPlaybackSource,
  normalizeArtworkUrl,
  normalizeScanCount,
  nowMs,
  resolveBackendRevisions,
  resolveLyricsDialogPath,
  resolveScanMode,
  sanitizeTrackArtwork,
  shouldResolvePlaybackMetadataDuringPrepare,
} from './appStateHelpers'
import { createBrowserCatalogController } from './browserCatalogController'
import {
  createPlaybackOrderSignature,
  createShuffledPlaybackOrder,
  normalizePlaybackOrderTrackIds,
  resolvePlaybackOrderTrackId as resolvePlaybackOrderTrackIdFromState,
} from './playbackOrder'
import { createRemoteMetadataHydrator } from './remoteMetadataHydrator'
import { createScanProgressController } from './scanProgressController'
import { createStorageWatchController } from './storageWatchController'

type UnknownRecord = Record<string, any>
type TimerId = number | ReturnType<typeof setTimeout>
type IntervalId = number | ReturnType<typeof setInterval>
type UnlistenFn = () => void
type DiagnosticsPayload = UnknownRecord | null | undefined
type DiagnosticsLogger = (
  scope: string,
  category: string,
  event: string,
  payload?: DiagnosticsPayload,
) => Promise<boolean>
type TrackLike = UnknownRecord | null | undefined
type RendererResourceSample = ReturnType<typeof captureRendererResourceSample>
type RendererStepProfile = ReturnType<typeof buildRendererStepProfile>
type NavigationSelection = {
  activeLibrary?: string | null
  activeCollectionKey?: string | null
}
type SyncQueueOptions = {
  shouldApply?: () => boolean
}
type PlayNextOptions = {
  reason?: string
}
type LibraryScanImportOptions = UnknownRecord & {
  source?: string
  interactive?: boolean
  directories?: string[]
  respectDeletedImportPaths?: boolean
}

const TRACK_ARTWORK_PREFETCH_BATCH_SIZE = 6

interface OFPlayerAppOptions {
  data?: UnknownRecord
  fileImportService?: UnknownRecord
  desktopStorageService?: UnknownRecord
  navigationQueryService?: UnknownRecord
  systemMediaService?: UnknownRecord
  appUpdateService?: UnknownRecord
  licenseService?: UnknownRecord
  lyricsService?: UnknownRecord
  license?: UnknownRecord
  startupDiagnosticsVersion?: string
  [key: string]: unknown
}

interface StartupDiagnostics {
  totalMs?: number
  elapsedMs?: number
  waitMs?: number
  bootstrapLoadMs?: number
  storeHydrateMs?: number
  historyHydrateMs?: number
  activeTrackHydrateMs?: number
  durationMs?: number
  directoriesScanned?: number
  entriesScanned?: number
  activeTrackId?: string
  syncQueueMs?: number
  bootstrapWaitMs?: number
  discoverMs?: number
  filterMs?: number
  prepareMs?: number
  persistMs?: number
  playbackSyncMs?: number
  copyMs?: number
  metadataMs?: number
  [key: string]: unknown
}

export type OFPlayerApp = UnknownRecord & {
  waitForVisualReady?: () => Promise<void>
  startDeferredStartup?: () => void
  dispose?: () => void
}

const logDiagnosticsErrorAny: DiagnosticsLogger = logDiagnosticsError
const logDiagnosticsInfoAny: DiagnosticsLogger = logDiagnosticsInfo
const logDiagnosticsWarnAny: DiagnosticsLogger = logDiagnosticsWarn

const OFPLAYER_APP_KEY: InjectionKey<OFPlayerApp> = Symbol('OFPLAYER_APP')
const BOOTSTRAP_HISTORY_LIMIT = 100
const STORAGE_WATCH_DEBOUNCE_MS = 1200
const SCAN_PROGRESS_RESET_MS = 4000
const SCAN_DIAGNOSTICS_LOG_THRESHOLD_MS = 1200
const STARTUP_LOG_THRESHOLD_MS = 200
const STARTUP_AUTO_SCAN_DELAY_MS = 900
const STARTUP_UPDATE_CHECK_DELAY_MS = 4200
const NAVIGATION_QUERY_LOG_THRESHOLD_MS = 12
const REMOTE_METADATA_START_DELAY_MS = 1200
const REMOTE_METADATA_IDLE_DELAY_MS = 900
const REMOTE_METADATA_PLAYING_DELAY_MS = 2600
const PLAYBACK_ARTWORK_FALLBACK_CLEAR_MS = 180
const STARTUP_PLAYBACK_SYNC_DELAY_MS = 1200
const STARTUP_DIAGNOSTICS_VERSION_DEFAULT = '2026-04-18-startup-v4'
const SMART_COLLECTION_RECENTLY_PLAYED = 'view:all-plays'
const SMART_COLLECTION_CURRENT_QUEUE = 'view:current-queue'

export async function createOFPlayerApp(options: OFPlayerAppOptions = {}): Promise<OFPlayerApp> {
  const startupStartedAt = nowMs()
  const startupDiagnostics: StartupDiagnostics = {}
  const startupRendererStepProfiles: UnknownRecord[] = []
  const startupRendererResourceStart = captureRendererResourceSample()
  const startupDiagnosticsVersion =
    options.startupDiagnosticsVersion ?? STARTUP_DIAGNOSTICS_VERSION_DEFAULT
  const dataService = createDataService(options.data ?? {}) as UnknownRecord
  const fileImportService = (options.fileImportService ?? createFileImportService()) as Record<string, any>
  const desktopStorageService = (options.desktopStorageService ?? createDesktopStorageService()) as Record<string, any>
  const navigationQueryService = (options.navigationQueryService ?? createNavigationQueryService()) as Record<string, any>
  const systemMediaService = (options.systemMediaService ?? createSystemMediaService()) as Record<string, any>
  const appUpdateService = (options.appUpdateService ?? createAppUpdateService()) as Record<string, any>
  const licenseService = (options.licenseService ?? createLicenseService(options.license ?? {})) as Record<string, any>
  const libraryService = createLibraryService({ dataService: dataService as any }) as Record<string, any>
  const playlistService = createPlaylistService({ dataService: dataService as any }) as Record<string, any>
  const trackService = createTrackService({ dataService: dataService as any }) as Record<string, any>
  const externalLibraryService = createExternalLibraryService({
    dataService: dataService as any,
    libraryService: libraryService as any,
  }) as Record<string, any>

  const libraryStore = createLibraryStore({
    libraryService: libraryService as any,
    playlistService: playlistService as any,
    trackService: trackService as any,
  }) as UnknownRecord
  const sessionStore = createSessionStore({ dataService: dataService as any }) as UnknownRecord
  const preferencesStore = createPreferencesStore({ dataService: dataService as any }) as UnknownRecord
  const lyricsService =
    options.lyricsService ??
    createLyricsService({
      dataService,
      getLyricsDirectories: () => preferencesStore.lyricsScanDirectories.value,
    })

  const playerStore = createPlayerStore({
    dataService: dataService as any,
    initialVolume: preferencesStore.volume.value,
    onTrackDurationChange: ({ trackId, duration }: { trackId: string; duration: number }) => {
      const currentTrackDuration = Number(libraryStore.getTrackById(trackId)?.duration)

      if (Number.isFinite(currentTrackDuration) && Math.abs(currentTrackDuration - duration) <= 0.25) {
        return
      }

      void libraryStore.updateTrackMetadata(trackId, { duration })
    },
    onTrackEnded: () => {
      void handlePlaybackEnded()
    },
  })
  const isBootstrapReady = ref(false)
  const remotePlaybackStatus = ref<UnknownRecord>(createIdleRemotePlaybackStatus())

  const tracks = libraryStore.tracks
  const libraries = libraryStore.libraries
  const playlists = libraryStore.playlists
  const playlistTrackRelations = libraryStore.playlistTrackRelations
  const licenseState = ref<UnknownRecord>(licenseService.load())
  const catalogRevision = libraryStore.catalogRevision
  const currentTrackId = computed(() => sessionStore.currentTrackId.value)
  const shuffledPlaybackOrderTrackIds = ref<string[]>([])
  const retainedPlaybackArtwork = ref<UnknownRecord | null>(null)
  const navigationSummary = ref<UnknownRecord>(createEmptyNavigationSummary())
  const hasTracks = computed(() => {
    if (libraryStore.hasTracks.value) {
      return true
    }

    return Object.values(navigationSummary.value?.libraryTrackCounts ?? {}).some(
      (count) => Number.isFinite(Number(count)) && Number(count) > 0,
    )
  })
  const licenseFeatureLimits = computed(() =>
    createFeatureLimitSnapshot({
      licenseState: licenseState.value,
      libraryCount: libraryStore.libraries.value.length,
      playlistCount: libraryStore.playlists.value.filter((playlist: UnknownRecord) => playlist.kind === 'user').length,
    }),
  )
  const rawCurrentTrack = computed(() => {
    const trackId = currentTrackId.value
    const cachedTrack = libraryStore.getTrackById(trackId)
    const activeTrack = playerStore.activeTrack.value?.id === trackId ? playerStore.activeTrack.value : null

    if (cachedTrack && normalizeArtworkUrl(cachedTrack.artwork)) {
      return sanitizeTrackArtwork(cachedTrack)
    }

    if (cachedTrack && activeTrack && normalizeArtworkUrl(activeTrack.artwork)) {
      return sanitizeTrackArtwork({
        ...cachedTrack,
        artwork: normalizeArtworkUrl(activeTrack.artwork),
      })
    }

    return sanitizeTrackArtwork(cachedTrack ?? activeTrack)
  })
  const currentTrack = computed(() => {
    const track = rawCurrentTrack.value

    if (!track || normalizeArtworkUrl(track.artwork)) {
      return sanitizeTrackArtwork(track)
    }

    const retainedArtwork = normalizeArtworkUrl(retainedPlaybackArtwork.value?.artwork)

    return retainedArtwork ? { ...track, artwork: retainedArtwork } : sanitizeTrackArtwork(track)
  })
  const currentRemoteTrackStatus = computed(() =>
    createRemoteTrackReadiness(currentTrack.value, remotePlaybackStatus.value),
  )
  const navigationSummaryRevision = computed(() => {
    const activeCollection = preferencesStore.activeCollection.value

    if (activeCollection === SMART_COLLECTION_RECENTLY_PLAYED) {
      return String(catalogRevision.value)
    }

    if (activeCollection === SMART_COLLECTION_CURRENT_QUEUE) {
      return `${catalogRevision.value}:${sessionStore.revision.value}`
    }

    return String(catalogRevision.value)
  })
  const collectionQueryRevision = computed(() => {
    const activeCollection = preferencesStore.activeCollection.value

    if (activeCollection === SMART_COLLECTION_RECENTLY_PLAYED) {
      return String(catalogRevision.value)
    }

    if (activeCollection === SMART_COLLECTION_CURRENT_QUEUE) {
      return `${catalogRevision.value}:${sessionStore.revision.value}`
    }

    return String(catalogRevision.value)
  })
  const navigationRefreshKey = computed(
    () => `${navigationSummaryRevision.value}|${preferencesStore.activeLibrary.value ?? ''}`,
  )
  const activeSortOption = computed(() =>
    preferencesStore.getCollectionSortOption(
      preferencesStore.activeCollection.value,
      preferencesStore.sortOption.value,
    ),
  )
  const browserCatalogReadiness = ref({
    status: 'ready',
    libraryId: null,
    collectionRef: null,
    reason: '',
    error: null,
  })
  const browserCatalog = createBrowserCatalogController({
    watch,
    readiness: browserCatalogReadiness,
    libraryStore: libraryStore as any,
    preferencesStore: preferencesStore as any,
    navigationSummary,
    navigationSummaryRevision,
    isBootstrapReady,
    catalogRevision,
    captureRendererResourceSample: captureRendererResourceSample as unknown as () => Record<string, unknown>,
    recordStartupRendererStep: recordStartupRendererStep as any,
    logDiagnosticsInfo,
    logDiagnosticsWarn,
  })
  const activeCollectionDataReady = computed(browserCatalog.getActiveCollectionDataReady)
  const activeCollectionDataStatus = computed(browserCatalog.getActiveCollectionDataStatus)
  const activeCollectionDataError = computed(browserCatalog.getActiveCollectionDataError)
  const hydrateFullCatalogForBrowserView = browserCatalog.hydrateFullCatalogForBrowserView
  const isBrowserCollectionRef = browserCatalog.isBrowserCollectionRef
  const needsFullCatalogForLibrary = browserCatalog.needsFullCatalogForLibrary
  const prepareBrowserCatalogForSelection = browserCatalog.prepareSelection
  const startBrowserCatalogHydrationWatcher = browserCatalog.startWatcher
  const scanProgress = ref<UnknownRecord>(createIdleScanProgress())
  const isResettingData = ref(false)
  const storageUsage = ref<UnknownRecord | null>(null)
  const isLoadingStorageUsage = ref(false)
  const isCollectingGarbage = ref(false)
  const storageMaintenanceError = ref('')
  const isUploadingDiagnosticsReport = ref(false)
  const diagnosticsReportStatus = ref({
    state: 'idle',
    message: '',
    uploadedAt: '',
    eventCount: 0,
  })
  let activeScanPromise: Promise<unknown> | null = null
  let unlistenStorageWatch: UnlistenFn | null = null
  let unlistenScanProgress: UnlistenFn | null = null
  let unlistenSystemMedia: UnlistenFn | null = null
  let stopNavigationWatcher: UnlistenFn | null = null
  let navigationRequestId = 0
  let lastNavigationSummaryRequestKey = ''
  let disposed = false
  let lifecycleRevision = 0
  let bootstrapState: UnknownRecord | null = null
  let hasBootstrapNavigationSummary = false
  let bootstrapPromise: Promise<UnknownRecord | null> = Promise.resolve(null)
  let deferredStartupPromise: Promise<unknown> | null = null
  let deferredStartupStarted = false
  let autoScanStartupTimerId: TimerId | null = null
  let updateCheckStartupTimerId: TimerId | null = null
  let updateCheckDailyTimerId: IntervalId | null = null
  let retainedPlaybackArtworkClearTimerId: TimerId | null = null
  let startupPlaybackSyncTimerId: TimerId | null = null
  let trackArtworkPrefetchTimerId: TimerId | null = null
  let trackArtworkPrefetchPromise: Promise<void> | null = null
  let currentArtworkRequestId = 0
  let selectTrackRequestId = 0
  let navigationSelectionRequestId = 0
  let playbackCommandRevision = 0
  const pendingTrackArtworkPrefetchIds = new Set<string>()
  let resolveVisualReady: (value?: void | PromiseLike<void>) => void = () => {}
  const visualReadyPromise = new Promise<void>((resolve) => {
    resolveVisualReady = resolve
  })
  const scanProgressController = createScanProgressController({
    scanProgress: scanProgress as any,
    getLifecycleToken: currentLifecycleToken,
    isLifecycleCurrent,
    resetDelayMs: SCAN_PROGRESS_RESET_MS,
  })
  const remoteMetadataHydrator = createRemoteMetadataHydrator({
    libraryStore: libraryStore as any,
    externalLibraryService: externalLibraryService as any,
    getActiveLibraryId: () => preferencesStore.activeLibrary.value,
    isPlaying: () => playerStore.isPlaying.value,
    isDisposed: () => disposed,
    getLifecycleToken: currentLifecycleToken,
    isLifecycleCurrent,
    logDiagnosticsInfo,
    startDelayMs: REMOTE_METADATA_START_DELAY_MS,
    idleDelayMs: REMOTE_METADATA_IDLE_DELAY_MS,
    playingDelayMs: REMOTE_METADATA_PLAYING_DELAY_MS,
  })
  const storageWatchController = createStorageWatchController({
    desktopStorageService: desktopStorageService as any,
    preferencesStore: preferencesStore as any,
    isDisposed: () => disposed,
    isResettingData: () => isResettingData.value,
    runLibraryScanImport,
    debounceMs: STORAGE_WATCH_DEBOUNCE_MS,
  })

  function clearStartupPlaybackSyncTimer() {
    if (startupPlaybackSyncTimerId === null) {
      return
    }

    clearTimeout(startupPlaybackSyncTimerId)
    startupPlaybackSyncTimerId = null
  }

  function clearRetainedPlaybackArtworkTimer() {
    if (retainedPlaybackArtworkClearTimerId === null) {
      return
    }

    clearTimeout(retainedPlaybackArtworkClearTimerId)
    retainedPlaybackArtworkClearTimerId = null
  }

  function scheduleRetainedPlaybackArtworkClear(trackId: string) {
    clearRetainedPlaybackArtworkTimer()
    retainedPlaybackArtworkClearTimerId = setTimeout(() => {
      retainedPlaybackArtworkClearTimerId = null

      if (rawCurrentTrack.value?.id !== trackId || normalizeArtworkUrl(rawCurrentTrack.value?.artwork)) {
        return
      }

      retainedPlaybackArtwork.value = null
    }, PLAYBACK_ARTWORK_FALLBACK_CLEAR_MS)
  }

  function updateRetainedPlaybackArtwork(track: TrackLike) {
    const artwork = normalizeArtworkUrl(track?.artwork)

    if (artwork) {
      clearRetainedPlaybackArtworkTimer()
      retainedPlaybackArtwork.value = {
        artwork,
        albumKey: buildArtworkAlbumKey(track),
        trackId: track?.id ?? null,
      }
      return
    }

    if (!track) {
      clearRetainedPlaybackArtworkTimer()
      retainedPlaybackArtwork.value = null
      return
    }

    const retained = retainedPlaybackArtwork.value
    const trackAlbumKey = buildArtworkAlbumKey(track)

    if (retained?.artwork && retained.albumKey && retained.albumKey === trackAlbumKey) {
      clearRetainedPlaybackArtworkTimer()
      return
    }

    if (retained?.artwork) {
      scheduleRetainedPlaybackArtworkClear(track.id)
    }
  }

  function currentLifecycleToken() {
    return lifecycleRevision
  }

  watch(rawCurrentTrack, (track) => {
    const cachedArtworkTrack = cachePlaybackArtworkForBrowserViews(track)
    updateRetainedPlaybackArtwork(cachedArtworkTrack ?? track)
  }, { immediate: true })

  watch(
    () => `${preferencesStore.shuffleEnabled.value ? '1' : '0'}|${createPlaybackOrderSignature(sessionStore.queueTrackIds.value)}`,
    () => {
      if (preferencesStore.shuffleEnabled.value) {
        rebuildShuffledPlaybackOrder()
      } else {
        shuffledPlaybackOrderTrackIds.value = []
      }
    },
    { immediate: true },
  )

  function isLifecycleCurrent(token: unknown) {
    return !disposed && token === lifecycleRevision
  }

  function isSelectionCurrent(token: unknown, selectionRequestId: number) {
    return isLifecycleCurrent(token) && selectionRequestId === selectTrackRequestId
  }

  function clearAppUpdateTimers() {
    if (updateCheckStartupTimerId !== null) {
      clearTimeout(updateCheckStartupTimerId)
      updateCheckStartupTimerId = null
    }

    if (updateCheckDailyTimerId !== null) {
      clearInterval(updateCheckDailyTimerId)
      updateCheckDailyTimerId = null
    }
  }

  function scheduleAppUpdateChecks(startupToken: number) {
    if (typeof window === 'undefined' || !isLifecycleCurrent(startupToken)) {
      return
    }

    clearAppUpdateTimers()
    updateCheckStartupTimerId = window.setTimeout(() => {
      updateCheckStartupTimerId = null

      if (!isLifecycleCurrent(startupToken)) {
        return
      }

      void appUpdateService.checkForUpdates({
        reason: 'startup',
        silent: true,
        force: true,
        minIntervalMs: 0,
      })
    }, STARTUP_UPDATE_CHECK_DELAY_MS)

    updateCheckDailyTimerId = window.setInterval(() => {
      if (!isLifecycleCurrent(startupToken)) {
        clearAppUpdateTimers()
        return
      }

      void appUpdateService.checkForUpdates({
        reason: 'daily',
        silent: true,
        minIntervalMs: UPDATE_CHECK_DAILY_MS,
      })
    }, UPDATE_CHECK_DAILY_MS)
  }

  function invalidateLifecycle() {
    lifecycleRevision += 1
    return lifecycleRevision
  }

  function cachePlaybackArtworkForBrowserViews(track: TrackLike) {
    if (!track?.id || !normalizeArtworkUrl(track.artwork)) {
      return track ?? null
    }

    const cachedTrack = libraryStore.getTrackById(track.id)

    if (normalizeArtworkUrl(cachedTrack?.artwork)) {
      return cachedTrack
    }

    return libraryStore.cacheTrackArtwork(track)
  }

  function normalizeTrackArtworkPrefetchIds(trackIds: unknown): string[] {
    const values = Array.isArray(trackIds) ? trackIds : [trackIds]

    return [...new Set(values
      .map((trackId) => (typeof trackId === 'string' ? trackId.trim() : ''))
      .filter(Boolean))]
  }

  function clearTrackArtworkPrefetchQueue() {
    pendingTrackArtworkPrefetchIds.clear()

    if (trackArtworkPrefetchTimerId !== null) {
      clearTimeout(trackArtworkPrefetchTimerId)
      trackArtworkPrefetchTimerId = null
    }
  }

  function scheduleTrackArtworkPrefetchDrain() {
    if (disposed || trackArtworkPrefetchTimerId !== null || trackArtworkPrefetchPromise) {
      return
    }

    trackArtworkPrefetchTimerId = setTimeout(() => {
      trackArtworkPrefetchTimerId = null
      void drainTrackArtworkPrefetchQueue()
    }, 40)
  }

  async function drainTrackArtworkPrefetchQueue() {
    if (trackArtworkPrefetchPromise) {
      return trackArtworkPrefetchPromise
    }

    const token = currentLifecycleToken()
    trackArtworkPrefetchPromise = (async () => {
      while (!disposed && isLifecycleCurrent(token) && pendingTrackArtworkPrefetchIds.size > 0) {
        const batch = [...pendingTrackArtworkPrefetchIds].slice(0, TRACK_ARTWORK_PREFETCH_BATCH_SIZE)

        for (const trackId of batch) {
          pendingTrackArtworkPrefetchIds.delete(trackId)
        }

        await Promise.all(batch.map(async (trackId) => {
          if (disposed || !isLifecycleCurrent(token)) {
            return
          }

          try {
            const cachedTrack = libraryStore.getTrackById(trackId)

            if (normalizeArtworkUrl(cachedTrack?.artwork)) {
              return
            }

            const loadedTrack = await libraryStore.getOrLoadTrack(trackId, {
              includeArtwork: true,
              cache: true,
            })

            if (normalizeArtworkUrl(loadedTrack?.artwork)) {
              cachePlaybackArtworkForBrowserViews(loadedTrack)
            }
          } catch (error) {
            void logDiagnosticsWarnAny('[OFPlayer track artwork]', 'catalog', 'track_artwork_prefetch_failed', {
              trackId,
              message: formatCommandError(error, 'track_artwork_prefetch_failed'),
            })
          }
        }))
      }
    })().finally(() => {
      trackArtworkPrefetchPromise = null

      if (!disposed && pendingTrackArtworkPrefetchIds.size > 0) {
        scheduleTrackArtworkPrefetchDrain()
      }
    })

    return trackArtworkPrefetchPromise
  }

  function hydrateTrackArtwork(trackIds: unknown) {
    for (const trackId of normalizeTrackArtworkPrefetchIds(trackIds)) {
      const cachedTrack = libraryStore.getTrackById(trackId)

      if (!normalizeArtworkUrl(cachedTrack?.artwork)) {
        pendingTrackArtworkPrefetchIds.add(trackId)
      }
    }

    scheduleTrackArtworkPrefetchDrain()
  }

  async function ensureCurrentTrackArtwork(trackId = currentTrackId.value) {
    const normalizedTrackId = typeof trackId === 'string' ? trackId.trim() : ''

    if (!normalizedTrackId) {
      return null
    }

    const activeTrack = playerStore.activeTrack.value?.id === normalizedTrackId
      ? playerStore.activeTrack.value
      : null
    const cachedTrack = libraryStore.getTrackById(normalizedTrackId)

    if (normalizeArtworkUrl(activeTrack?.artwork)) {
      const cachedArtworkTrack = cachePlaybackArtworkForBrowserViews(activeTrack)
      updateRetainedPlaybackArtwork(cachedArtworkTrack ?? activeTrack)
      return cachedArtworkTrack ?? activeTrack
    }

    if (normalizeArtworkUrl(cachedTrack?.artwork)) {
      updateRetainedPlaybackArtwork(cachedTrack)
      return cachedTrack
    }

    const requestId = ++currentArtworkRequestId
    const token = currentLifecycleToken()
    const loadedTrack = await libraryStore.getOrLoadTrack(normalizedTrackId, {
      includeArtwork: true,
      cache: true,
    })

    if (
      !isLifecycleCurrent(token) ||
      requestId !== currentArtworkRequestId ||
      currentTrackId.value !== normalizedTrackId
    ) {
      return null
    }

    const artwork = normalizeArtworkUrl(loadedTrack?.artwork)

    if (!artwork) {
      return loadedTrack
    }

    const currentActiveTrack = playerStore.activeTrack.value?.id === normalizedTrackId
      ? playerStore.activeTrack.value
      : null
    const nextActiveTrack = currentActiveTrack
      ? {
          ...currentActiveTrack,
          artwork,
        }
      : loadedTrack

    const cachedArtworkTrack = cachePlaybackArtworkForBrowserViews(nextActiveTrack)
    playerStore.setActiveTrack(cachedArtworkTrack ?? nextActiveTrack)
    updateRetainedPlaybackArtwork(cachedArtworkTrack ?? nextActiveTrack)
    return cachedArtworkTrack ?? nextActiveTrack
  }

  function recordStartupRendererStep(key: string, startedAt: number, resourceStart: RendererResourceSample | null = null) {
    const resourceEnd = captureRendererResourceSample()
    startupRendererStepProfiles.push(
      buildRendererStepProfile(key, nowMs() - startedAt, resourceStart, resourceEnd),
    )
    return resourceEnd
  }

  function logStartupPhase(event: string, payload: UnknownRecord = {}, thresholdMs = 0) {
    const durationCandidates = [
      payload.totalMs,
      payload.elapsedMs,
      payload.waitMs,
      payload.bootstrapLoadMs,
      payload.storeHydrateMs,
      payload.historyHydrateMs,
      payload.activeTrackHydrateMs,
      payload.durationMs,
    ].filter((value) => Number.isFinite(value))
    const peakDuration = durationCandidates.length > 0 ? Math.max(...durationCandidates) : 0

    if (thresholdMs > 0 && peakDuration < thresholdMs) {
      return
    }

    void logDiagnosticsInfoAny('[OFPlayer startup phase]', 'startup', event, {
      diagnosticsVersion: startupDiagnosticsVersion,
      ...payload,
    })
  }

  function applyPlaybackCommandResult(result: UnknownRecord) {
    if (disposed || !result) {
      return false
    }

    if (result.session) {
      sessionStore.applySnapshot(result.session)
    }

    playerStore.applyPlaybackSnapshot(result.playback ?? {})
    playerStore.prependHistoryEntries(result.historyEntries ?? [])
    const nextTrackId = result.session?.currentTrackId ?? null
    const cachedTrack = libraryStore.getTrackById(nextTrackId)
    const existingActiveTrack =
      playerStore.activeTrack.value?.id === nextTrackId ? playerStore.activeTrack.value : null
    let nextActiveTrack =
      cachedTrack && existingActiveTrack && !normalizeArtworkUrl(cachedTrack.artwork)
        ? {
            ...cachedTrack,
            artwork: normalizeArtworkUrl(existingActiveTrack.artwork),
          }
        : cachedTrack ?? existingActiveTrack

    nextActiveTrack = cachePlaybackArtworkForBrowserViews(nextActiveTrack) ?? nextActiveTrack

    playerStore.setActiveTrack(nextActiveTrack)

    if (nextTrackId && !normalizeArtworkUrl(nextActiveTrack?.artwork)) {
      void ensureCurrentTrackArtwork(nextTrackId)
    }

    return true
  }

  function rebuildShuffledPlaybackOrder(anchorTrackId = currentTrackId.value) {
    const queueTrackIds = normalizePlaybackOrderTrackIds(sessionStore.queueTrackIds.value)
    shuffledPlaybackOrderTrackIds.value = createShuffledPlaybackOrder(queueTrackIds, anchorTrackId)
    return shuffledPlaybackOrderTrackIds.value
  }

  function getPlaybackQueueOrder() {
    const queueTrackIds = normalizePlaybackOrderTrackIds(sessionStore.queueTrackIds.value)

    if (!preferencesStore.shuffleEnabled.value) {
      return queueTrackIds
    }

    const queueTrackIdSet = new Set(queueTrackIds)
    const shuffledTrackIds = normalizePlaybackOrderTrackIds(shuffledPlaybackOrderTrackIds.value)
      .filter((trackId) => queueTrackIdSet.has(trackId))
    const shuffledTrackIdSet = new Set(shuffledTrackIds)
    const hasCompleteShuffleOrder =
      shuffledTrackIds.length === queueTrackIds.length &&
      queueTrackIds.every((trackId) => shuffledTrackIdSet.has(trackId))

    if (hasCompleteShuffleOrder) {
      return shuffledTrackIds
    }

    return rebuildShuffledPlaybackOrder()
  }

  function resolvePlaybackOrderTrackId(step: number, options: UnknownRecord = {}) {
    const reason = typeof options === 'object' && options ? options.reason ?? 'user' : 'user'
    const repeatMode = preferencesStore.shuffleEnabled.value
      ? REPEAT_MODES.NONE
      : preferencesStore.repeatMode.value

    return resolvePlaybackOrderTrackIdFromState({
      queueTrackIds: getPlaybackQueueOrder(),
      currentTrackId: currentTrackId.value,
      repeatMode,
      step,
      reason,
    })
  }

  function setRepeatMode(repeatMode: string) {
    if (preferencesStore.shuffleEnabled.value) {
      return preferencesStore.setRepeatMode(REPEAT_MODES.NONE)
    }

    return preferencesStore.setRepeatMode(repeatMode)
  }

  function cycleRepeatMode() {
    const currentRepeatMode = preferencesStore.repeatMode.value
    const nextRepeatMode =
      currentRepeatMode === REPEAT_MODES.ALL
        ? REPEAT_MODES.ONE
        : currentRepeatMode === REPEAT_MODES.ONE
          ? REPEAT_MODES.NONE
          : REPEAT_MODES.ALL

    return setRepeatMode(nextRepeatMode)
  }

  function setShuffleEnabled(shuffleEnabled: boolean) {
    const nextShuffleEnabled = preferencesStore.setShuffleEnabled(shuffleEnabled)

    if (nextShuffleEnabled) {
      rebuildShuffledPlaybackOrder()
    } else {
      shuffledPlaybackOrderTrackIds.value = []
    }

    return nextShuffleEnabled
  }

  function toggleShuffle() {
    return setShuffleEnabled(!preferencesStore.shuffleEnabled.value)
  }

  function cyclePlaybackMode() {
    if (preferencesStore.shuffleEnabled.value) {
      setShuffleEnabled(false)
      return preferencesStore.setRepeatMode(REPEAT_MODES.NONE)
    }

    const currentRepeatMode = preferencesStore.repeatMode.value

    if (currentRepeatMode === REPEAT_MODES.NONE) {
      return preferencesStore.setRepeatMode(REPEAT_MODES.ALL)
    }

    if (currentRepeatMode === REPEAT_MODES.ALL) {
      return preferencesStore.setRepeatMode(REPEAT_MODES.ONE)
    }

    preferencesStore.setRepeatMode(REPEAT_MODES.NONE)
    return setShuffleEnabled(true)
  }

  async function syncQueueWithCatalog(options: SyncQueueOptions = {}) {
    const syncToken = currentLifecycleToken()
    const result = await dataService.playbackSession.syncCatalog()

    if (!isLifecycleCurrent(syncToken) || options.shouldApply?.() === false) {
      return false
    }

    return applyPlaybackCommandResult(result)
  }

  function scheduleStartupPlaybackSync(startupToken: number) {
    clearStartupPlaybackSyncTimer()
    const scheduledCommandRevision = playbackCommandRevision

    startupPlaybackSyncTimerId = setTimeout(() => {
      startupPlaybackSyncTimerId = null
      void runStartupPlaybackSync(startupToken, scheduledCommandRevision)
    }, STARTUP_PLAYBACK_SYNC_DELAY_MS)
  }

  async function runStartupPlaybackSync(startupToken: number, scheduledCommandRevision: number) {
    if (!isLifecycleCurrent(startupToken)) {
      return false
    }

    const syncQueueStartedAt = nowMs()
    const syncQueueResourceStart = captureRendererResourceSample()
    const applied = await syncQueueWithCatalog({
      shouldApply: () => scheduledCommandRevision === playbackCommandRevision,
    })

    if (!isLifecycleCurrent(startupToken)) {
      return false
    }

    startupDiagnostics.syncQueueMs = Math.round(nowMs() - syncQueueStartedAt)
    recordStartupRendererStep('syncQueue', syncQueueStartedAt, syncQueueResourceStart)

    void logDiagnosticsInfoAny('[OFPlayer app startup]', 'startup', 'startup_playback_sync', {
      diagnosticsVersion: startupDiagnosticsVersion,
      delayMs: STARTUP_PLAYBACK_SYNC_DELAY_MS,
      syncQueueMs: startupDiagnostics.syncQueueMs,
      applied,
      skippedApply: scheduledCommandRevision !== playbackCommandRevision,
    })

    return applied
  }

  function ensureActiveLibrarySelection() {
    const activeLibraryId = preferencesStore.activeLibrary.value
    const resolvedLibrary =
      libraryStore.getLibraryById(activeLibraryId) ?? libraryStore.libraries.value[0] ?? null

    if (!resolvedLibrary) {
      return null
    }

    if (resolvedLibrary.id !== activeLibraryId) {
      preferencesStore.setActiveLibrary(resolvedLibrary.id)
    }

    const currentCollection = preferencesStore.activeCollection.value

    if (!libraryStore.isCollectionAvailableForLibrary(resolvedLibrary.id, currentCollection)) {
      preferencesStore.setActiveCollection(libraryStore.getDefaultCollectionRef(resolvedLibrary.id))
    }

    applyNavigationSelection({
      activeLibrary: resolvedLibrary.id,
      activeCollectionKey: preferencesStore.activeCollection.value,
    })
    return resolvedLibrary.id
  }

  const clearScanProgressResetTimer = scanProgressController.clearResetTimer
  const finishScanProgress = scanProgressController.finish
  const resetScanProgress = scanProgressController.reset
  const startScanProgress = scanProgressController.start
  const updateScanProgress = scanProgressController.update
  const applyAutoScanPreference = storageWatchController.applyAutoScanPreference
  const applyLyricsScanDirectories = storageWatchController.applyLyricsScanDirectories
  const applyScanDirectories = storageWatchController.applyScanDirectories
  const applyStorageRoot = storageWatchController.applyStorageRoot
  const clearPendingStorageWatchScan = storageWatchController.clearPendingScan
  const handleStorageWatchEvent = storageWatchController.handleWatchEvent
  const syncStorageWatch = storageWatchController.syncWatch

  function handleLibraryScanProgressEvent(payload: UnknownRecord) {
    if (disposed) {
      return
    }

    scanProgressController.handleBackendProgress(payload)
  }

  function buildNavigationSummaryRequestKey() {
    return `${navigationSummaryRevision.value}|${preferencesStore.activeLibrary.value ?? ''}|${preferencesStore.activeCollection.value ?? ''}`
  }

  function applyNavigationSelection({ activeLibrary = null, activeCollectionKey = null }: NavigationSelection = {}) {
    navigationSummary.value = {
      ...navigationSummary.value,
      activeLibrary: activeLibrary ?? navigationSummary.value.activeLibrary ?? null,
      activeCollectionKey: activeCollectionKey ?? navigationSummary.value.activeCollectionKey ?? null,
    }
  }

  async function refreshNavigationSummary({ force = false } = {}) {
    const requestKey = buildNavigationSummaryRequestKey()

    if (!force && requestKey === lastNavigationSummaryRequestKey) {
      return navigationSummary.value
    }

    const requestId = ++navigationRequestId
    const summary = await navigationQueryService.resolveNavigationSummary({
      queryRevision: navigationSummaryRevision.value,
      activeLibrary: preferencesStore.activeLibrary.value,
      activeCollection: preferencesStore.activeCollection.value,
    })

    if (disposed || requestId !== navigationRequestId) {
      return summary
    }

    const navigationApplyStartedAt = nowMs()
    const navigationApplyResourceStart = captureRendererResourceSample()
    navigationSummary.value = {
      ...createEmptyNavigationSummary(),
      ...summary,
    }
    lastNavigationSummaryRequestKey = requestKey

    if (
      summary?.activeLibrary &&
      summary.activeLibrary !== preferencesStore.activeLibrary.value
    ) {
      preferencesStore.setActiveLibrary(summary.activeLibrary)
    }

    if (
      summary?.activeCollectionKey &&
      summary.activeCollectionKey !== preferencesStore.activeCollection.value
    ) {
      preferencesStore.setActiveCollection(summary.activeCollectionKey)
    }
    const navigationApplyProfile = buildRendererStepProfile(
      'uiApply',
      nowMs() - navigationApplyStartedAt,
      navigationApplyResourceStart,
      captureRendererResourceSample(),
    )

    const requestCacheStatus = summary?.diagnostics?.requestCacheStatus ?? 'miss'
    const roundTripMs = summary?.diagnostics?.roundTripMs ?? 0
    const requestCacheServedMs = summary?.diagnostics?.requestCacheServedMs ?? 0
    const shouldLogNavigationQuery =
      (summary?.diagnostics?.totalMs ?? 0) >= NAVIGATION_QUERY_LOG_THRESHOLD_MS ||
      roundTripMs >= NAVIGATION_QUERY_LOG_THRESHOLD_MS ||
      requestCacheServedMs >= NAVIGATION_QUERY_LOG_THRESHOLD_MS

    if (shouldLogNavigationQuery) {
      void logDiagnosticsInfoAny('[OFPlayer navigation query]', 'query', 'navigation_summary', {
        ...summary.diagnostics,
        navigationRevision: navigationSummaryRevision.value,
        requestCacheStatus,
        requestCacheHit: summary?.diagnostics?.requestCacheHit === true,
        requestCacheServedMs,
        uiApplyProfile: navigationApplyProfile,
        invokeOverheadMs:
          summary?.diagnostics?.invokeOverheadMs ??
          Math.max(0, roundTripMs - (summary?.diagnostics?.totalMs ?? 0)),
        activeLibrary: summary?.activeLibrary ?? preferencesStore.activeLibrary.value,
        activeCollection: summary?.activeCollectionKey ?? preferencesStore.activeCollection.value,
      })
    }

    return summary
  }

  function logLibraryScanDiagnostics(result: UnknownRecord, options: UnknownRecord = {}, roundTripMs = 0) {
    const diagnostics = (result?.diagnostics ?? null) as StartupDiagnostics | null

    if (!diagnostics) {
      return
    }

    const totalMs = normalizeScanCount(diagnostics.totalMs)
    const normalizedRoundTripMs = normalizeScanCount(roundTripMs)

    if (Math.max(totalMs, normalizedRoundTripMs) < SCAN_DIAGNOSTICS_LOG_THRESHOLD_MS) {
      return
    }

    const stageTimings = ([
      ['discover', normalizeScanCount(diagnostics.discoverMs)],
      ['filter', normalizeScanCount(diagnostics.filterMs)],
      ['prepare', normalizeScanCount(diagnostics.prepareMs)],
      ['persist', normalizeScanCount(diagnostics.persistMs)],
      ['playbackSync', normalizeScanCount(diagnostics.playbackSyncMs)],
      ['copy', normalizeScanCount(diagnostics.copyMs)],
      ['metadata', normalizeScanCount(diagnostics.metadataMs)],
    ] as Array<[string, number]>).sort((left, right) => right[1] - left[1])

    void logDiagnosticsInfoAny('[OFPlayer library scan]', 'storage', 'library_scan_import', {
      mode: resolveScanMode(options),
      jobId: result?.job?.id ?? '',
      jobMode: result?.job?.mode ?? '',
      jobStatus: result?.job?.status ?? '',
      jobStage: result?.job?.currentStage ?? '',
      invalidatedTrackCount: Array.isArray(result?.invalidatedTrackIds)
        ? result.invalidatedTrackIds.length
        : 0,
      roundTripMs: normalizedRoundTripMs,
      invokeOverheadMs: Math.max(0, normalizedRoundTripMs - totalMs),
      dominantStage: stageTimings[0]?.[0] ?? 'unknown',
      dominantStageMs: stageTimings[0]?.[1] ?? 0,
      scanDirectoryCount: preferencesStore.scanDirectories.value.length,
      ...diagnostics,
    })
  }

  function startNavigationWatcher({ immediate = false } = {}) {
    if (stopNavigationWatcher) {
      return stopNavigationWatcher
    }

    stopNavigationWatcher = watch(
      () => navigationRefreshKey.value,
      () => {
        void refreshNavigationSummary()
      },
      { immediate },
    )

    return stopNavigationWatcher
  }

  async function hydrateBootstrapState() {
    const bootstrapStartedAt = nowMs()
    let nextBootstrapState = null

    if (typeof dataService?.bootstrap?.loadAppState === 'function') {
      const bootstrapLoadStartedAt = nowMs()
      const bootstrapLoadResourceStart = captureRendererResourceSample()
      try {
        nextBootstrapState = await dataService.bootstrap.loadAppState({
          historyLimit: BOOTSTRAP_HISTORY_LIMIT,
        })
      } catch (error) {
        void logDiagnosticsWarnAny('[OFPlayer bootstrap]', 'startup', 'bootstrap_fallback', {
          error,
        })
      } finally {
        startupDiagnostics.bootstrapLoadMs = Math.round(nowMs() - bootstrapLoadStartedAt)
        recordStartupRendererStep(
          'bootstrapLoad',
          bootstrapLoadStartedAt,
          bootstrapLoadResourceStart,
        )
      }

      logStartupPhase(
        'bootstrap_state_loaded',
        {
          totalMs: Math.round(nowMs() - bootstrapStartedAt),
          bootstrapLoadMs: startupDiagnostics.bootstrapLoadMs ?? 0,
          bootstrapRoundTripMs: nextBootstrapState?.diagnostics?.roundTripMs ?? 0,
          bootstrapBackendMs: nextBootstrapState?.diagnostics?.totalMs ?? 0,
          bootstrapInvokeOverheadMs: nextBootstrapState?.diagnostics?.invokeOverheadMs ?? 0,
          bootstrapCatalogMs: nextBootstrapState?.diagnostics?.catalogMs ?? 0,
          bootstrapCatalogCacheHit: nextBootstrapState?.diagnostics?.catalogCacheHit === true,
          bootstrapCatalogTracksIncluded:
            nextBootstrapState?.diagnostics?.catalogTracksIncluded === true,
          bootstrapCatalogTrackCount: nextBootstrapState?.diagnostics?.catalogTrackCount ?? 0,
          bootstrapTrackCacheEntries: nextBootstrapState?.diagnostics?.trackCacheEntries ?? 0,
          bootstrapCatalogRevision: nextBootstrapState?.manifest?.revisions?.catalog ?? 0,
        },
        40,
      )
    }

    const storeHydrateStartedAt = nowMs()
    const storeHydrateResourceStart = captureRendererResourceSample()
    const backendRevisions = resolveBackendRevisions(nextBootstrapState?.manifest)
    const bootstrapCatalogTrackListComplete =
      !nextBootstrapState || nextBootstrapState?.diagnostics?.catalogTracksIncluded === true
    await Promise.all([
      libraryStore.hydrate(nextBootstrapState?.catalog ?? null, {
        revision: backendRevisions.catalog,
        trackListComplete: bootstrapCatalogTrackListComplete,
      }),
      sessionStore.hydrate(nextBootstrapState?.session ?? null, {
        revision: backendRevisions.session,
      }),
      preferencesStore.hydrate(nextBootstrapState?.preferences ?? null),
    ])
    startupDiagnostics.storeHydrateMs = Math.round(nowMs() - storeHydrateStartedAt)
    recordStartupRendererStep('storeHydrate', storeHydrateStartedAt, storeHydrateResourceStart)
    playerStore.setVolume(preferencesStore.volume.value)
    const appliedOutputDeviceId = await playerStore.setOutputDevicePreference(
      preferencesStore.playbackOutputDeviceId.value,
    )
    if (appliedOutputDeviceId !== preferencesStore.playbackOutputDeviceId.value) {
      preferencesStore.setPlaybackOutputDeviceId(appliedOutputDeviceId)
    }
    logStartupPhase(
      'bootstrap_stores_hydrated',
      {
        totalMs: Math.round(nowMs() - bootstrapStartedAt),
        storeHydrateMs: startupDiagnostics.storeHydrateMs,
        libraryCount: libraryStore.libraries.value.length,
        playlistCount: libraryStore.playlists.value.length,
        trackCount: libraryStore.tracks.value.length,
      },
      12,
    )

    const historyHydrateStartedAt = nowMs()
    const historyHydrateResourceStart = captureRendererResourceSample()
    await playerStore.hydrate(nextBootstrapState?.history ?? null, {
      revision: backendRevisions.history,
    })
    startupDiagnostics.historyHydrateMs = Math.round(nowMs() - historyHydrateStartedAt)
    recordStartupRendererStep(
      'historyHydrate',
      historyHydrateStartedAt,
      historyHydrateResourceStart,
    )
    logStartupPhase(
      'bootstrap_history_hydrated',
      {
        totalMs: Math.round(nowMs() - bootstrapStartedAt),
        historyHydrateMs: startupDiagnostics.historyHydrateMs,
        historyCount: playerStore.recentHistory.value.length,
      },
      12,
    )

    if (sessionStore.currentTrackId.value) {
      const activeTrackHydrateStartedAt = nowMs()
      const activeTrackHydrateResourceStart = captureRendererResourceSample()
      const activeTrack = await libraryStore.getOrLoadTrack(sessionStore.currentTrackId.value)
      playerStore.setActiveTrack(activeTrack)
      playerStore.applyPlaybackSnapshot({
        status: activeTrack ? 'paused' : 'idle',
        activeTrackId: activeTrack ? sessionStore.currentTrackId.value : null,
        currentTime: activeTrack ? sessionStore.currentTime.value : 0,
        duration: activeTrack
          ? Math.max(sessionStore.duration.value, Number.isFinite(activeTrack.duration) ? activeTrack.duration : 0)
          : 0,
        volume: preferencesStore.volume.value,
      })
      startupDiagnostics.activeTrackHydrateMs = Math.round(nowMs() - activeTrackHydrateStartedAt)
      recordStartupRendererStep(
        'activeTrackHydrate',
        activeTrackHydrateStartedAt,
        activeTrackHydrateResourceStart,
      )
      logStartupPhase(
        'bootstrap_active_track_hydrated',
        {
          totalMs: Math.round(nowMs() - bootstrapStartedAt),
          activeTrackHydrateMs: startupDiagnostics.activeTrackHydrateMs,
          hasActiveTrack: Boolean(activeTrack),
        },
        12,
      )
    } else {
      playerStore.setActiveTrack(null)
    }

    bootstrapState = nextBootstrapState
    hasBootstrapNavigationSummary = Boolean(nextBootstrapState?.navigationSummary)

    if (nextBootstrapState?.diagnostics) {
      void logDiagnosticsInfoAny('[OFPlayer bootstrap]', 'startup', 'bootstrap_snapshot', {
        diagnosticsVersion: startupDiagnosticsVersion,
        ...nextBootstrapState.diagnostics,
      })
    }

    ensureActiveLibrarySelection()
    navigationSummary.value = {
      ...createEmptyNavigationSummary(),
      ...(nextBootstrapState?.navigationSummary ?? {}),
    }
    lastNavigationSummaryRequestKey = hasBootstrapNavigationSummary ? buildNavigationSummaryRequestKey() : ''

    await prepareBrowserCatalogForSelection({
      libraryId: preferencesStore.activeLibrary.value,
      collectionRef: preferencesStore.activeCollection.value,
      reason: 'startup-active-browser',
    })

    startNavigationWatcher({ immediate: !hasBootstrapNavigationSummary })
    startBrowserCatalogHydrationWatcher({ immediate: true })
    logStartupPhase(
      'bootstrap_state_ready',
      {
        totalMs: Math.round(nowMs() - bootstrapStartedAt),
        hasBootstrapNavigationSummary,
        activeLibrary: preferencesStore.activeLibrary.value,
        activeCollection: preferencesStore.activeCollection.value,
      },
      20,
    )
    isBootstrapReady.value = true
    resolveVisualReady()
    return bootstrapState
  }

  bootstrapPromise = hydrateBootstrapState().catch((error) => {
    void logDiagnosticsErrorAny('[OFPlayer bootstrap]', 'startup', 'bootstrap_hydrate_failed', {
      error,
    })
    startNavigationWatcher({ immediate: true })
    startBrowserCatalogHydrationWatcher({ immediate: true })
    isBootstrapReady.value = true
    resolveVisualReady()
    return null
  })

  async function selectStorageRoot() {
    const selectedDirectory = await desktopStorageService.pickStorageDirectory()

    if (!selectedDirectory) {
      return preferencesStore.storageRoot.value
    }

    return applyStorageRoot(selectedDirectory)
  }

  async function addScanDirectory() {
    const selectedDirectory = await desktopStorageService.pickScanDirectory()

    if (!selectedDirectory) {
      return preferencesStore.scanDirectories.value
    }

    return applyScanDirectories([
      ...preferencesStore.scanDirectories.value,
      selectedDirectory,
    ])
  }

  function removeScanDirectory(directory: string) {
    return applyScanDirectories(
      preferencesStore.scanDirectories.value.filter((item: unknown) => item !== directory),
    )
  }

  async function addLyricsScanDirectory() {
    const selectedDirectory =
      typeof desktopStorageService.pickLyricsDirectory === 'function'
        ? await desktopStorageService.pickLyricsDirectory()
        : await desktopStorageService.pickScanDirectory()

    if (!selectedDirectory) {
      return preferencesStore.lyricsScanDirectories.value
    }

    return applyLyricsScanDirectories([
      ...preferencesStore.lyricsScanDirectories.value,
      selectedDirectory,
    ])
  }

  function removeLyricsScanDirectory(directory: string) {
    return applyLyricsScanDirectories(
      preferencesStore.lyricsScanDirectories.value.filter((item: unknown) => item !== directory),
    )
  }

  function setAutoScanOnLaunch(nextValue: boolean) {
    return applyAutoScanPreference(nextValue)
  }

  function markLibraryScanComplete() {
    return preferencesStore.setLastScanAt(new Date().toISOString())
  }

  const clearRemoteMetadataHydrationQueue = remoteMetadataHydrator.clearQueue
  const enqueueActiveRemoteLibraryMetadataHydration = remoteMetadataHydrator.enqueueActiveLibrary
  const enqueueRemoteLibraryMetadataHydration = remoteMetadataHydrator.enqueueLibrary

  async function prepareTrackForPlayback(track: TrackLike) {
    if (disposed || !track?.source?.connectionId) {
      return track
    }

    const prepareStartedAt = nowMs()
    const prepareResourceStart = captureRendererResourceSample()
    const statusTrackId = track.id
    const prepareStepProfiles: RendererStepProfile[] = []
    const recordPrepareStep = (key: string, startedAt: number, resourceStart: RendererResourceSample | null = null) => {
      const resourceEnd = captureRendererResourceSample()
      prepareStepProfiles.push(
        buildRendererStepProfile(key, nowMs() - startedAt, resourceStart, resourceEnd),
      )
      return resourceEnd
    }
    let sourceChanged = false
    let metadataPatchKeys: string[] = []

    remotePlaybackStatus.value = {
      active: true,
      trackId: statusTrackId,
      provider: track.source?.provider ?? '',
      phase: 'preparing',
      error: '',
    }

    try {
      const resolveStartedAt = nowMs()
      const resolveResourceStart = captureRendererResourceSample()
      const shouldResolvePlaybackMetadata = shouldResolvePlaybackMetadataDuringPrepare(track)
      const playableTrack = await externalLibraryService.resolvePlayableTrack(track, {
        includeMetadata: shouldResolvePlaybackMetadata,
        allowEmbeddedArtwork: shouldResolvePlaybackMetadata,
      })
      recordPrepareStep('resolvePlayableTrack', resolveStartedAt, resolveResourceStart)

      if (disposed || !playableTrack) {
        return null
      }

      let nextTrack = playableTrack

      if (
        playableTrack?.source?.path &&
        playableTrack.source.path !== track.source.path &&
        !isTransientPlaybackSource(playableTrack.source)
      ) {
        sourceChanged = true
        const updateSourceStartedAt = nowMs()
        const updateSourceResourceStart = captureRendererResourceSample()
        nextTrack = await libraryStore.updateTrackSource(track.id, playableTrack.source)
        recordPrepareStep('updateTrackSource', updateSourceStartedAt, updateSourceResourceStart)

        if (disposed || !nextTrack) {
          return null
        }
      } else if (playableTrack?.source?.path && playableTrack.source.path !== track.source.path) {
        sourceChanged = true
      }

      const metadataPatch = createRemotePlaybackMetadataPatch(track, playableTrack)
      metadataPatchKeys = Object.keys(metadataPatch)

      if (metadataPatchKeys.length > 0) {
        const updateMetadataStartedAt = nowMs()
        const updateMetadataResourceStart = captureRendererResourceSample()
        nextTrack = await libraryStore.updateTrackMetadata(track.id, metadataPatch)
        recordPrepareStep('updateTrackMetadata', updateMetadataStartedAt, updateMetadataResourceStart)

        if (disposed || !nextTrack) {
          return null
        }

        if (isTransientPlaybackSource(playableTrack.source)) {
          nextTrack = {
            ...nextTrack,
            source: {
              ...(nextTrack.source ?? {}),
              ...playableTrack.source,
            },
          }
        }
      }

      const prepareResourceEnd = captureRendererResourceSample()
      void logDiagnosticsInfoAny('[OFPlayer playback prepare track]', 'playback', 'prepare_track_profile', {
        trackId: track.id,
        sourceKind: nextTrack?.source?.kind ?? '',
        sourceProvider: nextTrack?.source?.provider ?? '',
        sourceChanged,
        metadataResolved: shouldResolvePlaybackMetadata,
        metadataPatchKeys,
        inputArtworkLength: typeof track?.artwork === 'string' ? track.artwork.length : 0,
        playableArtworkLength: typeof playableTrack?.artwork === 'string' ? playableTrack.artwork.length : 0,
        outputArtworkLength: typeof nextTrack?.artwork === 'string' ? nextTrack.artwork.length : 0,
        totalMs: Math.round(nowMs() - prepareStartedAt),
        frontendResources: buildRendererResourceProfile(prepareResourceStart, prepareResourceEnd),
        frontendStepProfiles: prepareStepProfiles,
      })

      return nextTrack
    } catch (error) {
      if (remotePlaybackStatus.value.trackId === statusTrackId) {
        remotePlaybackStatus.value = {
          ...remotePlaybackStatus.value,
          active: false,
          phase: 'error',
          error: formatCommandError(error, ''),
        }
      }

      throw error
    } finally {
      if (remotePlaybackStatus.value.trackId === statusTrackId && remotePlaybackStatus.value.active) {
        remotePlaybackStatus.value = {
          ...remotePlaybackStatus.value,
          active: false,
          phase: 'ready',
        }
      }
    }
  }

  async function selectTrack(trackIdOrOptions: string | UnknownRecord, options: UnknownRecord = {}) {
    const selectStartedAt = nowMs()
    const selectResourceStart = captureRendererResourceSample()
    const selectStepProfiles: RendererStepProfile[] = []
    const recordSelectStep = (key: string, startedAt: number, resourceStart: RendererResourceSample | null = null) => {
      const resourceEnd = captureRendererResourceSample()
      selectStepProfiles.push(
        buildRendererStepProfile(key, nowMs() - startedAt, resourceStart, resourceEnd),
      )
      return resourceEnd
    }
    const selectionToken = currentLifecycleToken()
    // CN: 支持新格式：{ trackId, queueTrackIds } 或旧格式：trackId
    // EN: Supports new format: { trackId, queueTrackIds } or legacy format: trackId
    let trackId: string | null = null
    let queueTrackIds: unknown[] | null = null

    if (typeof trackIdOrOptions === 'object' && trackIdOrOptions !== null) {
      trackId = typeof trackIdOrOptions.trackId === 'string' ? trackIdOrOptions.trackId : null
      queueTrackIds = Array.isArray(trackIdOrOptions.queueTrackIds) ? trackIdOrOptions.queueTrackIds : null
    } else {
      trackId = trackIdOrOptions
    }

    const autoplay = options.autoplay !== false
    const activeSelectionRequestId = ++selectTrackRequestId
    playbackCommandRevision += 1
    const getTrackStartedAt = nowMs()
    const getTrackResourceStart = captureRendererResourceSample()
    const track = await libraryStore.getOrLoadTrack(trackId, {
      includeArtwork: false,
      cache: false,
    })
    recordSelectStep('getOrLoadTrackWithoutArtwork', getTrackStartedAt, getTrackResourceStart)

    if (!isSelectionCurrent(selectionToken, activeSelectionRequestId) || !track) {
      return false
    }

    const prepareStartedAt = nowMs()
    const prepareResourceStart = captureRendererResourceSample()
    const playableTrack = await prepareTrackForPlayback(track)
    recordSelectStep('prepareTrackForPlayback', prepareStartedAt, prepareResourceStart)

    if (!isSelectionCurrent(selectionToken, activeSelectionRequestId) || !playableTrack) {
      return false
    }

    const playbackSourceOverride = createPlaybackSourceOverride(track, playableTrack)
    const backendSelectStartedAt = nowMs()
    const backendSelectResourceStart = captureRendererResourceSample()
    const result = await dataService.playbackSession.selectTrack({
      trackId,
      queueTrackIds: Array.isArray(queueTrackIds) && queueTrackIds.length > 0 ? queueTrackIds : null,
      autoplay,
      playbackSource: playbackSourceOverride,
    })
    recordSelectStep('playbackSessionSelectTrack', backendSelectStartedAt, backendSelectResourceStart)

    if (!isSelectionCurrent(selectionToken, activeSelectionRequestId)) {
      return false
    }

    const applyStartedAt = nowMs()
    const applyResourceStart = captureRendererResourceSample()
    playerStore.setActiveTrack(playableTrack)
    const applied = applyPlaybackCommandResult(result)
    recordSelectStep('applyPlaybackCommandResult', applyStartedAt, applyResourceStart)
    const selectResourceEnd = captureRendererResourceSample()

    void logDiagnosticsInfoAny('[OFPlayer playback select track]', 'playback', 'select_track_profile', {
      trackId,
      autoplay,
      queueTrackCount: Array.isArray(queueTrackIds) ? queueTrackIds.length : 0,
      sourceKind: playableTrack?.source?.kind ?? track?.source?.kind ?? '',
      sourceProvider: playableTrack?.source?.provider ?? track?.source?.provider ?? '',
      hasConnectionId: Boolean(playableTrack?.source?.connectionId ?? track?.source?.connectionId),
      playbackSourceOverridden: Boolean(playbackSourceOverride),
      trackArtworkLength: typeof track?.artwork === 'string' ? track.artwork.length : 0,
      playableArtworkLength: typeof playableTrack?.artwork === 'string' ? playableTrack.artwork.length : 0,
      backendHistoryEntryCount: Array.isArray(result?.historyEntries) ? result.historyEntries.length : 0,
      backendPlaybackStatus: result?.playback?.status ?? '',
      totalMs: Math.round(nowMs() - selectStartedAt),
      frontendResources: buildRendererResourceProfile(selectResourceStart, selectResourceEnd),
      frontendStepProfiles: selectStepProfiles,
    })

    return applied
  }

  async function playCurrentSelection() {
    const fallbackTrackId = currentTrackId.value ?? libraryStore.trackIds.value[0] ?? null

    if (!fallbackTrackId) {
      return false
    }

    if (!currentTrackId.value) {
      return selectTrack(
        {
          trackId: fallbackTrackId,
          queueTrackIds: libraryStore.trackIds.value,
        },
        { autoplay: true },
      )
    }

    const playToken = currentLifecycleToken()
    playbackCommandRevision += 1
    const track = await libraryStore.getOrLoadTrack(currentTrackId.value)

    if (!isLifecycleCurrent(playToken)) {
      return false
    }

    if (isExternalTrack(track)) {
      return selectTrack(currentTrackId.value, { autoplay: true })
    }

    const playableTrack = await prepareTrackForPlayback(track)

    if (!isLifecycleCurrent(playToken) || !playableTrack) {
      return false
    }

    playerStore.setActiveTrack(playableTrack)
    const result = await dataService.playbackSession.playCurrent()

    if (!isLifecycleCurrent(playToken)) {
      return false
    }

    return applyPlaybackCommandResult(result)
  }

  async function playNext(options: PlayNextOptions = {}) {
    const reason = typeof options === 'object' && options ? options.reason ?? 'user' : 'user'
    const nextTrackId = resolvePlaybackOrderTrackId(1, { reason })

    if (!nextTrackId) {
      if (reason === 'ended') {
        playbackCommandRevision += 1
        applyPlaybackCommandResult(await dataService.playbackSession.pause())
      }

      return false
    }

    const applied = await selectTrack(nextTrackId, { autoplay: true })

    if (applied && reason !== 'ended') {
      trackSkipNext()
    }

    return applied
  }

  async function playPrevious() {
    if (playerStore.currentTime.value > 3 && currentTrackId.value) {
      seek(0)
      return true
    }

    const previousTrackId = resolvePlaybackOrderTrackId(-1)

    if (!previousTrackId) {
      return false
    }

    const applied = await selectTrack(previousTrackId, { autoplay: true })

    if (applied) {
      trackSkipPrev()
    }

    return applied
  }

  async function handlePlaybackEnded() {
    return playNext({ reason: 'ended' })
  }

  async function importFiles(files: unknown[], options: UnknownRecord = {}) {
    const importToken = currentLifecycleToken()

    if (!isLifecycleCurrent(importToken)) {
      return []
    }

    const activeLibraryId = ensureActiveLibrarySelection()

    if (!isLifecycleCurrent(importToken) || !activeLibraryId) {
      return []
    }

    const importItems = desktopStorageService.createImportItems(files)

    if (!isLifecycleCurrent(importToken) || importItems.length === 0) {
      return []
    }

    const shouldShowImportProgress = options.interactive === true || importItems.length > 1
    const importStartedAt = nowMs()

    if (shouldShowImportProgress) {
      startScanProgress({
        interactive: options.interactive === true,
      })
      updateScanProgress({
        phase: 'preparing',
        percent: 8,
        processed: 0,
        total: importItems.length,
        imported: 0,
        discoveredTotal: importItems.length,
        candidateTotal: 0,
        directoriesScanned: 0,
        entriesScanned: 0,
        elapsedMs: 0,
        currentFile: '',
        error: '',
      })
    }

    let result

    try {
      result = await libraryStore.importSourceFiles({
        libraryId: activeLibraryId,
        files: importItems,
      })
    } catch (error) {
      if (shouldShowImportProgress && isLifecycleCurrent(importToken)) {
        finishScanProgress({
          phase: 'error',
          percent: 100,
          processed: 0,
          total: importItems.length,
          imported: 0,
          discoveredTotal: importItems.length,
          candidateTotal: 0,
          directoriesScanned: 0,
          entriesScanned: 0,
          elapsedMs: Math.round(nowMs() - importStartedAt),
          currentFile: '',
          error: formatCommandError(error, 'Scan failed.'),
        })
      }

      throw error
    }

    if (!isLifecycleCurrent(importToken)) {
      return []
    }

    applyPlaybackCommandResult(result)
    const importedTracks = result?.importedTracks ?? []

    if (shouldShowImportProgress) {
      const discoveredTotal = normalizeScanCount(result?.discoveredTotal) || importItems.length
      const candidateTotal = normalizeScanCount(result?.candidateTotal)

      finishScanProgress({
        phase: candidateTotal === 0 ? 'empty' : 'complete',
        percent: 100,
        processed: importedTracks.length,
        total: Math.max(discoveredTotal, candidateTotal, 1),
        imported: importedTracks.length,
        discoveredTotal,
        candidateTotal,
        directoriesScanned: 0,
        entriesScanned: importItems.length,
        elapsedMs: normalizeScanCount(result?.diagnostics?.totalMs) || Math.round(nowMs() - importStartedAt),
        currentFile: '',
        error: '',
      })
    }

    if (importedTracks.length === 0) {
      return []
    }

    return importedTracks
  }

  async function requestImportFiles() {
    if (fileImportService.importMode !== 'native-dialog') {
      return []
    }

    const files = await fileImportService.pickAudioFiles()

    if (files.length === 0) {
      return []
    }

    return importFiles(files, { interactive: true })
  }

  async function requestImportFolder() {
    if (!desktopStorageService.available || typeof desktopStorageService.pickScanDirectory !== 'function') {
      return []
    }

    const selectedDirectory = await desktopStorageService.pickScanDirectory()

    if (!selectedDirectory) {
      return []
    }

    if (!preferencesStore.scanDirectories.value.includes(selectedDirectory)) {
      applyScanDirectories([
        ...preferencesStore.scanDirectories.value,
        selectedDirectory,
      ])
    }

    return runLibraryScanImport({
      interactive: true,
      directories: [selectedDirectory],
    })
  }

  async function runLibraryScanImport(options: LibraryScanImportOptions = {}) {
    if (disposed || isResettingData.value) {
      return []
    }

    if (activeScanPromise) {
      return activeScanPromise
    }

    const scanToken = currentLifecycleToken()

    activeScanPromise = (async () => {
      let scanStartedAt = 0

      try {
        if (!isLifecycleCurrent(scanToken) || !desktopStorageService.available) {
          return []
        }

        if (!isLifecycleCurrent(scanToken)) {
          return []
        }

        const optionDirectories = Array.isArray(options.directories)
          ? options.directories.filter((item: unknown) => typeof item === 'string' && item.trim())
          : []
        const scanDirectories = optionDirectories.length > 0
          ? optionDirectories
          : preferencesStore.scanDirectories.value

        if (scanDirectories.length === 0) {
          if (options.interactive === true) {
            preferencesStore.openSettings()
          }

          return []
        }

        const activeLibraryId = ensureActiveLibrarySelection()

        if (!isLifecycleCurrent(scanToken) || !activeLibraryId) {
          return []
        }

        startScanProgress(options)
        updateScanProgress({
          phase: 'discovering',
          percent: 4,
          processed: 0,
          total: scanDirectories.length,
          imported: 0,
          discoveredTotal: 0,
          candidateTotal: 0,
          directoriesScanned: 0,
          entriesScanned: 0,
          elapsedMs: 0,
          currentFile: '',
          error: '',
        })

        scanStartedAt = nowMs()
        const result = await libraryStore.scanAndImportTracks({
          libraryId: activeLibraryId,
          directories: scanDirectories,
          respectDeletedImportPaths: typeof options.respectDeletedImportPaths === 'boolean'
            ? options.respectDeletedImportPaths
            : options.interactive !== true,
        })

        if (!isLifecycleCurrent(scanToken)) {
          return []
        }

        const roundTripMs = Math.round(nowMs() - scanStartedAt)
        logLibraryScanDiagnostics(result, options, roundTripMs)
        applyPlaybackCommandResult(result)
        const importedTracks = result?.importedTracks ?? []
        const discoveredTotal = normalizeScanCount(result?.discoveredTotal)
        const candidateTotal = normalizeScanCount(result?.candidateTotal)
        const diagnostics = result?.diagnostics ?? null

        markLibraryScanComplete()

        if (candidateTotal === 0) {
          finishScanProgress({
            phase: 'empty',
            percent: 100,
            processed: 0,
            total: Math.max(discoveredTotal, 1),
            imported: 0,
            discoveredTotal,
            candidateTotal,
            directoriesScanned: normalizeScanCount(diagnostics?.directoriesScanned),
            entriesScanned: normalizeScanCount(diagnostics?.entriesScanned),
            elapsedMs: normalizeScanCount(diagnostics?.totalMs) || roundTripMs,
            currentFile: '',
            error: '',
          })
          return []
        }

        finishScanProgress({
          phase: 'complete',
          percent: 100,
          processed: importedTracks.length,
          total: candidateTotal,
          imported: importedTracks.length,
          discoveredTotal,
          candidateTotal,
          directoriesScanned: normalizeScanCount(diagnostics?.directoriesScanned),
          entriesScanned: normalizeScanCount(diagnostics?.entriesScanned),
          elapsedMs: normalizeScanCount(diagnostics?.totalMs) || roundTripMs,
          currentFile: '',
          error: '',
        })

        return importedTracks
      } catch (error) {
        if (!isLifecycleCurrent(scanToken)) {
          return []
        }

        finishScanProgress({
          phase: 'error',
          percent: scanProgress.value.percent,
          elapsedMs: scanStartedAt > 0 ? Math.round(nowMs() - scanStartedAt) : scanProgress.value.elapsedMs,
          currentFile: '',
          error: formatCommandError(error, ''),
        })
        throw error
      } finally {
        activeScanPromise = null
      }
    })()

    return activeScanPromise
  }

  async function togglePlayback() {
    if (playerStore.isPlaying.value) {
      playbackCommandRevision += 1
      const applied = applyPlaybackCommandResult(await dataService.playbackSession.pause())

      if (applied) {
        trackPause()
      }

      return applied
    }

    const applied = await playCurrentSelection()

    if (applied) {
      trackPlay()
    }

    return applied
  }

  function seek(nextTime: number) {
    const result = playerStore.seek(nextTime)
    trackSeek()
    return result
  }

  function setTelemetryConsent(value: boolean) {
    const normalized = preferencesStore.setTelemetryConsent(value)
    trackTelemetryConsent(normalized)
    return normalized
  }

  async function uploadDiagnosticsReportNow() {
    if (isUploadingDiagnosticsReport.value) {
      return diagnosticsReportStatus.value
    }

    if (preferencesStore.telemetryEnabled.value !== true) {
      diagnosticsReportStatus.value = {
        state: 'blocked',
        message: 'consent_required',
        uploadedAt: '',
        eventCount: 0,
      }
      return diagnosticsReportStatus.value
    }

    if (!hasDiagnosticsReportEndpoint()) {
      diagnosticsReportStatus.value = {
        state: 'blocked',
        message: 'endpoint_missing',
        uploadedAt: '',
        eventCount: 0,
      }
      return diagnosticsReportStatus.value
    }

    isUploadingDiagnosticsReport.value = true
    diagnosticsReportStatus.value = {
      state: 'uploading',
      message: '',
      uploadedAt: '',
      eventCount: 0,
    }

    try {
      const result = await uploadDiagnosticsReport({
        consent: preferencesStore.telemetryEnabled.value === true,
        reason: 'settings-manual',
      })
      diagnosticsReportStatus.value = {
        state: result?.uploaded ? 'uploaded' : 'skipped',
        message: '',
        uploadedAt: new Date().toISOString(),
        eventCount: result?.eventCount ?? 0,
      }
      return diagnosticsReportStatus.value
    } catch (error) {
      diagnosticsReportStatus.value = {
        state: 'error',
        message: formatCommandError(error, 'diagnostics_upload_failed'),
        uploadedAt: '',
        eventCount: 0,
      }
      return diagnosticsReportStatus.value
    } finally {
      isUploadingDiagnosticsReport.value = false
    }
  }

  function setVolume(nextVolume: number) {
    const safeVolume = preferencesStore.setVolume(nextVolume)
    playerStore.setVolume(safeVolume)
    return safeVolume
  }

  function refreshPlaybackOutputDevices() {
    return playerStore.refreshOutputDevices()
  }

  async function setPlaybackOutputDevice(nextDeviceId: string) {
    if (playerStore.isPlaying.value) {
      void logDiagnosticsWarn('[OFPlayer playback devices]', 'playback', 'output_device_switch_blocked_while_playing', {
        requestedDeviceId: typeof nextDeviceId === 'string' ? nextDeviceId : null,
        currentPreferredDeviceId: preferencesStore.playbackOutputDeviceId.value || null,
        currentActiveDeviceId: playerStore.activeOutputDeviceId.value || null,
        activeTrackId: currentTrackId.value || null,
      })
      return preferencesStore.playbackOutputDeviceId.value
    }

    const safeDeviceId = preferencesStore.setPlaybackOutputDeviceId(nextDeviceId)
    const appliedDeviceId = await playerStore.setOutputDevicePreference(safeDeviceId)
    if (appliedDeviceId !== safeDeviceId) {
      preferencesStore.setPlaybackOutputDeviceId(appliedDeviceId)
    }
    return appliedDeviceId
  }

  function setSearchQuery(query: string) {
    return preferencesStore.setSearchQuery(query)
  }

  function setSortOption(optionOrParams: string | UnknownRecord) {
    // CN: 支持新格式：{ sortOption, collectionRef } 或旧格式：sortOption
    // EN: Supports new format: { sortOption, collectionRef } or legacy format: sortOption
    if (typeof optionOrParams === 'object' && optionOrParams !== null) {
      const { sortOption, collectionRef } = optionOrParams
      // CN: 如果有 collectionRef，保存到集合排序偏好
      // EN: If collectionRef exists, save to collection sort preference
      if (collectionRef) {
        preferencesStore.setCollectionSortOption(collectionRef, sortOption)
      }
      // CN: 同时更新全局排序（作为默认值）
      // EN: Also update global sort (as default value)
      return preferencesStore.setSortOption(sortOption)
    }
    return preferencesStore.setSortOption(optionOrParams)
  }

  function setTypeFilter(option: string) {
    return preferencesStore.setTypeFilter(option)
  }

  async function setActiveLibrary(libraryId: string) {
    const library = libraryStore.getLibraryById(libraryId)

    if (!library) {
      return preferencesStore.activeLibrary.value
    }

    const requestId = ++navigationSelectionRequestId
    const nextLibraryId = library.id
    const nextCollectionRef = libraryStore.isCollectionAvailableForLibrary(
      nextLibraryId,
      preferencesStore.activeCollection.value,
    )
      ? preferencesStore.activeCollection.value
      : libraryStore.getDefaultCollectionRef(nextLibraryId)

    if (isBrowserCollectionRef(nextCollectionRef)) {
      const ready = await prepareBrowserCatalogForSelection({
        libraryId: nextLibraryId,
        collectionRef: nextCollectionRef,
        reason: 'library-selected-preflight',
      })

      if (!ready || disposed || requestId !== navigationSelectionRequestId) {
        return preferencesStore.activeLibrary.value
      }
    }

    preferencesStore.setActiveLibrary(nextLibraryId)

    if (nextCollectionRef !== preferencesStore.activeCollection.value) {
      preferencesStore.setActiveCollection(nextCollectionRef)
    }

    applyNavigationSelection({
      activeLibrary: nextLibraryId,
      activeCollectionKey: preferencesStore.activeCollection.value,
    })
    void hydrateFullCatalogForBrowserView({ reason: 'library-selected' })
    enqueueRemoteLibraryMetadataHydration(nextLibraryId)
    return nextLibraryId
  }

  async function setActiveCollection(collectionKey: string) {
    if (!libraryStore.isCollectionAvailableForLibrary(preferencesStore.activeLibrary.value, collectionKey)) {
      return preferencesStore.activeCollection.value
    }

    const requestId = ++navigationSelectionRequestId

    if (isBrowserCollectionRef(collectionKey)) {
      const ready = await prepareBrowserCatalogForSelection({
        libraryId: preferencesStore.activeLibrary.value,
        collectionRef: collectionKey,
        reason: 'collection-selected-preflight',
      })

      if (!ready || disposed || requestId !== navigationSelectionRequestId) {
        return preferencesStore.activeCollection.value
      }
    }

    const nextCollectionKey = preferencesStore.setActiveCollection(collectionKey)
    applyNavigationSelection({
      activeLibrary: preferencesStore.activeLibrary.value,
      activeCollectionKey: nextCollectionKey,
    })
    void hydrateFullCatalogForBrowserView({ reason: 'collection-selected' })
    return nextCollectionKey
  }

  function assertFeatureCanCreate(feature: string) {
    const limits = licenseFeatureLimits.value

    if (feature === LICENSE_FEATURES.PLAYLIST && !limits.canCreatePlaylist) {
      throw createFeatureLimitError(feature, limits)
    }

    if (feature === LICENSE_FEATURES.LIBRARY && !limits.canCreateLibrary) {
      throw createFeatureLimitError(feature, limits)
    }

    return true
  }

  async function createLibrary(name: string) {
    assertFeatureCanCreate(LICENSE_FEATURES.LIBRARY)
    const result = await libraryStore.createLibrary(name)
    await setActiveLibrary(result.library.id)
    await setActiveCollection(libraryStore.getDefaultCollectionRef(result.library.id))
    return result
  }

  async function connectExternalLibrary(connection: UnknownRecord) {
    assertFeatureCanCreate(LICENSE_FEATURES.LIBRARY)
    const connectionToken = currentLifecycleToken()
    const result = await externalLibraryService.connectLibrary({ connection })

    if (!isLifecycleCurrent(connectionToken)) {
      return result
    }

    await libraryStore.hydrate()
    if (!isLifecycleCurrent(connectionToken)) {
      return result
    }
    await setActiveLibrary(result.library.id)
    await setActiveCollection(libraryStore.getDefaultCollectionRef(result.library.id))
    await syncQueueWithCatalog()
    enqueueRemoteLibraryMetadataHydration(result.library.id, { retryFailed: true })
    return result
  }

  async function syncExternalLibrary(libraryId = preferencesStore.activeLibrary.value) {
    const syncToken = currentLifecycleToken()
    const result = await externalLibraryService.syncLibrary({ libraryId })

    if (!isLifecycleCurrent(syncToken)) {
      return result
    }

    await libraryStore.hydrate()
    if (!isLifecycleCurrent(syncToken)) {
      return result
    }
    await syncQueueWithCatalog()
    enqueueRemoteLibraryMetadataHydration(libraryId, { retryFailed: true })
    return result
  }

  async function probeExternalLibrary(libraryId = preferencesStore.activeLibrary.value) {
    const probeToken = currentLifecycleToken()
    const result = await externalLibraryService.testLibrary({ libraryId })

    if (!isLifecycleCurrent(probeToken)) {
      return result
    }

    return result
  }

  async function renameLibrary(libraryId: string, name: string) {
    return libraryStore.renameLibrary(libraryId, name)
  }

  async function deleteLibrary(libraryId: string) {
    const result = await libraryStore.deleteLibrary(libraryId)
    applyPlaybackCommandResult(result)

    if (preferencesStore.activeLibrary.value === result.deletedLibraryId && result.fallbackLibraryId) {
      await setActiveLibrary(result.fallbackLibraryId)
      await setActiveCollection(libraryStore.getDefaultCollectionRef(result.fallbackLibraryId))
    }

    return result
  }

  async function reorderLibraries(orderedLibraryIds: string[]) {
    return libraryStore.reorderLibraries(orderedLibraryIds)
  }

  async function createPlaylist(name: string) {
    assertFeatureCanCreate(LICENSE_FEATURES.PLAYLIST)
    const libraryId = preferencesStore.activeLibrary.value
    return libraryStore.createPlaylist({
      libraryId,
      name,
    })
  }

  async function renamePlaylist(playlistId: string, name: string) {
    return libraryStore.renamePlaylist(playlistId, name)
  }

  async function deletePlaylist(playlistId: string) {
    const result = await libraryStore.deletePlaylist(playlistId)

    if (preferencesStore.activeCollection.value === `playlist:${result.deletedPlaylistId}`) {
      preferencesStore.setActiveCollection(libraryStore.getDefaultCollectionRef(result.libraryId))
    }

    return result
  }

  async function reorderPlaylists(orderedPlaylistIds: string[]) {
    return libraryStore.reorderPlaylists({
      libraryId: preferencesStore.activeLibrary.value,
      orderedPlaylistIds,
    })
  }

  async function addTrackToPlaylist({ playlistId, trackId, index }: { playlistId: string; trackId: string; index?: number }) {
    return libraryStore.addTrackToPlaylist({
      playlistId,
      trackId,
      index,
    })
  }

  async function removeTrackFromPlaylist({ playlistId, trackId }: { playlistId: string; trackId: string }) {
    return libraryStore.removeTrackFromPlaylist({
      playlistId,
      trackId,
    })
  }

  async function deleteTrackFromLibrary(trackId: string) {
    const normalizedTrackId = typeof trackId === 'string' ? trackId.trim() : ''

    if (!normalizedTrackId) {
      const error = new Error('Cannot delete a track without a track id.')
      void logDiagnosticsErrorAny('[OFPlayer track delete]', 'catalog', 'track_delete_invalid_request', {
        trackId,
      })
      throw error
    }

    const startedAt = nowMs()

    try {
      const result = await libraryStore.deleteTrackFromLibrary(normalizedTrackId)
      applyPlaybackCommandResult(result)
      void logDiagnosticsInfoAny('[OFPlayer track delete]', 'catalog', 'track_delete_completed', {
        requestedTrackId: normalizedTrackId,
        deletedTrackId: result?.deletedTrackId ?? '',
        deletedRelationCount: result?.deletedRelationIds?.length ?? 0,
        reorderedTrackCount: result?.reorderedTracks?.length ?? 0,
        activeTrackId: result?.playback?.activeTrackId ?? null,
        totalMs: Math.round(nowMs() - startedAt),
      })

      return result
    } catch (error) {
      void logDiagnosticsErrorAny('[OFPlayer track delete]', 'catalog', 'track_delete_failed', {
        requestedTrackId: normalizedTrackId,
        error,
        totalMs: Math.round(nowMs() - startedAt),
      })
      throw error
    }
  }

  async function deleteTracksFromLibrary(trackIds: string[]) {
    const normalizedTrackIds = Array.from(
      new Set(
        (Array.isArray(trackIds) ? trackIds : [])
          .map((trackId) => (typeof trackId === 'string' ? trackId.trim() : ''))
          .filter(Boolean),
      ),
    )

    if (normalizedTrackIds.length === 0) {
      const error = new Error('Cannot delete tracks without track ids.')
      void logDiagnosticsErrorAny('[OFPlayer track delete]', 'catalog', 'track_batch_delete_invalid_request', {
        trackIds,
      })
      throw error
    }

    const startedAt = nowMs()

    try {
      const result = await libraryStore.deleteTracksFromLibrary(normalizedTrackIds)
      applyPlaybackCommandResult(result)
      void logDiagnosticsInfoAny('[OFPlayer track delete]', 'catalog', 'track_batch_delete_completed', {
        requestedTrackCount: normalizedTrackIds.length,
        deletedTrackCount: result?.deletedTrackIds?.length ?? 0,
        deletedRelationCount: result?.deletedRelationIds?.length ?? 0,
        reorderedTrackCount: result?.reorderedTracks?.length ?? 0,
        activeTrackId: result?.playback?.activeTrackId ?? null,
        totalMs: Math.round(nowMs() - startedAt),
      })

      return result
    } catch (error) {
      void logDiagnosticsErrorAny('[OFPlayer track delete]', 'catalog', 'track_batch_delete_failed', {
        requestedTrackCount: normalizedTrackIds.length,
        error,
        totalMs: Math.round(nowMs() - startedAt),
      })
      throw error
    }
  }

  async function toggleFavorite(trackId: string) {
    return libraryStore.toggleFavorite(trackId)
  }

  async function bindLyricsFile(trackId = currentTrackId.value) {
    if (!desktopStorageService.available || typeof desktopStorageService.pickLyricsFile !== 'function') {
      return null
    }

    const track = await libraryStore.getOrLoadTrack(trackId)

    if (!track) {
      return null
    }

    const selectedPath = await desktopStorageService.pickLyricsFile({
      defaultPath: resolveLyricsDialogPath(track),
    })

    if (!selectedPath) {
      return null
    }

    return libraryStore.updateTrackMetadata(track.id, {
      lyricsPath: selectedPath,
    })
  }

  async function clearLyricsBinding(trackId = currentTrackId.value) {
    const track = await libraryStore.getOrLoadTrack(trackId)

    if (!track) {
      return null
    }

    return libraryStore.updateTrackMetadata(track.id, {
      lyricsPath: '',
    })
  }

  async function refreshStorageUsage() {
    if (!desktopStorageService.available || isLoadingStorageUsage.value) {
      return storageUsage.value
    }

    isLoadingStorageUsage.value = true
    storageMaintenanceError.value = ''

    try {
      const snapshot = await desktopStorageService.analyzeStorageUsage()
      storageUsage.value = snapshot ?? null
      return storageUsage.value
    } catch (error) {
      const message = formatCommandError(error, 'Failed to analyze OFPlayer storage.')
      storageMaintenanceError.value = message
      void logDiagnosticsWarnAny('[OFPlayer storage]', 'storage', 'storage_usage_failed', {
        error,
      })
      return storageUsage.value
    } finally {
      isLoadingStorageUsage.value = false
    }
  }

  async function collectStorageGarbage() {
    if (!desktopStorageService.available || isCollectingGarbage.value) {
      return null
    }

    if (scanProgress.value.active) {
      throw new Error('Cannot clean OFPlayer storage while a library scan is still running.')
    }

    isCollectingGarbage.value = true
    storageMaintenanceError.value = ''
    const startedAt = nowMs()

    try {
      const result = await desktopStorageService.collectGarbage()
      storageUsage.value = result?.after ?? storageUsage.value
      void logDiagnosticsInfoAny('[OFPlayer storage]', 'storage', 'storage_gc', {
        roundTripMs: Math.round(nowMs() - startedAt),
        totalMs: result?.totalMs ?? null,
        reclaimedBytes: result?.reclaimedBytes ?? 0,
        removedFiles: result?.removedFiles ?? 0,
        removedDirectories: result?.removedDirectories ?? 0,
        warnings: result?.warnings ?? [],
      })
      return result
    } catch (error) {
      const message = formatCommandError(error, 'Failed to clean OFPlayer storage.')
      storageMaintenanceError.value = message
      void logDiagnosticsErrorAny('[OFPlayer storage]', 'storage', 'storage_gc_failed', {
        error,
      })
      throw error
    } finally {
      isCollectingGarbage.value = false
    }
  }

  async function reorderPlaylistTracks({ playlistId, orderedTrackIds }: { playlistId: string; orderedTrackIds: string[] }) {
    return libraryStore.reorderPlaylistTracks({
      playlistId,
      orderedTrackIds,
    })
  }

  async function resetAllData() {
    if (disposed || isResettingData.value) {
      return false
    }

    if (scanProgress.value.active) {
      throw new Error('Cannot clear OFPlayer data while a library scan is still running.')
    }

    isResettingData.value = true
    isBootstrapReady.value = false
    const resetToken = invalidateLifecycle()
    const resetStartedAt = nowMs()
    const navigationWatcherWasActive = typeof stopNavigationWatcher === 'function'

    try {
      clearPendingStorageWatchScan()
      clearScanProgressResetTimer()
      clearRemoteMetadataHydrationQueue()

      if (autoScanStartupTimerId !== null) {
        clearTimeout(autoScanStartupTimerId)
        autoScanStartupTimerId = null
      }

      const resetResult = await dataService.maintenance.resetAllData()

      if (!isLifecycleCurrent(resetToken)) {
        return false
      }

      playerStore.setActiveTrack(null)
      playerStore.applyPlaybackSnapshot(resetResult?.playback ?? {})
      clearPersistedStartupState()

      if (typeof stopNavigationWatcher === 'function') {
        stopNavigationWatcher()
        stopNavigationWatcher = null
      }

      navigationRequestId += 1
      lastNavigationSummaryRequestKey = ''
      navigationSummary.value = createEmptyNavigationSummary()
      bootstrapState = null
      hasBootstrapNavigationSummary = false
      storageUsage.value = null
      storageMaintenanceError.value = ''
      resetScanProgress()

      await Promise.all([
        libraryStore.hydrate(null, { revision: 0 }),
        sessionStore.hydrate(null, { revision: 0 }),
        preferencesStore.hydrate(null),
      ])

      if (!isLifecycleCurrent(resetToken)) {
        return false
      }

      await playerStore.hydrate(null, { revision: 0 })
      playerStore.setVolume(preferencesStore.volume.value)
      const appliedOutputDeviceId = await playerStore.setOutputDevicePreference(
        preferencesStore.playbackOutputDeviceId.value,
      )
      if (appliedOutputDeviceId !== preferencesStore.playbackOutputDeviceId.value) {
        preferencesStore.setPlaybackOutputDeviceId(appliedOutputDeviceId)
      }
      playerStore.setActiveTrack(libraryStore.getTrackById(sessionStore.currentTrackId.value) ?? null)

      ensureActiveLibrarySelection()
      await refreshNavigationSummary({ force: true })
      await syncStorageWatch()

      if (!disposed) {
        isBootstrapReady.value = true
        startNavigationWatcher({ immediate: false })
      }

      void logDiagnosticsInfoAny('[OFPlayer maintenance]', 'storage', 'reset_all_data', {
        totalMs: Math.round(nowMs() - resetStartedAt),
        managedStorageDeleted: resetResult?.managedStorageDeleted === true,
        managedStoragePath: resetResult?.managedStoragePath || null,
      })

      return true
    } catch (error) {
      void logDiagnosticsErrorAny('[OFPlayer maintenance]', 'storage', 'reset_all_data_failed', {
        error,
      })
      throw error
    } finally {
      if (navigationWatcherWasActive && !disposed && !stopNavigationWatcher) {
        startNavigationWatcher({ immediate: false })
      }

      isResettingData.value = false
      if (!disposed) {
        isBootstrapReady.value = true
      }
    }
  }

  function dispose() {
    if (disposed) {
      return
    }

    disposed = true
    invalidateLifecycle()
    navigationRequestId += 1
    clearPendingStorageWatchScan()
    clearScanProgressResetTimer()
    clearStartupPlaybackSyncTimer()
    clearRemoteMetadataHydrationQueue()
    clearTrackArtworkPrefetchQueue()
    clearRetainedPlaybackArtworkTimer()
    clearAppUpdateTimers()

    if (autoScanStartupTimerId !== null) {
      clearTimeout(autoScanStartupTimerId)
      autoScanStartupTimerId = null
    }

    if (typeof stopNavigationWatcher === 'function') {
      stopNavigationWatcher()
      stopNavigationWatcher = null
    }

    browserCatalog.stopWatcher()

    if (typeof unlistenStorageWatch === 'function') {
      unlistenStorageWatch()
      unlistenStorageWatch = null
    }

    if (typeof unlistenScanProgress === 'function') {
      unlistenScanProgress()
      unlistenScanProgress = null
    }

    if (typeof unlistenSystemMedia === 'function') {
      unlistenSystemMedia()
      unlistenSystemMedia = null
    }

    void desktopStorageService.configureWatchDirectories({
      storageRoot: preferencesStore.storageRoot.value,
      directories: [],
      enabled: false,
    })
    playerStore.dispose()
    libraryStore.dispose()
  }

  async function handleSystemMediaControlEvent(payload: UnknownRecord) {
    if (disposed) {
      return
    }

    switch (payload?.action) {
      case 'play':
        await playCurrentSelection()
        break
      case 'pause':
      case 'stop':
        playbackCommandRevision += 1
        applyPlaybackCommandResult(await dataService.playbackSession.pause())
        break
      case 'toggle':
        await togglePlayback()
        break
      case 'next':
        await playNext()
        break
      case 'previous':
        await playPrevious()
        break
      case 'seekTo':
        if (payload.seconds !== null) {
          seek(payload.seconds)
        }
        break
      case 'seekBy':
        if (payload.seconds !== null) {
          seek(playerStore.currentTime.value + payload.seconds)
        }
        break
      default:
        break
    }
  }
  async function finalizeStartup() {
    const deferredStartedAt = nowMs()
    const startupToken = currentLifecycleToken()

    try {
      const bootstrapWaitStartedAt = nowMs()
      const bootstrapWaitResourceStart = captureRendererResourceSample()
      await bootstrapPromise
      if (!isLifecycleCurrent(startupToken)) {
        return
      }
      startupDiagnostics.bootstrapWaitMs = Math.round(nowMs() - bootstrapWaitStartedAt)
      recordStartupRendererStep(
        'bootstrapWait',
        bootstrapWaitStartedAt,
        bootstrapWaitResourceStart,
      )
      logStartupPhase(
        'deferred_startup_waited_for_bootstrap',
        {
          waitMs: startupDiagnostics.bootstrapWaitMs,
        },
        20,
      )

      scheduleStartupPlaybackSync(startupToken)
      enqueueActiveRemoteLibraryMetadataHydration({
        delayMs: REMOTE_METADATA_START_DELAY_MS * 2,
      })

      if (desktopStorageService.available && isLifecycleCurrent(startupToken)) {
        const watchListenStartedAt = nowMs()
        const watchListenResourceStart = captureRendererResourceSample()
        const nextUnlistenStorageWatch =
          await desktopStorageService.listenForWatchEvents(handleStorageWatchEvent)
        if (!isLifecycleCurrent(startupToken)) {
          nextUnlistenStorageWatch?.()
          return
        }
        unlistenStorageWatch = nextUnlistenStorageWatch
        startupDiagnostics.storageWatchListenMs = Math.round(nowMs() - watchListenStartedAt)
        recordStartupRendererStep(
          'storageWatchListen',
          watchListenStartedAt,
          watchListenResourceStart,
        )
        const scanProgressListenStartedAt = nowMs()
        const scanProgressListenResourceStart = captureRendererResourceSample()
        const nextUnlistenScanProgress =
          await desktopStorageService.listenForScanProgress(handleLibraryScanProgressEvent)
        if (!isLifecycleCurrent(startupToken)) {
          nextUnlistenScanProgress?.()
          return
        }
        unlistenScanProgress = nextUnlistenScanProgress
        startupDiagnostics.scanProgressListenMs = Math.round(nowMs() - scanProgressListenStartedAt)
        recordStartupRendererStep(
          'scanProgressListen',
          scanProgressListenStartedAt,
          scanProgressListenResourceStart,
        )
        void syncStorageWatch()
      }

      if (systemMediaService.available && isLifecycleCurrent(startupToken)) {
        const systemMediaListenStartedAt = nowMs()
        const systemMediaListenResourceStart = captureRendererResourceSample()
        const nextUnlistenSystemMedia = await systemMediaService.listen((payload: UnknownRecord) => {
          void handleSystemMediaControlEvent(payload)
        })
        if (!isLifecycleCurrent(startupToken)) {
          nextUnlistenSystemMedia?.()
          return
        }
        unlistenSystemMedia = nextUnlistenSystemMedia
        startupDiagnostics.systemMediaListenMs = Math.round(nowMs() - systemMediaListenStartedAt)
        recordStartupRendererStep(
          'systemMediaListen',
          systemMediaListenStartedAt,
          systemMediaListenResourceStart,
        )
      }

      if (
        desktopStorageService.available &&
        preferencesStore.autoScanOnLaunch.value &&
        preferencesStore.scanDirectories.value.length > 0 &&
        isLifecycleCurrent(startupToken)
      ) {
        autoScanStartupTimerId = window.setTimeout(() => {
          autoScanStartupTimerId = null

          if (!isLifecycleCurrent(startupToken)) {
            return
          }

          void runLibraryScanImport()
        }, STARTUP_AUTO_SCAN_DELAY_MS)
      }

      scheduleAppUpdateChecks(startupToken)
    } finally {
      startupDiagnostics.deferredMs = Math.round(nowMs() - deferredStartedAt)
      startupDiagnostics.totalMs = Math.round(nowMs() - startupStartedAt)
      const startupRendererResourceEnd = captureRendererResourceSample()

      if (startupDiagnostics.totalMs >= STARTUP_LOG_THRESHOLD_MS) {
        void logDiagnosticsInfoAny('[OFPlayer app startup]', 'startup', 'app_startup', {
          diagnosticsVersion: startupDiagnosticsVersion,
          ...startupDiagnostics,
          rendererResources: buildRendererResourceProfile(
            startupRendererResourceStart,
            startupRendererResourceEnd,
          ),
          rendererStepProfiles: startupRendererStepProfiles,
          bootstrapDiagnostics: bootstrapState?.diagnostics ?? null,
          bootstrapInvokeOverheadMs: Math.max(
            0,
            (startupDiagnostics.bootstrapLoadMs ?? 0) - (bootstrapState?.diagnostics?.totalMs ?? 0),
          ),
          usedBootstrapNavigationSummary: hasBootstrapNavigationSummary,
          deferredAutoScan: Boolean(
            desktopStorageService.available &&
              preferencesStore.autoScanOnLaunch.value &&
              preferencesStore.scanDirectories.value.length > 0,
          ),
        })
      }
    }
  }

  function startDeferredStartup() {
    if (disposed) {
      return Promise.resolve(null)
    }

    if (deferredStartupStarted) {
      return deferredStartupPromise ?? Promise.resolve()
    }

    deferredStartupStarted = true
    deferredStartupPromise = finalizeStartup().catch((error) => {
      void logDiagnosticsErrorAny('[OFPlayer app startup]', 'startup', 'deferred_startup_failed', {
        error,
      })
      return null
    })
    return deferredStartupPromise
  }

  return {
    libraries,
    playlists,
    tracks,
    playlistTrackRelations,
    catalogRevision,
    isBootstrapReady: computed(() => isBootstrapReady.value),
    activeCollectionDataReady,
    activeCollectionDataStatus,
    activeCollectionDataError,
    collectionQueryRevision,
    hasTracks,
    currentTrackId,
    currentTrack,
    currentRemoteTrackStatus,
    navigationSummary: computed(() => navigationSummary.value),
    isPlaying: playerStore.isPlaying,
    playerError: playerStore.error,
    currentTime: playerStore.currentTime,
    duration: computed(() => playerStore.duration.value || currentTrack.value?.duration || 0),
    volume: preferencesStore.volume,
    rememberVolume: preferencesStore.rememberVolume,
    repeatMode: preferencesStore.repeatMode,
    shuffleEnabled: preferencesStore.shuffleEnabled,
    playbackSignalPath: playerStore.signalPath,
    playbackAudioLevels: playerStore.audioLevels,
    playbackOutputDevices: playerStore.outputDevices,
    playbackOutputDeviceId: preferencesStore.playbackOutputDeviceId,
    activePlaybackOutputDeviceId: playerStore.activeOutputDeviceId,
    activePlaybackOutputDeviceName: playerStore.activeOutputDeviceName,
    prefersSystemPlaybackOutput: playerStore.prefersSystemOutputDevice,
    playbackOutputDeviceAvailable: playerStore.preferredOutputDeviceAvailable,
    language: preferencesStore.language,
    theme: preferencesStore.theme,
    colorScheme: preferencesStore.colorScheme,
    motion: preferencesStore.motion,
    windowEffects: preferencesStore.windowEffects,
    showTechnicalMetadata: preferencesStore.showTechnicalMetadata,
    immersiveTaskbarMode: preferencesStore.immersiveTaskbarMode,
    licenseState: computed(() => licenseState.value),
    licenseFeatureLimits,
    telemetryEnabled: preferencesStore.telemetryEnabled,
    recentHistory: playerStore.recentHistory,
    queueTrackIds: sessionStore.queueTrackIds,
    searchQuery: preferencesStore.searchQuery,
    sortOption: preferencesStore.sortOption,
    activeSortOption,
    collectionSortOptions: preferencesStore.collectionSortOptions,
    typeFilter: preferencesStore.typeFilter,
    importMode: fileImportService.importMode,
    activeLibrary: preferencesStore.activeLibrary,
    activeCollection: preferencesStore.activeCollection,
    sidebarSection: preferencesStore.sidebarSection,
    storageRoot: preferencesStore.storageRoot,
    scanDirectories: preferencesStore.scanDirectories,
    lyricsScanDirectories: preferencesStore.lyricsScanDirectories,
    autoScanOnLaunch: preferencesStore.autoScanOnLaunch,
    lastScanAt: preferencesStore.lastScanAt,
    scanProgress: computed(() => scanProgress.value),
    isResettingData: computed(() => isResettingData.value),
    storageUsage: computed(() => storageUsage.value),
    isLoadingStorageUsage: computed(() => isLoadingStorageUsage.value),
    isCollectingGarbage: computed(() => isCollectingGarbage.value),
    storageMaintenanceError: computed(() => storageMaintenanceError.value),
    canManageStorage: computed(() => desktopStorageService.available),
    isUploadingDiagnosticsReport: computed(() => isUploadingDiagnosticsReport.value),
    diagnosticsReportStatus: computed(() => diagnosticsReportStatus.value),
    appUpdateState: computed(() => appUpdateService.state),
    isSettingsOpen: preferencesStore.isSettingsOpen,
    settingsInitialCategory: preferencesStore.settingsInitialCategory,
    settingsNotice: preferencesStore.settingsNotice,
    importFiles,
    requestImportFiles,
    requestImportFolder,
    selectStorageRoot,
    addScanDirectory,
    removeScanDirectory,
    addLyricsScanDirectory,
    removeLyricsScanDirectory,
    setAutoScanOnLaunch,
    runLibraryScanImport,
    selectTrack,
    togglePlayback,
    playPrevious,
    playNext,
    seek,
    setVolume,
    cycleRepeatMode,
    cyclePlaybackMode,
    toggleShuffle,
    refreshPlaybackOutputDevices,
    setPlaybackOutputDevice,
    setSearchQuery,
    setSortOption,
    setTypeFilter,
    setActiveLibrary,
    setActiveCollection,
    createLibrary,
    connectExternalLibrary,
    syncExternalLibrary,
    probeExternalLibrary,
    renameLibrary,
    deleteLibrary,
    reorderLibraries,
    createPlaylist,
    renamePlaylist,
    deletePlaylist,
    reorderPlaylists,
    addTrackToPlaylist,
    removeTrackFromPlaylist,
    deleteTrackFromLibrary,
    deleteTracksFromLibrary,
    toggleFavorite,
    hydrateTrackArtwork,
    bindLyricsFile,
    clearLyricsBinding,
    refreshStorageUsage,
    collectStorageGarbage,
    resetAllData,
    checkForUpdates: appUpdateService.checkForUpdates,
    downloadAndInstallUpdate: appUpdateService.downloadAndInstallUpdate,
    dismissAvailableUpdate: appUpdateService.dismissAvailableUpdate,
    resolveLyricsForTrack: lyricsService.resolveForTrack,
    reorderPlaylistTracks,
    setRememberVolume: preferencesStore.setRememberVolume,
    setLanguage: preferencesStore.setLanguage,
    setTheme: preferencesStore.setTheme,
    setColorScheme: preferencesStore.setColorScheme,
    setMotion: preferencesStore.setMotion,
    setWindowEffects: preferencesStore.setWindowEffects,
    setShowTechnicalMetadata: preferencesStore.setShowTechnicalMetadata,
    setImmersiveTaskbarMode: preferencesStore.setImmersiveTaskbarMode,
    setTelemetryConsent,
    uploadDiagnosticsReportNow,
    setSidebarSection: preferencesStore.setSidebarSection,
    openSettings: preferencesStore.openSettings,
    closeSettings: preferencesStore.closeSettings,
    toggleSettings: preferencesStore.toggleSettings,
    stores: {
      libraryStore,
      sessionStore,
      playerStore,
      preferencesStore,
      externalLibraryService,
    },
    lyrics: lyricsService,
    waitForVisualReady: () => visualReadyPromise,
    startDeferredStartup,
    dispose,
  }
}

export function installOFPlayerApp(app: App, ofplayer: OFPlayerApp) {
  app.provide(OFPLAYER_APP_KEY, ofplayer)
}

export function useOFPlayerApp() {
  const ofplayer = inject(OFPLAYER_APP_KEY, null)

  if (!ofplayer) {
    throw new Error('OFPlayer app has not been installed.')
  }

  return ofplayer
}
