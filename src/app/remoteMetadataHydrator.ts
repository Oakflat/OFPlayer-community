import {
  canHydrateRemoteTrackMetadata,
  hasCompleteRemoteMetadata,
  isExternalLibrary,
  isExternalTrack,
  nowMs,
  type RemotePlaybackMetadataPatch,
  waitForDelay,
} from './appStateHelpers.ts'
import type { TrackModelInput } from '../models/track.ts'

type TimerId = ReturnType<typeof setTimeout>

export type RemoteMetadataLibraryStore = {
  getLibraryById: (libraryId: string | null | undefined) => {
    id: string
    source?: { kind?: string; connectionId?: string | null } | null
  } | null | undefined
  getTracksForLibrary: (libraryId: string) => TrackModelInput[]
  getTrackById: (trackId: string | undefined) => TrackModelInput | null | undefined
  updateTrackMetadata: (trackId: string, patch: RemotePlaybackMetadataPatch) => Promise<unknown>
}

export type RemoteMetadataHydratorOptions = {
  libraryStore: RemoteMetadataLibraryStore
  externalLibraryService: {
    resolveTrackMetadata: (
      track: TrackModelInput,
      options: { reason: string },
    ) => Promise<RemotePlaybackMetadataPatch | null | undefined>
  }
  getActiveLibraryId: () => string | null | undefined
  isPlaying: () => boolean
  isDisposed: () => boolean
  getLifecycleToken: () => unknown
  isLifecycleCurrent: (token: unknown) => boolean
  logDiagnosticsInfo: (scope: string, category: string, event: string, payload: Record<string, unknown>) => unknown
  startDelayMs: number
  idleDelayMs: number
  playingDelayMs: number
  now?: () => number
  wait?: (delayMs: number) => Promise<void>
  setTimeoutFn?: (callback: () => void, delayMs: number) => TimerId
  clearTimeoutFn?: (timerId: TimerId) => void
}

function defaultSetTimeout(callback: () => void, delayMs: number): TimerId {
  const timer = typeof window !== 'undefined' ? window.setTimeout : setTimeout
  return timer(callback, delayMs)
}

function defaultClearTimeout(timerId: TimerId): void {
  const clear = typeof window !== 'undefined' ? window.clearTimeout : clearTimeout
  clear(timerId)
}

export function createRemoteMetadataHydrator({
  libraryStore,
  externalLibraryService,
  getActiveLibraryId,
  isPlaying,
  isDisposed,
  getLifecycleToken,
  isLifecycleCurrent,
  logDiagnosticsInfo,
  startDelayMs,
  idleDelayMs,
  playingDelayMs,
  now = nowMs,
  wait = waitForDelay,
  setTimeoutFn = defaultSetTimeout,
  clearTimeoutFn = defaultClearTimeout,
}: RemoteMetadataHydratorOptions) {
  const queue: string[] = []
  const queuedIds = new Set<string>()
  const failedIds = new Set<string>()
  let timerId: TimerId | null = null
  let active = false

  function resolveIdleDelay(): number {
    return isPlaying() ? playingDelayMs : idleDelayMs
  }

  function shouldHydrateTrack(track: TrackModelInput | null | undefined): boolean {
    if (!track) {
      return false
    }

    return (
      isExternalTrack(track) &&
      canHydrateRemoteTrackMetadata(track) &&
      !failedIds.has(track.id as string) &&
      !hasCompleteRemoteMetadata(track)
    )
  }

  function clearQueue(): void {
    queue.length = 0
    queuedIds.clear()
    failedIds.clear()

    if (timerId !== null) {
      clearTimeoutFn(timerId)
      timerId = null
    }
  }

  function schedule(delayMs = startDelayMs): void {
    if (isDisposed() || active || timerId !== null || queue.length === 0) {
      return
    }

    timerId = setTimeoutFn(() => {
      timerId = null
      void drain()
    }, delayMs)
  }

  function enqueueLibrary(
    libraryId: string | null | undefined,
    { delayMs = startDelayMs, retryFailed = false }: { delayMs?: number; retryFailed?: boolean } = {},
  ): number {
    const library = libraryStore.getLibraryById(libraryId)

    if (!library || !isExternalLibrary(library)) {
      return 0
    }

    let queuedCount = 0

    for (const track of libraryStore.getTracksForLibrary(library.id)) {
      if (retryFailed) {
        failedIds.delete(track.id)
      }

      if (!shouldHydrateTrack(track) || queuedIds.has(track.id)) {
        continue
      }

      queue.push(track.id)
      queuedIds.add(track.id)
      queuedCount += 1
    }

    if (queuedCount > 0) {
      schedule(delayMs)
    }

    return queuedCount
  }

  function enqueueActiveLibrary(options: { delayMs?: number; retryFailed?: boolean } = {}): number {
    return enqueueLibrary(getActiveLibraryId(), options)
  }

  async function drain(): Promise<void> {
    if (isDisposed() || active || queue.length === 0) {
      return
    }

    const hydrationToken = getLifecycleToken()
    const startedAt = now()
    let processed = 0
    let hydrated = 0
    let failed = 0
    active = true

    try {
      while (queue.length > 0 && isLifecycleCurrent(hydrationToken)) {
        const trackId = queue.shift() as string
        queuedIds.delete(trackId)
        const track = libraryStore.getTrackById(trackId)

        if (!track || !shouldHydrateTrack(track)) {
          continue
        }

        processed += 1

        try {
          const metadataPatch = await externalLibraryService.resolveTrackMetadata(track, {
            reason: 'background-metadata',
          })

          if (!isLifecycleCurrent(hydrationToken)) {
            return
          }

          if (metadataPatch && Object.keys(metadataPatch).length > 0) {
            await libraryStore.updateTrackMetadata(track.id, metadataPatch)
            hydrated += 1
          } else {
            failedIds.add(track.id)
            failed += 1
          }
        } catch {
          failedIds.add(track.id)
          failed += 1
        }

        if (queue.length > 0 && isLifecycleCurrent(hydrationToken)) {
          await wait(resolveIdleDelay())
        }
      }
    } finally {
      active = false

      if (!isDisposed() && processed > 0) {
        void logDiagnosticsInfo('[OFPlayer external metadata]', 'metadata', 'background_hydrate', {
          totalMs: Math.round(now() - startedAt),
          processed,
          hydrated,
          failed,
          remaining: queue.length,
        })
      }

      if (queue.length > 0 && isLifecycleCurrent(hydrationToken)) {
        schedule(resolveIdleDelay())
      }
    }
  }

  return {
    clearQueue,
    drain,
    enqueueActiveLibrary,
    enqueueLibrary,
    schedule,
    shouldHydrateTrack,
  }
}
