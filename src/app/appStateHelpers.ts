import { CURRENT_TRACK_METADATA_VERSION, type TrackModelInput, type TrackSource } from '../models/track.ts'

export type BackendRevisions = {
  catalog: number
  navigation: number
  history: number
  preferences: number
  session: number
}

export type BackendManifestLike = {
  revisions?: Partial<Record<keyof BackendRevisions, unknown>> | null
} | null

export type ScanMode = 'manual' | 'auto' | 'watch'

export type ScanProgressState = {
  visible: boolean
  active: boolean
  phase: string
  percent: number
  processed: number
  total: number
  imported: number
  mode: ScanMode
  discoveredTotal: number
  candidateTotal: number
  directoriesScanned: number
  entriesScanned: number
  elapsedMs: number
  jobId: string
  jobMode: string
  jobStatus: string
  jobStage: string
  jobCreatedAt: string
  jobUpdatedAt: string
  jobCompletedAt: string
  jobStages: unknown[]
  currentFile: string
  error: string
}

export type NavigationSummary = {
  activeLibrary: string | null
  activeCollectionKey: string | null
  libraryTrackCounts: Record<string, number>
  playlistTrackCounts: Record<string, number>
  smartCollectionCounts: Record<string, number>
}

export type RemotePlaybackStatus = {
  active: boolean
  trackId: string
  provider: string
  phase: string
  error: string
}

export type RemoteTrackReadiness = {
  isRemote: boolean
  provider: string
  isPreparing: boolean
  metadataReady: boolean
  artworkReady: boolean
  playbackReady: boolean
}

export type RemotePlaybackMetadataPatch = Partial<TrackModelInput>

type ScanModeOptions = {
  source?: string
  interactive?: boolean
}

type ExternalLibraryLike = {
  source?: {
    kind?: string
    connectionId?: string | null
  } | null
} | null

export const BROWSER_CATALOG_ARTWORK_MODE = 'album-covers'
export const BROWSER_CATALOG_READY_ARTWORK_MODES = new Set([BROWSER_CATALOG_ARTWORK_MODE, 'all'])

const REMOTE_PLAYBACK_METADATA_FIELDS = [
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
]
const REMOTE_METADATA_COMPLETION_FIELDS = ['duration']
const REMOTE_METADATA_DOWNLOAD_HEAVY_PROVIDERS = new Set(['webdav'])
const MAX_REMOTE_EMBEDDED_ARTWORK_BYTES = 768 * 1024
const EMPTY_BACKEND_REVISIONS = Object.freeze({
  catalog: 0,
  navigation: 0,
  history: 0,
  preferences: 0,
  session: 0,
}) satisfies BackendRevisions

export function nowMs(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function normalizeRevision(value: unknown): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : 0
}

export function resolveBackendRevisions(manifest: BackendManifestLike = null): BackendRevisions {
  const revisions = manifest?.revisions ?? EMPTY_BACKEND_REVISIONS

  return {
    catalog: normalizeRevision(revisions.catalog),
    navigation: normalizeRevision(revisions.navigation),
    history: normalizeRevision(revisions.history),
    preferences: normalizeRevision(revisions.preferences),
    session: normalizeRevision(revisions.session),
  }
}

export function createIdleScanProgress(): ScanProgressState {
  return {
    visible: false,
    active: false,
    phase: 'idle',
    percent: 0,
    processed: 0,
    total: 0,
    imported: 0,
    mode: 'manual',
    discoveredTotal: 0,
    candidateTotal: 0,
    directoriesScanned: 0,
    entriesScanned: 0,
    elapsedMs: 0,
    jobId: '',
    jobMode: '',
    jobStatus: 'queued',
    jobStage: '',
    jobCreatedAt: '',
    jobUpdatedAt: '',
    jobCompletedAt: '',
    jobStages: [],
    currentFile: '',
    error: '',
  }
}

export function clampPercent(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 0
  }

  return Math.max(0, Math.min(100, Math.round(value)))
}

export function normalizeScanCount(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 0
  }

  return Math.max(0, Math.round(value))
}

export function resolveScanMode(options: ScanModeOptions = {}): ScanMode {
  if (options.source === 'watch') {
    return 'watch'
  }

  return options.interactive === true ? 'manual' : 'auto'
}

export function createEmptyNavigationSummary(): NavigationSummary {
  return {
    activeLibrary: null,
    activeCollectionKey: null,
    libraryTrackCounts: {},
    playlistTrackCounts: {},
    smartCollectionCounts: {},
  }
}

export function resolveLyricsDialogPath(track: TrackModelInput | null | undefined): string {
  if (!track) {
    return ''
  }

  return track?.lyricsPath || track?.source?.originPath || track?.source?.path || ''
}

export function hasResolvedMetadataValue(value: unknown): boolean {
  if (typeof value === 'string') {
    return value.trim().length > 0
  }

  if (typeof value === 'number') {
    return Number.isFinite(value) && value > 0
  }

  return value !== null && value !== undefined
}

export function isOversizedEmbeddedArtwork(value: unknown): boolean {
  const artwork = typeof value === 'string' ? value.trim() : ''

  return (
    artwork.length > MAX_REMOTE_EMBEDDED_ARTWORK_BYTES &&
    artwork.toLowerCase().startsWith('data:')
  )
}

export function normalizeArtworkUrl(value: unknown): string {
  const artwork = typeof value === 'string' ? value.trim() : ''

  if (!artwork || isOversizedEmbeddedArtwork(artwork)) {
    return ''
  }

  return artwork
}

export function sanitizeTrackArtwork<T extends TrackModelInput | null | undefined>(track: T): T | (TrackModelInput & { artwork: string }) {
  if (!track || !isOversizedEmbeddedArtwork(track.artwork)) {
    return track
  }

  return {
    ...track,
    artwork: '',
  }
}

function normalizeArtworkIdentityText(value: unknown): string {
  return String(value ?? '').trim().toLowerCase()
}

export function buildArtworkAlbumKey(track: TrackModelInput | null | undefined): string {
  const album = normalizeArtworkIdentityText(track?.album)

  if (!album) {
    return ''
  }

  const artist =
    normalizeArtworkIdentityText(track?.albumArtist) || normalizeArtworkIdentityText(track?.artist)
  return `${artist}::${album}`
}

export function createRemotePlaybackMetadataPatch(
  currentTrack: TrackModelInput | null | undefined,
  resolvedTrack: TrackModelInput | null | undefined,
): RemotePlaybackMetadataPatch {
  const patch: RemotePlaybackMetadataPatch = {}

  if (isOversizedEmbeddedArtwork(currentTrack?.artwork) && !normalizeArtworkUrl(resolvedTrack?.artwork)) {
    patch.artwork = ''
  }

  for (const field of REMOTE_PLAYBACK_METADATA_FIELDS) {
    const value = resolvedTrack?.[field]

    if (!hasResolvedMetadataValue(value) || currentTrack?.[field] === value) {
      continue
    }

    patch[field] = value
  }

  if (Number.isFinite(patch.fileSize) && !Number.isFinite(patch.size)) {
    patch.size = patch.fileSize
  } else if (Number.isFinite(patch.size) && !Number.isFinite(patch.fileSize)) {
    patch.fileSize = patch.size
  }

  return patch
}

export function isExternalLibrary(library: ExternalLibraryLike): boolean {
  return library?.source?.kind === 'external' && Boolean(library?.source?.connectionId)
}

export function isExternalTrack(track: TrackModelInput | null | undefined): boolean {
  return Boolean(track?.source?.connectionId)
}

export function canHydrateRemoteTrackMetadata(track: TrackModelInput | null | undefined): boolean {
  return !REMOTE_METADATA_DOWNLOAD_HEAVY_PROVIDERS.has(track?.source?.provider ?? '')
}

export function hasCompleteRemoteMetadata(track: TrackModelInput | null | undefined): boolean {
  const metadataVersion = track?.metadataVersion

  return (
    typeof metadataVersion === 'number' &&
    Number.isInteger(metadataVersion) &&
    metadataVersion >= CURRENT_TRACK_METADATA_VERSION &&
    REMOTE_METADATA_COMPLETION_FIELDS.every((field) => hasResolvedMetadataValue(track?.[field]))
  )
}

export function createIdleRemotePlaybackStatus(): RemotePlaybackStatus {
  return {
    active: false,
    trackId: '',
    provider: '',
    phase: 'idle',
    error: '',
  }
}

export function hasRemotePlaybackData(track: TrackModelInput | null | undefined): boolean {
  const source = track?.source ?? {}

  return (
    source.kind === 'external-cache' ||
    source.kind === 'external-temp' ||
    (source.kind === 'external-url' && normalizeArtworkIdentityText(source.url).length > 0) ||
    normalizeArtworkIdentityText(source.path).length > 0
  )
}

export function isTransientPlaybackSource(source: Partial<TrackSource> | null | undefined = {}): boolean {
  return source?.transient === true || source?.deleteOnRelease === true || source?.kind === 'external-temp'
}

export function createPlaybackSourceOverride(
  originalTrack: TrackModelInput | null | undefined,
  playableTrack: TrackModelInput | null | undefined,
): Partial<TrackSource> | null | undefined {
  const source = playableTrack?.source

  if (!isExternalTrack(originalTrack) || !isTransientPlaybackSource(source)) {
    return null
  }

  return source
}

export function shouldResolvePlaybackMetadataDuringPrepare(track: TrackModelInput | null | undefined): boolean {
  return (
    isExternalTrack(track) &&
    (!hasCompleteRemoteMetadata(track) || !normalizeArtworkUrl(track?.artwork))
  )
}

export function createRemoteTrackReadiness(
  track: TrackModelInput | null | undefined,
  playbackStatus: Partial<RemotePlaybackStatus> | null = null,
): RemoteTrackReadiness {
  if (!isExternalTrack(track)) {
    return {
      isRemote: false,
      provider: '',
      isPreparing: false,
      metadataReady: true,
      artworkReady: true,
      playbackReady: true,
    }
  }

  return {
    isRemote: true,
    provider: track?.source?.provider ?? '',
    isPreparing: playbackStatus?.active === true && playbackStatus?.trackId === track?.id,
    metadataReady: hasCompleteRemoteMetadata(track) || hasResolvedMetadataValue(track?.duration),
    artworkReady: Boolean(normalizeArtworkUrl(track?.artwork)),
    playbackReady: hasRemotePlaybackData(track),
  }
}

export function waitForDelay(delayMs: number): Promise<void> {
  return new Promise((resolve) => {
    const timer = typeof window !== 'undefined' ? window.setTimeout : setTimeout
    timer(resolve, delayMs)
  })
}
