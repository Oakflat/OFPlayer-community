type SessionPlaybackStatus = 'idle' | 'paused' | 'playing'

export interface SessionModelOverrides {
  id?: string
  startedAt?: string
  lastInteractedAt?: string
  currentTrackId?: unknown
  queueTrackIds?: unknown
  playbackStatus?: unknown
  currentTime?: unknown
  duration?: unknown
}

export interface SessionModel {
  id: string
  startedAt: string
  lastInteractedAt: string
  currentTrackId: string | null
  queueTrackIds: string[]
  playbackStatus: SessionPlaybackStatus
  currentTime: number
  duration: number
}

function asSessionOverrides(value: unknown): SessionModelOverrides {
  return value && typeof value === 'object' ? value as SessionModelOverrides : {}
}

function createSessionId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }

  return `session-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function normalizeQueueTrackIds(trackIds: unknown): string[] {
  if (!Array.isArray(trackIds)) {
    return []
  }

  return [...new Set(trackIds.filter(Boolean).map((trackId) => String(trackId)))]
}

function normalizePlaybackStatus(status: unknown): SessionPlaybackStatus {
  return typeof status === 'string' && ['idle', 'paused', 'playing'].includes(status)
    ? status as SessionPlaybackStatus
    : 'idle'
}

function normalizeTime(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0
}

export function createSessionModel(overrides: unknown = {}): SessionModel {
  const input = asSessionOverrides(overrides)

  return {
    id: input.id ?? createSessionId(),
    startedAt: input.startedAt ?? new Date().toISOString(),
    lastInteractedAt: input.lastInteractedAt ?? new Date().toISOString(),
    currentTrackId: input.currentTrackId ? String(input.currentTrackId) : null,
    queueTrackIds: normalizeQueueTrackIds(input.queueTrackIds),
    playbackStatus: normalizePlaybackStatus(input.playbackStatus),
    currentTime: normalizeTime(input.currentTime),
    duration: normalizeTime(input.duration),
  }
}

export function createSessionSnapshotModel(session: unknown): SessionModel {
  const normalized = createSessionModel(session)

  return {
    id: normalized.id,
    startedAt: normalized.startedAt,
    lastInteractedAt: normalized.lastInteractedAt,
    currentTrackId: normalized.currentTrackId,
    queueTrackIds: [...normalized.queueTrackIds],
    playbackStatus: normalized.playbackStatus,
    currentTime: normalized.currentTime,
    duration: normalized.duration,
  }
}
