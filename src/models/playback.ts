export const PLAYBACK_STATUS = Object.freeze({
  IDLE: 'idle',
  PAUSED: 'paused',
  PLAYING: 'playing',
} as const)

export type PlaybackStatus = (typeof PLAYBACK_STATUS)[keyof typeof PLAYBACK_STATUS]

export interface PlaybackStateOverrides {
  status?: PlaybackStatus | string
  currentTime?: unknown
  duration?: unknown
  volume?: unknown
  activeTrackId?: string | null
  error?: unknown
}

export interface PlaybackStateModel {
  status: PlaybackStatus | string
  currentTime: number
  duration: number
  volume: number
  activeTrackId: string | null
  error: unknown
}

export function clampVolume(value: unknown, fallback = 0.8): number {
  const numeric = Number(value)

  if (!Number.isFinite(numeric)) {
    return fallback
  }

  return Math.min(Math.max(numeric, 0), 1)
}

export function clampTime(value: unknown): number {
  const numeric = Number(value)
  return Number.isFinite(numeric) && numeric > 0 ? numeric : 0
}

export function createPlaybackStateModel(overrides: PlaybackStateOverrides = {}): PlaybackStateModel {
  return {
    status: overrides.status ?? PLAYBACK_STATUS.IDLE,
    currentTime: clampTime(overrides.currentTime),
    duration: clampTime(overrides.duration),
    volume: clampVolume(overrides.volume),
    activeTrackId: overrides.activeTrackId ?? null,
    error: overrides.error ?? null,
  }
}

export function isPlayingStatus(status: unknown): boolean {
  return status === PLAYBACK_STATUS.PLAYING
}
