<script setup lang="ts">
import { invoke, isTauri } from '@tauri-apps/api/core'
import { listen, type Event as TauriEvent, type UnlistenFn } from '@tauri-apps/api/event'
import { getCurrentWindow, PhysicalPosition, PhysicalSize, type CloseRequestedEvent } from '@tauri-apps/api/window'
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useLyrics } from './composables/useLyrics'
import LibraryPanel from './components/LibraryPanel.vue'
import PlayerPanel from './components/PlayerPanel.vue'
import WindowTitlebar from './components/WindowTitlebar.vue'
import { useOFPlayerApp } from './app/ofplayerApp'
import { useI18n } from './composables/useI18n'
import { buildLibraryNavigationView } from './models/libraryNavigation'
import { IMMERSIVE_TASKBAR_MODES } from './models/preferences'
import {
  closeLyricCapsuleWindow,
  createLyricCapsuleWindow,
  isLyricCapsuleWindowEnabled,
} from './services/lyricCapsuleWindow'
import { LYRIC_CAPSULE_CONTROL_EVENT } from './services/lyricCapsuleBridge'
import {
  createLyricCapsuleAttemptId,
  elapsedMs,
  logLyricCapsuleError as logLyricCapsuleErrorRaw,
  logLyricCapsuleInfo as logLyricCapsuleInfoRaw,
  logLyricCapsuleWarn as logLyricCapsuleWarnRaw,
  nowMs,
} from './services/lyricCapsuleDiagnostics'
import { getDiagnosticsLogStatus } from './services/diagnosticsLogger'
import { applyNativeImmersiveWindowMode } from './services/immersiveWindowMode'

const ImmersivePlayerView = defineAsyncComponent(
  () => import('./components/ImmersivePlayerView.vue'),
)
const CloseBehaviorDialog = defineAsyncComponent(() => import('./components/CloseBehaviorDialog.vue'))
const SettingsModal = defineAsyncComponent(() => import('./components/SettingsModal.vue'))
const ExternalLibraryDialog = defineAsyncComponent(() => import('./components/ExternalLibraryDialog.vue'))
const OnboardingGuide = defineAsyncComponent(() => import('./components/OnboardingGuide.vue'))
const TelemetryConsentDialog = defineAsyncComponent(() => import('./components/TelemetryConsentDialog.vue'))
const ListeningStatsPanel = defineAsyncComponent(() => import('./components/ListeningStatsPanel.vue'))

const ONBOARDING_KEY = 'ofplayer:onboarding:seen:v1'
const IMMERSIVE_CLOSE_RESTORE_FALLBACK_MS = 900

type UnknownRecord = Record<string, any>
type ImmersiveTaskbarMode = (typeof IMMERSIVE_TASKBAR_MODES)[keyof typeof IMMERSIVE_TASKBAR_MODES]
type AppWindow = ReturnType<typeof getCurrentWindow>
type LyricCapsuleLogger = (event: string, payload?: UnknownRecord | null) => Promise<void>

interface LyricCapsuleAttempt {
  id: string
  reason: string
  startedAt: number
}

interface LyricCapsuleControlPayload {
  action?: 'previous' | 'toggle-playback' | 'next' | string
}

interface ImmersiveWindowRestoreState {
  fullscreen: boolean
  maximized: boolean
  alwaysOnTop: boolean
  outerPosition: PhysicalPosition | null
  outerSize: PhysicalSize | null
}

interface ExternalLibrarySyncStatus {
  active: boolean
  phase: string
  libraryId: string
  provider: string
  remoteTotal: number
  imported: number
  updated: number
  error: string
}

interface RemoteLibraryProbeStatus {
  active: boolean
  phase: string
  libraryId: string
  provider: string
  ok: boolean | null
  synced: boolean
  remoteTotal: number
  checkedAt: string
  error: string
}

interface TrackPlaylistRequest {
  playlistId?: string | null
  trackId?: string | null
}

const logLyricCapsuleInfo = logLyricCapsuleInfoRaw as LyricCapsuleLogger
const logLyricCapsuleWarn = logLyricCapsuleWarnRaw as LyricCapsuleLogger
const logLyricCapsuleError = logLyricCapsuleErrorRaw as LyricCapsuleLogger

const { locale, t } = useI18n()
const showsCustomTitlebar = isTauri()
const ofplayerApp = useOFPlayerApp() as UnknownRecord

const {
  libraries,
  playlists,
  tracks,
  playlistTrackRelations,
  isBootstrapReady,
  activeCollectionDataReady,
  activeCollectionDataStatus,
  activeCollectionDataError,
  collectionQueryRevision,
  hasTracks,
  currentTrackId,
  currentTrack,
  currentRemoteTrackStatus,
  navigationSummary,
  isPlaying,
  playerError,
  currentTime,
  duration,
  volume,
  rememberVolume,
  repeatMode,
  shuffleEnabled,
  playbackSignalPath,
  playbackOutputDevices,
  playbackOutputDeviceId,
  activePlaybackOutputDeviceName,
  prefersSystemPlaybackOutput,
  playbackOutputDeviceAvailable,
  language,
  theme,
  colorScheme,
  motion,
  windowEffects,
  showTechnicalMetadata,
  immersiveTaskbarMode,
  licenseFeatureLimits,
  telemetryEnabled,
  historyRevision,
  searchQuery,
  sortOption,
  activeSortOption,
  typeFilter,
  importMode,
  activeLibrary,
  activeCollection,
  storageRoot,
  scanDirectories,
  lyricsScanDirectories,
  autoScanOnLaunch,
  lastScanAt,
  scanProgress,
  isResettingData,
  storageUsage,
  isLoadingStorageUsage,
  isCollectingGarbage,
  storageMaintenanceError,
  canManageStorage,
  isUploadingDiagnosticsReport,
  diagnosticsReportStatus,
  appUpdateState,
  isSettingsOpen,
  settingsInitialCategory,
  settingsNotice,
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
  createPlaylist,
  renamePlaylist,
  deletePlaylist,
  addTrackToPlaylist,
  removeTrackFromPlaylist,
  reorderPlaylistTracks,
  deleteTrackFromLibrary,
  deleteTracksFromLibrary,
  toggleFavorite,
  hydrateTrackArtwork,
  bindLyricsFile,
  clearLyricsBinding,
  refreshStorageUsage,
  collectStorageGarbage,
  loadListeningStats,
  resetAllData,
  checkForUpdates,
  downloadAndInstallUpdate,
  resolveLyricsForTrack,
  lyrics,
  setRememberVolume,
  setLanguage,
  setTheme,
  setColorScheme,
  setMotion,
  setWindowEffects,
  setShowTechnicalMetadata,
  setImmersiveTaskbarMode,
  setTelemetryConsent,
  uploadDiagnosticsReportNow,
  openSettings,
  closeSettings,
  dispose,
} = ofplayerApp
let activeLyricCapsuleAttempt: LyricCapsuleAttempt | null = null
let unlistenLyricCapsuleControl: UnlistenFn | null = null
let unlistenMainWindowCloseRequested: UnlistenFn | null = null
let unlistenMainWindowFocusChanged: UnlistenFn | null = null
let immersiveWindowRestoreTimerId: number | null = null

onBeforeUnmount(() => {
  if (typeof unlistenLyricCapsuleControl === 'function') {
    unlistenLyricCapsuleControl()
    unlistenLyricCapsuleControl = null
  }
  if (typeof unlistenMainWindowCloseRequested === 'function') {
    unlistenMainWindowCloseRequested()
    unlistenMainWindowCloseRequested = null
  }
  if (typeof unlistenMainWindowFocusChanged === 'function') {
    unlistenMainWindowFocusChanged()
    unlistenMainWindowFocusChanged = null
  }
  if (externalLibrarySyncStatusTimer) {
    window.clearTimeout(externalLibrarySyncStatusTimer)
    externalLibrarySyncStatusTimer = null
  }
  if (immersiveWindowRestoreTimerId !== null) {
    window.clearTimeout(immersiveWindowRestoreTimerId)
    immersiveWindowRestoreTimerId = null
  }
  clearImmersiveTransitionState()
  void restoreImmersiveWindowMode()
  dispose()
})

async function handleResetAllData() {
  try {
    await resetAllData()
  } catch (error) {
    const fallbackMessage = t('settings.resetData.errorFallback')
    const message =
      error instanceof Error && error.message
        ? error.message
        : typeof error === 'string' && error
          ? error
          : fallbackMessage

    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(message)
    }
  }
}

async function handleCollectStorageGarbage() {
  try {
    await collectStorageGarbage()
  } catch (error) {
    const fallbackMessage = t('settings.storageUsage.errorFallback')
    const message =
      error instanceof Error && error.message
        ? error.message
        : typeof error === 'string' && error
          ? error
          : fallbackMessage

    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(message)
    }
  }
}

async function handleCheckForUpdates() {
  await checkForUpdates({
    reason: 'settings-manual',
    silent: false,
    force: true,
    minIntervalMs: 0,
  })
}

async function handleDownloadAndInstallUpdate() {
  const shouldInstall =
    typeof window === 'undefined' ||
    typeof window.confirm !== 'function' ||
    window.confirm(t('settings.updates.installConfirm'))

  if (!shouldInstall) {
    return
  }

  try {
    await downloadAndInstallUpdate()
  } catch (error) {
    const message =
      error instanceof Error && error.message
        ? error.message
        : typeof error === 'string' && error
          ? error
          : t('settings.updates.installError')

    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(message)
    }
  }
}

async function handleUploadDiagnosticsReport() {
  await uploadDiagnosticsReportNow()
}

const navigation = computed(() =>
  buildLibraryNavigationView({
    libraries: libraries.value,
    playlists: playlists.value,
    summary: navigationSummary.value,
    t,
  }),
)

const activeLibraryItem = computed(() => navigation.value.activeLibraryItem)
const activeCollectionItem = computed(() => navigation.value.activeCollection)
const isImmersivePlayerOpen = ref(false)
const isImmersiveWindowed = computed(
  () => showsCustomTitlebar && immersiveTaskbarMode.value !== IMMERSIVE_TASKBAR_MODES.HIDE,
)
const isExternalLibraryDialogOpen = ref(false)
const isListeningStatsOpen = ref(false)
const isConnectingExternalLibrary = ref(false)
const externalLibraryError = ref('')
const REMOTE_PROBE_SYNC_PROVIDERS = new Set(['subsonic'])
const externalLibrarySyncStatus = ref<ExternalLibrarySyncStatus>({
  active: false,
  phase: 'idle',
  libraryId: '',
  provider: '',
  remoteTotal: 0,
  imported: 0,
  updated: 0,
  error: '',
})
const remoteLibraryProbeStatus = ref<RemoteLibraryProbeStatus>({
  active: false,
  phase: 'idle',
  libraryId: '',
  provider: '',
  ok: null,
  synced: false,
  remoteTotal: 0,
  checkedAt: '',
  error: '',
})
let externalLibrarySyncStatusTimer: number | null = null
let immersiveWindowRestoreState: ImmersiveWindowRestoreState | null = null
const isOnboardingOpen = ref(shouldOpenOnboarding())
const isLyricCapsuleWindowActive = ref(isLyricCapsuleWindowEnabled())
const isOpeningLyricCapsuleWindow = ref(false)
const isCloseBehaviorDialogOpen = ref(false)
const isResolvingCloseBehavior = ref(false)
const isQuittingFromCloseBehavior = ref(false)
const isPlayerInBackground = ref(false)
const {
  lyrics: immersiveLyricsSnapshot,
  activeIndex: immersiveLyricsActiveIndex,
  hasTimestamps: immersiveLyricsHasTimestamps,
  isLoading: immersiveLyricsLoading,
  refresh: refreshImmersiveLyrics,
} = useLyrics({
  currentTrack,
  currentTime,
  cacheContext: computed(() => lyricsScanDirectories.value.join('|')),
  resolveLyrics: resolveLyricsForTrack,
  findActiveLineIndex: lyrics?.findActiveLineIndex,
})

async function openLyricCapsuleWindow(reason = 'manual') {
  if (!showsCustomTitlebar || isOpeningLyricCapsuleWindow.value) {
    void logLyricCapsuleWarn('main_open_ignored', {
      reason,
      showsCustomTitlebar,
      isOpening: isOpeningLyricCapsuleWindow.value,
      isActive: isLyricCapsuleWindowActive.value,
    })
    return
  }

  const attemptId = createLyricCapsuleAttemptId(reason)
  const startedAt = nowMs()
  activeLyricCapsuleAttempt = {
    id: attemptId,
    reason,
    startedAt,
  }
  isOpeningLyricCapsuleWindow.value = true

  void getDiagnosticsLogStatus().then((status) => {
    void logLyricCapsuleInfo('main_open_diagnostics_log', {
      attemptId,
      path: status?.path ?? null,
      directory: status?.directory ?? null,
      directoryKind: status?.directoryKind ?? null,
      fallbackReason: status?.fallbackReason ?? null,
    })
  })

  void logLyricCapsuleInfo('main_open_start', {
    attemptId,
    reason,
    hasTrack: Boolean(currentTrack.value),
    trackId: currentTrackId.value,
    artworkLength: typeof currentTrack.value?.artwork === 'string' ? currentTrack.value.artwork.length : 0,
    isPlaying: isPlaying.value,
    currentTime: currentTime.value,
    duration: duration.value,
  })

  try {
    const createStartedAt = nowMs()
    await createLyricCapsuleWindow({
      attemptId,
      reason,
    })
    const createMs = elapsedMs(createStartedAt)
    isLyricCapsuleWindowActive.value = true

    void logLyricCapsuleInfo('main_open_complete', {
      attemptId,
      reason,
      createMs,
      totalMs: elapsedMs(startedAt),
      dataPath: 'capsule-pull-boot-state',
    })
  } catch (error) {
    void logLyricCapsuleError('main_open_failed', {
      attemptId,
      reason,
      totalMs: elapsedMs(startedAt),
      error,
    })
    isLyricCapsuleWindowActive.value = false
  } finally {
    isOpeningLyricCapsuleWindow.value = false
  }
}

async function closeLyricCapsuleWindowFromPlayer() {
  if (isOpeningLyricCapsuleWindow.value) {
    void logLyricCapsuleWarn('main_close_ignored', {
      attemptId: activeLyricCapsuleAttempt?.id ?? null,
      isOpening: isOpeningLyricCapsuleWindow.value,
    })
    return
  }

  const startedAt = nowMs()
  const attemptId = createLyricCapsuleAttemptId('close')
  isOpeningLyricCapsuleWindow.value = true

  void logLyricCapsuleInfo('main_close_start', {
    attemptId,
    activeAttemptId: activeLyricCapsuleAttempt?.id ?? null,
  })

  try {
    await closeLyricCapsuleWindow({
      attemptId,
      activeAttemptId: activeLyricCapsuleAttempt?.id ?? null,
    })

    if (isTauri()) {
      await invoke('capsule_release').catch((error) => {
        void logLyricCapsuleWarn('main_close_release_failed', {
          attemptId,
          error,
        })
      })
    }
  } finally {
    isLyricCapsuleWindowActive.value = false
    isOpeningLyricCapsuleWindow.value = false
    activeLyricCapsuleAttempt = null
    void logLyricCapsuleInfo('main_close_complete', {
      attemptId,
      totalMs: elapsedMs(startedAt),
    })
  }
}

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, ms)
  })
}

async function waitForLyricCapsuleWindowIdle(timeoutMs = 1800): Promise<boolean> {
  const startedAt = nowMs()

  while (isOpeningLyricCapsuleWindow.value && elapsedMs(startedAt) < timeoutMs) {
    await wait(80)
  }

  return !isOpeningLyricCapsuleWindow.value
}

async function closeLyricCapsuleWindowForExit() {
  if (!isTauri()) {
    return
  }

  const startedAt = nowMs()
  const attemptId = createLyricCapsuleAttemptId('app-exit')
  const openedBeforeExit = isLyricCapsuleWindowActive.value
  const idleBeforeClose = await waitForLyricCapsuleWindowIdle()

  void logLyricCapsuleInfo('main_exit_capsule_close_start', {
    attemptId,
    openedBeforeExit,
    idleBeforeClose,
    activeAttemptId: activeLyricCapsuleAttempt?.id ?? null,
  })

  try {
    await closeLyricCapsuleWindow({
      attemptId,
      activeAttemptId: activeLyricCapsuleAttempt?.id ?? null,
      reason: 'main-window-exit',
      idleBeforeClose,
    })

    await invoke('capsule_release').catch((error) => {
      void logLyricCapsuleWarn('main_exit_capsule_release_failed', {
        attemptId,
        error,
      })
    })
  } finally {
    isLyricCapsuleWindowActive.value = false
    isOpeningLyricCapsuleWindow.value = false
    activeLyricCapsuleAttempt = null
    void logLyricCapsuleInfo('main_exit_capsule_close_complete', {
      attemptId,
      totalMs: elapsedMs(startedAt),
    })
  }
}

function toggleLyricCapsuleWindow() {
  void logLyricCapsuleInfo('main_toggle', {
    isActive: isLyricCapsuleWindowActive.value,
    isOpening: isOpeningLyricCapsuleWindow.value,
  })

  if (isLyricCapsuleWindowActive.value) {
    void closeLyricCapsuleWindowFromPlayer()
    return
  }

  void openLyricCapsuleWindow('toggle')
}

function shouldOpenOnboarding() {
  if (typeof window === 'undefined') {
    return false
  }

  try {
    return !window.localStorage.getItem(ONBOARDING_KEY)
  } catch {
    return false
  }
}

function maybeOpenOnboarding() {
  if (!isOnboardingOpen.value && shouldOpenOnboarding()) {
    window.setTimeout(() => {
      isOnboardingOpen.value = true
    }, 180)
  }
}

function closeOnboarding() {
  isOnboardingOpen.value = false
}

function reopenOnboarding() {
  try {
    window.localStorage.removeItem(ONBOARDING_KEY)
  } catch {
    // Storage can be unavailable in restricted WebView modes.
  }

  closeSettings()
  isOnboardingOpen.value = true
}

function handleTelemetryConsent(value: boolean) {
  setTelemetryConsent(value)
}

function requestCloseBehaviorDialog(reason = 'titlebar') {
  if (!isTauri() || isQuittingFromCloseBehavior.value) {
    return
  }

  void logLyricCapsuleInfo('main_close_choice_open', {
    reason,
    capsuleActive: isLyricCapsuleWindowActive.value,
    playerInBackground: isPlayerInBackground.value,
  })
  isCloseBehaviorDialogOpen.value = true
}

function closeCloseBehaviorDialog() {
  if (!isResolvingCloseBehavior.value) {
    isCloseBehaviorDialogOpen.value = false
  }
}

async function minimizePlayerToBackground() {
  if (!isTauri() || isResolvingCloseBehavior.value) {
    return
  }

  const startedAt = nowMs()
  isResolvingCloseBehavior.value = true

  try {
    isCloseBehaviorDialogOpen.value = false
    isPlayerInBackground.value = true
    await getCurrentWindow().minimize()
    void logLyricCapsuleInfo('main_close_choice_background', {
      totalMs: elapsedMs(startedAt),
      capsuleActive: isLyricCapsuleWindowActive.value,
    })
  } catch (error) {
    isPlayerInBackground.value = false
    void logLyricCapsuleError('main_close_choice_background_failed', {
      totalMs: elapsedMs(startedAt),
      error,
    })
  } finally {
    isResolvingCloseBehavior.value = false
  }
}

async function quitFromCloseBehaviorDialog() {
  if (!isTauri() || isResolvingCloseBehavior.value) {
    return
  }

  const startedAt = nowMs()
  isResolvingCloseBehavior.value = true

  try {
    isCloseBehaviorDialogOpen.value = false
    isQuittingFromCloseBehavior.value = true
    await closeLyricCapsuleWindowForExit()
    void logLyricCapsuleInfo('main_close_choice_quit', {
      totalMs: elapsedMs(startedAt),
    })
    await invoke('desktop_app_exit')
  } catch (error) {
    isQuittingFromCloseBehavior.value = false
    isResolvingCloseBehavior.value = false
    void logLyricCapsuleError('main_close_choice_quit_failed', {
      totalMs: elapsedMs(startedAt),
      error,
    })

    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(t('window.quitError'))
    }
  }
}

function handleMainWindowCloseRequested(event: CloseRequestedEvent) {
  if (isQuittingFromCloseBehavior.value) {
    return
  }

  event?.preventDefault?.()
  requestCloseBehaviorDialog('window-close-request')
}

function handleMainWindowFocusChanged(event: TauriEvent<boolean>) {
  if (event?.payload === true) {
    isPlayerInBackground.value = false
    void checkForUpdates({
      reason: 'window-restore',
      silent: true,
    })
  }
}

function handleLyricCapsuleControl(event: TauriEvent<LyricCapsuleControlPayload>) {
  const action = event?.payload?.action

  void logLyricCapsuleInfo('main_control_event', {
    action,
    source: 'lyric-capsule',
  })

  if (action === 'previous') {
    void playPrevious()
    return
  }

  if (action === 'toggle-playback') {
    void togglePlayback()
    return
  }

  if (action === 'next') {
    void playNext()
    return
  }

  void logLyricCapsuleWarn('main_control_event_ignored', {
    action,
  })
}

onMounted(() => {
  maybeOpenOnboarding()

  if (isTauri()) {
    const appWindow = getCurrentWindow()

    appWindow
      .onCloseRequested(handleMainWindowCloseRequested)
      .then((unlisten) => {
        unlistenMainWindowCloseRequested = unlisten
      })
      .catch((error) => {
        void logLyricCapsuleWarn('main_close_request_listener_failed', { error })
      })

    appWindow
      .onFocusChanged(handleMainWindowFocusChanged)
      .then((unlisten) => {
        unlistenMainWindowFocusChanged = unlisten
      })
      .catch((error) => {
        void logLyricCapsuleWarn('main_focus_listener_failed', { error })
      })

    listen(LYRIC_CAPSULE_CONTROL_EVENT, handleLyricCapsuleControl)
      .then((unlisten) => {
        unlistenLyricCapsuleControl = unlisten
      })
      .catch((error) => {
        void logLyricCapsuleWarn('main_control_listener_failed', { error })
      })
  }

  if (!isLyricCapsuleWindowActive.value) {
    return
  }

  void openLyricCapsuleWindow('startup-auto')
})

watch(
  () => currentTrackId.value,
  (trackId) => {
    if (!trackId) {
      closeImmersivePlayer()
    }
  },
)

watch(
  () => immersiveTaskbarMode.value,
  (mode) => {
    if (isImmersivePlayerOpen.value) {
      void applyImmersiveWindowMode(mode)
    }
  },
)

async function handleAddTrackToPlaylist({ playlistId, trackId }: TrackPlaylistRequest) {
  if (!playlistId || !trackId) {
    return
  }

  await addTrackToPlaylist({
    playlistId,
    trackId,
  })
}

async function refreshCurrentTrackLyrics() {
  await refreshImmersiveLyrics(currentTrack.value)
}

function waitForNextAnimationFrame(): Promise<void> {
  if (typeof window === 'undefined' || typeof window.requestAnimationFrame !== 'function') {
    return Promise.resolve()
  }

  return new Promise((resolve) => {
    window.requestAnimationFrame(() => resolve())
  })
}

async function captureImmersiveWindowState(): Promise<ImmersiveWindowRestoreState | null> {
  if (!showsCustomTitlebar) {
    return null
  }

  try {
    const appWindow = getCurrentWindow()
    return {
      fullscreen: await appWindow.isFullscreen(),
      maximized: await appWindow.isMaximized(),
      alwaysOnTop: await appWindow.isAlwaysOnTop(),
      outerPosition: await appWindow.outerPosition(),
      outerSize: await appWindow.outerSize(),
    }
  } catch {
    return {
      fullscreen: false,
      maximized: false,
      alwaysOnTop: false,
      outerPosition: null,
      outerSize: null,
    }
  }
}

async function ensureImmersiveWindowRestoreState() {
  if (!immersiveWindowRestoreState) {
    immersiveWindowRestoreState = await captureImmersiveWindowState()
  }
}

function setImmersiveTransitionState(state: string) {
  if (typeof document === 'undefined') {
    return
  }

  if (state) {
    document.documentElement.dataset.immersiveTransition = state
  } else {
    delete document.documentElement.dataset.immersiveTransition
  }
}

function clearImmersiveTransitionState() {
  setImmersiveTransitionState('')
}

function beginImmersiveCloseTransition() {
  setImmersiveTransitionState('closing')

  if (immersiveWindowRestoreTimerId !== null) {
    window.clearTimeout(immersiveWindowRestoreTimerId)
  }

  immersiveWindowRestoreTimerId = window.setTimeout(() => {
    immersiveWindowRestoreTimerId = null
    void finishImmersiveCloseTransition()
  }, IMMERSIVE_CLOSE_RESTORE_FALLBACK_MS)
}

async function finishImmersiveCloseTransition() {
  if (immersiveWindowRestoreTimerId !== null) {
    window.clearTimeout(immersiveWindowRestoreTimerId)
    immersiveWindowRestoreTimerId = null
  }

  try {
    await restoreImmersiveWindowMode()
  } finally {
    clearImmersiveTransitionState()
  }
}

async function enterImmersiveFullscreen(appWindow: AppWindow) {
  try {
    await applyNativeImmersiveWindowMode({ hideTaskbar: true })
  } catch {
    await appWindow.setAlwaysOnTop(true).catch(() => {})
    await appWindow.setFullscreen(true)
    await appWindow.setFocus().catch(() => {})
  }

  await waitForNextAnimationFrame()

  const fullscreenApplied = await appWindow.isFullscreen().catch(() => false)

  if (!fullscreenApplied) {
    await appWindow.setFullscreen(true)
    await appWindow.setFocus().catch(() => {})
  }
}

async function restoreCapturedWindowShape({ clearRestoreState = false }: { clearRestoreState?: boolean } = {}) {
  const restoreState = immersiveWindowRestoreState

  if (!showsCustomTitlebar || !restoreState) {
    if (clearRestoreState) {
      immersiveWindowRestoreState = null
    }
    return
  }

  try {
    const appWindow = getCurrentWindow()
    await appWindow.setFullscreen(restoreState.fullscreen === true)
    await appWindow.setAlwaysOnTop(restoreState.alwaysOnTop === true)

    if (restoreState.fullscreen === true) {
      return
    }

    if (restoreState.maximized === true) {
      await appWindow.maximize()
      return
    }

    if (restoreState.outerPosition && restoreState.outerSize) {
      await appWindow.setPosition(
        new PhysicalPosition(restoreState.outerPosition.x, restoreState.outerPosition.y),
      )
      await appWindow.setSize(
        new PhysicalSize(restoreState.outerSize.width, restoreState.outerSize.height),
      )
    }
  } finally {
    if (clearRestoreState) {
      immersiveWindowRestoreState = null
    }
  }
}

async function applyImmersiveWindowMode(mode: ImmersiveTaskbarMode = immersiveTaskbarMode.value) {
  if (!showsCustomTitlebar) {
    return
  }

  const shouldHideTaskbar = mode === IMMERSIVE_TASKBAR_MODES.HIDE
  const appWindow = getCurrentWindow()

  try {
    if (shouldHideTaskbar) {
      await ensureImmersiveWindowRestoreState()
      await enterImmersiveFullscreen(appWindow)
      return
    }

    await restoreCapturedWindowShape({ clearRestoreState: true })
  } catch {
    // Keep the overlay usable if the platform refuses a window transition.
  }
}

async function restoreImmersiveWindowMode() {
  if (immersiveWindowRestoreTimerId !== null) {
    window.clearTimeout(immersiveWindowRestoreTimerId)
    immersiveWindowRestoreTimerId = null
  }

  try {
    await restoreCapturedWindowShape({ clearRestoreState: true })
  } catch {
    // Restoring window state is best effort during app shutdown or platform transitions.
  }
}

async function openImmersivePlayer() {
  if (!currentTrack.value) {
    return
  }

  if (immersiveWindowRestoreTimerId !== null) {
    window.clearTimeout(immersiveWindowRestoreTimerId)
    immersiveWindowRestoreTimerId = null
  }
  clearImmersiveTransitionState()

  if (immersiveTaskbarMode.value === IMMERSIVE_TASKBAR_MODES.HIDE) {
    await applyImmersiveWindowMode()
    await waitForNextAnimationFrame()
  }

  isImmersivePlayerOpen.value = true

  if (immersiveTaskbarMode.value === IMMERSIVE_TASKBAR_MODES.HIDE) {
    await waitForNextAnimationFrame()
    void applyImmersiveWindowMode()
  }

  // Windowed immersive mode is an in-app overlay; it must not resize the app window.
}

function closeImmersivePlayer() {
  if (!isImmersivePlayerOpen.value && !immersiveWindowRestoreState) {
    return
  }

  if (!isImmersivePlayerOpen.value) {
    void finishImmersiveCloseTransition()
    return
  }

  beginImmersiveCloseTransition()
  isImmersivePlayerOpen.value = false
}

async function setImmersiveTaskbarModeAndApply(mode: ImmersiveTaskbarMode) {
  const nextMode = setImmersiveTaskbarMode(mode)

  if (isImmersivePlayerOpen.value) {
    await applyImmersiveWindowMode(nextMode)
  }

  return nextMode
}

function toggleImmersiveTaskbarMode() {
  const nextMode =
    immersiveTaskbarMode.value === IMMERSIVE_TASKBAR_MODES.HIDE
      ? IMMERSIVE_TASKBAR_MODES.SHOW
      : IMMERSIVE_TASKBAR_MODES.HIDE

  void setImmersiveTaskbarModeAndApply(nextMode)
}

function openExternalLibraryDialog() {
  externalLibraryError.value = ''
  isExternalLibraryDialogOpen.value = true
}

function closeExternalLibraryDialog() {
  if (isConnectingExternalLibrary.value) {
    return
  }

  isExternalLibraryDialogOpen.value = false
}

function openListeningStats() {
  isListeningStatsOpen.value = true
}

function closeListeningStats() {
  isListeningStatsOpen.value = false
}

async function handleLoadListeningStats(request: UnknownRecord) {
  return loadListeningStats(request)
}

function handleSelectStatsTrack(trackId: string) {
  void selectTrack(trackId)
}

function createIdleRemoteProbeStatus(libraryId = ''): RemoteLibraryProbeStatus {
  return {
    active: false,
    phase: 'idle',
    libraryId,
    provider: '',
    ok: null,
    synced: false,
    remoteTotal: 0,
    checkedAt: '',
    error: '',
  }
}

const activeRemoteProbeStatus = computed(() => {
  const library = activeLibraryItem.value

  if (library?.source?.kind !== 'external') {
    return createIdleRemoteProbeStatus()
  }

  if (
    externalLibrarySyncStatus.value.active &&
    externalLibrarySyncStatus.value.libraryId === library.id
  ) {
    return {
      ...createIdleRemoteProbeStatus(library.id),
      active: true,
      phase: 'syncing',
      provider: externalLibrarySyncStatus.value.provider || library.source?.provider || '',
      ok: true,
    }
  }

  if (remoteLibraryProbeStatus.value.libraryId === library.id) {
    return remoteLibraryProbeStatus.value
  }

  return createIdleRemoteProbeStatus(library.id)
})

function setRemoteLibraryProbeStatus(status: Partial<RemoteLibraryProbeStatus> = {}) {
  remoteLibraryProbeStatus.value = {
    active: status.active === true,
    phase: status.phase ?? 'idle',
    libraryId: status.libraryId ?? '',
    provider: status.provider ?? '',
    ok: status.ok ?? null,
    synced: status.synced === true,
    remoteTotal: Number.isFinite(Number(status.remoteTotal)) ? Number(status.remoteTotal) : 0,
    checkedAt: status.checkedAt ?? '',
    error: status.error ?? '',
  }
}

function resolveExternalErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message
    ? error.message
    : typeof error === 'string' && error
      ? error
      : fallback
}

function setExternalLibrarySyncStatus(status: Partial<ExternalLibrarySyncStatus>) {
  if (externalLibrarySyncStatusTimer) {
    window.clearTimeout(externalLibrarySyncStatusTimer)
    externalLibrarySyncStatusTimer = null
  }

  externalLibrarySyncStatus.value = {
    active: status.active === true,
    phase: status.phase ?? 'idle',
    libraryId: status.libraryId ?? '',
    provider: status.provider ?? '',
    remoteTotal: Number.isFinite(Number(status.remoteTotal)) ? Number(status.remoteTotal) : 0,
    imported: Number.isFinite(Number(status.imported)) ? Number(status.imported) : 0,
    updated: Number.isFinite(Number(status.updated)) ? Number(status.updated) : 0,
    error: status.error ?? '',
  }

  if (['ready', 'error'].includes(externalLibrarySyncStatus.value.phase)) {
    externalLibrarySyncStatusTimer = window.setTimeout(() => {
      externalLibrarySyncStatus.value = {
        active: false,
        phase: 'idle',
        libraryId: '',
        provider: '',
        remoteTotal: 0,
        imported: 0,
        updated: 0,
        error: '',
      }
      externalLibrarySyncStatusTimer = null
    }, 8000)
  }
}

function applyExternalLibrarySyncResult(result: UnknownRecord | null | undefined, fallbackLibraryId = '') {
  setExternalLibrarySyncStatus({
    active: false,
    phase: 'ready',
    libraryId: result?.libraryId ?? fallbackLibraryId,
    provider: result?.connection?.provider ?? '',
    remoteTotal: result?.remoteTotal ?? 0,
    imported: result?.importedTracks?.length ?? 0,
    updated: result?.updatedTracks?.length ?? 0,
  })
}

async function handleConnectExternalLibrary(connection: UnknownRecord) {
  if (isConnectingExternalLibrary.value) {
    return
  }

  isConnectingExternalLibrary.value = true
  externalLibraryError.value = ''

  try {
    const result = await connectExternalLibrary(connection)
    applyExternalLibrarySyncResult(result?.sync, result?.library?.id ?? '')
    isExternalLibraryDialogOpen.value = false
  } catch (error) {
    externalLibraryError.value =
      error instanceof Error && error.message
        ? error.message
        : typeof error === 'string'
          ? error
          : t('externalLibrary.errorFallback')
  } finally {
    isConnectingExternalLibrary.value = false
  }
}

async function handleSyncExternalLibrary(libraryId: string) {
  setExternalLibrarySyncStatus({
    active: true,
    phase: 'syncing',
    libraryId,
  })

  try {
    const result = await syncExternalLibrary(libraryId)
    applyExternalLibrarySyncResult(result, libraryId)
  } catch (error) {
    const message =
      error instanceof Error && error.message
        ? error.message
        : typeof error === 'string'
          ? error
          : t('externalLibrary.errorFallback')

    setExternalLibrarySyncStatus({
      active: false,
      phase: 'error',
      libraryId,
      error: message,
    })

    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(message)
    }
  }
}

async function handleProbeRemoteLibrary(libraryId?: string) {
  const resolvedLibraryId = libraryId || activeLibraryItem.value?.id

  if (
    !resolvedLibraryId ||
    remoteLibraryProbeStatus.value.active ||
    (externalLibrarySyncStatus.value.active && externalLibrarySyncStatus.value.libraryId === resolvedLibraryId)
  ) {
    return
  }

  const rawProvider = activeLibraryItem.value?.source?.provider
  const provider = typeof rawProvider === 'string' ? rawProvider : ''

  setRemoteLibraryProbeStatus({
    active: true,
    phase: 'probing',
    libraryId: resolvedLibraryId,
    provider,
  })

  try {
    const result = await probeExternalLibrary(resolvedLibraryId)
    const rawResolvedProvider = result?.provider ?? result?.connection?.provider ?? provider
    const resolvedProvider = typeof rawResolvedProvider === 'string' ? rawResolvedProvider : provider

    if (result?.ok !== true) {
      throw new Error(result?.message || t('player.remoteProbe.errorFallback'))
    }

    if (REMOTE_PROBE_SYNC_PROVIDERS.has(resolvedProvider)) {
      setRemoteLibraryProbeStatus({
        active: true,
        phase: 'syncing',
        libraryId: resolvedLibraryId,
        provider: resolvedProvider,
        ok: true,
        checkedAt: new Date().toISOString(),
      })
      setExternalLibrarySyncStatus({
        active: true,
        phase: 'syncing',
        libraryId: resolvedLibraryId,
        provider: resolvedProvider,
      })

      const syncResult = await syncExternalLibrary(resolvedLibraryId)
      applyExternalLibrarySyncResult(syncResult, resolvedLibraryId)
      setRemoteLibraryProbeStatus({
        active: false,
        phase: 'ready',
        libraryId: resolvedLibraryId,
        provider: resolvedProvider,
        ok: true,
        synced: true,
        remoteTotal: syncResult?.remoteTotal ?? 0,
        checkedAt: new Date().toISOString(),
      })
      return
    }

    setRemoteLibraryProbeStatus({
      active: false,
      phase: 'ready',
      libraryId: resolvedLibraryId,
      provider: resolvedProvider,
      ok: true,
      checkedAt: new Date().toISOString(),
    })
  } catch (error) {
    const message = resolveExternalErrorMessage(error, t('player.remoteProbe.errorFallback'))

    setRemoteLibraryProbeStatus({
      active: false,
      phase: 'error',
      libraryId: resolvedLibraryId,
      provider,
      ok: false,
      checkedAt: new Date().toISOString(),
      error: message,
    })
    setExternalLibrarySyncStatus({
      active: false,
      phase: 'error',
      libraryId: resolvedLibraryId,
      provider,
      error: message,
    })
  }
}
</script>

<template>
  <div class="app-window">
    <WindowTitlebar v-if="showsCustomTitlebar" @request-close="requestCloseBehaviorDialog" />

    <main class="page-shell">
      <section :key="locale" class="player-shell">
        <LibraryPanel
          :libraries="navigation.libraries"
          :playlists="navigation.playlists"
          :smart-collections="navigation.smartCollections"
          :active-library="navigation.activeLibrary"
          :active-collection="activeCollectionItem?.key ?? activeCollection"
          :import-mode="importMode"
          :external-library-sync-status="externalLibrarySyncStatus"
          :license-feature-limits="licenseFeatureLimits"
          @request-import="requestImportFiles"
          @request-folder-import="requestImportFolder"
          @import-files="importFiles"
          @open-external-library="openExternalLibraryDialog"
          @sync-external-library="handleSyncExternalLibrary"
          @set-active-library="setActiveLibrary"
          @set-active-collection="setActiveCollection"
          @open-settings="openSettings"
          @open-listening-stats="openListeningStats"
          @create-library="createLibrary"
          @rename-library="renameLibrary"
          @delete-library="deleteLibrary"
          @create-playlist="createPlaylist"
          @rename-playlist="renamePlaylist"
          @delete-playlist="deletePlaylist"
        />

        <PlayerPanel
          :query-ready="isBootstrapReady"
          :collection-data-ready="activeCollectionDataReady"
          :collection-data-status="activeCollectionDataStatus"
          :collection-data-error="activeCollectionDataError"
          :query-revision="collectionQueryRevision"
          :tracks="tracks"
          :current-library="activeLibraryItem"
          :libraries="libraries"
          :playlists="playlists"
          :current-collection="activeCollectionItem"
          :remote-probe-status="activeRemoteProbeStatus"
          :current-track-id="currentTrackId"
          :current-track="currentTrack"
          :remote-track-status="currentRemoteTrackStatus"
          :has-any-tracks="hasTracks"
          :is-playing="isPlaying"
          :player-error="playerError"
          :current-time="currentTime"
          :duration="duration"
          :volume="volume"
          :repeat-mode="repeatMode"
          :shuffle-enabled="shuffleEnabled"
          :playback-signal-path="playbackSignalPath"
          :playback-output-devices="playbackOutputDevices"
          :playback-output-device-id="playbackOutputDeviceId"
          :active-playback-output-device-name="activePlaybackOutputDeviceName"
          :prefers-system-playback-output="prefersSystemPlaybackOutput"
          :playback-output-device-available="playbackOutputDeviceAvailable"
          :lyrics-snapshot="immersiveLyricsSnapshot"
          :lyrics-loading="immersiveLyricsLoading"
          :can-open-lyric-capsule-window="showsCustomTitlebar"
          :lyric-capsule-window-active="isLyricCapsuleWindowActive"
          :search-query="searchQuery"
          :sort-option="activeSortOption"
          :type-filter="typeFilter"
          :show-technical-metadata="showTechnicalMetadata"
          @select-track="selectTrack"
          @toggle-playback="togglePlayback"
          @play-previous="playPrevious"
          @play-next="playNext"
          @seek="seek"
          @set-volume="setVolume"
          @cycle-repeat-mode="cycleRepeatMode"
          @cycle-playback-mode="cyclePlaybackMode"
          @toggle-shuffle="toggleShuffle"
          @set-search-query="setSearchQuery"
          @set-sort-option="setSortOption"
          @set-type-filter="setTypeFilter"
          @probe-remote-library="handleProbeRemoteLibrary"
          @set-playback-output-device="setPlaybackOutputDevice"
          @refresh-playback-output-devices="refreshPlaybackOutputDevices"
          @add-track-to-playlist="handleAddTrackToPlaylist"
          @remove-track-from-playlist="removeTrackFromPlaylist"
          @reorder-tracks="reorderPlaylistTracks"
          @delete-track="deleteTrackFromLibrary"
          @delete-tracks="deleteTracksFromLibrary"
          @toggle-favorite="toggleFavorite"
          @hydrate-track-artwork="hydrateTrackArtwork"
          @bind-lyrics-file="bindLyricsFile"
          @clear-lyrics-binding="clearLyricsBinding"
          @refresh-lyrics="refreshCurrentTrackLyrics"
          @open-immersive-player="openImmersivePlayer"
          @toggle-lyric-capsule-window="toggleLyricCapsuleWindow"
        />
      </section>

      <Teleport to="body">
        <Transition
          name="immersive"
          @after-leave="finishImmersiveCloseTransition"
          @leave-cancelled="clearImmersiveTransitionState"
        >
          <ImmersivePlayerView
            v-if="isImmersivePlayerOpen"
            :current-track="currentTrack"
            :is-playing="isPlaying"
            :current-time="currentTime"
            :duration="duration"
            :volume="volume"
            :repeat-mode="repeatMode"
            :shuffle-enabled="shuffleEnabled"
            :lyrics="immersiveLyricsSnapshot"
            :lyrics-active-index="immersiveLyricsActiveIndex"
            :lyrics-has-timestamps="immersiveLyricsHasTimestamps"
            :lyrics-loading="immersiveLyricsLoading"
            :windowed="isImmersiveWindowed"
            :taskbar-mode="immersiveTaskbarMode"
            @close="closeImmersivePlayer"
            @toggle-taskbar-mode="toggleImmersiveTaskbarMode"
            @toggle-playback="togglePlayback"
            @play-previous="playPrevious"
            @play-next="playNext"
            @seek="seek"
            @set-volume="setVolume"
            @cycle-repeat-mode="cycleRepeatMode"
            @toggle-shuffle="toggleShuffle"
            @toggle-favorite="toggleFavorite"
            @bind-lyrics-file="bindLyricsFile"
            @clear-lyrics-binding="clearLyricsBinding"
          />
        </Transition>
      </Teleport>

      <SettingsModal
        :is-open="isSettingsOpen"
        :language="language"
        :theme="theme"
        :color-scheme="colorScheme"
        :motion="motion"
        :window-effects="windowEffects"
        :immersive-taskbar-mode="immersiveTaskbarMode"
        :sort-option="sortOption"
        :remember-volume="rememberVolume"
        :is-playing="isPlaying"
        :playback-output-devices="playbackOutputDevices"
        :playback-output-device-id="playbackOutputDeviceId"
        :active-playback-output-device-name="activePlaybackOutputDeviceName"
        :prefers-system-playback-output="prefersSystemPlaybackOutput"
        :playback-output-device-available="playbackOutputDeviceAvailable"
        :show-technical-metadata="showTechnicalMetadata"
        :storage-root="storageRoot"
        :scan-directories="scanDirectories"
        :lyrics-scan-directories="lyricsScanDirectories"
        :auto-scan-on-launch="autoScanOnLaunch"
        :last-scan-at="lastScanAt"
        :scan-progress="scanProgress"
        :is-resetting-data="isResettingData"
        :storage-usage="storageUsage"
        :is-loading-storage-usage="isLoadingStorageUsage"
        :is-collecting-garbage="isCollectingGarbage"
        :storage-maintenance-error="storageMaintenanceError"
        :can-manage-storage="canManageStorage"
        :telemetry-enabled="telemetryEnabled"
        :is-uploading-diagnostics-report="isUploadingDiagnosticsReport"
        :diagnostics-report-status="diagnosticsReportStatus"
        :app-update-state="appUpdateState"
        :initial-category="settingsInitialCategory"
        :notice="settingsNotice"
        @close="closeSettings"
        @set-language="setLanguage"
        @set-theme="setTheme"
        @set-color-scheme="setColorScheme"
        @set-motion="setMotion"
        @set-window-effects="setWindowEffects"
        @set-immersive-taskbar-mode="setImmersiveTaskbarModeAndApply"
        @set-sort-option="setSortOption"
        @set-remember-volume="setRememberVolume"
        @set-playback-output-device="setPlaybackOutputDevice"
        @refresh-playback-output-devices="refreshPlaybackOutputDevices"
        @set-show-technical-metadata="setShowTechnicalMetadata"
        @select-storage-root="selectStorageRoot"
        @add-scan-directory="addScanDirectory"
        @remove-scan-directory="removeScanDirectory"
        @add-lyrics-scan-directory="addLyricsScanDirectory"
        @remove-lyrics-scan-directory="removeLyricsScanDirectory"
        @set-auto-scan-on-launch="setAutoScanOnLaunch"
        @run-library-scan-import="runLibraryScanImport({ interactive: true })"
        @refresh-storage-usage="refreshStorageUsage"
        @collect-storage-garbage="handleCollectStorageGarbage"
        @reset-all-data="handleResetAllData"
        @reopen-onboarding="reopenOnboarding"
        @set-telemetry-consent="handleTelemetryConsent"
        @upload-diagnostics-report="handleUploadDiagnosticsReport"
        @check-app-update="handleCheckForUpdates"
        @download-and-install-update="handleDownloadAndInstallUpdate"
      />

      <ListeningStatsPanel
        :is-open="isListeningStatsOpen"
        :library-id="activeLibraryItem?.id ?? activeLibrary"
        :library-name="activeLibraryItem?.label ?? ''"
        :revision="historyRevision"
        :load-stats="handleLoadListeningStats"
        @close="closeListeningStats"
        @select-track="handleSelectStatsTrack"
      />

      <ExternalLibraryDialog
        :is-open="isExternalLibraryDialogOpen"
        :is-connecting="isConnectingExternalLibrary"
        :error="externalLibraryError"
        @close="closeExternalLibraryDialog"
        @connect="handleConnectExternalLibrary"
      />

      <OnboardingGuide v-if="isOnboardingOpen" @close="closeOnboarding" />

      <TelemetryConsentDialog
        v-if="!isOnboardingOpen && telemetryEnabled === null"
        @accept="handleTelemetryConsent(true)"
        @decline="handleTelemetryConsent(false)"
      />

      <CloseBehaviorDialog
        :is-open="isCloseBehaviorDialogOpen"
        :capsule-active="isLyricCapsuleWindowActive"
        :is-busy="isResolvingCloseBehavior"
        @close="closeCloseBehaviorDialog"
        @minimize="minimizePlayerToBackground"
        @quit="quitFromCloseBehaviorDialog"
      />
    </main>
  </div>
</template>
