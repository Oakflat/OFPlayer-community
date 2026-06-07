import { ref } from 'vue'
import { createPlaybackHistoryEntryModel } from '../models/playbackHistory'
import { useAudioPlayer } from '../composables/useAudioPlayer'

const HISTORY_LIMIT = 100
const PLAYED_HISTORY_TYPE = 'played'

type PlayerStoreOptions = {
  dataService?: any
  initialVolume?: number
  onTrackEnded?: (payload: { trackId: string | null }) => void
  onTrackDurationChange?: (...args: any[]) => void
}

function normalizeRevision(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

export function createPlayerStore({
  dataService,
  initialVolume,
  onTrackEnded,
  onTrackDurationChange,
}: PlayerStoreOptions = {}) {
  const activeTrack = ref<any | null>(null)
  const recentHistory = ref<any[]>([])
  const historyRevision = ref(0)
  const recentPlayedRevision = ref(0)

  const audioPlayer = useAudioPlayer({
    initialVolume,
    onTrackEnded: ({ trackId }: { trackId: string | null }) => {
      onTrackEnded?.({ trackId })
    },
    onTrackDurationChange,
  })

  function setActiveTrack(track: any) {
    activeTrack.value = track ?? null
    return activeTrack.value
  }

  function setHistoryRevision(nextRevision: unknown) {
    historyRevision.value = normalizeRevision(nextRevision, historyRevision.value)
    recentPlayedRevision.value = normalizeRevision(nextRevision, recentPlayedRevision.value)
    return historyRevision.value
  }

  function markRecentPlayedChanged(entries: any[] = []) {
    if (
      entries.some(
        (entry) =>
          entry?.type === PLAYED_HISTORY_TYPE &&
          typeof entry.trackId === 'string' &&
          entry.trackId.trim().length > 0,
      )
    ) {
      recentPlayedRevision.value += 1
    }
  }

  function prependHistoryEntries(
    entries: any[] = [],
    { revision: nextRevision = null }: { revision?: number | null } = {},
  ) {
    const normalizedEntries = (entries ?? []).map((entry) => createPlaybackHistoryEntryModel(entry))

    if (normalizedEntries.length === 0) {
      if (nextRevision !== null) {
        setHistoryRevision(nextRevision)
      }

      return recentHistory.value
    }

    recentHistory.value = [...normalizedEntries, ...recentHistory.value].slice(0, HISTORY_LIMIT)

    if (nextRevision !== null) {
      setHistoryRevision(nextRevision)
    } else {
      historyRevision.value += 1
      markRecentPlayedChanged(normalizedEntries)
    }

    return recentHistory.value
  }

  function applyPlaybackSnapshot(snapshot: any) {
    return audioPlayer.applySnapshot(snapshot)
  }

  async function hydrate(
    preloadedHistory: any[] | null = null,
    { revision: nextRevision = null }: { revision?: number | null } = {},
  ) {
    recentHistory.value = preloadedHistory ?? await dataService.history.loadRecent(HISTORY_LIMIT)

    if (nextRevision !== null) {
      setHistoryRevision(nextRevision)
    } else {
      historyRevision.value += 1
      recentPlayedRevision.value += 1
    }

    return recentHistory.value
  }

  async function loadTrack(track: any, loadOptions: Record<string, any> = {}) {
    const loaded = await audioPlayer.loadTrack(track, loadOptions)

    if (loaded) {
      setActiveTrack(track)
    }

    return loaded
  }

  async function play() {
    return audioPlayer.play()
  }

  function pause() {
    return audioPlayer.pause()
  }

  async function toggle() {
    if (audioPlayer.isPlaying.value) {
      pause()
      return true
    }

    return play()
  }

  function seek(nextTime: number) {
    return audioPlayer.seek(nextTime)
  }

  function setVolume(nextVolume: number) {
    return audioPlayer.setVolume(nextVolume)
  }

  function refreshOutputDevices() {
    return audioPlayer.refreshOutputDevices()
  }

  function setOutputDevicePreference(nextDeviceId: string | null) {
    return audioPlayer.setOutputDevicePreference(nextDeviceId)
  }

  function reset() {
    setActiveTrack(null)
    audioPlayer.reset()
  }

  function dispose() {
    setActiveTrack(null)
    audioPlayer.dispose()
  }

  return {
    status: audioPlayer.status,
    currentTime: audioPlayer.currentTime,
    duration: audioPlayer.duration,
    volume: audioPlayer.volume,
    activeTrackId: audioPlayer.activeTrackId,
    signalPath: audioPlayer.signalPath,
    audioLevels: audioPlayer.audioLevels,
    outputDevices: audioPlayer.outputDevices,
    preferredOutputDeviceId: audioPlayer.preferredOutputDeviceId,
    activeOutputDeviceId: audioPlayer.activeOutputDeviceId,
    activeOutputDeviceName: audioPlayer.activeOutputDeviceName,
    prefersSystemOutputDevice: audioPlayer.prefersSystemOutputDevice,
    preferredOutputDeviceAvailable: audioPlayer.preferredOutputDeviceAvailable,
    isPlaying: audioPlayer.isPlaying,
    error: audioPlayer.error,
    activeTrack,
    recentHistory,
    historyRevision,
    recentPlayedRevision,
    setHistoryRevision,
    setActiveTrack,
    prependHistoryEntries,
    applyPlaybackSnapshot,
    hydrate,
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
  }
}
