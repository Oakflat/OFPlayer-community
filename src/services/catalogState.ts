import { sortByOrder } from './catalogHelpers'
import type { OrderedCatalogEntity } from './catalogHelpers'
import type { TrackModel } from '../models/track'

type CatalogRecord = Record<string, any>
type CatalogDataService = CatalogRecord

interface PlaylistTrackRelation {
  id?: string
  playlistId?: string
  order?: number
  addedAt?: string
  [key: string]: any
}

export interface CatalogStateSnapshot {
  libraries: OrderedCatalogEntity[]
  playlists: OrderedCatalogEntity[]
  tracks: TrackModel[]
  playlistTrackRelations: PlaylistTrackRelation[]
}

function sortTracks(tracks: TrackModel[] = []): TrackModel[] {
  return [...tracks].sort((left, right) => {
    const orderDiff = (left?.libraryOrder ?? 0) - (right?.libraryOrder ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.importedAt ?? left?.id ?? '').localeCompare(String(right?.importedAt ?? right?.id ?? ''))
  })
}

function sortRelations(relations: PlaylistTrackRelation[] = []): PlaylistTrackRelation[] {
  return [...relations].sort((left, right) => {
    if (left?.playlistId !== right?.playlistId) {
      return String(left?.playlistId ?? '').localeCompare(String(right?.playlistId ?? ''))
    }

    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.addedAt ?? left?.id ?? '').localeCompare(String(right?.addedAt ?? right?.id ?? ''))
  })
}

export async function loadCatalogState(
  dataService: CatalogDataService,
  preloadedSnapshot: Partial<CatalogStateSnapshot> | null = null,
  options: CatalogRecord = {},
): Promise<CatalogStateSnapshot> {
  if (preloadedSnapshot) {
    return {
      libraries: sortByOrder(preloadedSnapshot.libraries ?? []),
      playlists: [...(preloadedSnapshot.playlists ?? [])],
      tracks: sortTracks(preloadedSnapshot.tracks ?? []),
      playlistTrackRelations: sortRelations(preloadedSnapshot.playlistTrackRelations ?? []),
    }
  }

  if (typeof dataService?.catalog?.loadSnapshot === 'function') {
    const snapshot = await dataService.catalog.loadSnapshot(options)

    return {
      libraries: sortByOrder(snapshot.libraries),
      playlists: [...snapshot.playlists],
      tracks: sortTracks(snapshot.tracks),
      playlistTrackRelations: sortRelations(snapshot.playlistTrackRelations),
    }
  }

  const [libraries, playlists, tracks, playlistTrackRelations] = await Promise.all([
    dataService.catalog.getLibraries(),
    dataService.catalog.getPlaylists(),
    dataService.catalog.getTracks(),
    dataService.catalog.getPlaylistTrackRelations(),
  ])

  return {
    libraries: sortByOrder(libraries),
    playlists: [...playlists],
    tracks: sortTracks(tracks),
    playlistTrackRelations: sortRelations(playlistTrackRelations),
  }
}
