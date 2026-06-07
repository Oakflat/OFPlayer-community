export const PLAYBACK_ORDER_SEPARATOR = '\u001f'
const REPEAT_MODE_NONE = 'none'
const REPEAT_MODE_ALL = 'all'
const REPEAT_MODE_ONE = 'one'

export type PlaybackRepeatMode = typeof REPEAT_MODE_NONE | typeof REPEAT_MODE_ALL | typeof REPEAT_MODE_ONE

export type ResolvePlaybackOrderTrackIdOptions = {
  queueTrackIds?: unknown[]
  currentTrackId?: string | null
  repeatMode?: PlaybackRepeatMode | string
  step?: number
  reason?: string
}

export function normalizePlaybackOrderTrackIds(trackIds: unknown[] = []): string[] {
  if (!Array.isArray(trackIds)) {
    return []
  }

  const seenTrackIds = new Set<string>()
  const normalizedTrackIds: string[] = []

  trackIds
    .map((trackId) => String(trackId ?? '').trim())
    .filter(Boolean)
    .forEach((trackId) => {
      if (seenTrackIds.has(trackId)) {
        return
      }

      seenTrackIds.add(trackId)
      normalizedTrackIds.push(trackId)
    })

  return normalizedTrackIds
}

export function createShuffledPlaybackOrder(
  trackIds: unknown[] = [],
  anchorTrackId: string | null = null,
  random: (() => number) | unknown = Math.random,
): string[] {
  const normalizedTrackIds = normalizePlaybackOrderTrackIds(trackIds)
  const anchorId = typeof anchorTrackId === 'string' ? anchorTrackId.trim() : ''
  const remainingTrackIds = normalizedTrackIds.filter((trackId) => trackId !== anchorId)
  const nextRandom = typeof random === 'function' ? random : Math.random

  for (let index = remainingTrackIds.length - 1; index > 0; index -= 1) {
    const swapIndex = Math.floor(nextRandom() * (index + 1))
    const trackId = remainingTrackIds[index]
    remainingTrackIds[index] = remainingTrackIds[swapIndex]
    remainingTrackIds[swapIndex] = trackId
  }

  return anchorId && normalizedTrackIds.includes(anchorId)
    ? [anchorId, ...remainingTrackIds]
    : remainingTrackIds
}

export function createPlaybackOrderSignature(trackIds: unknown[] = []): string {
  return normalizePlaybackOrderTrackIds(trackIds).join(PLAYBACK_ORDER_SEPARATOR)
}

export function resolvePlaybackOrderTrackId({
  queueTrackIds = [],
  currentTrackId = null,
  repeatMode = REPEAT_MODE_ALL,
  step = 1,
  reason = 'user',
}: ResolvePlaybackOrderTrackIdOptions = {}): string | null {
  const normalizedQueueTrackIds = normalizePlaybackOrderTrackIds(queueTrackIds)
  const activeTrackId = typeof currentTrackId === 'string' ? currentTrackId : ''

  if (normalizedQueueTrackIds.length === 0) {
    return null
  }

  if (reason === 'ended' && repeatMode === REPEAT_MODE_ONE && activeTrackId) {
    return activeTrackId
  }

  const currentIndex = activeTrackId
    ? normalizedQueueTrackIds.findIndex((trackId) => trackId === activeTrackId)
    : -1

  if (currentIndex === -1) {
    return normalizedQueueTrackIds[0]
  }

  const nextIndex = currentIndex + step

  if (nextIndex >= 0 && nextIndex < normalizedQueueTrackIds.length) {
    return normalizedQueueTrackIds[nextIndex]
  }

  if (repeatMode === REPEAT_MODE_NONE) {
    return null
  }

  return normalizedQueueTrackIds[
    (nextIndex + normalizedQueueTrackIds.length) % normalizedQueueTrackIds.length
  ]
}
