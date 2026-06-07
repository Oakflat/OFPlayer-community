import { DEFAULT_LIBRARY_ID } from './library'

export const PLAYLIST_KINDS = Object.freeze({
  SYSTEM: 'system',
  USER: 'user',
} as const)

export const SYSTEM_PLAYLIST_KEYS = Object.freeze({
  ALL_TRACKS: 'all-tracks',
} as const)

export type PlaylistKind = (typeof PLAYLIST_KINDS)[keyof typeof PLAYLIST_KINDS]
export type SystemPlaylistKey = (typeof SYSTEM_PLAYLIST_KEYS)[keyof typeof SYSTEM_PLAYLIST_KEYS]

export interface PlaylistModelOverrides {
  id?: string
  libraryId?: unknown
  name?: unknown
  order?: unknown
  kind?: unknown
  systemKey?: unknown
  createdAt?: unknown
  updatedAt?: unknown
}

export interface PlaylistModel {
  id: string
  libraryId: string
  name: string
  order: number
  kind: PlaylistKind
  systemKey: SystemPlaylistKey | null
  createdAt: string
  updatedAt: string
}

export interface CreateDefaultAllTracksPlaylistOptions extends PlaylistModelOverrides {
  libraryId?: string
  order?: number
}

export const DEFAULT_ALL_TRACKS_PLAYLIST_ID = 'playlist-default-all-tracks'

function createPlaylistId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `playlist-${crypto.randomUUID()}`
  }

  return `playlist-${Date.now()}-${Math.random().toString(16).slice(2)}`
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

function normalizeKind(value: unknown): PlaylistKind {
  return value === PLAYLIST_KINDS.SYSTEM ? PLAYLIST_KINDS.SYSTEM : PLAYLIST_KINDS.USER
}

function normalizeSystemKey(value: unknown): SystemPlaylistKey | null {
  return value === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS ? value : null
}

export function createPlaylistModel(overrides: PlaylistModelOverrides = {}): PlaylistModel {
  const now = new Date().toISOString()
  const kind = normalizeKind(overrides.kind)

  return {
    id: overrides.id ?? createPlaylistId(),
    libraryId: normalizeText(overrides.libraryId),
    name: normalizeText(overrides.name),
    order: normalizeOrder(overrides.order),
    kind,
    systemKey: kind === PLAYLIST_KINDS.SYSTEM ? normalizeSystemKey(overrides.systemKey) : null,
    createdAt: normalizeDate(overrides.createdAt, now),
    updatedAt: normalizeDate(overrides.updatedAt, now),
  }
}

export function createDefaultAllTracksPlaylistModel({
  libraryId,
  order = 0,
  id,
  ...overrides
}: CreateDefaultAllTracksPlaylistOptions = {}): PlaylistModel {
  return createPlaylistModel({
    id: id ?? (libraryId === DEFAULT_LIBRARY_ID ? DEFAULT_ALL_TRACKS_PLAYLIST_ID : undefined),
    libraryId,
    order,
    kind: PLAYLIST_KINDS.SYSTEM,
    systemKey: SYSTEM_PLAYLIST_KEYS.ALL_TRACKS,
    name: '',
    ...overrides,
  })
}

export function isSystemPlaylist(playlist: PlaylistModel | null | undefined): boolean {
  return playlist?.kind === PLAYLIST_KINDS.SYSTEM
}
