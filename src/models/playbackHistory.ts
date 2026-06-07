import { clampTime } from './playback'

export const PLAYBACK_HISTORY_TYPES = Object.freeze({
  PLAYED: 'played',
  PAUSED: 'paused',
  ENDED: 'ended',
} as const)

export type PlaybackHistoryType =
  (typeof PLAYBACK_HISTORY_TYPES)[keyof typeof PLAYBACK_HISTORY_TYPES]

export interface PlaybackHistoryEntryOverrides {
  id?: string
  trackId?: string | null
  type?: PlaybackHistoryType | string
  position?: unknown
  duration?: unknown
  recordedAt?: string
}

export interface PlaybackHistoryEntryModel {
  id: string
  trackId: string | null
  type: PlaybackHistoryType | string
  position: number
  duration: number
  recordedAt: string
}

function createHistoryId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }

  return `history-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export function createPlaybackHistoryEntryModel(
  overrides: PlaybackHistoryEntryOverrides = {},
): PlaybackHistoryEntryModel {
  return {
    id: overrides.id ?? createHistoryId(),
    trackId: overrides.trackId ?? null,
    type: overrides.type ?? PLAYBACK_HISTORY_TYPES.PLAYED,
    position: clampTime(overrides.position),
    duration: clampTime(overrides.duration),
    recordedAt: overrides.recordedAt ?? new Date().toISOString(),
  }
}
