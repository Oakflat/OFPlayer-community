import { DEFAULT_LIBRARY_ID, createDefaultLibraryModel } from '../models/library'
import { createDefaultAllTracksPlaylistModel, SYSTEM_PLAYLIST_KEYS } from '../models/playlist'
import { updateTrackModel, type TrackModelInput } from '../models/track'

export interface OrderedCatalogEntity {
  id: string
  order?: number
  createdAt?: string
  [key: string]: any
}

export interface CatalogSeedState {
  libraries?: OrderedCatalogEntity[]
  playlists?: OrderedCatalogEntity[]
  tracks?: TrackModelInput[]
}

export interface CatalogSeedResult {
  libraries: OrderedCatalogEntity[]
  playlists: OrderedCatalogEntity[]
  tracks: TrackModelInput[]
  didSeed?: boolean
}

export function sortByOrder<T extends OrderedCatalogEntity>(items: T[] = []): T[] {
  return [...items].sort((left, right) => {
    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.createdAt ?? left?.id ?? '').localeCompare(String(right?.createdAt ?? right?.id ?? ''))
  })
}

export function normalizeEntityName(value: unknown, fallbackLabel = 'Item'): string {
  if (typeof value !== 'string') {
    throw new Error(`${fallbackLabel} name is required.`)
  }

  const trimmed = value.trim()

  if (!trimmed) {
    throw new Error(`${fallbackLabel} name is required.`)
  }

  return trimmed
}

export function ensureSequentialOrder<T extends OrderedCatalogEntity>(items: T[] = []): T[] {
  return sortByOrder(items).map((item, index) => ({
    ...item,
    order: index,
  }))
}

export function reorderEntities<T extends OrderedCatalogEntity>(
  items: T[] = [],
  orderedIds: string[] = [],
): T[] {
  const itemMap = new Map(items.map((item) => [item.id, item]))
  const orderedItems: T[] = []
  const seenIds = new Set()

  ;(orderedIds ?? []).forEach((id) => {
    const item = itemMap.get(id)

    if (!item || seenIds.has(id)) {
      return
    }

    seenIds.add(id)
    orderedItems.push(item)
  })

  sortByOrder(items).forEach((item) => {
    if (seenIds.has(item.id)) {
      return
    }

    orderedItems.push(item)
  })

  return orderedItems.map((item, index) => ({
    ...item,
    order: index,
  }))
}

export function createDefaultCatalogSeed(tracks: TrackModelInput[] = []): CatalogSeedResult {
  const library = createDefaultLibraryModel()
  const playlist = createDefaultAllTracksPlaylistModel({
    libraryId: library.id,
  })
  const seededTracks = (tracks ?? []).map((track, index) =>
    updateTrackModel(track, {
      libraryId: track.libraryId || library.id,
      libraryOrder: Number.isInteger(track.libraryOrder) ? track.libraryOrder : index,
      isFavorite: typeof track.isFavorite === 'boolean' ? track.isFavorite : false,
    }),
  )

  return {
    libraries: [library],
    playlists: [playlist],
    tracks: seededTracks,
  }
}

export function getDefaultPlaylistForLibrary(
  playlists: OrderedCatalogEntity[] = [],
  libraryId: string,
): OrderedCatalogEntity | null {
  return playlists.find(
    (playlist) =>
      playlist.libraryId === libraryId && playlist.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS,
  ) ?? null
}

export function ensureLibraryHasDefaultPlaylist(
  playlists: OrderedCatalogEntity[] = [],
  libraryId: string,
): { playlist: OrderedCatalogEntity; didCreate: boolean } {
  const existingDefault = getDefaultPlaylistForLibrary(playlists, libraryId)

  if (existingDefault) {
    return {
      playlist: existingDefault,
      didCreate: false,
    }
  }

  const libraryPlaylists = playlists.filter((playlist) => playlist.libraryId === libraryId)

  return {
    playlist: createDefaultAllTracksPlaylistModel({
      libraryId,
      order: libraryPlaylists.length,
    }),
    didCreate: true,
  }
}

export function ensureCatalogSeedState({
  libraries = [],
  playlists = [],
  tracks = [],
}: CatalogSeedState = {}): CatalogSeedResult {
  const nextLibraries = sortByOrder(libraries)
  const nextPlaylists = sortByOrder(playlists)

  if (nextLibraries.length === 0) {
    return {
      ...createDefaultCatalogSeed(tracks),
      didSeed: true,
    }
  }

  let didMutate = false
  let resolvedPlaylists = [...nextPlaylists]

  nextLibraries.forEach((library) => {
    const { playlist, didCreate } = ensureLibraryHasDefaultPlaylist(resolvedPlaylists, library.id)

    if (didCreate) {
      resolvedPlaylists = [...resolvedPlaylists, playlist]
      didMutate = true
    }
  })

  return {
    libraries: ensureSequentialOrder(nextLibraries),
    playlists: [...resolvedPlaylists],
    tracks: [...tracks],
    didSeed: didMutate,
  }
}

export function assertDefaultLibraryIsProtected(library: OrderedCatalogEntity | null | undefined): void {
  if (library?.isDefault || library?.id === DEFAULT_LIBRARY_ID) {
    throw new Error('Default library cannot be deleted.')
  }
}
