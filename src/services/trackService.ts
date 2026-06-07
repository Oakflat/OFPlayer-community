import { loadCatalogState } from './catalogState'
import type { TrackModel } from '../models/track'

type ServiceRecord = Record<string, any>

function sortTracksByLibraryOrder<T extends TrackModel>(tracks: T[] = []): T[] {
  return [...tracks].sort((left, right) => {
    const orderDiff = (left?.libraryOrder ?? 0) - (right?.libraryOrder ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.importedAt ?? left?.id ?? '').localeCompare(String(right?.importedAt ?? right?.id ?? ''))
  })
}

export function createTrackService({ dataService }: { dataService: ServiceRecord }) {
  const trackTransactions = dataService.catalog.trackTransactions

  async function listTracks(libraryId: string | null = null) {
    const snapshot = await loadCatalogState(dataService)
    const tracks =
      typeof libraryId === 'string'
        ? snapshot.tracks.filter((track) => track.libraryId === libraryId)
        : snapshot.tracks

    return sortTracksByLibraryOrder(tracks)
  }

  async function getTrack(trackId: string | null | undefined, options: ServiceRecord = {}) {
    if (!trackId || typeof dataService?.catalog?.getTrack !== 'function') {
      return null
    }

    return dataService.catalog.getTrack(trackId, options)
  }

  async function importSourceFiles({ libraryId, files }: { libraryId?: string; files?: unknown[] } = {}) {
    const result = await trackTransactions.importSourceFiles({
      libraryId,
      files,
    })

    return {
      ...result,
      importedTracks: sortTracksByLibraryOrder(result.importedTracks),
    }
  }

  async function scanAndImport({
    libraryId,
    directories,
    respectDeletedImportPaths,
  }: { libraryId?: string; directories?: string[]; respectDeletedImportPaths?: boolean } = {}) {
    const result = await trackTransactions.scanAndImport({
      libraryId,
      directories,
      respectDeletedImportPaths,
    })

    return {
      ...result,
      importedTracks: sortTracksByLibraryOrder(result.importedTracks),
    }
  }

  async function updateTrackMetadata(trackId: string, patch: ServiceRecord) {
    const nextTrack = await trackTransactions.updateTrackMetadata({
      trackId,
      patch,
    })

    if (!nextTrack) {
      throw new Error('Track not found.')
    }

    return nextTrack
  }

  async function deleteTrackFromLibrary(trackId: string) {
    return trackTransactions.deleteTrackFromLibrary({ trackId })
  }

  async function deleteTracksFromLibrary(trackIds: string[] = []) {
    return trackTransactions.deleteTracksFromLibrary({ trackIds })
  }

  async function setFavorite(trackId: string, isFavorite: unknown) {
    return trackTransactions.setFavorite({
      trackId,
      isFavorite: Boolean(isFavorite),
    })
  }

  async function toggleFavorite(trackId: string) {
    return trackTransactions.toggleFavorite({ trackId })
  }

  function releaseTracks(tracks: TrackModel[]) {
    dataService.catalog.releaseTracks(tracks)
  }

  return {
    listTracks,
    getTrack,
    importSourceFiles,
    scanAndImport,
    updateTrackMetadata,
    deleteTrackFromLibrary,
    deleteTracksFromLibrary,
    setFavorite,
    toggleFavorite,
    releaseTracks,
  }
}
