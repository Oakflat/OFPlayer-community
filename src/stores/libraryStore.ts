import { computed, ref } from 'vue'
import type { ComputedRef, Ref } from 'vue'
import { parseCollectionRef, SMART_VIEW_KEYS, createPlaylistCollectionRef } from '../models/collection'
import { SYSTEM_PLAYLIST_KEYS } from '../models/playlist'
import type { TrackFileLike, TrackModel, TrackSource } from '../models/track'

interface OrderedEntity {
  id: string
  order?: number
  createdAt?: string
}

interface LibraryModel extends OrderedEntity {
  name: string
  isDefault?: boolean
  source?: Record<string, unknown>
  updatedAt?: string
}

interface PlaylistModel extends OrderedEntity {
  libraryId: string
  name: string
  kind?: string
  systemKey?: string | null
  updatedAt?: string
}

interface PlaylistTrackRelationModel extends OrderedEntity {
  playlistId: string
  trackId: string
  addedAt?: string
}

interface CatalogSnapshot {
  libraries: LibraryModel[]
  playlists: PlaylistModel[]
  tracks: StoreTrackModel[]
  playlistTrackRelations: PlaylistTrackRelationModel[]
}

interface HydrateOptions {
  revision?: number | null
  trackArtworkMode?: unknown
  trackListComplete?: boolean
}

interface LibraryService {
  loadCatalog(preloadedSnapshot?: CatalogSnapshot | null, options?: { trackArtworkMode: TrackArtworkMode }): Promise<CatalogSnapshot>
  createLibrary(input: { name: string }): Promise<{ library: LibraryModel; defaultPlaylist: PlaylistModel }>
  renameLibrary(libraryId: string, name: string): Promise<LibraryModel>
  deleteLibrary(libraryId: string): Promise<DeleteLibraryResult>
  reorderLibraries(orderedLibraryIds: string[]): Promise<LibraryModel[]>
}

interface PlaylistService {
  createPlaylist(input: { libraryId: string; name: string }): Promise<PlaylistModel>
  renamePlaylist(playlistId: string, name: string): Promise<PlaylistModel>
  deletePlaylist(playlistId: string): Promise<DeletePlaylistResult>
  reorderPlaylists(libraryId: string, orderedPlaylistIds: string[]): Promise<PlaylistModel[]>
  addTrackToPlaylist(input: PlaylistTrackMutation): Promise<PlaylistTrackMutationResult>
  removeTrackFromPlaylist(input: Omit<PlaylistTrackMutation, 'index'>): Promise<PlaylistTrackMutationResult>
  reorderPlaylistTracks(playlistId: string, orderedTrackIds: string[]): Promise<PlaylistTrackRelationModel[]>
}

interface TrackService {
  getTrack(trackId: string, options?: { includeArtwork: boolean }): Promise<StoreTrackModel | null>
  importSourceFiles(input?: ImportSourceFilesInput): Promise<ImportTracksResult>
  scanAndImport(input?: ScanAndImportTracksInput): Promise<ImportTracksResult>
  updateTrackMetadata(trackId: string, patch: TrackMetadataPatch): Promise<StoreTrackModel>
  deleteTrackFromLibrary(trackId: string): Promise<DeleteTrackResult>
  deleteTracksFromLibrary(trackIds: string[]): Promise<DeleteTracksResult>
  setFavorite(trackId: string, isFavorite: boolean): Promise<StoreTrackModel>
  toggleFavorite(trackId: string): Promise<StoreTrackModel>
  releaseTracks(tracks: StoreTrackModel[]): void
}

interface CreateLibraryStoreOptions {
  libraryService: LibraryService
  playlistService: PlaylistService
  trackService: TrackService
}

type TrackArtworkMode = 'none' | 'album-covers' | 'all'
type StoreTrackModel = TrackModel & { [ARTWORK_LOADED_FLAG]?: boolean }
type TrackMetadataPatch = Partial<TrackModel> & Record<string, unknown>

interface MergeTrackOptions {
  preserveArtwork?: boolean
}

interface TrackLookupOptions {
  includeArtwork?: boolean
  cache?: boolean
}

interface ImportSourceFilesInput {
  libraryId?: string | null
  files?: TrackFileLike[]
}

interface ScanAndImportTracksInput {
  libraryId?: string | null
  directories?: string[]
  respectDeletedImportPaths?: boolean
}

interface ImportInvalidationResult {
  invalidatedTrackIds?: string[]
  invalidatedRelationIds?: string[]
  reorderedTracks?: StoreTrackModel[]
}

interface ImportTracksResult extends ImportInvalidationResult {
  importedTracks: StoreTrackModel[]
  [key: string]: unknown
}

interface DeleteLibraryResult {
  deletedLibraryId: string
  deletedPlaylistIds: string[]
  deletedRelationIds: string[]
  deletedTrackIds: string[]
  fallbackLibraryId?: string | null
  libraries?: LibraryModel[]
  [key: string]: unknown
}

interface DeletePlaylistResult {
  libraryId: string
  deletedPlaylistId: string
  deletedRelationIds: string[]
  playlists?: PlaylistModel[]
  [key: string]: unknown
}

interface DeleteTrackResult {
  deletedTrackId: string
  deletedRelationIds: string[]
  reorderedTracks?: StoreTrackModel[]
  [key: string]: unknown
}

interface DeleteTracksResult {
  deletedTrackIds: string[]
  deletedRelationIds: string[]
  libraryIds?: string[]
  reorderedTracks?: StoreTrackModel[]
  [key: string]: unknown
}

interface PlaylistTrackMutation {
  playlistId: string
  trackId: string
  index?: number | null
}

interface PlaylistTrackMutationResult {
  relations: PlaylistTrackRelationModel[]
  deletedRelationId?: string | null
  [key: string]: unknown
}

export interface LibraryStore {
  libraries: Ref<LibraryModel[]>
  playlists: Ref<PlaylistModel[]>
  tracks: Ref<StoreTrackModel[]>
  playlistTrackRelations: Ref<PlaylistTrackRelationModel[]>
  catalogRevision: Ref<number>
  catalogTrackListComplete: Ref<boolean>
  catalogTrackArtworkMode: Ref<TrackArtworkMode>
  setCatalogRevision: (nextRevision: unknown) => number
  libraryIds: ComputedRef<string[]>
  trackIds: ComputedRef<string[]>
  trackCount: ComputedRef<number>
  hasTracks: ComputedRef<boolean>
  hydrate: (preloadedSnapshot?: CatalogSnapshot | null, options?: HydrateOptions) => Promise<CatalogSnapshot>
  getLibraryById: (libraryId: string | null | undefined) => LibraryModel | null
  getPlaylistById: (playlistId: string | null | undefined) => PlaylistModel | null
  getTrackById: (trackId: string | null | undefined) => StoreTrackModel | null
  getOrLoadTrack: (trackId: string | null | undefined, options?: TrackLookupOptions) => Promise<StoreTrackModel | null>
  cacheTrackArtwork: (track: StoreTrackModel | null | undefined) => StoreTrackModel | null
  getTracksForLibrary: (libraryId: string | null | undefined) => StoreTrackModel[]
  getPlaylistTrackRelations: (playlistId: string) => PlaylistTrackRelationModel[]
  getDefaultPlaylistForLibrary: (libraryId: string | null | undefined) => PlaylistModel | null
  getDefaultCollectionRef: (libraryId: string | null | undefined) => string | null
  isCollectionAvailableForLibrary: (libraryId: string | null | undefined, collectionRef: string | null | undefined) => boolean
  createLibrary: (name: string) => Promise<{ library: LibraryModel; defaultPlaylist: PlaylistModel }>
  renameLibrary: (libraryId: string, name: string) => Promise<LibraryModel>
  deleteLibrary: (libraryId: string) => Promise<DeleteLibraryResult>
  reorderLibraries: (orderedLibraryIds: string[]) => Promise<LibraryModel[]>
  createPlaylist: (input: { libraryId: string; name: string }) => Promise<PlaylistModel>
  renamePlaylist: (playlistId: string, name: string) => Promise<PlaylistModel>
  deletePlaylist: (playlistId: string) => Promise<DeletePlaylistResult>
  reorderPlaylists: (input: { libraryId: string; orderedPlaylistIds: string[] }) => Promise<PlaylistModel[]>
  importSourceFiles: (input?: ImportSourceFilesInput) => Promise<ImportTracksResult>
  scanAndImportTracks: (input?: ScanAndImportTracksInput) => Promise<ImportTracksResult>
  updateTrackMetadata: (trackId: string, patch: TrackMetadataPatch) => Promise<StoreTrackModel>
  updateTrackSource: (trackId: string, source: TrackSource) => Promise<StoreTrackModel>
  addTrackToPlaylist: (input: PlaylistTrackMutation) => Promise<PlaylistTrackMutationResult>
  removeTrackFromPlaylist: (input: Omit<PlaylistTrackMutation, 'index'>) => Promise<PlaylistTrackMutationResult>
  deleteTrackFromLibrary: (trackId: string) => Promise<DeleteTrackResult>
  deleteTracksFromLibrary: (trackIds: string[]) => Promise<DeleteTracksResult>
  toggleFavorite: (trackId: string) => Promise<StoreTrackModel>
  setFavorite: (trackId: string, isFavorite: boolean) => Promise<StoreTrackModel>
  reorderPlaylistTracks: (input: { playlistId: string; orderedTrackIds: string[] }) => Promise<PlaylistTrackRelationModel[]>
  dispose: () => void
}

const SMART_VIEW_KEY_SET = new Set<string>(Object.values(SMART_VIEW_KEYS))
const ARTWORK_LOADED_FLAG = '__ofplayerArtworkLoaded'
const TRACK_ARTWORK_MODES = new Set<TrackArtworkMode>(['none', 'album-covers', 'all'])

function normalizeRevision(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

function sortByOrder<T extends OrderedEntity>(items: T[] = []): T[] {
  return [...items].sort((left, right) => {
    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.createdAt ?? left?.id ?? '').localeCompare(String(right?.createdAt ?? right?.id ?? ''))
  })
}

function hasArtwork(track: StoreTrackModel | null | undefined): boolean {
  return typeof track?.artwork === 'string' && track.artwork.trim().length > 0
}

function normalizeTrackArtworkMode(value: unknown): TrackArtworkMode {
  return TRACK_ARTWORK_MODES.has(value as TrackArtworkMode) ? value as TrackArtworkMode : 'all'
}

export function createLibraryStore({ libraryService, playlistService, trackService }: CreateLibraryStoreOptions): LibraryStore {
  const libraries = ref<LibraryModel[]>([])
  const playlists = ref<PlaylistModel[]>([])
  const tracks = ref<StoreTrackModel[]>([])
  const playlistTrackRelations = ref<PlaylistTrackRelationModel[]>([])
  const catalogRevision = ref(0)
  const catalogTrackListComplete = ref(false)
  const catalogTrackArtworkMode = ref<TrackArtworkMode>('none')
  const trackLookupPromises = new Map<string, Promise<StoreTrackModel | null>>()

  const libraryIds = computed(() => libraries.value.map((library) => library.id))
  const trackIds = computed(() => tracks.value.map((track) => track.id))
  const trackCount = computed(() => tracks.value.length)
  const hasTracks = computed(() => trackCount.value > 0)

  function markCatalogChanged() {
    catalogRevision.value += 1
  }

  function setCatalogRevision(nextRevision: unknown): number {
    catalogRevision.value = normalizeRevision(nextRevision, catalogRevision.value)
    return catalogRevision.value
  }

  function releaseTrack(track: StoreTrackModel | null) {
    if (!track) {
      return
    }

    trackService.releaseTracks([track])
  }

  function mergeTrackPreservingSource(nextTrack: StoreTrackModel, { preserveArtwork = true }: MergeTrackOptions = {}): StoreTrackModel {
    const currentTrack = getTrackById(nextTrack.id)

    if (!currentTrack) {
      return nextTrack
    }

    const currentArtwork = hasArtwork(currentTrack) ? currentTrack.artwork : ''
    const nextArtwork = hasArtwork(nextTrack) ? nextTrack.artwork : ''

    return {
      ...currentTrack,
      ...nextTrack,
      artwork: preserveArtwork && currentArtwork && !nextArtwork ? currentArtwork : nextTrack.artwork,
      source: currentTrack.source,
      file: currentTrack.file ?? nextTrack.file ?? null,
    }
  }

  function replaceTrack(nextTrack: StoreTrackModel) {
    const previousTrack = getTrackById(nextTrack.id)
    let replacedTrack = false

    tracks.value = tracks.value.map((track) => {
      if (track.id !== nextTrack.id) {
        return track
      }

      replacedTrack = true
      return nextTrack
    })

    if (!replacedTrack) {
      tracks.value = [...tracks.value, nextTrack]
    }

    if (
      previousTrack &&
      previousTrack !== nextTrack &&
      previousTrack?.source?.url !== nextTrack?.source?.url
    ) {
      releaseTrack(previousTrack)
    }
  }

  function cacheTrackArtwork(track: StoreTrackModel | null | undefined): StoreTrackModel | null {
    if (!track?.id || !hasArtwork(track)) {
      return track ?? null
    }

    const nextTrack = mergeTrackPreservingSource({
      ...track,
      [ARTWORK_LOADED_FLAG]: true,
    })

    replaceTrack(nextTrack)
    return getTrackById(nextTrack.id) ?? nextTrack
  }

  function removeTrackFromState(trackId: string) {
    let removedTrack: StoreTrackModel | null = null

    tracks.value = tracks.value.filter((track) => {
      if (track.id !== trackId) {
        return true
      }

      removedTrack = track
      return false
    })

    releaseTrack(removedTrack)
  }

  function trackLookupKey(trackId: string, includeArtwork: boolean): string {
    return `${trackId}:${includeArtwork ? 'artwork' : 'metadata'}`
  }

  async function loadTrackRecord(trackId: string, includeArtwork: boolean): Promise<StoreTrackModel | null> {
    const exactKey = trackLookupKey(trackId, includeArtwork)
    const artworkKey = trackLookupKey(trackId, true)

    if (!includeArtwork && trackLookupPromises.has(artworkKey)) {
      return trackLookupPromises.get(artworkKey)!
    }

    if (trackLookupPromises.has(exactKey)) {
      return trackLookupPromises.get(exactKey)!
    }

    const lookupPromise = trackService
      .getTrack(trackId, { includeArtwork })
      .finally(() => {
        trackLookupPromises.delete(exactKey)
      })

    trackLookupPromises.set(exactKey, lookupPromise)
    return lookupPromise
  }

  async function hydrate(
    preloadedSnapshot: CatalogSnapshot | null = null,
    {
      revision = null,
      trackArtworkMode = 'all',
      trackListComplete = preloadedSnapshot === null,
    }: HydrateOptions = {},
  ): Promise<CatalogSnapshot> {
    trackService.releaseTracks(tracks.value)

    const normalizedTrackArtworkMode = normalizeTrackArtworkMode(trackArtworkMode)
    const nextTrackListComplete = trackListComplete === true
    const snapshot = await libraryService.loadCatalog(preloadedSnapshot, {
      trackArtworkMode: normalizedTrackArtworkMode,
    })

    libraries.value = sortByOrder(snapshot.libraries)
    playlists.value = sortByOrder(snapshot.playlists)
    tracks.value = snapshot.tracks
    playlistTrackRelations.value = snapshot.playlistTrackRelations
    catalogTrackListComplete.value = nextTrackListComplete
    catalogTrackArtworkMode.value = nextTrackListComplete ? normalizedTrackArtworkMode : 'none'

    if (revision !== null) {
      setCatalogRevision(revision)
    } else {
      markCatalogChanged()
    }

    return {
      libraries: libraries.value,
      playlists: playlists.value,
      tracks: tracks.value,
      playlistTrackRelations: playlistTrackRelations.value,
    }
  }

  function getLibraryById(libraryId: string | null | undefined): LibraryModel | null {
    return libraries.value.find((library) => library.id === libraryId) ?? null
  }

  function getPlaylistById(playlistId: string | null | undefined): PlaylistModel | null {
    return playlists.value.find((playlist) => playlist.id === playlistId) ?? null
  }

  function getTrackById(trackId: string | null | undefined): StoreTrackModel | null {
    return tracks.value.find((track) => track.id === trackId) ?? null
  }

  async function getOrLoadTrack(
    trackId: string | null | undefined,
    { includeArtwork = true, cache = true }: TrackLookupOptions = {},
  ): Promise<StoreTrackModel | null> {
    if (!trackId) {
      return null
    }

    const cachedTrack = getTrackById(trackId)

    if (
      cachedTrack &&
      (!includeArtwork || hasArtwork(cachedTrack) || cachedTrack[ARTWORK_LOADED_FLAG] === true)
    ) {
      return cachedTrack
    }

    const loadedTrack = await loadTrackRecord(trackId, includeArtwork)

    if (!loadedTrack) {
      return cachedTrack ?? null
    }

    const nextTrack = includeArtwork
      ? {
          ...loadedTrack,
          [ARTWORK_LOADED_FLAG]: true,
        }
      : loadedTrack

    if (cache) {
      replaceTrack(nextTrack)
    }

    return nextTrack
  }

  function getTracksForLibrary(libraryId: string | null | undefined): StoreTrackModel[] {
    return tracks.value.filter((track) => track.libraryId === libraryId)
  }

  function getPlaylistTrackRelations(playlistId: string): PlaylistTrackRelationModel[] {
    return playlistTrackRelations.value
      .filter((relation) => relation.playlistId === playlistId)
      .sort((left, right) => (left.order ?? 0) - (right.order ?? 0))
  }

  function getDefaultPlaylistForLibrary(libraryId: string | null | undefined): PlaylistModel | null {
    return (
      playlists.value.find(
        (playlist) =>
          playlist.libraryId === libraryId && playlist.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS,
      ) ?? null
    )
  }

  function getDefaultCollectionRef(libraryId: string | null | undefined): string | null {
    const playlist = getDefaultPlaylistForLibrary(libraryId)
    return playlist ? createPlaylistCollectionRef(playlist.id) : null
  }

  function isCollectionAvailableForLibrary(libraryId: string | null | undefined, collectionRef: string | null | undefined): boolean {
    const parsedCollection = parseCollectionRef(collectionRef)

    if (parsedCollection.type === 'playlist') {
      const playlist = getPlaylistById(parsedCollection.value)
      return playlist?.libraryId === libraryId
    }

    if (parsedCollection.type === 'view' && parsedCollection.value) {
      return SMART_VIEW_KEY_SET.has(parsedCollection.value)
    }

    return false
  }

  async function createLibrary(name: string) {
    const { library, defaultPlaylist } = await libraryService.createLibrary({ name })

    libraries.value = sortByOrder([...libraries.value, library])
    playlists.value = sortByOrder([...playlists.value, defaultPlaylist])
    markCatalogChanged()

    return {
      library,
      defaultPlaylist,
    }
  }

  async function renameLibrary(libraryId: string, name: string) {
    const nextLibrary = await libraryService.renameLibrary(libraryId, name)

    libraries.value = libraries.value.map((library) => (library.id === nextLibrary.id ? nextLibrary : library))
    markCatalogChanged()
    return nextLibrary
  }

  async function deleteLibrary(libraryId: string) {
    const result = await libraryService.deleteLibrary(libraryId)

    if (Array.isArray(result.libraries) && result.libraries.length > 0) {
      libraries.value = sortByOrder(result.libraries)
    } else {
      libraries.value = libraries.value.filter((library) => library.id !== result.deletedLibraryId)
      libraries.value = sortByOrder(libraries.value)
    }

    playlists.value = playlists.value.filter((playlist) => !result.deletedPlaylistIds.includes(playlist.id))
    playlistTrackRelations.value = playlistTrackRelations.value.filter(
      (relation) => !result.deletedRelationIds.includes(relation.id),
    )
    result.deletedTrackIds.forEach((trackId) => {
      removeTrackFromState(trackId)
    })
    playlists.value = sortByOrder(playlists.value)
    markCatalogChanged()

    return result
  }

  async function reorderLibraries(orderedLibraryIds: string[]) {
    const nextLibraries = await libraryService.reorderLibraries(orderedLibraryIds)
    libraries.value = sortByOrder(nextLibraries)
    markCatalogChanged()
    return libraries.value
  }

  async function createPlaylist({ libraryId, name }: { libraryId: string; name: string }) {
    const playlist = await playlistService.createPlaylist({ libraryId, name })
    playlists.value = sortByOrder([...playlists.value, playlist])
    markCatalogChanged()
    return playlist
  }

  async function renamePlaylist(playlistId: string, name: string) {
    const nextPlaylist = await playlistService.renamePlaylist(playlistId, name)
    playlists.value = playlists.value.map((playlist) => (playlist.id === nextPlaylist.id ? nextPlaylist : playlist))
    markCatalogChanged()
    return nextPlaylist
  }

  async function deletePlaylist(playlistId: string) {
    const result = await playlistService.deletePlaylist(playlistId)

    if (Array.isArray(result.playlists) && result.playlists.length > 0) {
      const updatedPlaylistIds = new Set(result.playlists.map((playlist) => playlist.id))
      const preservedPlaylists = playlists.value.filter(
        (playlist) =>
          playlist.libraryId !== result.libraryId &&
          playlist.id !== result.deletedPlaylistId &&
          !updatedPlaylistIds.has(playlist.id),
      )

      playlists.value = sortByOrder([...preservedPlaylists, ...result.playlists])
    } else {
      playlists.value = playlists.value.filter((playlist) => playlist.id !== result.deletedPlaylistId)
      playlists.value = sortByOrder(playlists.value)
    }

    playlistTrackRelations.value = playlistTrackRelations.value.filter(
      (relation) => !result.deletedRelationIds.includes(relation.id),
    )
    markCatalogChanged()

    return result
  }

  async function reorderPlaylists({ libraryId, orderedPlaylistIds }: { libraryId: string; orderedPlaylistIds: string[] }) {
    const nextLibraryPlaylists = await playlistService.reorderPlaylists(libraryId, orderedPlaylistIds)
    const libraryPlaylistIdSet = new Set(nextLibraryPlaylists.map((playlist) => playlist.id))
    const preservedPlaylists = playlists.value.filter((playlist) => !libraryPlaylistIdSet.has(playlist.id))

    playlists.value = sortByOrder([...preservedPlaylists, ...nextLibraryPlaylists])
    markCatalogChanged()
    return nextLibraryPlaylists
  }

  function appendImportedTracks(importedTracks: StoreTrackModel[] = []): StoreTrackModel[] {
    if (importedTracks.length === 0) {
      return []
    }

    tracks.value = [...tracks.value, ...importedTracks]
    markCatalogChanged()
    return importedTracks
  }

  function applyImportInvalidations(result: ImportInvalidationResult = {}) {
    const invalidatedTrackIds = Array.isArray(result.invalidatedTrackIds) ? result.invalidatedTrackIds : []
    const invalidatedRelationIds = Array.isArray(result.invalidatedRelationIds) ? result.invalidatedRelationIds : []
    const reorderedTracks = Array.isArray(result.reorderedTracks) ? result.reorderedTracks : []

    if (invalidatedTrackIds.length === 0 && invalidatedRelationIds.length === 0 && reorderedTracks.length === 0) {
      return
    }

    const invalidatedTrackIdSet = new Set(invalidatedTrackIds)
    const invalidatedRelationIdSet = new Set(invalidatedRelationIds)
    const reorderedTrackMap = new Map(reorderedTracks.map((track) => [track.id, track]))

    invalidatedTrackIds.forEach((trackId) => {
      removeTrackFromState(trackId)
    })

    if (reorderedTrackMap.size > 0) {
      tracks.value = tracks.value.map((track) => reorderedTrackMap.get(track.id) ?? track)
    }

    playlistTrackRelations.value = playlistTrackRelations.value.filter(
      (relation) =>
        !invalidatedRelationIdSet.has(relation.id) &&
        !invalidatedTrackIdSet.has(relation.trackId),
    )
    markCatalogChanged()
  }

  async function importSourceFiles({ libraryId, files }: ImportSourceFilesInput = {}) {
    const result = await trackService.importSourceFiles({
      libraryId,
      files,
    })

    applyImportInvalidations(result)
    appendImportedTracks(result.importedTracks)
    return result
  }

  async function scanAndImportTracks({
    libraryId,
    directories,
    respectDeletedImportPaths,
  }: ScanAndImportTracksInput = {}) {
    const result = await trackService.scanAndImport({
      libraryId,
      directories,
      respectDeletedImportPaths,
    })

    applyImportInvalidations(result)
    appendImportedTracks(result.importedTracks)
    return result
  }

  async function updateTrackMetadata(trackId: string, patch: TrackMetadataPatch) {
    const nextTrack = await trackService.updateTrackMetadata(trackId, patch)
    replaceTrack(
      mergeTrackPreservingSource(nextTrack, {
        preserveArtwork: !Object.prototype.hasOwnProperty.call(patch ?? {}, 'artwork'),
      }),
    )
    markCatalogChanged()
    return nextTrack
  }

  async function updateTrackSource(trackId: string, source: TrackSource) {
    const nextTrack = await trackService.updateTrackMetadata(trackId, { source })
    replaceTrack(nextTrack)
    markCatalogChanged()
    return nextTrack
  }

  async function addTrackToPlaylist({ playlistId, trackId, index }: PlaylistTrackMutation) {
    const result = await playlistService.addTrackToPlaylist({ playlistId, trackId, index })
    const relationIds = new Set(result.relations.map((relation) => relation.id))

    playlistTrackRelations.value = [
      ...playlistTrackRelations.value.filter(
        (relation) => relation.playlistId !== playlistId || !relationIds.has(relation.id),
      ),
      ...result.relations,
    ]
    markCatalogChanged()

    return result
  }

  async function removeTrackFromPlaylist({ playlistId, trackId }: Omit<PlaylistTrackMutation, 'index'>) {
    const result = await playlistService.removeTrackFromPlaylist({ playlistId, trackId })
    const relationIds = new Set(result.relations.map((relation) => relation.id))

    playlistTrackRelations.value = [
      ...playlistTrackRelations.value.filter(
        (relation) => {
          if (relation.playlistId !== playlistId) {
            return true
          }

          if (result.deletedRelationId && relation.id === result.deletedRelationId) {
            return false
          }

          return !relationIds.has(relation.id)
        },
      ),
      ...result.relations,
    ]
    markCatalogChanged()

    return result
  }

  async function deleteTrackFromLibrary(trackId: string) {
    const result = await trackService.deleteTrackFromLibrary(trackId)

    removeTrackFromState(result.deletedTrackId)
    playlistTrackRelations.value = playlistTrackRelations.value.filter(
      (relation) => !result.deletedRelationIds.includes(relation.id),
    )
    ;(result.reorderedTracks ?? []).forEach((track) => {
      replaceTrack(mergeTrackPreservingSource(track))
    })
    markCatalogChanged()

    return result
  }

  async function deleteTracksFromLibrary(trackIds: string[]) {
    const result = await trackService.deleteTracksFromLibrary(trackIds)

    ;(result.deletedTrackIds ?? []).forEach((trackId) => {
      removeTrackFromState(trackId)
    })
    playlistTrackRelations.value = playlistTrackRelations.value.filter(
      (relation) => !result.deletedRelationIds.includes(relation.id),
    )
    ;(result.reorderedTracks ?? []).forEach((track) => {
      replaceTrack(mergeTrackPreservingSource(track))
    })
    markCatalogChanged()

    return result
  }

  async function toggleFavorite(trackId: string) {
    const nextTrack = await trackService.toggleFavorite(trackId)
    replaceTrack(mergeTrackPreservingSource(nextTrack))
    markCatalogChanged()
    return nextTrack
  }

  async function setFavorite(trackId: string, isFavorite: boolean) {
    const nextTrack = await trackService.setFavorite(trackId, isFavorite)
    replaceTrack(mergeTrackPreservingSource(nextTrack))
    markCatalogChanged()
    return nextTrack
  }

  async function reorderPlaylistTracks({ playlistId, orderedTrackIds }: { playlistId: string; orderedTrackIds: string[] }) {
    const nextRelations = await playlistService.reorderPlaylistTracks(playlistId, orderedTrackIds)
    const relationIdSet = new Set(nextRelations.map((relation) => relation.id))

    playlistTrackRelations.value = [
      ...playlistTrackRelations.value.filter(
        (relation) => relation.playlistId !== playlistId || !relationIdSet.has(relation.id),
      ),
      ...nextRelations,
    ]
    markCatalogChanged()

    return nextRelations
  }

  function dispose() {
    trackLookupPromises.clear()
    trackService.releaseTracks(tracks.value)
    libraries.value = []
    playlists.value = []
    tracks.value = []
    playlistTrackRelations.value = []
    catalogTrackListComplete.value = false
    catalogTrackArtworkMode.value = 'none'
    markCatalogChanged()
  }

  return {
    libraries,
    playlists,
    tracks,
    playlistTrackRelations,
    catalogRevision,
    catalogTrackListComplete,
    catalogTrackArtworkMode,
    setCatalogRevision,
    libraryIds,
    trackIds,
    trackCount,
    hasTracks,
    hydrate,
    getLibraryById,
    getPlaylistById,
    getTrackById,
    getOrLoadTrack,
    cacheTrackArtwork,
    getTracksForLibrary,
    getPlaylistTrackRelations,
    getDefaultPlaylistForLibrary,
    getDefaultCollectionRef,
    isCollectionAvailableForLibrary,
    createLibrary,
    renameLibrary,
    deleteLibrary,
    reorderLibraries,
    createPlaylist,
    renamePlaylist,
    deletePlaylist,
    reorderPlaylists,
    importSourceFiles,
    scanAndImportTracks,
    updateTrackMetadata,
    updateTrackSource,
    addTrackToPlaylist,
    removeTrackFromPlaylist,
    deleteTrackFromLibrary,
    deleteTracksFromLibrary,
    toggleFavorite,
    setFavorite,
    reorderPlaylistTracks,
    dispose,
  }
}
