import { convertFileSrc, invoke, isTauri } from '@tauri-apps/api/core'
import { createLibraryModel } from '../../models/library'
import { createLyricsSnapshotModel } from '../../models/lyrics'
import { createPlaybackHistoryEntryModel } from '../../models/playbackHistory'
import { createPlaylistModel } from '../../models/playlist'
import { createPlaylistTrackRelationModel } from '../../models/playlistTrackRelation'
import { createPreferencesModel, createPersistablePreferencesModel } from '../../models/preferences'
import { createSessionSnapshotModel } from '../../models/session'
import { createExternalLibraryConnectionModel } from '../../models/externalLibrary'
import {
  createPersistedTrackModel,
  createRuntimeTrackFromPersistedTrack as createBaseRuntimeTrackFromPersistedTrack,
  revokeTrackResource,
} from '../../models/track'
import { formatCommandError } from '../errorNormalizer'

const DESKTOP_DATA_DRIVER = 'desktop'

type DesktopRecord = Record<string, any>
type DesktopRecordArray = DesktopRecord[]
type DesktopDataServiceOptions = DesktopRecord
type DesktopDataService = DesktopRecord

function normalizeObject(value: unknown): DesktopRecord {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {}
}

function normalizeRecordArray(value: unknown): DesktopRecordArray {
  return Array.isArray(value) ? value.filter((record) => record && typeof record === 'object') : []
}

function requireDesktopRecord(value: unknown, label: string): DesktopRecord {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`Desktop bridge returned an invalid ${label}.`)
  }

  return value
}

function optionalDesktopRecord(value: unknown): DesktopRecord | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : null
}

function isLocalAssetPath(value: string): boolean {
  return /^[a-zA-Z]:[\\/]/.test(value) || value.startsWith('/') || value.startsWith('\\\\')
}

function createRuntimeTrackFromPersistedTrack(record: DesktopRecord): DesktopRecord {
  const track = createBaseRuntimeTrackFromPersistedTrack(record)
  const artwork = typeof track.artwork === 'string' ? track.artwork.trim() : ''

  if (!artwork || !isTauri() || !isLocalAssetPath(artwork)) {
    return track
  }

  return {
    ...track,
    artwork: convertFileSrc(artwork),
    artworkAssetPath: artwork,
  }
}

function normalizeDesktopArtwork(value: unknown): string {
  const artwork = typeof value === 'string' ? value.trim() : ''

  if (!artwork || !isLocalAssetPath(artwork)) {
    return artwork
  }

  return isTauri() ? convertFileSrc(artwork) : ''
}

function withDesktopDriver(preferences: unknown = {}): DesktopRecord {
  const record = normalizeObject(preferences)

  return createPreferencesModel({
    ...record,
    dataDriver: DESKTOP_DATA_DRIVER,
  })
}

function normalizeCatalogSnapshot(snapshot: unknown = {}): DesktopRecord {
  const catalog = normalizeObject(snapshot)

  return {
    libraries: normalizeRecordArray(catalog.libraries).map((record) => createLibraryModel(record)),
    playlists: normalizeRecordArray(catalog.playlists).map((record) => createPlaylistModel(record)),
    tracks: normalizeRecordArray(catalog.tracks).map((record) => createRuntimeTrackFromPersistedTrack(record)),
    playlistTrackRelations: normalizeRecordArray(catalog.playlistTrackRelations).map((record) =>
      createPlaylistTrackRelationModel(record),
    ),
  }
}

function normalizeNavigationSummary(summary: unknown = {}): DesktopRecord {
  const record = normalizeObject(summary)

  return {
    activeLibrary: record.activeLibrary ?? null,
    activeCollectionKey: record.activeCollectionKey ?? null,
    libraryTrackCounts: normalizeObject(record.libraryTrackCounts),
    playlistTrackCounts: normalizeObject(record.playlistTrackCounts),
    smartCollectionCounts: normalizeObject(record.smartCollectionCounts),
    diagnostics: record?.diagnostics ?? null,
  }
}

function normalizeExternalRequestDiagnostics(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    url: typeof record.url === 'string' ? record.url : '',
    depth: typeof record.depth === 'string' ? record.depth : '',
    status: Number.isInteger(record.status) ? record.status : 0,
    durationMs: Number.isFinite(record.durationMs) ? record.durationMs : 0,
    entryCount: Number.isInteger(record.entryCount) ? record.entryCount : 0,
    byteCount: Number.isInteger(record.byteCount) ? record.byteCount : 0,
  }
}

function normalizeExternalTrackListDiagnostics(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    provider: typeof record.provider === 'string' ? record.provider : '',
    rootUrl: typeof record.rootUrl === 'string' ? record.rootUrl : '',
    totalMs: Number.isFinite(record.totalMs) ? record.totalMs : 0,
    requestCount: Number.isInteger(record.requestCount) ? record.requestCount : 0,
    failedRequestCount: Number.isInteger(record.failedRequestCount) ? record.failedRequestCount : 0,
    directoriesScanned: Number.isInteger(record.directoriesScanned) ? record.directoriesScanned : 0,
    directoriesQueued: Number.isInteger(record.directoriesQueued) ? record.directoriesQueued : 0,
    duplicateDirectoryCount: Number.isInteger(record.duplicateDirectoryCount)
      ? record.duplicateDirectoryCount
      : 0,
    entriesSeen: Number.isInteger(record.entriesSeen) ? record.entriesSeen : 0,
    collectionsSeen: Number.isInteger(record.collectionsSeen) ? record.collectionsSeen : 0,
    filesSeen: Number.isInteger(record.filesSeen) ? record.filesSeen : 0,
    audioFilesSeen: Number.isInteger(record.audioFilesSeen) ? record.audioFilesSeen : 0,
    nonAudioFilesSkipped: Number.isInteger(record.nonAudioFilesSkipped) ? record.nonAudioFilesSkipped : 0,
    maxConcurrency: Number.isInteger(record.maxConcurrency) ? record.maxConcurrency : 0,
    limit: Number.isInteger(record.limit) ? record.limit : null,
    truncated: record.truncated === true,
    slowRequests: normalizeRecordArray(record.slowRequests).map((item) =>
      normalizeExternalRequestDiagnostics(item),
    ),
  }
}

function normalizePerformanceResourceDiagnostics(value: unknown = null): object | null {
  return value && typeof value === 'object' ? value : null
}

function normalizeDiagnosticStepProfiles(value: unknown = null): unknown[] {
  return Array.isArray(value) ? value : []
}

function normalizeBootstrapDiagnostics(diagnostics: unknown = {}): DesktopRecord {
  const record = normalizeObject(diagnostics)
  const totalMs = Number.isFinite(record.totalMs) ? record.totalMs : 0
  const roundTripMs = Number.isFinite(record.roundTripMs) ? record.roundTripMs : totalMs

  return {
    connectionMs: Number.isFinite(record.connectionMs) ? record.connectionMs : 0,
    revisionsMs: Number.isFinite(record.revisionsMs) ? record.revisionsMs : 0,
    preferencesMs: Number.isFinite(record.preferencesMs) ? record.preferencesMs : 0,
    sessionMs: Number.isFinite(record.sessionMs) ? record.sessionMs : 0,
    catalogCacheHit: record?.catalogCacheHit === true,
    catalogCacheMs: Number.isFinite(record.catalogCacheMs) ? record.catalogCacheMs : 0,
    catalogConsistencyMs: Number.isFinite(record.catalogConsistencyMs)
      ? record.catalogConsistencyMs
      : 0,
    catalogLoadMs: Number.isFinite(record.catalogLoadMs) ? record.catalogLoadMs : 0,
    catalogMs: Number.isFinite(record.catalogMs) ? record.catalogMs : 0,
    catalogTracksIncluded: record?.catalogTracksIncluded === true,
    catalogTrackCount: Number.isInteger(record.catalogTrackCount) ? record.catalogTrackCount : 0,
    catalogRelationCount: Number.isInteger(record.catalogRelationCount) ? record.catalogRelationCount : 0,
    trackCacheWarmMs: Number.isFinite(record.trackCacheWarmMs) ? record.trackCacheWarmMs : 0,
    trackCacheEntries: Number.isInteger(record.trackCacheEntries) ? record.trackCacheEntries : 0,
    historyMs: Number.isFinite(record.historyMs) ? record.historyMs : 0,
    navigationMs: Number.isFinite(record.navigationMs) ? record.navigationMs : 0,
    totalMs,
    roundTripMs,
    invokeOverheadMs: Math.max(0, roundTripMs - totalMs),
    process: normalizePerformanceResourceDiagnostics(record?.process),
    stepProfiles: normalizeDiagnosticStepProfiles(record?.stepProfiles),
  }
}

function normalizeBootstrapManifest(manifest: unknown = {}): DesktopRecord {
  const record = normalizeObject(manifest)
  const revisions = normalizeObject(record?.revisions)

  return {
    version: typeof record.version === 'string' ? record.version : 'desktop-bootstrap-v1',
    generatedAt: typeof record.generatedAt === 'string' ? record.generatedAt : '',
    revisions: {
      catalog: Number.isFinite(revisions.catalog) ? revisions.catalog : 0,
      navigation: Number.isFinite(revisions.navigation) ? revisions.navigation : 0,
      history: Number.isFinite(revisions.history) ? revisions.history : 0,
      preferences: Number.isFinite(revisions.preferences) ? revisions.preferences : 0,
      session: Number.isFinite(revisions.session) ? revisions.session : 0,
    },
    catalogConsistencyChecked: record?.catalogConsistencyChecked === true,
    trackQueryCacheReady: record?.trackQueryCacheReady === true,
  }
}

function normalizePlaybackCommandResult(result: unknown = {}): DesktopRecord {
  const record = normalizeObject(result)

  return {
    session: createSessionSnapshotModel(record?.session ?? {}),
    playback: normalizeObject(record?.playback),
    historyEntries: normalizeRecordArray(record?.historyEntries).map((entry) => createPlaybackHistoryEntryModel(entry)),
  }
}

function normalizeFiniteNumber(value: unknown, fallback = 0): number {
  return Number.isFinite(value) ? Number(value) : fallback
}

function normalizeNonNegativeNumber(value: unknown, fallback = 0): number {
  return Math.max(0, normalizeFiniteNumber(value, fallback))
}

function normalizeNonNegativeInteger(value: unknown, fallback = 0): number {
  return Number.isInteger(value) && Number(value) >= 0 ? Number(value) : fallback
}

function normalizeListeningStatsSummary(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    totalSeconds: normalizeNonNegativeNumber(record.totalSeconds),
    playCount: normalizeNonNegativeInteger(record.playCount),
    trackCount: normalizeNonNegativeInteger(record.trackCount),
    albumCount: normalizeNonNegativeInteger(record.albumCount),
    activeDays: normalizeNonNegativeInteger(record.activeDays),
    peakDay: typeof record.peakDay === 'string' ? record.peakDay : null,
    peakDaySeconds: normalizeNonNegativeNumber(record.peakDaySeconds),
    longestStreakDays: normalizeNonNegativeInteger(record.longestStreakDays),
  }
}

function normalizeListeningStatsTrack(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    trackId: typeof record.trackId === 'string' ? record.trackId : '',
    title: typeof record.title === 'string' ? record.title : '',
    artist: typeof record.artist === 'string' ? record.artist : '',
    album: typeof record.album === 'string' ? record.album : '',
    albumArtist: typeof record.albumArtist === 'string' ? record.albumArtist : '',
    artwork: normalizeDesktopArtwork(record.artwork),
    duration: normalizeNonNegativeNumber(record.duration),
    listenSeconds: normalizeNonNegativeNumber(record.listenSeconds),
    playCount: normalizeNonNegativeInteger(record.playCount),
  }
}

function normalizeListeningStatsAlbumGroup(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    key: typeof record.key === 'string' ? record.key : '',
    album: typeof record.album === 'string' ? record.album : '',
    albumArtist: typeof record.albumArtist === 'string' ? record.albumArtist : '',
    artwork: normalizeDesktopArtwork(record.artwork),
    listenSeconds: normalizeNonNegativeNumber(record.listenSeconds),
    playCount: normalizeNonNegativeInteger(record.playCount),
    trackCount: normalizeNonNegativeInteger(record.trackCount),
    tracks: normalizeRecordArray(record.tracks).map((track) => normalizeListeningStatsTrack(track)),
  }
}

function normalizeListeningStatsSnapshot(value: unknown = {}): DesktopRecord {
  const record = normalizeObject(value)

  return {
    generatedAt: typeof record.generatedAt === 'string' ? record.generatedAt : '',
    libraryId: typeof record.libraryId === 'string' ? record.libraryId : null,
    days: normalizeNonNegativeInteger(record.days, 365),
    summary: normalizeListeningStatsSummary(record.summary),
    daily: normalizeRecordArray(record.daily)
      .map((day) => ({
        date: typeof day.date === 'string' ? day.date : '',
        seconds: normalizeNonNegativeNumber(day.seconds),
        playCount: normalizeNonNegativeInteger(day.playCount),
      }))
      .filter((day) => day.date),
    topTracks: normalizeRecordArray(record.topTracks).map((track) => normalizeListeningStatsTrack(track)),
    albumGroups: normalizeRecordArray(record.albumGroups).map((album) => normalizeListeningStatsAlbumGroup(album)),
  }
}

function normalizeDesktopResetResult(result: unknown = {}): DesktopRecord {
  const record = normalizeObject(result)

  return {
    managedStorageDeleted: record?.managedStorageDeleted === true,
    managedStoragePath:
      typeof record?.managedStoragePath === 'string' ? record.managedStoragePath : '',
    playback: normalizeObject(record?.playback),
  }
}

function sanitizeTrackPatch(patch: unknown = {}): DesktopRecord {
  const record = normalizeObject(patch)
  const nextPatch: DesktopRecord = {}

  if (typeof record.libraryId === 'string') {
    nextPatch.libraryId = record.libraryId
  }

  if (Number.isInteger(record.libraryOrder) && record.libraryOrder >= 0) {
    nextPatch.libraryOrder = record.libraryOrder
  }

  if (typeof record.isFavorite === 'boolean') {
    nextPatch.isFavorite = record.isFavorite
  }

  if (typeof record.title === 'string') {
    nextPatch.title = record.title
  }

  if (typeof record.artist === 'string') {
    nextPatch.artist = record.artist
  }

  if (typeof record.albumArtist === 'string') {
    nextPatch.albumArtist = record.albumArtist
  }

  if (typeof record.album === 'string') {
    nextPatch.album = record.album
  }

  if (typeof record.genre === 'string') {
    nextPatch.genre = record.genre
  }

  if (Number.isInteger(record.year)) {
    nextPatch.year = record.year
  }

  if (Number.isInteger(record.trackNumber)) {
    nextPatch.trackNumber = record.trackNumber
  }

  if (Number.isInteger(record.trackTotal)) {
    nextPatch.trackTotal = record.trackTotal
  }

  if (Number.isInteger(record.discNumber)) {
    nextPatch.discNumber = record.discNumber
  }

  if (Number.isInteger(record.discTotal)) {
    nextPatch.discTotal = record.discTotal
  }

  if (typeof record.composer === 'string') {
    nextPatch.composer = record.composer
  }

  if (typeof record.lyricist === 'string') {
    nextPatch.lyricist = record.lyricist
  }

  if (typeof record.comment === 'string') {
    nextPatch.comment = record.comment
  }

  if (typeof record.lyricsPath === 'string') {
    nextPatch.lyricsPath = record.lyricsPath
  }

  if (typeof record.displayTitle === 'string') {
    nextPatch.displayTitle = record.displayTitle
  }

  if (typeof record.fileName === 'string') {
    nextPatch.fileName = record.fileName
  }

  if (Number.isFinite(record.fileSize)) {
    nextPatch.fileSize = record.fileSize
    nextPatch.size = record.fileSize
  }

  if (Number.isFinite(record.size)) {
    nextPatch.fileSize = record.size
    nextPatch.size = record.size
  }

  if (Number.isFinite(record.duration)) {
    nextPatch.duration = record.duration
  }

  if (typeof record.format === 'string') {
    nextPatch.format = record.format
  }

  if (Number.isFinite(record.bitrate)) {
    nextPatch.bitrate = record.bitrate
  }

  if (Number.isFinite(record.sampleRate)) {
    nextPatch.sampleRate = record.sampleRate
  }

  if (Number.isFinite(record.bitDepth)) {
    nextPatch.bitDepth = record.bitDepth
  }

  if (typeof record.artwork === 'string') {
    nextPatch.artwork = record.artwork
  }

  if (typeof record.mimeType === 'string') {
    nextPatch.mimeType = record.mimeType
  }

  if (typeof record.importedAt === 'string') {
    nextPatch.importedAt = record.importedAt
  }

  if (Number.isInteger(record.metadataVersion)) {
    nextPatch.metadataVersion = record.metadataVersion
  }

  if (record.source && typeof record.source === 'object') {
    nextPatch.source = record.source
  }

  return nextPatch
}

export function createDesktopDataService(options: DesktopDataServiceOptions = {}): DesktopDataService {
  if (!isTauri()) {
    throw new Error('OFPlayer desktop data service requires the Tauri runtime.')
  }

  async function loadDesktopCatalogSnapshot({ trackArtworkMode = 'all' }: DesktopRecord = {}) {
    const snapshot = await invoke('desktop_catalog_load_snapshot', {
      request: {
        trackArtworkMode,
      },
    })
    return normalizeCatalogSnapshot(snapshot)
  }

  async function ensureCatalogBootstrapped() {
    return undefined
  }

  async function loadDesktopHistory(limit: number) {
    const entries = await invoke('desktop_history_load_recent', {
      request: {
        limit,
      },
    })

    return normalizeRecordArray(entries).map((entry) => createPlaybackHistoryEntryModel(entry))
  }

  async function ensureHistoryBootstrapped() {
    return undefined
  }

  async function upsertCatalogRecords(command: string, records: DesktopRecordArray) {
    await ensureCatalogBootstrapped()
    await invoke(command, {
      request: {
        records,
      },
    })
  }

  async function deleteCatalogRecords(command: string, ids: string[]) {
    await ensureCatalogBootstrapped()
    await invoke(command, {
      request: {
        ids,
      },
    })
  }

  function normalizePlaylistTrackMutationResult(result: unknown = {}) {
    const record = normalizeObject(result)

    return {
      relation: record.relation ? createPlaylistTrackRelationModel(record.relation) : null,
      relations: normalizeRecordArray(record.relations).map((item) => createPlaylistTrackRelationModel(item)),
    }
  }

  function normalizePlaylistTrackRemoveResult(result: unknown = {}) {
    const record = normalizeObject(result)

    return {
      deletedRelationId: record.deletedRelationId ?? null,
      relations: normalizeRecordArray(record.relations).map((item) => createPlaylistTrackRelationModel(item)),
    }
  }

  function normalizePlaylistDeleteResult(result: unknown = {}) {
    const record = normalizeObject(result)

    return {
      deletedPlaylistId: record.deletedPlaylistId ?? '',
      deletedRelationIds: Array.isArray(record.deletedRelationIds) ? record.deletedRelationIds : [],
      libraryId: record.libraryId ?? '',
      playlists: normalizeRecordArray(record.playlists).map((item) => createPlaylistModel(item)),
    }
  }

  function normalizeLibraryCreateResult(result: unknown = {}) {
    const record = normalizeObject(result)

    return {
      library: createLibraryModel(requireDesktopRecord(record.library, 'library create result')),
      defaultPlaylist: createPlaylistModel(
        requireDesktopRecord(record.defaultPlaylist, 'default playlist create result'),
      ),
    }
  }

function normalizeLibraryDeleteResult(result: unknown = {}) {
  const record = normalizeObject(result)

  return {
    deletedLibraryId: record.deletedLibraryId ?? '',
    deletedPlaylistIds: Array.isArray(record.deletedPlaylistIds) ? record.deletedPlaylistIds : [],
    deletedTrackIds: Array.isArray(record.deletedTrackIds) ? record.deletedTrackIds : [],
    deletedRelationIds: Array.isArray(record.deletedRelationIds) ? record.deletedRelationIds : [],
    fallbackLibraryId: record.fallbackLibraryId ?? null,
    libraries: normalizeRecordArray(record.libraries).map((item) => createLibraryModel(item)),
    session: record?.session ? createSessionSnapshotModel(record.session) : null,
    playback: normalizeObject(record?.playback),
  }
}

function normalizeTrackDeleteResult(result: unknown = {}) {
  const record = normalizeObject(result)

  return {
    deletedTrackId: record.deletedTrackId ?? '',
    deletedRelationIds: Array.isArray(record.deletedRelationIds) ? record.deletedRelationIds : [],
    libraryId: record.libraryId ?? '',
    reorderedTracks: normalizeRecordArray(record.reorderedTracks).map((item) =>
      createRuntimeTrackFromPersistedTrack(item),
    ),
    session: record?.session ? createSessionSnapshotModel(record.session) : null,
    playback: normalizeObject(record?.playback),
  }
}

function normalizeTrackBatchDeleteResult(result: unknown = {}) {
  const record = normalizeObject(result)

  return {
    deletedTrackIds: Array.isArray(record.deletedTrackIds) ? record.deletedTrackIds : [],
    deletedRelationIds: Array.isArray(record.deletedRelationIds) ? record.deletedRelationIds : [],
    libraryIds: Array.isArray(record.libraryIds) ? record.libraryIds : [],
    reorderedTracks: normalizeRecordArray(record.reorderedTracks).map((item) =>
      createRuntimeTrackFromPersistedTrack(item),
    ),
    session: record?.session ? createSessionSnapshotModel(record.session) : null,
    playback: normalizeObject(record?.playback),
  }
}

function normalizeLibraryImportDiagnostics(diagnostics: unknown = null) {
  if (!diagnostics || typeof diagnostics !== 'object' || Array.isArray(diagnostics)) {
    return null
  }
  const record = diagnostics as DesktopRecord

  return {
    totalMs: Number.isFinite(record.totalMs) ? record.totalMs : 0,
    discoverMs: Number.isFinite(record.discoverMs) ? record.discoverMs : 0,
    filterMs: Number.isFinite(record.filterMs) ? record.filterMs : 0,
    prepareMs: Number.isFinite(record.prepareMs) ? record.prepareMs : 0,
    persistMs: Number.isFinite(record.persistMs) ? record.persistMs : 0,
    playbackSyncMs: Number.isFinite(record.playbackSyncMs) ? record.playbackSyncMs : 0,
    copyMs: Number.isFinite(record.copyMs) ? record.copyMs : 0,
    metadataMs: Number.isFinite(record.metadataMs) ? record.metadataMs : 0,
    metadataFallbackCount: Number.isInteger(record.metadataFallbackCount)
      ? record.metadataFallbackCount
      : 0,
    directoriesScanned: Number.isInteger(record.directoriesScanned)
      ? record.directoriesScanned
      : 0,
    entriesScanned: Number.isInteger(record.entriesScanned) ? record.entriesScanned : 0,
    discoveredTotal: Number.isInteger(record.discoveredTotal) ? record.discoveredTotal : 0,
    candidateTotal: Number.isInteger(record.candidateTotal) ? record.candidateTotal : 0,
    importedTotal: Number.isInteger(record.importedTotal) ? record.importedTotal : 0,
    process: normalizePerformanceResourceDiagnostics(record?.process),
    stepProfiles: normalizeDiagnosticStepProfiles(record?.stepProfiles),
  }
}

function normalizeLibraryImportJobStage(stage: unknown = {}) {
  const record = normalizeObject(stage)

  return {
    key: typeof record.key === 'string' ? record.key : '',
    status: typeof record.status === 'string' ? record.status : 'pending',
    startedAt: typeof record.startedAt === 'string' ? record.startedAt : null,
    completedAt: typeof record.completedAt === 'string' ? record.completedAt : null,
    durationMs: Number.isFinite(record.durationMs) ? record.durationMs : null,
    processed: Number.isInteger(record.processed) ? record.processed : 0,
    total: Number.isInteger(record.total) ? record.total : 0,
  }
}

function normalizeLibraryImportJob(job: unknown = {}) {
  const record = normalizeObject(job)

  return {
    id: typeof record.id === 'string' ? record.id : '',
    mode: typeof record.mode === 'string' ? record.mode : '',
    status: typeof record.status === 'string' ? record.status : 'queued',
    libraryId: typeof record.libraryId === 'string' ? record.libraryId : '',
    createdAt: typeof record.createdAt === 'string' ? record.createdAt : '',
    updatedAt: typeof record.updatedAt === 'string' ? record.updatedAt : '',
    completedAt: typeof record.completedAt === 'string' ? record.completedAt : null,
    currentStage: typeof record.currentStage === 'string' ? record.currentStage : '',
    discoveredTotal: Number.isInteger(record.discoveredTotal) ? record.discoveredTotal : 0,
    candidateTotal: Number.isInteger(record.candidateTotal) ? record.candidateTotal : 0,
    importedTotal: Number.isInteger(record.importedTotal) ? record.importedTotal : 0,
    directoriesScanned: Number.isInteger(record.directoriesScanned) ? record.directoriesScanned : 0,
    entriesScanned: Number.isInteger(record.entriesScanned) ? record.entriesScanned : 0,
    currentFile: typeof record.currentFile === 'string' ? record.currentFile : '',
    error: record.error ? formatCommandError(record.error, '') : null,
    diagnostics: normalizeLibraryImportDiagnostics(record.diagnostics),
    stages: normalizeRecordArray(record.stages).map((stage) => normalizeLibraryImportJobStage(stage)),
  }
}

function normalizeLibraryImportResult(result: unknown = {}) {
  const record = normalizeObject(result)

  return {
    job: record?.job ? normalizeLibraryImportJob(record.job) : null,
    importedTracks: normalizeRecordArray(record.importedTracks).map((item) =>
      createRuntimeTrackFromPersistedTrack(item),
    ),
    invalidatedTrackIds: Array.isArray(record.invalidatedTrackIds)
      ? record.invalidatedTrackIds.filter((trackId) => typeof trackId === 'string')
      : [],
    invalidatedRelationIds: Array.isArray(record.invalidatedRelationIds)
      ? record.invalidatedRelationIds.filter((relationId) => typeof relationId === 'string')
      : [],
    reorderedTracks: normalizeRecordArray(record.reorderedTracks).map((item) =>
      createRuntimeTrackFromPersistedTrack(item),
    ),
    discoveredTotal: Number.isInteger(record.discoveredTotal) ? record.discoveredTotal : 0,
    candidateTotal: Number.isInteger(record.candidateTotal) ? record.candidateTotal : 0,
    diagnostics: normalizeLibraryImportDiagnostics(record?.diagnostics),
    session: record?.session ? createSessionSnapshotModel(record.session) : null,
    playback: normalizeObject(record?.playback),
    historyEntries: normalizeRecordArray(record?.historyEntries).map((entry) =>
      createPlaybackHistoryEntryModel(entry),
    ),
  }
}

  return {
    driver: DESKTOP_DATA_DRIVER,
    capabilities: {
      persistentLibraryAssets: true,
    },
    bootstrap: {
      async loadAppState({ historyLimit = 100 } = {}) {
        const requestStartedAt = performance.now()
        const result = normalizeObject(await invoke('desktop_state_load_bootstrap', {
          request: {
            historyLimit,
            includeCatalogTracks: false,
            includePlaylistTrackRelations: true,
            warmTrackQueryCache: true,
          },
        }))
        const roundTripMs = Math.round(performance.now() - requestStartedAt)

        return {
          manifest: normalizeBootstrapManifest(result?.manifest),
          preferences: withDesktopDriver(result?.preferences ?? {}),
          session: createSessionSnapshotModel(result?.session ?? {}),
          catalog: normalizeCatalogSnapshot(result?.catalog),
          history: normalizeRecordArray(result?.history).map((entry) => createPlaybackHistoryEntryModel(entry)),
          navigationSummary: normalizeNavigationSummary(result?.navigation),
          diagnostics: normalizeBootstrapDiagnostics({
            ...(result?.diagnostics ?? {}),
            roundTripMs,
          }),
        }
      },
    },
    maintenance: {
      async resetAllData() {
        return normalizeDesktopResetResult(await invoke('desktop_state_reset_all_data'))
      },
    },
    preferences: {
      async load() {
        const persistedPreferences = await invoke('desktop_state_load_preferences')
        return withDesktopDriver(persistedPreferences ?? {})
      },
      async save(preferences: unknown) {
        const nextPreferences = createPersistablePreferencesModel(withDesktopDriver(preferences))
        await invoke('desktop_state_save_preferences', {
          value: nextPreferences,
        })
        return true
      },
    },
    session: {
      async loadSnapshot() {
        const persistedSession = await invoke('desktop_state_load_session')
        return createSessionSnapshotModel(persistedSession ?? {})
      },
      async saveSnapshot(session: unknown) {
        const snapshot = createSessionSnapshotModel(session)
        await invoke('desktop_state_save_session', {
          value: snapshot,
        })
        return true
      },
    },
    externalSources: {
      async providerCapabilities(provider: string) {
        return invoke('external_library_provider_capabilities', {
          request: {
            provider,
          },
        })
      },
      async testConnection(connection: unknown) {
        return invoke('external_library_test_connection', {
          request: {
            connection,
          },
        })
      },
      async listLibraries({ connection, limit = null }: DesktopRecord = {}) {
        const result = normalizeObject(await invoke('external_library_list_libraries', {
          request: {
            connection,
            limit,
          },
        }))

        return result?.libraries ?? []
      },
      async listTracks({ connection, limit = null }: DesktopRecord = {}) {
        const result = normalizeObject(await invoke('external_library_list_tracks', {
          request: {
            connection,
            limit,
          },
        }))
        const tracks = normalizeRecordArray(result?.tracks)

        return {
          tracks,
          total: Number.isInteger(result?.total) ? result.total : tracks.length,
          diagnostics: normalizeExternalTrackListDiagnostics(result?.diagnostics),
        }
      },
      async resolvePlaybackSource({
        connection,
        track,
        includeMetadata = true,
        metadataOnly = false,
      }: DesktopRecord = {}) {
        const result = normalizeObject(await invoke('external_library_resolve_playback_source', {
          request: {
            connection,
            track,
            includeMetadata,
            metadataOnly,
          },
        }))

        return {
          source: result?.source ?? null,
          metadata: normalizeObject(result?.metadata),
        }
      },
    },
    externalLibraries: {
      async getConnections() {
        const records = await invoke('desktop_external_library_load_connections')
        return normalizeRecordArray(records).map((record) => createExternalLibraryConnectionModel(record))
      },
      async putConnection(record: DesktopRecord) {
        const nextRecord = createExternalLibraryConnectionModel({
          ...record,
          updatedAt: new Date().toISOString(),
        })
        const savedRecord = await invoke('desktop_external_library_put_connection', {
          value: nextRecord,
        })

        return createExternalLibraryConnectionModel(optionalDesktopRecord(savedRecord) ?? nextRecord)
      },
      async deleteConnection(connectionId: string) {
        return invoke('desktop_external_library_delete_connection', {
          connectionId,
        })
      },
    },
    playbackSession: {
      async syncCatalog() {
        const result = await invoke('playback_session_sync_catalog')
        return normalizePlaybackCommandResult(result)
      },
      async setQueue(trackIds = []) {
        const session = await invoke('playback_session_set_queue', {
          request: {
            trackIds,
          },
        })
        return createSessionSnapshotModel(session ?? {})
      },
      async selectTrack({
        trackId,
        queueTrackIds = null,
        autoplay = true,
        playbackSource = null,
      }: DesktopRecord = {}) {
        const result = await invoke('playback_session_select_track', {
          request: {
            trackId,
            queueTrackIds,
            autoplay,
            playbackSource,
          },
        })

        return normalizePlaybackCommandResult(result)
      },
      async playCurrent() {
        const result = await invoke('playback_session_play_current')
        return normalizePlaybackCommandResult(result)
      },
      async pause() {
        const result = await invoke('playback_session_pause')
        return normalizePlaybackCommandResult(result)
      },
      async playNext() {
        const result = await invoke('playback_session_next')
        return normalizePlaybackCommandResult(result)
      },
      async playPrevious({ restartThresholdSeconds = 3 } = {}) {
        const result = await invoke('playback_session_previous', {
          request: {
            restartThresholdSeconds,
          },
        })
        return normalizePlaybackCommandResult(result)
      },
      async handleEnded() {
        const result = await invoke('playback_session_handle_ended')
        return normalizePlaybackCommandResult(result)
      },
    },
    catalog: {
      async loadSnapshot(options = {}) {
        await ensureCatalogBootstrapped()
        return loadDesktopCatalogSnapshot(options)
      },
      async getLibraries() {
        return (await this.loadSnapshot()).libraries
      },
      async putLibrary(record: DesktopRecord) {
        const nextRecord = createLibraryModel(record)
        await upsertCatalogRecords('desktop_catalog_put_libraries', [nextRecord])
        return nextRecord
      },
      async putLibraries(records: unknown) {
        const nextRecords = normalizeRecordArray(records).map((record) => createLibraryModel(record))
        await upsertCatalogRecords('desktop_catalog_put_libraries', nextRecords)
        return nextRecords
      },
      async deleteLibrary(libraryId: string) {
        await deleteCatalogRecords('desktop_catalog_delete_libraries', [libraryId])
        return true
      },
      async getPlaylists() {
        return (await this.loadSnapshot()).playlists
      },
      async putPlaylist(record: DesktopRecord) {
        const nextRecord = createPlaylistModel(record)
        await upsertCatalogRecords('desktop_catalog_put_playlists', [nextRecord])
        return nextRecord
      },
      async putPlaylists(records: unknown) {
        const nextRecords = normalizeRecordArray(records).map((record) => createPlaylistModel(record))
        await upsertCatalogRecords('desktop_catalog_put_playlists', nextRecords)
        return nextRecords
      },
      async deletePlaylist(playlistId: string) {
        await deleteCatalogRecords('desktop_catalog_delete_playlists', [playlistId])
        return true
      },
      async getPlaylistTrackRelations() {
        return (await this.loadSnapshot()).playlistTrackRelations
      },
      async putPlaylistTrackRelation(record: DesktopRecord) {
        const nextRecord = createPlaylistTrackRelationModel(record)
        await upsertCatalogRecords('desktop_catalog_put_playlist_track_relations', [nextRecord])
        return nextRecord
      },
      async putPlaylistTrackRelations(records: unknown) {
        const nextRecords = normalizeRecordArray(records).map((record) =>
          createPlaylistTrackRelationModel(record),
        )
        await upsertCatalogRecords('desktop_catalog_put_playlist_track_relations', nextRecords)
        return nextRecords
      },
      async deletePlaylistTrackRelation(relationId: string) {
        await deleteCatalogRecords('desktop_catalog_delete_playlist_track_relations', [relationId])
        return true
      },
      async deletePlaylistTrackRelations(relationIds: string[] | null | undefined) {
        await deleteCatalogRecords('desktop_catalog_delete_playlist_track_relations', relationIds ?? [])
        return true
      },
      async getTrack(trackId: string, { includeArtwork = true }: DesktopRecord = {}) {
        await ensureCatalogBootstrapped()
        const record = await invoke('desktop_catalog_get_track', {
          request: {
            trackId,
            includeArtwork,
          },
        })
        const trackRecord = optionalDesktopRecord(record)

        return trackRecord ? createRuntimeTrackFromPersistedTrack(trackRecord) : null
      },
      async getTracks() {
        return (await this.loadSnapshot()).tracks
      },
      async updateTrack(trackId: string, patch: unknown) {
        await ensureCatalogBootstrapped()
        const nextTrack = await invoke('desktop_catalog_update_track', {
          request: {
            trackId,
            patch: sanitizeTrackPatch(patch),
          },
        })

        const trackRecord = optionalDesktopRecord(nextTrack)

        return trackRecord ? createRuntimeTrackFromPersistedTrack(trackRecord) : null
      },
      async putTrack(record: DesktopRecord) {
        const persistedTrack = createPersistedTrackModel(record)
        await upsertCatalogRecords('desktop_catalog_put_tracks', [persistedTrack])
        return createRuntimeTrackFromPersistedTrack(persistedTrack)
      },
      async putTracks(records: unknown) {
        const persistedTracks = normalizeRecordArray(records).map((record) => createPersistedTrackModel(record))
        await upsertCatalogRecords('desktop_catalog_put_tracks', persistedTracks)
        return persistedTracks.map((record) => createRuntimeTrackFromPersistedTrack(record))
      },
      async deleteTrack(trackId: string) {
        await deleteCatalogRecords('desktop_catalog_delete_tracks', [trackId])
        return true
      },
      async deleteTracks(trackIds: string[] | null | undefined) {
        await deleteCatalogRecords('desktop_catalog_delete_tracks', trackIds ?? [])
        return true
      },
      trackTransactions: {
        async importSourceFiles({ libraryId, files = [] }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeLibraryImportResult(
            await invoke('desktop_library_import_files', {
              request: {
                libraryId,
                files,
              },
            }),
          )
        },
        async scanAndImport({
          libraryId,
          directories = [],
          respectDeletedImportPaths = true,
        }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeLibraryImportResult(
            await invoke('desktop_library_scan_import', {
              request: {
                libraryId,
                directories,
                respectDeletedImportPaths: respectDeletedImportPaths !== false,
              },
            }),
          )
        },
        async updateTrackMetadata({ trackId, patch }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          const nextTrack = await invoke('desktop_catalog_update_track', {
            request: {
              trackId,
              patch: sanitizeTrackPatch(patch),
            },
          })

          const trackRecord = optionalDesktopRecord(nextTrack)

          return trackRecord ? createRuntimeTrackFromPersistedTrack(trackRecord) : null
        },
        async setFavorite({ trackId, isFavorite }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return createRuntimeTrackFromPersistedTrack(
            requireDesktopRecord(
              await invoke('desktop_track_set_favorite', {
                request: {
                  trackId,
                  isFavorite: Boolean(isFavorite),
                },
              }),
              'favorite track result',
            ),
          )
        },
        async toggleFavorite({ trackId }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return createRuntimeTrackFromPersistedTrack(
            requireDesktopRecord(
              await invoke('desktop_track_toggle_favorite', {
                request: {
                  trackId,
                },
              }),
              'favorite toggle track result',
            ),
          )
        },
        async deleteTrackFromLibrary({ trackId }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeTrackDeleteResult(
            await invoke('desktop_track_delete_from_library', {
              request: {
                trackId,
              },
            }),
          )
        },
        async deleteTracksFromLibrary({ trackIds = [] }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeTrackBatchDeleteResult(
            await invoke('desktop_tracks_delete_from_library', {
              request: {
                trackIds,
              },
            }),
          )
        },
      },
      libraryTransactions: {
        async createLibrary({ name }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeLibraryCreateResult(
            await invoke('desktop_library_create', {
              request: {
                name,
              },
            }),
          )
        },
        async renameLibrary({ libraryId, name }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return createLibraryModel(
            requireDesktopRecord(
              await invoke('desktop_library_rename', {
                request: {
                  libraryId,
                  name,
                },
              }),
              'library rename result',
            ),
          )
        },
        async deleteLibrary({ libraryId }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizeLibraryDeleteResult(
            await invoke('desktop_library_delete', {
              request: {
                libraryId,
              },
            }),
          )
        },
        async reorderLibraries({ orderedLibraryIds }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          const records = await invoke('desktop_library_reorder', {
            request: {
              orderedLibraryIds,
            },
          })

          return normalizeRecordArray(records).map((record) => createLibraryModel(record))
        },
      },
      playlistTransactions: {
        async createPlaylist({ libraryId, name }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          const record = await invoke('desktop_playlist_create', {
            request: {
              libraryId,
              name,
            },
          })

          return createPlaylistModel(requireDesktopRecord(record, 'playlist create result'))
        },
        async renamePlaylist({ playlistId, name }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return createPlaylistModel(
            requireDesktopRecord(
              await invoke('desktop_playlist_rename', {
                request: {
                  playlistId,
                  name,
                },
              }),
              'playlist rename result',
            ),
          )
        },
        async deletePlaylist({ playlistId }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizePlaylistDeleteResult(
            await invoke('desktop_playlist_delete', {
              request: {
                playlistId,
              },
            }),
          )
        },
        async reorderPlaylists({ libraryId, orderedPlaylistIds }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          const records = await invoke('desktop_playlist_reorder', {
            request: {
              libraryId,
              orderedPlaylistIds,
            },
          })

          return normalizeRecordArray(records).map((record) => createPlaylistModel(record))
        },
        async addTrackToPlaylist({ playlistId, trackId, index = null }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizePlaylistTrackMutationResult(
            await invoke('desktop_playlist_add_track', {
              request: {
                playlistId,
                trackId,
                index,
              },
            }),
          )
        },
        async removeTrackFromPlaylist({ playlistId, trackId }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          return normalizePlaylistTrackRemoveResult(
            await invoke('desktop_playlist_remove_track', {
              request: {
                playlistId,
                trackId,
              },
            }),
          )
        },
        async reorderPlaylistTracks({ playlistId, orderedTrackIds }: DesktopRecord) {
          await ensureCatalogBootstrapped()
          const records = await invoke('desktop_playlist_reorder_tracks', {
            request: {
              playlistId,
              orderedTrackIds,
            },
          })

          return normalizeRecordArray(records).map((record) => createPlaylistTrackRelationModel(record))
        },
      },
      releaseTracks(tracks: DesktopRecord[] | null | undefined) {
        ;(tracks ?? []).forEach((track) => {
          revokeTrackResource(track)
        })
      },
    },
    history: {
      async loadRecent(limit = 50) {
        await ensureHistoryBootstrapped()
        return loadDesktopHistory(limit)
      },
      async loadStats({
        libraryId = null,
        days = 365,
        trackLimit = 24,
        albumLimit = 12,
        albumTrackLimit = 6,
        timezoneOffsetMinutes = 0,
      }: DesktopRecord = {}) {
        await ensureHistoryBootstrapped()
        return normalizeListeningStatsSnapshot(
          await invoke('desktop_history_load_stats', {
            request: {
              libraryId,
              days,
              trackLimit,
              albumLimit,
              albumTrackLimit,
              timezoneOffsetMinutes,
            },
          }),
        )
      },
      async append(entry: DesktopRecord) {
        await ensureHistoryBootstrapped()
        const nextEntry = createPlaybackHistoryEntryModel(entry)
        await invoke('desktop_history_append', {
          value: nextEntry,
        })
        return nextEntry
      },
    },
    lyrics: {
      async resolveTrack({
        trackId = null,
        audioPath = '',
        originPath = '',
        title = '',
        artist = '',
        album = '',
        fileName = '',
        lyricsPath = '',
        lyricsDirectories = [],
        positionSeconds = null,
      } = {}) {
        if (typeof audioPath !== 'string' || audioPath.trim().length === 0) {
          return createLyricsSnapshotModel({
            trackId,
            audioPath,
            status: 'missing',
          })
        }

        return createLyricsSnapshotModel(
          await invoke('lyrics_resolve_track', {
            request: {
              trackId,
              audioPath,
              originPath,
              title,
              artist,
              album,
              fileName,
              lyricsPath,
              lyricsDirectories,
              positionSeconds,
            },
          }),
        )
      },
    },
  }
}
