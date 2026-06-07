import {
  createExternalLibraryConnectionModel,
  createLibraryExternalSource,
} from '../models/externalLibrary'
import { SYSTEM_PLAYLIST_KEYS } from '../models/playlist'
import { createPlaylistTrackRelationModel } from '../models/playlistTrackRelation'
import { createTrackModel } from '../models/track'
import {
  logDiagnosticsError as logDiagnosticsErrorRaw,
  logDiagnosticsInfo as logDiagnosticsInfoRaw,
} from './diagnosticsLogger'
import { normalizeExternalLibraryConnectionInput } from './externalLibraryConnectionNormalizer'

type ExternalProvider = 'webdav' | 'ftp' | 'subsonic' | (string & {})
type RemoteScalar = string | number | boolean | null | undefined
type RemoteRecord = Record<string, any>

interface ExternalLibraryAuth {
  username: string
  password: string
  token: string
  salt: string
  strategy: string
}

interface ExternalLibrarySyncOptions {
  lastCursor: string
}

interface ExternalLibraryConnection {
  id: string
  provider: ExternalProvider
  name: string
  endpoint: string
  rootPath: string
  auth: ExternalLibraryAuth
  sync: ExternalLibrarySyncOptions
  enabled: boolean
  createdAt: string
  updatedAt: string
  lastSyncAt: string
}

type ExternalLibraryConnectionInput = Partial<ExternalLibraryConnection> & RemoteRecord

interface TrackSource {
  kind?: string
  url?: string
  path?: string
  originPath?: string
  provider?: ExternalProvider
  connectionId?: string
  remoteId?: string
  remoteKey?: string
  contentType?: string
  etag?: string
  persistUrl?: boolean
  indexed?: boolean
  [key: string]: unknown
}

interface TrackModel {
  id: string
  libraryId: string
  libraryOrder: number
  isFavorite: boolean
  importedAt: string
  artwork: string
  source: TrackSource
  [key: string]: unknown
}

interface RemoteTrack extends Partial<TrackModel> {
  remoteId?: string
  path?: string
  source?: TrackSource
}

interface PlaylistModel {
  id: string
  libraryId: string
  systemKey?: string
  order?: number
  [key: string]: unknown
}

interface LibraryModel {
  id: string
  name?: string
  source?: {
    kind?: string
    connectionId?: string
    [key: string]: unknown
  }
  [key: string]: unknown
}

interface PlaylistTrackRelationModel {
  playlistId: string
  trackId: string
  order: number
  [key: string]: unknown
}

interface CatalogSnapshot {
  libraries: LibraryModel[]
  playlists?: PlaylistModel[]
  tracks: TrackModel[]
  playlistTrackRelations: PlaylistTrackRelationModel[]
}

interface RemoteLibrary {
  id?: string
  name?: string
  rootPath?: string
  [key: string]: unknown
}

interface RemoteCapabilities extends RemoteRecord {}

interface ExternalProbeResult {
  capabilities?: RemoteCapabilities | null
  message?: string
  ok?: boolean
  [key: string]: unknown
}

interface RemoteDiagnostics extends RemoteRecord {
  rootUrl?: string
  slowRequests?: RemoteRecord[]
}

interface SanitizedRemoteDiagnostics extends RemoteRecord {
  rootUrl: string
  slowRequests: RemoteRecord[]
}

interface TrackListResult {
  tracks: RemoteTrack[]
  total: number
  diagnostics: RemoteDiagnostics | null
}

interface ResolvePlaybackOptions {
  includeMetadata?: boolean
  metadataOnly?: boolean
  allowEmbeddedArtwork?: boolean
  [key: string]: unknown
}

interface ResolvePlaybackResult {
  source?: TrackSource
  metadata?: RemoteTrackMetadata
  [key: string]: unknown
}

type RemoteTrackMetadata = Partial<Record<(typeof TRACK_METADATA_FIELDS)[number], RemoteScalar>>

interface MetadataPatch extends RemoteRecord {
  fileSize?: number
  size?: number
  artwork?: string
}

interface ListTracksOptions {
  limit?: number
  [key: string]: unknown
}

interface ExternalSourcesBridge {
  testConnection(connection: ExternalLibraryConnection): Promise<ExternalProbeResult>
  listLibraries(options: RemoteRecord): Promise<unknown>
  listTracks(options: RemoteRecord): Promise<TrackListResult | RemoteTrack[] | unknown>
  resolvePlaybackSource(options: RemoteRecord): Promise<ResolvePlaybackResult | TrackSource | null | undefined>
}

interface ExternalLibrariesStore {
  getConnections?: () => Promise<ExternalLibraryConnection[]> | ExternalLibraryConnection[]
  putConnection?: (connection: ExternalLibraryConnection) => Promise<ExternalLibraryConnection> | ExternalLibraryConnection
  deleteConnection?: (connectionId: string) => Promise<boolean> | boolean
}

interface CatalogStore {
  putTrack(track: TrackModel): Promise<unknown> | unknown
  putTracks?: (tracks: TrackModel[]) => Promise<unknown> | unknown
  putPlaylistTrackRelations(relations: PlaylistTrackRelationModel[]): Promise<unknown> | unknown
}

interface ExternalLibraryDataService {
  externalSources: ExternalSourcesBridge
  externalLibraries?: ExternalLibrariesStore
  catalog: CatalogStore
}

interface LibraryService {
  loadCatalog(libraryId?: string | null, options?: RemoteRecord): Promise<CatalogSnapshot>
  createLibrary?: (input: RemoteRecord) => Promise<{
    library: LibraryModel
    defaultPlaylist: PlaylistModel | null
  }>
}

interface ExternalLibraryServiceOptions {
  dataService?: ExternalLibraryDataService
  libraryService?: LibraryService
}

interface TestLibraryOptions {
  libraryId?: string
}

interface ConnectLibraryOptions {
  connection?: ExternalLibraryConnectionInput | null
  remoteLibrary?: RemoteLibrary | null
}

interface SyncLibraryOptions {
  libraryId?: string
  connectionId?: string | null
  limit?: number | null
}

type DiagnosticsLogger = (
  label: string,
  category: string,
  event: string,
  payload?: RemoteRecord | null,
) => Promise<boolean>

const logDiagnosticsInfo = logDiagnosticsInfoRaw as DiagnosticsLogger
const logDiagnosticsError = logDiagnosticsErrorRaw as DiagnosticsLogger

const EXTERNAL_SYNC_PROGRESS_LOG_INTERVAL = 500
const MAX_REMOTE_EMBEDDED_ARTWORK_BYTES = 768 * 1024

const TRACK_METADATA_FIELDS = [
  'title',
  'artist',
  'albumArtist',
  'album',
  'genre',
  'year',
  'trackNumber',
  'trackTotal',
  'discNumber',
  'discTotal',
  'composer',
  'lyricist',
  'comment',
  'duration',
  'fileSize',
  'size',
  'format',
  'bitrate',
  'sampleRate',
  'bitDepth',
  'artwork',
  'mimeType',
  'metadataVersion',
] as const

function hashString(value = ''): string {
  let hash = 5381
  const text = String(value)

  for (let index = 0; index < text.length; index += 1) {
    hash = (hash * 33) ^ text.charCodeAt(index)
  }

  return (hash >>> 0).toString(36)
}

function createExternalTrackId({
  provider,
  connectionId,
  remoteKey,
}: {
  provider: ExternalProvider
  connectionId: string
  remoteKey: string
}): string {
  return `external-${provider}-${hashString(`${connectionId}:${remoteKey}`)}`
}

function createRemoteKey({
  provider,
  connectionId,
  remoteTrack,
}: {
  provider: ExternalProvider
  connectionId: string
  remoteTrack: RemoteTrack
}): string {
  return [
    provider,
    connectionId,
    remoteTrack?.source?.remoteId ?? remoteTrack?.remoteId ?? '',
    remoteTrack?.source?.path ?? remoteTrack?.path ?? '',
  ]
    .filter(Boolean)
    .join(':')
}

function sortTracksByLibraryOrder(tracks: TrackModel[] = []): TrackModel[] {
  return [...tracks].sort((left, right) => {
    const orderDiff = (left?.libraryOrder ?? 0) - (right?.libraryOrder ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.importedAt ?? left?.id ?? '').localeCompare(String(right?.importedAt ?? right?.id ?? ''))
  })
}

function getTrackRemoteKey(track: TrackModel): string {
  return track?.source?.remoteKey || [
    track?.source?.provider,
    track?.source?.connectionId,
    track?.source?.remoteId,
    track?.source?.originPath || track?.source?.path,
  ]
    .filter(Boolean)
    .join(':')
}

function hasMeaningfulMetadataValue(value: unknown): boolean {
  if (typeof value === 'string') {
    return value.trim().length > 0
  }

  if (typeof value === 'number') {
    return Number.isFinite(value) && value > 0
  }

  return value !== null && value !== undefined
}

function isOversizedEmbeddedArtwork(value: unknown): boolean {
  const artwork = typeof value === 'string' ? value.trim() : ''

  return (
    artwork.length > MAX_REMOTE_EMBEDDED_ARTWORK_BYTES &&
    artwork.toLowerCase().startsWith('data:')
  )
}

function safeRemoteArtwork(
  value: unknown,
  { allowEmbeddedArtwork = false }: { allowEmbeddedArtwork?: boolean } = {},
): string {
  if (allowEmbeddedArtwork) {
    return typeof value === 'string' ? value.trim() : ''
  }

  if (isOversizedEmbeddedArtwork(value)) {
    return ''
  }

  return typeof value === 'string' ? value.trim() : ''
}

function resolveRemoteTrackArtwork(remoteTrack: RemoteTrack, existingTrack?: TrackModel): string {
  return safeRemoteArtwork(remoteTrack?.artwork) || safeRemoteArtwork(existingTrack?.artwork)
}

function sanitizeRemoteRuntimeTrack<T extends TrackModel | null | undefined>(track: T): T | (T & { artwork: string }) {
  if (!track) {
    return track
  }

  const artwork = safeRemoteArtwork(track.artwork)
  return artwork === track.artwork ? track : { ...track, artwork }
}

function createMetadataPatch(
  metadata: RemoteTrackMetadata | null | undefined = {},
  { allowEmbeddedArtwork = false }: { allowEmbeddedArtwork?: boolean } = {},
): MetadataPatch {
  const patch: MetadataPatch = {}

  if (!metadata || typeof metadata !== 'object' || Array.isArray(metadata)) {
    return patch
  }

  for (const field of TRACK_METADATA_FIELDS) {
    const value = metadata[field]

    if (field === 'artwork' && !allowEmbeddedArtwork && isOversizedEmbeddedArtwork(value)) {
      continue
    }

    if (hasMeaningfulMetadataValue(value)) {
      patch[field] = value
    }
  }

  if (Number.isFinite(patch.size) && !Number.isFinite(patch.fileSize)) {
    patch.fileSize = patch.size
  } else if (Number.isFinite(patch.fileSize) && !Number.isFinite(patch.size)) {
    patch.size = patch.fileSize
  }

  return patch
}

function findLibraryDefaultPlaylist(snapshot: CatalogSnapshot, libraryId?: string): PlaylistModel | null {
  const libraryPlaylists = (snapshot.playlists ?? []).filter((playlist) => playlist.libraryId === libraryId)

  return (
    libraryPlaylists.find((playlist) => playlist.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS) ??
    [...libraryPlaylists].sort((left, right) => (left?.order ?? 0) - (right?.order ?? 0))[0] ??
    null
  )
}

function getNextPlaylistRelationOrder(relations: PlaylistTrackRelationModel[] = []): number {
  return relations.reduce((maxOrder, relation) => {
    const order = Number.isInteger(relation?.order) ? relation.order : -1
    return Math.max(maxOrder, order ?? -1)
  }, -1) + 1
}

function nowMs(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function elapsedMs(startedAt: number): number {
  return Math.max(0, Math.round(nowMs() - startedAt))
}

function normalizeTrackListResult(result: TrackListResult | RemoteTrack[] | unknown): TrackListResult {
  if (Array.isArray(result)) {
    return {
      tracks: result,
      total: result.length,
      diagnostics: null,
    }
  }

  if (!result || typeof result !== 'object') {
    return {
      tracks: [],
      total: 0,
      diagnostics: null,
    }
  }

  const trackList = result as Partial<TrackListResult>
  const tracks = Array.isArray(trackList.tracks) ? trackList.tracks : []

  return {
    tracks,
    total: Number.isInteger(trackList.total) ? Number(trackList.total) : tracks.length,
    diagnostics:
      trackList.diagnostics && typeof trackList.diagnostics === 'object'
        ? trackList.diagnostics
        : null,
  }
}

function sanitizeRemoteDiagnosticsForLog(
  diagnostics: RemoteDiagnostics | null,
): SanitizedRemoteDiagnostics | null {
  if (!diagnostics || typeof diagnostics !== 'object') {
    return null
  }

  return {
    ...diagnostics,
    rootUrl: diagnostics.rootUrl ? '[redacted-url]' : '',
    slowRequests: Array.isArray(diagnostics.slowRequests)
      ? diagnostics.slowRequests.map((request) => ({
          ...request,
          url: request?.url ? '[redacted-url]' : '',
        }))
      : [],
  }
}

export function createExternalLibraryService({
  dataService,
  libraryService,
}: ExternalLibraryServiceOptions = {}) {
  if (!dataService) {
    throw new Error('External library service requires a data service.')
  }

  if (!dataService.externalSources) {
    throw new Error('External source support requires the desktop Rust bridge.')
  }

  const activeDataService = dataService

  async function listConnections(): Promise<ExternalLibraryConnection[]> {
    return activeDataService.externalLibraries?.getConnections?.() ?? []
  }

  async function getConnection(connectionId?: string | null): Promise<ExternalLibraryConnection | null> {
    const connections = await listConnections()
    return connections.find((connection) => connection.id === connectionId) ?? null
  }

  function createAdapter(connection: ExternalLibraryConnection) {
    return {
      async testConnection(): Promise<ExternalProbeResult> {
        return activeDataService.externalSources.testConnection(connection)
      },
      async listLibraries(options: RemoteRecord = {}): Promise<unknown> {
        return activeDataService.externalSources.listLibraries({
          connection,
          ...options,
        })
      },
      async listTracks(options: ListTracksOptions = {}): Promise<TrackListResult | RemoteTrack[] | unknown> {
        return activeDataService.externalSources.listTracks({
          connection,
          ...options,
        })
      },
      async resolvePlaybackSource(
        track: TrackModel,
        options: ResolvePlaybackOptions = {},
      ): Promise<ResolvePlaybackResult | TrackSource | null | undefined> {
        return activeDataService.externalSources.resolvePlaybackSource({
          connection,
          track,
          includeMetadata: options.includeMetadata !== false,
          metadataOnly: options.metadataOnly === true,
        })
      },
    }
  }

  async function saveConnection(connectionInput: ExternalLibraryConnectionInput): Promise<ExternalLibraryConnection> {
    const connection = createExternalLibraryConnectionModel(
      normalizeExternalLibraryConnectionInput(connectionInput).connection,
    ) as ExternalLibraryConnection

    if (!activeDataService.externalLibraries?.putConnection) {
      throw new Error('The active data service does not support external library connections.')
    }

    return activeDataService.externalLibraries.putConnection(connection)
  }

  async function deleteConnection(connectionId: string): Promise<boolean> {
    return activeDataService.externalLibraries?.deleteConnection?.(connectionId) ?? false
  }

  async function testConnection(connectionInput: ExternalLibraryConnectionInput) {
    const connection = createExternalLibraryConnectionModel(
      normalizeExternalLibraryConnectionInput(connectionInput).connection,
    ) as ExternalLibraryConnection
    const adapter = createAdapter(connection)
    const result = await adapter.testConnection()

    return {
      connection,
      capabilities: result?.capabilities ?? null,
      message: result?.message ?? '',
      ok: result?.ok === true,
    }
  }

  async function testLibrary({ libraryId }: TestLibraryOptions = {}) {
    if (!libraryService) {
      throw new Error('Testing an external library requires the library service.')
    }

    const probeStartedAt = nowMs()
    const snapshot = await libraryService.loadCatalog(null, { trackArtworkMode: 'none' })
    const library = snapshot.libraries.find((item) => item.id === libraryId)

    if (!library) {
      throw new Error('Library not found.')
    }

    if (library.source?.kind !== 'external') {
      throw new Error('The selected library is not a remote library.')
    }

    const connection = await getConnection(library.source?.connectionId)

    if (!connection) {
      throw new Error('External library connection not found.')
    }

    void logDiagnosticsInfo('[OFPlayer external library probe]', 'external_library', 'probe_start', {
      libraryId,
      connectionId: connection.id,
      provider: connection.provider,
    })

    try {
      const adapter = createAdapter(connection)
      const result = await adapter.testConnection()
      const probeMs = elapsedMs(probeStartedAt)

      void logDiagnosticsInfo('[OFPlayer external library probe]', 'external_library', 'probe_complete', {
        libraryId,
        connectionId: connection.id,
        provider: connection.provider,
        ok: result?.ok === true,
        probeMs,
      })

      return {
        libraryId,
        connection,
        provider: connection.provider,
        capabilities: result?.capabilities ?? null,
        message: result?.message ?? '',
        ok: result?.ok === true,
        probeMs,
      }
    } catch (error) {
      void logDiagnosticsError('[OFPlayer external library probe]', 'external_library', 'probe_failed', {
        libraryId,
        connectionId: connection.id,
        provider: connection.provider,
        probeMs: elapsedMs(probeStartedAt),
        error,
      })
      throw error
    }
  }

  async function connectLibrary({ connection: connectionInput, remoteLibrary = null }: ConnectLibraryOptions = {}) {
    if (!libraryService?.createLibrary) {
      throw new Error('Connecting an external library requires the library service.')
    }

    const normalizedInput = normalizeExternalLibraryConnectionInput(connectionInput ?? {}).connection
    const previewConnection = createExternalLibraryConnectionModel({
      ...normalizedInput,
      rootPath: remoteLibrary?.id ?? remoteLibrary?.rootPath ?? normalizedInput?.rootPath,
    }) as ExternalLibraryConnection
    const adapter = createAdapter(previewConnection)

    await adapter.testConnection()

    const connection = await saveConnection(previewConnection)
    const libraryName = remoteLibrary?.name || connection.name
    const { library, defaultPlaylist } = await libraryService.createLibrary({
      name: libraryName,
      source: createLibraryExternalSource(connection, {
        remoteId: remoteLibrary?.id ?? '',
        rootPath: remoteLibrary?.rootPath ?? connection.rootPath,
      }),
    })
    const syncResult = await syncLibrary({
      libraryId: library.id,
      connectionId: connection.id,
    })

    return {
      connection,
      library,
      defaultPlaylist,
      sync: syncResult,
    }
  }

  async function syncLibrary({ libraryId, connectionId = null, limit = null }: SyncLibraryOptions = {}) {
    if (!libraryService) {
      throw new Error('Syncing an external library requires the library service.')
    }

    const syncStartedAt = nowMs()
    let listMs = 0
    let prepareMs = 0
    let persistMs = 0
    let relationPersistMs = 0
    let connectionPersistMs = 0
    let remoteDiagnostics: RemoteDiagnostics | null = null

    const snapshot = await libraryService.loadCatalog(null, { trackArtworkMode: 'none' })
    const library = snapshot.libraries.find((item) => item.id === libraryId)

    if (!library) {
      throw new Error('Library not found.')
    }

    const resolvedConnectionId = connectionId || library.source?.connectionId
    const connection = await getConnection(resolvedConnectionId)

    if (!connection) {
      throw new Error('External library connection not found.')
    }

    const adapter = createAdapter(connection)
    const listOptions: ListTracksOptions = {}

    if (typeof limit === 'number' && Number.isInteger(limit) && limit > 0) {
      listOptions.limit = limit
    }

    void logDiagnosticsInfo('[OFPlayer external library sync]', 'external_library', 'sync_start', {
      libraryId,
      connectionId: connection.id,
      provider: connection.provider,
      rootPath: connection.rootPath,
      limit: typeof limit === 'number' && Number.isInteger(limit) ? limit : null,
    })

    try {
      const listStartedAt = nowMs()
      const trackListResult = normalizeTrackListResult(await adapter.listTracks(listOptions))
      listMs = elapsedMs(listStartedAt)
      const remoteTracks = trackListResult.tracks
      remoteDiagnostics = trackListResult.diagnostics

      void logDiagnosticsInfo('[OFPlayer external library sync]', 'external_library', 'list_tracks_complete', {
        libraryId,
        connectionId: connection.id,
        provider: connection.provider,
        remoteTotal: trackListResult.total,
        trackCount: remoteTracks.length,
        listMs,
        diagnostics: sanitizeRemoteDiagnosticsForLog(remoteDiagnostics),
      })

      const prepareStartedAt = nowMs()
      const existingLibraryTracks = sortTracksByLibraryOrder(
        snapshot.tracks.filter((track) => track.libraryId === libraryId),
      )
      const existingExternalTracksByKey = new Map(
        existingLibraryTracks
          .filter((track) => track.source?.connectionId === connection.id)
          .map((track) => [getTrackRemoteKey(track), track]),
      )
      const defaultPlaylist = findLibraryDefaultPlaylist(snapshot, libraryId)
      const defaultPlaylistRelations = defaultPlaylist
        ? snapshot.playlistTrackRelations.filter((relation) => relation.playlistId === defaultPlaylist.id)
        : []
      const defaultPlaylistTrackIds = new Set(
        defaultPlaylistRelations.map((relation) => relation.trackId).filter(Boolean),
      )
      let nextOrder = existingLibraryTracks.length
      let nextDefaultPlaylistOrder = getNextPlaylistRelationOrder(defaultPlaylistRelations)
      const importedTracks: TrackModel[] = []
      const updatedTracks: TrackModel[] = []
      const tracksToUpsert: TrackModel[] = []
      const defaultPlaylistRelationsToUpsert: PlaylistTrackRelationModel[] = []
      const seenRemoteKeys = new Set<string>()

      for (const remoteTrack of remoteTracks) {
        const remoteKey = createRemoteKey({
          provider: connection.provider,
          connectionId: connection.id,
          remoteTrack,
        })

        if (!remoteKey || seenRemoteKeys.has(remoteKey)) {
          continue
        }

        seenRemoteKeys.add(remoteKey)
        const existingTrack = existingExternalTracksByKey.get(remoteKey)
        const previousSource = existingTrack?.source ?? {}
        const remoteSource = remoteTrack.source ?? {}
        const nextArtwork = resolveRemoteTrackArtwork(remoteTrack, existingTrack)
        const nextTrack = createTrackModel(null, {
          ...existingTrack,
          ...remoteTrack,
          artwork: nextArtwork,
          id:
            existingTrack?.id ??
            createExternalTrackId({
              provider: connection.provider,
              connectionId: connection.id,
              remoteKey,
            }),
          libraryId,
          libraryOrder: existingTrack?.libraryOrder ?? nextOrder,
          isFavorite: existingTrack?.isFavorite ?? false,
          importedAt: existingTrack?.importedAt ?? remoteTrack.importedAt ?? new Date().toISOString(),
          source: {
            ...previousSource,
            ...remoteSource,
            provider: connection.provider,
            connectionId: connection.id,
            remoteId: remoteSource.remoteId ?? remoteTrack.remoteId ?? previousSource.remoteId ?? '',
            remoteKey,
            originPath: remoteSource.originPath ?? remoteSource.path ?? previousSource.originPath ?? '',
          },
        }) as unknown as TrackModel

        if (!existingTrack) {
          nextOrder += 1
          importedTracks.push(nextTrack)
        } else {
          updatedTracks.push(nextTrack)
        }

        tracksToUpsert.push(nextTrack)

        if (defaultPlaylist && !defaultPlaylistTrackIds.has(nextTrack.id)) {
          defaultPlaylistRelationsToUpsert.push(
            createPlaylistTrackRelationModel({
              playlistId: defaultPlaylist.id,
              trackId: nextTrack.id,
              order: nextDefaultPlaylistOrder,
            }),
          )
          defaultPlaylistTrackIds.add(nextTrack.id)
          nextDefaultPlaylistOrder += 1
        }

        if (seenRemoteKeys.size % EXTERNAL_SYNC_PROGRESS_LOG_INTERVAL === 0) {
          void logDiagnosticsInfo('[OFPlayer external library sync]', 'external_library', 'prepare_progress', {
            libraryId,
            connectionId: connection.id,
            processed: seenRemoteKeys.size,
            remoteTotal: remoteTracks.length,
            imported: importedTracks.length,
            updated: updatedTracks.length,
            elapsedMs: elapsedMs(prepareStartedAt),
          })
        }
      }

      prepareMs = elapsedMs(prepareStartedAt)

      const persistStartedAt = nowMs()
      if (tracksToUpsert.length > 0) {
        if (typeof activeDataService.catalog.putTracks === 'function') {
          await activeDataService.catalog.putTracks(tracksToUpsert)
        } else {
          for (const track of tracksToUpsert) {
            await activeDataService.catalog.putTrack(track)
          }
        }
      }
      persistMs = elapsedMs(persistStartedAt)

      if (defaultPlaylistRelationsToUpsert.length > 0) {
        const relationPersistStartedAt = nowMs()
        await activeDataService.catalog.putPlaylistTrackRelations(defaultPlaylistRelationsToUpsert)
        relationPersistMs = elapsedMs(relationPersistStartedAt)
      }

      const connectionPersistStartedAt = nowMs()
      const updatedConnection = await saveConnection({
        ...connection,
        lastSyncAt: new Date().toISOString(),
      })
      connectionPersistMs = elapsedMs(connectionPersistStartedAt)

      const result = {
        connection: updatedConnection,
        libraryId,
        importedTracks,
        updatedTracks,
        defaultPlaylistRelations: defaultPlaylistRelationsToUpsert,
        remoteTotal: remoteTracks.length,
        skippedTotal: remoteTracks.length - seenRemoteKeys.size,
        diagnostics: {
          listMs,
          prepareMs,
          persistMs,
          relationPersistMs,
          connectionPersistMs,
          totalMs: elapsedMs(syncStartedAt),
          remote: remoteDiagnostics,
        },
      }

      void logDiagnosticsInfo('[OFPlayer external library sync]', 'external_library', 'sync_complete', {
        libraryId,
        connectionId: connection.id,
        provider: connection.provider,
        remoteTotal: result.remoteTotal,
        imported: importedTracks.length,
        updated: updatedTracks.length,
        skipped: result.skippedTotal,
        defaultPlaylistRelations: defaultPlaylistRelationsToUpsert.length,
        diagnostics: {
          ...result.diagnostics,
          remote: sanitizeRemoteDiagnosticsForLog(result.diagnostics.remote),
        },
      })

      return result
    } catch (error) {
      void logDiagnosticsError('[OFPlayer external library sync]', 'external_library', 'sync_failed', {
        libraryId,
        connectionId: connection.id,
        provider: connection.provider,
        listMs,
        prepareMs,
        persistMs,
        relationPersistMs,
        connectionPersistMs,
        totalMs: elapsedMs(syncStartedAt),
        remote: sanitizeRemoteDiagnosticsForLog(remoteDiagnostics),
        error,
      })
      throw error
    }
  }

  async function resolvePlayableTrack(track: TrackModel, options: ResolvePlaybackOptions = {}) {
    if (!track?.source?.connectionId) {
      return track
    }

    const connection = await getConnection(track.source.connectionId)

    if (!connection) {
      return track
    }

    const adapter = createAdapter(connection)
    const resolved = await adapter.resolvePlaybackSource(track, {
      ...options,
      includeMetadata: options.includeMetadata === true,
    })
    const source = ((resolved as ResolvePlaybackResult | null | undefined)?.source ?? resolved ?? {}) as TrackSource
    const sanitizedTrack = sanitizeRemoteRuntimeTrack(track)
    const allowEmbeddedArtwork = options.allowEmbeddedArtwork === true
    const metadataPatch = createMetadataPatch((resolved as ResolvePlaybackResult | null | undefined)?.metadata, {
      allowEmbeddedArtwork,
    })
    const nextArtwork = safeRemoteArtwork(metadataPatch.artwork ?? sanitizedTrack.artwork, {
      allowEmbeddedArtwork,
    })

    return {
      ...sanitizedTrack,
      ...metadataPatch,
      artwork: nextArtwork,
      source: {
        ...sanitizedTrack.source,
        ...source,
      },
    }
  }

  async function resolveTrackMetadata(track: TrackModel, options: ResolvePlaybackOptions = {}): Promise<MetadataPatch> {
    if (!track?.source?.connectionId) {
      return {}
    }

    const connection = await getConnection(track.source.connectionId)

    if (!connection) {
      return {}
    }

    const adapter = createAdapter(connection)
    const resolved = await adapter.resolvePlaybackSource(track, {
      ...options,
      includeMetadata: true,
      metadataOnly: true,
    })

    return createMetadataPatch((resolved as ResolvePlaybackResult | null | undefined)?.metadata, {
      allowEmbeddedArtwork: options.allowEmbeddedArtwork === true,
    })
  }

  return {
    listConnections,
    getConnection,
    saveConnection,
    deleteConnection,
    testConnection,
    testLibrary,
    connectLibrary,
    syncLibrary,
    resolvePlayableTrack,
    resolveTrackMetadata,
  }
}
