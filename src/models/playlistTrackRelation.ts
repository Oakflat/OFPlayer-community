export interface PlaylistTrackRelationOverrides {
  id?: string
  playlistId?: unknown
  trackId?: unknown
  order?: unknown
  addedAt?: unknown
}

export interface PlaylistTrackRelationModel {
  id: string
  playlistId: string
  trackId: string
  order: number
  addedAt: string
  [key: string]: unknown
}

function createPlaylistTrackRelationId(playlistId: string, trackId: string): string {
  return `${playlistId}:${trackId}`
}

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeOrder(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

function normalizeDate(value: unknown, fallback: string): string {
  return typeof value === 'string' && value ? value : fallback
}

export function createPlaylistTrackRelationModel(
  overrides: PlaylistTrackRelationOverrides = {},
): PlaylistTrackRelationModel {
  const playlistId = normalizeText(overrides.playlistId)
  const trackId = normalizeText(overrides.trackId)
  const now = new Date().toISOString()

  return {
    id: overrides.id ?? createPlaylistTrackRelationId(playlistId, trackId),
    playlistId,
    trackId,
    order: normalizeOrder(overrides.order),
    addedAt: normalizeDate(overrides.addedAt, now),
  }
}
