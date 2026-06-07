import { computed, ref, watch, type Ref } from 'vue'
import { createLyricsSnapshotModel } from '../models/lyrics'

type LyricsTrack = {
  id?: string | null
  lyricsPath?: string | null
  source?: {
    path?: string | null
  } | null
}

type LyricsSnapshot = ReturnType<typeof createLyricsSnapshotModel>

interface UseLyricsOptions {
  currentTrack?: Ref<LyricsTrack | null | undefined>
  currentTime?: Ref<number | null | undefined>
  cacheContext?: Ref<string | null | undefined>
  resolveLyrics?: (track: LyricsTrack) => Promise<unknown>
  findActiveLineIndex?: (lyrics: LyricsSnapshot, currentTime: number) => number
}

const lyricsCache = new Map<string, LyricsSnapshot>()
const MAX_CACHE_SIZE = 48

function buildCacheKey(track?: LyricsTrack | null) {
  return buildCacheKeyWithContext(track, '')
}

function buildCacheKeyWithContext(track?: LyricsTrack | null, cacheContext = '') {
  const trackId = typeof track?.id === 'string' ? track.id : ''
  const audioPath = typeof track?.source?.path === 'string' ? track.source.path : ''
  const lyricsPath = typeof track?.lyricsPath === 'string' ? track.lyricsPath : ''
  const normalizedContext = typeof cacheContext === 'string' ? cacheContext.trim() : ''

  return `${trackId}::${audioPath}::${lyricsPath}::${normalizedContext}`
}

function pruneCache() {
  if (lyricsCache.size <= MAX_CACHE_SIZE) {
    return
  }

  const staleKeys = [...lyricsCache.keys()].slice(0, lyricsCache.size - MAX_CACHE_SIZE)
  staleKeys.forEach((key) => lyricsCache.delete(key))
}

export function useLyrics({
  currentTrack,
  currentTime,
  cacheContext,
  resolveLyrics,
  findActiveLineIndex,
}: UseLyricsOptions = {}) {
  const lyrics = ref<LyricsSnapshot>(createLyricsSnapshotModel())
  const isLoading = ref(false)
  let latestRequestKey = ''

  function invalidateTrackCache(track?: LyricsTrack | null) {
    const cacheKey = buildCacheKeyWithContext(track, cacheContext?.value ?? '')

    if (cacheKey) {
      lyricsCache.delete(cacheKey)
    }
  }

  async function loadLyrics(track?: LyricsTrack | null) {
    const cacheKey = buildCacheKeyWithContext(track, cacheContext?.value ?? '')

    if (!track || !cacheKey || typeof resolveLyrics !== 'function') {
      lyrics.value = createLyricsSnapshotModel({
        trackId: track?.id ?? null,
        audioPath: track?.source?.path ?? '',
        status: 'missing',
      })
      isLoading.value = false
      return
    }

    if (lyricsCache.has(cacheKey)) {
      lyrics.value = lyricsCache.get(cacheKey) ?? createLyricsSnapshotModel()
      isLoading.value = false
      return
    }

    latestRequestKey = cacheKey
    isLoading.value = true

    try {
      const resolvedLyrics = createLyricsSnapshotModel(await resolveLyrics(track) as Record<string, unknown>)

      if (latestRequestKey !== cacheKey) {
        return
      }

      lyricsCache.set(cacheKey, resolvedLyrics)
      pruneCache()
      lyrics.value = resolvedLyrics
    } catch {
      if (latestRequestKey !== cacheKey) {
        return
      }

      lyrics.value = createLyricsSnapshotModel({
        trackId: track?.id ?? null,
        audioPath: track?.source?.path ?? '',
        status: 'missing',
      })
    } finally {
      if (latestRequestKey === cacheKey) {
        isLoading.value = false
      }
    }
  }

  watch(
    () => currentTrack?.value,
    (track) => {
      void loadLyrics(track)
    },
    { immediate: true },
  )

  watch(
    () => cacheContext?.value,
    () => {
      void loadLyrics(currentTrack?.value)
    },
  )

  const activeIndex = computed(() => {
    if (!lyrics.value.isSynced || typeof findActiveLineIndex !== 'function') {
      return lyrics.value.activeLineIndex ?? -1
    }

    const nextIndex = findActiveLineIndex(lyrics.value, currentTime?.value ?? 0)
    return Number.isInteger(nextIndex) ? nextIndex : -1
  })

  const hasLyrics = computed(() => lyrics.value.hasLyrics)
  const hasTimestamps = computed(() => lyrics.value.isSynced)

  async function refresh(track = currentTrack?.value) {
    invalidateTrackCache(track)
    await loadLyrics(track)
  }

  return {
    lyrics,
    activeIndex,
    hasLyrics,
    hasTimestamps,
    isLoading,
    refresh,
  }
}
