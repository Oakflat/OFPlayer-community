import { computed, ref } from 'vue'
import { invoke, isTauri } from '@tauri-apps/api/core'
import { listen, type Event as TauriEvent, type UnlistenFn } from '@tauri-apps/api/event'
import {
  clampTime,
  clampVolume,
  createPlaybackStateModel,
  isPlayingStatus,
  PLAYBACK_STATUS,
} from '../models/playback'
import {
  logDiagnosticsError as logDiagnosticsErrorRaw,
  logDiagnosticsInfo as logDiagnosticsInfoRaw,
  logDiagnosticsWarn as logDiagnosticsWarnRaw,
} from '../services/diagnosticsLogger'

const PLAYBACK_SNAPSHOT_EVENT = 'playback://snapshot'
const PLAYBACK_AUDIO_LEVELS_EVENT = 'playback://audio-levels'
const TAURI_RUNTIME_REQUIRED_MESSAGE =
  'OFPlayer Tauri playback is only available inside the desktop runtime.'
const NO_TRACK_LOADED_MESSAGE = 'No track has been loaded into the Rust playback backend.'
const MISSING_NATIVE_SOURCE_MESSAGE =
  'This track does not have an indexed local path. Re-scan its source folder before playing it.'
const DEVICE_CHANGE_RECOVERY_FAILURE_MESSAGE =
  'OFPlayer could not safely reconnect playback after the system audio output changed.'
const OUTPUT_DEVICE_ENUMERATION_FAILURE_MESSAGE =
  'OFPlayer could not list audio output devices from the Rust playback backend.'
const OUTPUT_DEVICE_SWITCH_FAILURE_MESSAGE =
  'OFPlayer could not switch to the requested audio output device.'
const DEVICE_CHANGE_RECOVERY_DEBOUNCE_MS = 180
const MAX_SYSTEM_MEDIA_EMBEDDED_COVER_BYTES = 768 * 1024

type UnknownRecord = Record<string, unknown>
type PlaybackStatus = string
type DiagnosticsLogger = (
  label: string,
  category: string,
  event: string,
  payload?: unknown,
) => unknown

interface PlaybackSignalFormat {
  sampleRate: number
  channels: number
  bitDepth: number
  sampleFormat: string
}

interface PlaybackSignalPath {
  source: PlaybackSignalFormat
  output: PlaybackSignalFormat
  resampled: boolean
  channelConverted: boolean
  sampleFormatConverted: boolean
  softwareMixer: boolean
  softwareVolume: boolean
  bitPerfect: boolean
  integrityStatus: string
}

interface PlaybackSnapshot {
  status: PlaybackStatus
  currentTime: number
  duration: number
  volume: number
  activeTrackId: string | null
  error: Error | null
  endedCounter: number
  endedTrackId: string | null
  signalPath: PlaybackSignalPath | null
  audioLevels: number[]
}

interface AudioLevelsSnapshot {
  activeTrackId: string | null
  isPlaying: boolean
  audioLevels: number[]
}

interface OutputDevice {
  id: string
  name: string
  backend: string
  backendLabel: string
  isDefault: boolean
}

interface OutputDevicesSnapshot {
  devices: OutputDevice[]
  preferredDeviceId: string
  activeDeviceId: string
  activeDeviceName: string
  prefersSystemDefault: boolean
  preferredDeviceAvailable: boolean
}

interface NativeAudioTrack {
  id?: string | null
  title?: string | null
  displayTitle?: string | null
  fileName?: string | null
  artist?: string | null
  albumArtist?: string | null
  album?: string | null
  artwork?: string | null
  duration?: number | null
  sampleRate?: number | null
  bitDepth?: number | null
  source?: {
    path?: string | null
    deleteOnRelease?: boolean
    transient?: boolean
    kind?: string
  } | null
}

export interface NativeAudioPlayerOptions {
  enableAudioLevels?: boolean
  initialVolume?: number
  onTrackEnded?: (payload: { trackId: string | null }) => void
  onTrackDurationChange?: (payload: { trackId: string; duration: number }) => void
}

interface LoadTrackOptions {
  autoplay?: boolean
  startTime?: number
}

interface OutputDevicesApplyOptions {
  reason?: string
  shouldLog?: boolean
}

const logDiagnosticsInfo = logDiagnosticsInfoRaw as DiagnosticsLogger
const logDiagnosticsWarn = logDiagnosticsWarnRaw as DiagnosticsLogger
const logDiagnosticsError = logDiagnosticsErrorRaw as DiagnosticsLogger

function asRecord(value: unknown): UnknownRecord {
  return value && typeof value === 'object' ? value as UnknownRecord : {}
}

function resolveStatus(status: unknown): PlaybackStatus {
  const supportedStatuses = Object.values(PLAYBACK_STATUS) as string[]
  return typeof status === 'string' && supportedStatuses.includes(status) ? status : PLAYBACK_STATUS.IDLE
}

function normalizeSnapshot(snapshot: unknown = {}): PlaybackSnapshot {
  const rawSnapshot = asRecord(snapshot)
  return {
    status: resolveStatus(rawSnapshot.status),
    currentTime: clampTime(rawSnapshot.currentTime),
    duration: clampTime(rawSnapshot.duration),
    volume: clampVolume(rawSnapshot.volume),
    activeTrackId: typeof rawSnapshot.activeTrackId === 'string' ? rawSnapshot.activeTrackId : null,
    error: typeof rawSnapshot.error === 'string' && rawSnapshot.error ? new Error(rawSnapshot.error) : null,
    endedCounter:
      Number.isInteger(rawSnapshot.endedCounter) && Number(rawSnapshot.endedCounter) >= 0
        ? Number(rawSnapshot.endedCounter)
        : 0,
    endedTrackId: typeof rawSnapshot.endedTrackId === 'string' ? rawSnapshot.endedTrackId : null,
    signalPath: normalizeSignalPath(rawSnapshot.signalPath),
    audioLevels: normalizeAudioLevels(rawSnapshot.audioLevels),
  }
}

function normalizeAudioLevels(levels: unknown): number[] {
  if (!Array.isArray(levels)) {
    return []
  }

  return levels
    .map((level) => {
      const numericLevel = Number(level)
      return Number.isFinite(numericLevel) ? Math.max(0, Math.min(1, numericLevel)) : 0
    })
    .slice(0, 12)
}

function normalizeAudioLevelsSnapshot(snapshot: unknown = {}): AudioLevelsSnapshot {
  const rawSnapshot = asRecord(snapshot)
  return {
    activeTrackId: typeof rawSnapshot.activeTrackId === 'string' ? rawSnapshot.activeTrackId : null,
    isPlaying: rawSnapshot.isPlaying === true,
    audioLevels: normalizeAudioLevels(rawSnapshot.audioLevels),
  }
}

function createRequestError(error: unknown, fallbackMessage: string): Error {
  if (error instanceof Error) {
    return error
  }

  if (typeof error === 'string' && error) {
    return new Error(error)
  }

  return new Error(fallbackMessage)
}

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeOutputDevice(device: unknown = {}): OutputDevice | null {
  const rawDevice = asRecord(device)
  const id = normalizeText(rawDevice.id)
  const name = normalizeText(rawDevice.name, id || 'Unknown output')

  if (!id) {
    return null
  }

  return {
    id,
    name,
    backend: normalizeText(rawDevice.backend),
    backendLabel: normalizeText(rawDevice.backendLabel),
    isDefault: rawDevice.isDefault === true,
  }
}

function normalizeOutputDevicesSnapshot(snapshot: unknown = {}): OutputDevicesSnapshot {
  const rawSnapshot = asRecord(snapshot)
  const rawDevices = Array.isArray(rawSnapshot.devices) ? rawSnapshot.devices : []
  const devices = rawDevices
    .map((device) => normalizeOutputDevice(device))
    .filter((device): device is OutputDevice => Boolean(device))
  const preferredDeviceId = normalizeText(rawSnapshot.preferredDeviceId)
  const activeDeviceId = normalizeText(rawSnapshot.activeDeviceId)

  return {
    devices,
    preferredDeviceId,
    activeDeviceId,
    activeDeviceName: normalizeText(rawSnapshot.activeDeviceName),
    prefersSystemDefault: rawSnapshot.prefersSystemDefault !== false,
    preferredDeviceAvailable:
      typeof rawSnapshot.preferredDeviceAvailable === 'boolean'
        ? rawSnapshot.preferredDeviceAvailable
        : true,
  }
}

function normalizeSignalFormat(format: unknown = {}): PlaybackSignalFormat {
  const rawFormat = asRecord(format)
  const sampleRate = Number(rawFormat.sampleRate)
  const channels = Number(rawFormat.channels)
  const bitDepth = Number(rawFormat.bitDepth)
  const sampleFormat = normalizeText(rawFormat.sampleFormat)

  return {
    sampleRate: Number.isFinite(sampleRate) && sampleRate > 0 ? sampleRate : 0,
    channels: Number.isFinite(channels) && channels > 0 ? channels : 0,
    bitDepth: Number.isFinite(bitDepth) && bitDepth > 0 ? bitDepth : 0,
    sampleFormat,
  }
}

function normalizeSignalPath(signalPath: unknown = null): PlaybackSignalPath | null {
  if (!signalPath || typeof signalPath !== 'object') {
    return null
  }

  const rawSignalPath = asRecord(signalPath)
  const source = normalizeSignalFormat(rawSignalPath.source)
  const output = normalizeSignalFormat(rawSignalPath.output)

  if (source.sampleRate <= 0 && output.sampleRate <= 0) {
    return null
  }

  return {
    source,
    output,
    resampled: rawSignalPath.resampled === true,
    channelConverted: rawSignalPath.channelConverted === true,
    sampleFormatConverted: rawSignalPath.sampleFormatConverted === true,
    softwareMixer: rawSignalPath.softwareMixer === true,
    softwareVolume: rawSignalPath.softwareVolume === true,
    bitPerfect: rawSignalPath.bitPerfect === true,
    integrityStatus: normalizeText(rawSignalPath.integrityStatus, 'unknown'),
  }
}

function resolveSystemMediaCoverUrl(track: NativeAudioTrack | null | undefined): string {
  const artwork = normalizeText(track?.artwork)

  if (!artwork) {
    return ''
  }

  if (artwork.startsWith('data:') && artwork.length > MAX_SYSTEM_MEDIA_EMBEDDED_COVER_BYTES) {
    return ''
  }

  return artwork
}

function buildSystemMediaMetadata(track: NativeAudioTrack | null | undefined) {
  return {
    title: normalizeText(track?.title, normalizeText(track?.displayTitle, normalizeText(track?.fileName, 'Untitled'))),
    artist: normalizeText(track?.artist, normalizeText(track?.albumArtist)),
    album: normalizeText(track?.album),
    coverUrl: resolveSystemMediaCoverUrl(track),
  }
}

async function invokePlayback<T = unknown>(command: string, args?: Record<string, unknown>): Promise<T> {
  return invoke<T>(command, args)
}

export function useNativeAudioPlayer(options: NativeAudioPlayerOptions = {}) {
  const enableAudioLevels = options.enableAudioLevels === true
  const playbackState = createPlaybackStateModel({
    volume: options.initialVolume,
  })

  const status = ref(playbackState.status)
  const currentTime = ref(playbackState.currentTime)
  const duration = ref(playbackState.duration)
  const volume = ref(playbackState.volume)
  const activeTrackId = ref<string | null>(playbackState.activeTrackId)
  const error = ref<Error | null>(playbackState.error instanceof Error ? playbackState.error : null)
  const signalPath = ref<PlaybackSignalPath | null>(null)
  const audioLevels = ref<number[]>([])
  const backendReady = ref(isTauri())
  const outputDevices = ref<OutputDevice[]>([])
  const preferredOutputDeviceId = ref('')
  const activeOutputDeviceId = ref('')
  const activeOutputDeviceName = ref('')
  const prefersSystemOutputDevice = ref(true)
  const preferredOutputDeviceAvailable = ref(true)

  let endedCounter = 0
  let unlistenPlaybackSnapshots: UnlistenFn | null = null
  let unlistenPlaybackAudioLevels: UnlistenFn | null = null
  let playbackSnapshotListenerPromise: Promise<void> | null = null
  let playbackAudioLevelsListenerPromise: Promise<void> | null = null
  let recoverOutputPromise: Promise<boolean> | null = null
  let pendingDeviceChangeRecoveryTimer: ReturnType<typeof setTimeout> | null = null
  let detachDeviceChangeListener: (() => void) | null = null
  let lastOutputDevicesSignature = ''
  let lastSignalPathSignature = ''
  let seekRequestRevision = 0
  let disposed = false

  function createOutputDevicesPayload(normalized: OutputDevicesSnapshot, extra: UnknownRecord = {}) {
    return {
      preferredDeviceId: normalized.preferredDeviceId || null,
      activeDeviceId: normalized.activeDeviceId || null,
      activeDeviceName: normalized.activeDeviceName || null,
      prefersSystemDefault: normalized.prefersSystemDefault,
      preferredDeviceAvailable: normalized.preferredDeviceAvailable,
      deviceCount: normalized.devices.length,
      devices: normalized.devices.map((device) => ({
        id: device.id,
        name: device.name,
        backend: device.backend || null,
        isDefault: device.isDefault,
      })),
      ...extra,
    }
  }

  function clearLocalState({ clearError = true }: { clearError?: boolean } = {}) {
    endedCounter = 0
    status.value = PLAYBACK_STATUS.IDLE
    currentTime.value = 0
    duration.value = 0
    activeTrackId.value = null
    signalPath.value = null
    audioLevels.value = []
    lastSignalPathSignature = ''

    if (clearError) {
      error.value = null
    }
  }

  async function resetBackend() {
    if (!backendReady.value) {
      return
    }

    try {
      await invokePlayback('playback_reset')
    } catch {
      // Ignore reset failures during backend teardown and keep the UI consistent.
    }
  }

  async function clearPlaybackSession({ clearError = true }: { clearError?: boolean } = {}) {
    clearLocalState({ clearError })
    await resetBackend()
  }

  function applySnapshot(snapshot: unknown): PlaybackSnapshot {
    const normalized = normalizeSnapshot(snapshot)

    if (disposed) {
      return normalized
    }

    const previousDuration = duration.value
    status.value = normalized.status
    currentTime.value = normalized.currentTime
    duration.value = normalized.duration
    volume.value = normalized.volume
    activeTrackId.value = normalized.activeTrackId
    error.value = normalized.error
    signalPath.value = normalized.signalPath
    if (enableAudioLevels) {
      audioLevels.value = normalized.audioLevels
    }

    const nextSignalPathSignature = normalized.signalPath ? JSON.stringify(normalized.signalPath) : ''
    if (nextSignalPathSignature && nextSignalPathSignature !== lastSignalPathSignature) {
      void logDiagnosticsInfo('[OFPlayer playback signal path]', 'playback', 'signal_path_snapshot', {
        activeTrackId: normalized.activeTrackId,
        signalPath: normalized.signalPath,
      })
    }
    lastSignalPathSignature = nextSignalPathSignature

    if (normalized.endedCounter > endedCounter) {
      endedCounter = normalized.endedCounter
      options.onTrackEnded?.({
        trackId: normalized.endedTrackId ?? normalized.activeTrackId,
      })
    }

    if (
      normalized.activeTrackId &&
      normalized.duration > 0 &&
      Math.abs(normalized.duration - previousDuration) > 0.25
    ) {
      options.onTrackDurationChange?.({
        trackId: normalized.activeTrackId,
        duration: normalized.duration,
      })
    }

    return normalized
  }

  function applyAudioLevelsSnapshot(snapshot: unknown): AudioLevelsSnapshot {
    const normalized = normalizeAudioLevelsSnapshot(snapshot)

    if (disposed || !enableAudioLevels) {
      return normalized
    }

    if (!normalized.activeTrackId) {
      audioLevels.value = []
      return normalized
    }

    if (activeTrackId.value && normalized.activeTrackId !== activeTrackId.value) {
      return normalized
    }

    audioLevels.value = normalized.audioLevels
    return normalized
  }

  function applyOutputDevicesSnapshot(
    snapshot: unknown,
    { reason = 'sync', shouldLog = false }: OutputDevicesApplyOptions = {},
  ): OutputDevicesSnapshot {
    const normalized = normalizeOutputDevicesSnapshot(snapshot)

    if (disposed) {
      return normalized
    }

    outputDevices.value = normalized.devices
    preferredOutputDeviceId.value = normalized.preferredDeviceId
    activeOutputDeviceId.value = normalized.activeDeviceId
    activeOutputDeviceName.value =
      normalized.activeDeviceName ||
      normalized.devices.find((device) => device.id === normalized.activeDeviceId)?.name ||
      ''
    prefersSystemOutputDevice.value = normalized.prefersSystemDefault
    preferredOutputDeviceAvailable.value = normalized.preferredDeviceAvailable
    const nextSignature = JSON.stringify({
      preferredDeviceId: normalized.preferredDeviceId,
      activeDeviceId: normalized.activeDeviceId,
      prefersSystemDefault: normalized.prefersSystemDefault,
      preferredDeviceAvailable: normalized.preferredDeviceAvailable,
      devices: normalized.devices.map((device) => ({
        id: device.id,
        backend: device.backend || null,
        isDefault: device.isDefault,
      })),
    })

    if (shouldLog && nextSignature !== lastOutputDevicesSignature) {
      const payload = createOutputDevicesPayload(normalized, { reason })

      if (!normalized.preferredDeviceAvailable && normalized.preferredDeviceId) {
        void logDiagnosticsWarn(
          '[OFPlayer playback devices]',
          'playback',
          'output_device_snapshot_unavailable',
          payload,
        )
      } else {
        void logDiagnosticsInfo(
          '[OFPlayer playback devices]',
          'playback',
          'output_device_snapshot',
          payload,
        )
      }
    }

    lastOutputDevicesSignature = nextSignature
    return normalized
  }

  async function syncSnapshot() {
    if (!backendReady.value || disposed) {
      return null
    }

    try {
      const snapshot = await invokePlayback<UnknownRecord>('playback_snapshot')
      if (disposed) {
        return null
      }
      return applySnapshot(snapshot)
    } catch (snapshotError) {
      error.value = createRequestError(snapshotError, 'Failed to read playback state from Rust backend.')
      return null
    }
  }

  async function refreshOutputDevices(options: { reason?: string } = {}) {
    const { reason = 'manual' } = options

    if (disposed) {
      return null
    }

    if (!backendReady.value) {
      outputDevices.value = []
      preferredOutputDeviceId.value = ''
      activeOutputDeviceId.value = ''
      activeOutputDeviceName.value = ''
      prefersSystemOutputDevice.value = true
      preferredOutputDeviceAvailable.value = true
      lastOutputDevicesSignature = ''
      return null
    }

    try {
      const snapshot = await invokePlayback<UnknownRecord>('playback_list_output_devices')
      if (disposed) {
        return null
      }
      return applyOutputDevicesSnapshot(snapshot, {
        reason,
        shouldLog: true,
      })
    } catch (deviceError) {
      error.value = createRequestError(
        deviceError,
        OUTPUT_DEVICE_ENUMERATION_FAILURE_MESSAGE,
      )
      void logDiagnosticsError(
        '[OFPlayer playback devices]',
        'playback',
        'output_device_enumeration_failed',
        {
          reason,
          error: deviceError,
        },
      )
      return null
    }
  }

  async function ensurePlaybackSnapshotListener() {
    if (!backendReady.value || disposed) {
      return
    }

    if (typeof unlistenPlaybackSnapshots === 'function') {
      return
    }

    if (playbackSnapshotListenerPromise) {
      await playbackSnapshotListenerPromise
      return
    }

    playbackSnapshotListenerPromise = listen(PLAYBACK_SNAPSHOT_EVENT, (event: TauriEvent<unknown>) => {
      if (!disposed) {
        applySnapshot(event.payload ?? {})
      }
    })
      .then((unlisten) => {
        if (disposed) {
          unlisten()
          return
        }

        unlistenPlaybackSnapshots = unlisten
      })
      .finally(() => {
        playbackSnapshotListenerPromise = null
      })

    await playbackSnapshotListenerPromise
  }

  async function ensurePlaybackAudioLevelsListener() {
    if (!backendReady.value || disposed) {
      return
    }

    if (typeof unlistenPlaybackAudioLevels === 'function') {
      return
    }

    if (playbackAudioLevelsListenerPromise) {
      await playbackAudioLevelsListenerPromise
      return
    }

    playbackAudioLevelsListenerPromise = listen(PLAYBACK_AUDIO_LEVELS_EVENT, (event: TauriEvent<unknown>) => {
      if (!disposed) {
        applyAudioLevelsSnapshot(event.payload ?? {})
      }
    })
      .then((unlisten) => {
        if (disposed) {
          unlisten()
          return
        }

        unlistenPlaybackAudioLevels = unlisten
      })
      .finally(() => {
        playbackAudioLevelsListenerPromise = null
      })

    await playbackAudioLevelsListenerPromise
  }

  function stopPlaybackSnapshotListener() {
    if (typeof unlistenPlaybackSnapshots !== 'function') {
      return
    }

    unlistenPlaybackSnapshots()
    unlistenPlaybackSnapshots = null
  }

  function stopPlaybackAudioLevelsListener() {
    if (typeof unlistenPlaybackAudioLevels !== 'function') {
      return
    }

    unlistenPlaybackAudioLevels()
    unlistenPlaybackAudioLevels = null
  }

  async function recoverOutputAfterDeviceChange() {
    if (!backendReady.value || disposed || !activeTrackId.value) {
      return false
    }

    if (recoverOutputPromise) {
      return recoverOutputPromise
    }

    void logDiagnosticsInfo('[OFPlayer playback recovery]', 'playback', 'output_device_recovery_started', {
      activeTrackId: activeTrackId.value,
      preferredDeviceId: preferredOutputDeviceId.value || null,
      activeDeviceId: activeOutputDeviceId.value || null,
    })

    recoverOutputPromise = invokePlayback<UnknownRecord>('playback_recover_output')
      .then(async (snapshot) => {
        if (disposed) {
          return false
        }

        applySnapshot(snapshot)
        const devices = await refreshOutputDevices({ reason: 'devicechange-recovery' })
        if (disposed) {
          return false
        }

        void logDiagnosticsInfo('[OFPlayer playback recovery]', 'playback', 'output_device_recovery_succeeded', {
          activeTrackId: activeTrackId.value,
          playbackStatus: snapshot?.status ?? null,
          currentTime: snapshot?.currentTime ?? null,
          activeDeviceId: devices?.activeDeviceId ?? activeOutputDeviceId.value ?? null,
          preferredDeviceId: devices?.preferredDeviceId ?? preferredOutputDeviceId.value ?? null,
        })
        return true
      })
      .catch((recoverError) => {
        error.value = createRequestError(
          recoverError,
          DEVICE_CHANGE_RECOVERY_FAILURE_MESSAGE,
        )
        void logDiagnosticsError('[OFPlayer playback recovery]', 'playback', 'output_device_recovery_failed', {
          activeTrackId: activeTrackId.value,
          preferredDeviceId: preferredOutputDeviceId.value || null,
          activeDeviceId: activeOutputDeviceId.value || null,
          error: recoverError,
        })
        return false
      })
      .finally(() => {
        recoverOutputPromise = null
      })

    return recoverOutputPromise
  }

  function clearPendingDeviceChangeRecovery() {
    if (pendingDeviceChangeRecoveryTimer == null) {
      return
    }

    clearTimeout(pendingDeviceChangeRecoveryTimer)
    pendingDeviceChangeRecoveryTimer = null
  }

  function stopDeviceChangeListener() {
    clearPendingDeviceChangeRecovery()

    if (typeof detachDeviceChangeListener !== 'function') {
      return
    }

    detachDeviceChangeListener()
    detachDeviceChangeListener = null
  }

  function ensureDeviceChangeListener() {
    if (!backendReady.value || disposed || typeof navigator === 'undefined') {
      return
    }

    if (typeof detachDeviceChangeListener === 'function') {
      return
    }

    const mediaDevices = navigator.mediaDevices

    if (!mediaDevices || typeof mediaDevices.addEventListener !== 'function') {
      return
    }

    const handleDeviceChange = () => {
      if (disposed) {
        return
      }

      void logDiagnosticsInfo('[OFPlayer playback devices]', 'playback', 'output_device_change_detected', {
        activeTrackId: activeTrackId.value,
        preferredDeviceId: preferredOutputDeviceId.value || null,
        activeDeviceId: activeOutputDeviceId.value || null,
      })

      clearPendingDeviceChangeRecovery()
      pendingDeviceChangeRecoveryTimer = setTimeout(() => {
        pendingDeviceChangeRecoveryTimer = null

        if (disposed) {
          return
        }

        if (!activeTrackId.value) {
          void refreshOutputDevices({ reason: 'devicechange-idle' })
          return
        }

        void recoverOutputAfterDeviceChange()
      }, DEVICE_CHANGE_RECOVERY_DEBOUNCE_MS)
    }

    mediaDevices.addEventListener('devicechange', handleDeviceChange)
    detachDeviceChangeListener = () => {
      mediaDevices.removeEventListener('devicechange', handleDeviceChange)
    }
  }

  async function loadTrack(track: NativeAudioTrack | null | undefined, loadOptions: LoadTrackOptions = {}) {
    if (disposed) {
      return false
    }

    if (!backendReady.value) {
      await clearPlaybackSession({ clearError: false })
      error.value = new Error(TAURI_RUNTIME_REQUIRED_MESSAGE)
      return false
    }

    const sourcePath = track?.source?.path

    if (!track?.id) {
      await clearPlaybackSession({ clearError: false })
      error.value = new Error(NO_TRACK_LOADED_MESSAGE)
      return false
    }

    if (typeof sourcePath !== 'string' || sourcePath.length === 0) {
      await clearPlaybackSession({ clearError: false })
      error.value = new Error(MISSING_NATIVE_SOURCE_MESSAGE)
      return false
    }

    try {
      await ensurePlaybackSnapshotListener()
      if (disposed) {
        return false
      }

      const sampleRate = Number(track.sampleRate)
      const bitDepth = Number(track.bitDepth)
      const snapshot = await invokePlayback<UnknownRecord>('playback_load_track', {
        request: {
          trackId: track.id,
          path: sourcePath,
          autoplay: loadOptions.autoplay === true,
          startTime: clampTime(loadOptions.startTime),
          durationHint: Number.isFinite(track.duration) ? track.duration : null,
          sampleRate: Number.isFinite(sampleRate) && sampleRate > 0 ? sampleRate : null,
          bitDepth: Number.isFinite(bitDepth) && bitDepth > 0 ? bitDepth : null,
          volume: volume.value,
          deleteOnRelease:
            track?.source?.deleteOnRelease === true ||
            track?.source?.transient === true ||
            track?.source?.kind === 'external-temp',
          media: buildSystemMediaMetadata(track),
        },
      })

      if (disposed) {
        return false
      }

      const normalized = applySnapshot(snapshot)
      endedCounter = normalized.endedCounter
      return true
    } catch (loadError) {
      await clearPlaybackSession({ clearError: false })
      error.value = createRequestError(loadError, 'Failed to load track with Rust backend.')
      return false
    }
  }

  async function play() {
    if (disposed) {
      return false
    }

    if (!backendReady.value) {
      error.value = new Error(TAURI_RUNTIME_REQUIRED_MESSAGE)
      return false
    }

    if (!activeTrackId.value) {
      error.value = new Error(NO_TRACK_LOADED_MESSAGE)
      return false
    }

    try {
      await ensurePlaybackSnapshotListener()
      if (disposed) {
        return false
      }

      const snapshot = await invokePlayback<UnknownRecord>('playback_play')
      if (disposed) {
        return false
      }
      applySnapshot(snapshot)
      return isPlayingStatus(resolveStatus(snapshot?.status))
    } catch (playError) {
      error.value = createRequestError(playError, 'Failed to start playback with Rust backend.')
      return false
    }
  }

  async function pause() {
    if (!backendReady.value || disposed) {
      return
    }

    try {
      const snapshot = await invokePlayback<UnknownRecord>('playback_pause')
      if (disposed) {
        return
      }
      applySnapshot(snapshot)
    } catch (pauseError) {
      error.value = createRequestError(pauseError, 'Failed to pause Rust playback.')
    }
  }

  async function toggle() {
    if (isPlaying.value) {
      await pause()
      return true
    }

    return play()
  }

  async function seek(nextTime: number) {
    const safeTime = clampTime(nextTime)
    const requestRevision = ++seekRequestRevision

    if (disposed) {
      return safeTime
    }

    if (!backendReady.value || !activeTrackId.value) {
      currentTime.value = safeTime
      return safeTime
    }

    currentTime.value = safeTime

    try {
      const snapshot = await invokePlayback<UnknownRecord>('playback_seek', {
        request: {
          seconds: safeTime,
        },
      })
      if (disposed) {
        return safeTime
      }

      if (requestRevision === seekRequestRevision) {
        applySnapshot(snapshot)
      }

      return clampTime(snapshot?.currentTime ?? safeTime)
    } catch (seekError) {
      error.value = createRequestError(seekError, 'Failed to seek Rust playback.')
      return safeTime
    }
  }

  async function setVolume(nextVolume: number) {
    const safeVolume = clampVolume(nextVolume, volume.value)

    if (disposed) {
      return safeVolume
    }

    volume.value = safeVolume

    if (!backendReady.value) {
      return safeVolume
    }

    try {
      const snapshot = await invokePlayback<UnknownRecord>('playback_set_volume', {
        request: {
          volume: safeVolume,
        },
      })
      if (disposed) {
        return safeVolume
      }
      applySnapshot(snapshot)
      return clampVolume(snapshot?.volume, safeVolume)
    } catch (volumeError) {
      error.value = createRequestError(volumeError, 'Failed to update Rust playback volume.')
      return safeVolume
    }
  }

  async function setOutputDevicePreference(nextDeviceId: string | null | undefined) {
    const safeDeviceId = normalizeText(nextDeviceId)

    if (disposed) {
      return safeDeviceId
    }

    if (!backendReady.value) {
      preferredOutputDeviceId.value = safeDeviceId
      prefersSystemOutputDevice.value = safeDeviceId.length === 0
      return safeDeviceId
    }

    try {
      const result = await invokePlayback<UnknownRecord>('playback_set_output_device', {
        request: {
          deviceId: safeDeviceId || null,
        },
      })

      if (disposed) {
        return safeDeviceId
      }

      const resultDevices = asRecord(result?.devices)
      applySnapshot(result?.playback ?? {})
      const normalizedDevices = applyOutputDevicesSnapshot(resultDevices, {
        reason: 'manual-switch',
        shouldLog: true,
      })
      void logDiagnosticsInfo('[OFPlayer playback devices]', 'playback', 'output_device_selected', {
        requestedDeviceId: safeDeviceId || null,
        activeTrackId: activeTrackId.value,
        ...createOutputDevicesPayload(normalizedDevices),
      })
      return normalizeText(resultDevices.preferredDeviceId, safeDeviceId)
    } catch (deviceError) {
      error.value = createRequestError(
        deviceError,
        OUTPUT_DEVICE_SWITCH_FAILURE_MESSAGE,
      )
      void logDiagnosticsError('[OFPlayer playback devices]', 'playback', 'output_device_selection_failed', {
        requestedDeviceId: safeDeviceId || null,
        activeTrackId: activeTrackId.value,
        currentPreferredDeviceId: preferredOutputDeviceId.value || null,
        currentActiveDeviceId: activeOutputDeviceId.value || null,
        error: deviceError,
      })
      return preferredOutputDeviceId.value
    }
  }

  async function reset() {
    await clearPlaybackSession()
  }

  function dispose() {
    if (disposed) {
      return
    }

    disposed = true
    stopDeviceChangeListener()
    stopPlaybackSnapshotListener()
    stopPlaybackAudioLevelsListener()
    clearLocalState()
    void resetBackend()
  }

  const isPlaying = computed(() => isPlayingStatus(status.value))

  if (backendReady.value) {
    ensureDeviceChangeListener()
    const listenerPromises = [ensurePlaybackSnapshotListener()]

    if (enableAudioLevels) {
      listenerPromises.push(ensurePlaybackAudioLevelsListener())
    }

    void Promise.all(listenerPromises).then(async () => {
      if (disposed) {
        return
      }
      await syncSnapshot()
      if (disposed) {
        return
      }
      await refreshOutputDevices({ reason: 'startup' })
    })
  }

  return {
    kind: 'native',
    status,
    currentTime,
    duration,
    volume,
    activeTrackId,
    signalPath,
    audioLevels,
    outputDevices,
    preferredOutputDeviceId,
    activeOutputDeviceId,
    activeOutputDeviceName,
    prefersSystemOutputDevice,
    preferredOutputDeviceAvailable,
    isPlaying,
    error,
    applySnapshot,
    applyOutputDevicesSnapshot,
    loadTrack,
    play,
    pause,
    toggle,
    seek,
    setVolume,
    refreshOutputDevices,
    setOutputDevicePreference,
    reset,
    dispose,
    syncSnapshot,
  }
}
