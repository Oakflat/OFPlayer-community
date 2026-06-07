import { normalizeEntityName, sortByOrder } from './catalogHelpers'
import { loadCatalogState } from './catalogState'

type ServiceRecord = Record<string, any>

export function createPlaylistService({ dataService }: { dataService: ServiceRecord }) {
  const playlistTransactions = dataService.catalog.playlistTransactions

  async function listPlaylists(libraryId: string | null = null) {
    const snapshot = await loadCatalogState(dataService)
    const filteredPlaylists =
      typeof libraryId === 'string'
        ? snapshot.playlists.filter((playlist) => playlist.libraryId === libraryId)
        : snapshot.playlists

    return sortByOrder(filteredPlaylists)
  }

  async function listPlaylistTrackRelations() {
    const snapshot = await loadCatalogState(dataService)
    return [...snapshot.playlistTrackRelations]
  }

  async function createPlaylist({ libraryId, name }: { libraryId?: string; name: unknown }) {
    const normalizedName = normalizeEntityName(name, 'Playlist')
    return playlistTransactions.createPlaylist({
      libraryId,
      name: normalizedName,
    })
  }

  async function renamePlaylist(playlistId: string, name: unknown) {
    const normalizedName = normalizeEntityName(name, 'Playlist')
    return playlistTransactions.renamePlaylist({
      playlistId,
      name: normalizedName,
    })
  }

  async function deletePlaylist(playlistId: string) {
    return playlistTransactions.deletePlaylist({ playlistId })
  }

  async function reorderPlaylists(libraryId: string, orderedPlaylistIds: string[]) {
    return playlistTransactions.reorderPlaylists({
      libraryId,
      orderedPlaylistIds,
    })
  }

  async function addTrackToPlaylist({
    playlistId,
    trackId,
    index = null,
  }: {
    playlistId: string
    trackId: string
    index?: number | null
  }) {
    return playlistTransactions.addTrackToPlaylist({
      playlistId,
      trackId,
      index,
    })
  }

  async function removeTrackFromPlaylist({ playlistId, trackId }: { playlistId: string; trackId: string }) {
    return playlistTransactions.removeTrackFromPlaylist({
      playlistId,
      trackId,
    })
  }

  async function reorderPlaylistTracks(playlistId: string, orderedTrackIds: string[]) {
    return playlistTransactions.reorderPlaylistTracks({
      playlistId,
      orderedTrackIds,
    })
  }

  return {
    listPlaylists,
    listPlaylistTrackRelations,
    createPlaylist,
    renamePlaylist,
    deletePlaylist,
    reorderPlaylists,
    addTrackToPlaylist,
    removeTrackFromPlaylist,
    reorderPlaylistTracks,
  }
}
