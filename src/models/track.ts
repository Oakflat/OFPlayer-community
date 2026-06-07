export type TrackSourceKind =
  | 'native-file'
  | 'external-url'
  | 'external-cache'
  | 'external-temp'
  | 'subsonic'
  | 'unavailable'
  | string

export interface TrackSource {
  kind: TrackSourceKind
  url: string
  path: string
  originPath: string
  provider: string
  connectionId: string
  remoteId: string
  remoteKey: string
  contentType: string
  etag: string
  persistUrl: boolean
  indexed: boolean
  transient?: boolean
  deleteOnRelease?: boolean
  [key: string]: any
}

export interface TrackFileLike {
  name?: string
  size?: number
  type?: string
  nativePath?: string
  path?: string
  originalPath?: string
}

export interface TrackModel {
  id: string
  libraryId: string
  libraryOrder: number
  isFavorite: boolean
  title: string
  artist: string
  albumArtist: string
  album: string
  genre: string
  year: number
  trackNumber: number
  trackTotal: number
  discNumber: number
  discTotal: number
  composer: string
  lyricist: string
  comment: string
  lyricsPath: string
  displayTitle: string
  fileName: string
  fileSize: number
  size: number
  duration: number
  format: string
  bitrate: number
  sampleRate: number
  bitDepth: number
  artwork: string
  mimeType: string
  importedAt: string
  metadataVersion: number
  source: TrackSource
  file?: TrackFileLike | null
  artworkAssetPath?: string
  [key: string]: any
}

export type TrackModelInput = Record<string, any> & {
  source?: (Partial<TrackSource> & Record<string, any>) | null
  file?: TrackFileLike | null
  nativePath?: string
  path?: string
  originalPath?: string
}

type DisplayTitleInput = {
  title?: unknown
  artist?: unknown
  fileName?: unknown
  [key: string]: any
}

const SUPPORTED_AUDIO_EXTENSION_VALUES = Object.freeze([
  'mp3',
  'mp2',
  'mp1',
  'mpa',
  'wav',
  'wave',
  'flac',
  'ogg',
  'oga',
  'm4a',
  'm4b',
  'm4r',
  'mp4',
  'aac',
  'adts',
  'aif',
  'aiff',
  'aifc',
  'caf',
  'mka',
  'dsf',
  'dff',
]) as readonly string[]
const SUPPORTED_AUDIO_EXTENSIONS = new Set<string>(SUPPORTED_AUDIO_EXTENSION_VALUES)
const SUPPORTED_AUDIO_MIME_TYPE_VALUES = Object.freeze([
  'audio/mpeg',
  'audio/mp3',
  'audio/wav',
  'audio/wave',
  'audio/x-wav',
  'audio/flac',
  'audio/x-flac',
  'audio/ogg',
  'audio/mp4',
  'audio/x-m4a',
  'audio/aac',
  'audio/aacp',
  'audio/x-aac',
  'audio/aif',
  'audio/aiff',
  'audio/x-aiff',
  'audio/x-caf',
  'audio/caf',
  'audio/x-matroska',
  'audio/dsd',
  'audio/x-dsd',
  'audio/x-dsf',
  'audio/x-dff',
]) as readonly string[]
const SUPPORTED_AUDIO_MIME_TYPES = new Set<string>(SUPPORTED_AUDIO_MIME_TYPE_VALUES)
const SUPPORTED_AUDIO_ACCEPT = [
  ...SUPPORTED_AUDIO_MIME_TYPE_VALUES,
  ...SUPPORTED_AUDIO_EXTENSION_VALUES.map((extension) => `.${extension}`),
].join(',')
const AUDIO_MIME_BY_EXTENSION = Object.freeze({
  mp3: 'audio/mpeg',
  mp2: 'audio/mpeg',
  mp1: 'audio/mpeg',
  mpa: 'audio/mpeg',
  wav: 'audio/wav',
  wave: 'audio/wav',
  flac: 'audio/flac',
  ogg: 'audio/ogg',
  oga: 'audio/ogg',
  m4a: 'audio/mp4',
  m4b: 'audio/mp4',
  m4r: 'audio/mp4',
  mp4: 'audio/mp4',
  aac: 'audio/aac',
  adts: 'audio/aac',
  aif: 'audio/aiff',
  aiff: 'audio/aiff',
  aifc: 'audio/aiff',
  caf: 'audio/x-caf',
  mka: 'audio/x-matroska',
  dsf: 'audio/x-dsf',
  dff: 'audio/x-dff',
}) as Readonly<Record<string, string>>
export const CURRENT_TRACK_METADATA_VERSION = 3

function createTrackId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }

  return `track-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export {
  SUPPORTED_AUDIO_ACCEPT,
  SUPPORTED_AUDIO_EXTENSIONS,
  SUPPORTED_AUDIO_EXTENSION_VALUES,
  SUPPORTED_AUDIO_MIME_TYPES,
  SUPPORTED_AUDIO_MIME_TYPE_VALUES,
}

export function isSupportedAudioMimeType(mimeType: unknown = ''): boolean {
  const normalizedMimeType = String(mimeType).split(';')[0].trim().toLowerCase()

  return SUPPORTED_AUDIO_MIME_TYPES.has(normalizedMimeType)
}

export function resolveAudioMimeType(extension: unknown = ''): string {
  const normalizedExtension = String(extension).trim().replace(/^\./, '').toLowerCase()

  return AUDIO_MIME_BY_EXTENSION[normalizedExtension] ?? ''
}

export function sanitizeTrackTitle(fileName: unknown = ''): string {
  const title = String(fileName).replace(/\.[^.]+$/, '').trim()
  return title || 'Untitled'
}

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeOptionalNumber(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : fallback
}

function isSubsonicSource(source: Partial<TrackSource> | null | undefined = {}): boolean {
  return source?.provider === 'subsonic' || source?.kind === 'subsonic'
}

function normalizeBitrate(value: unknown, source: Partial<TrackSource> | null | undefined = {}): number {
  const bitrate = normalizeOptionalNumber(value)

  if (bitrate > 0 && bitrate < 10_000 && isSubsonicSource(source)) {
    return bitrate * 1_000
  }

  return bitrate
}

function normalizePositiveInteger(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : fallback
}

function normalizeOrder(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

function normalizeBoolean(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function normalizeSourcePath(value: unknown): string {
  return normalizeText(value)
}

function normalizeLyricsPath(value: unknown): string {
  return normalizeText(value)
}

function normalizeSourceText(value: unknown, fallback = ''): string {
  return normalizeText(value, fallback)
}

function normalizeTrackSource(
  value: Partial<TrackSource> | null | undefined = {},
  fallback: Partial<TrackSource> = {},
): TrackSource {
  const source = value && typeof value === 'object' ? value : {}
  const path = normalizeSourceText(source.path ?? fallback.path)
  const url = normalizeSourceText(source.url ?? fallback.url)
  const kind = normalizeSourceText(
    source.kind ?? fallback.kind,
    path ? 'native-file' : url ? 'external-url' : 'unavailable',
  )

  return {
    kind,
    url,
    path,
    originPath: normalizeSourceText(source.originPath ?? fallback.originPath),
    provider: normalizeSourceText(source.provider ?? fallback.provider),
    connectionId: normalizeSourceText(source.connectionId ?? fallback.connectionId),
    remoteId: normalizeSourceText(source.remoteId ?? fallback.remoteId),
    remoteKey: normalizeSourceText(source.remoteKey ?? fallback.remoteKey),
    contentType: normalizeSourceText(source.contentType ?? fallback.contentType),
    etag: normalizeSourceText(source.etag ?? fallback.etag),
    persistUrl: normalizeBoolean(source.persistUrl ?? fallback.persistUrl, false),
    indexed: normalizeBoolean(source.indexed ?? fallback.indexed, false),
  }
}

function createPersistableTrackSource(source: Partial<TrackSource> | null | undefined = {}): TrackSource {
  const normalized = normalizeTrackSource(source)

  return {
    kind: normalized.kind,
    url: normalized.persistUrl ? normalized.url : '',
    path: normalized.path,
    originPath: normalized.originPath,
    provider: normalized.provider,
    connectionId: normalized.connectionId,
    remoteId: normalized.remoteId,
    remoteKey: normalized.remoteKey,
    contentType: normalized.contentType,
    etag: normalized.etag,
    persistUrl: normalized.persistUrl,
    indexed: normalized.indexed,
  }
}

function resolveFormat(fileName: unknown, format: unknown): string {
  const normalizedFormat = normalizeText(format)

  if (normalizedFormat) {
    return normalizedFormat.toUpperCase()
  }

  const extension = String(fileName ?? '')
    .split('.')
    .pop()
    ?.trim()
    .toUpperCase()

  return extension || ''
}

export function createDisplayTitle({ title, artist, fileName }: DisplayTitleInput = {}): string {
  const safeTitle = normalizeText(title, sanitizeTrackTitle(fileName))
  const safeArtist = normalizeText(artist)

  return safeArtist ? `${safeTitle} - ${safeArtist}` : safeTitle
}

export function isSupportedAudioFile(file: TrackFileLike | null | undefined): boolean {
  if (!file) {
    return false
  }

  const extension = String(file.name ?? '')
    .split('.')
    .pop()
    ?.toLowerCase()

  return isSupportedAudioMimeType(file.type) || SUPPORTED_AUDIO_EXTENSIONS.has(extension ?? '')
}

export function createTrackModel(
  file: TrackFileLike | null | undefined,
  overrides: TrackModelInput = {},
): TrackModel {
  const sourcePath = normalizeSourcePath(overrides.source?.path ?? file?.nativePath ?? file?.path)
  const sourceOriginPath = normalizeSourcePath(
    overrides.source?.originPath ?? file?.originalPath ?? file?.nativePath ?? file?.path,
  )
  const sourceKind = overrides.source?.kind ?? (sourcePath ? 'native-file' : 'unavailable')
  const source = normalizeTrackSource(overrides.source, {
    kind: sourceKind,
    path: sourcePath,
    originPath: sourceOriginPath,
  })
  const fileName = overrides.fileName ?? file?.name ?? ''
  const fileSize = Number.isFinite(overrides.fileSize)
    ? overrides.fileSize
    : Number.isFinite(overrides.size)
      ? overrides.size
      : Number.isFinite(file?.size)
        ? file?.size
        : 0
  const title = normalizeText(overrides.title, sanitizeTrackTitle(fileName))
  const artist = normalizeText(overrides.artist)

  return {
    id: overrides.id ?? createTrackId(),
    libraryId: normalizeText(overrides.libraryId),
    libraryOrder: normalizeOrder(overrides.libraryOrder),
    isFavorite: normalizeBoolean(overrides.isFavorite),
    title,
    artist,
    albumArtist: normalizeText(overrides.albumArtist),
    album: normalizeText(overrides.album),
    genre: normalizeText(overrides.genre),
    year: normalizePositiveInteger(overrides.year),
    trackNumber: normalizePositiveInteger(overrides.trackNumber),
    trackTotal: normalizePositiveInteger(overrides.trackTotal),
    discNumber: normalizePositiveInteger(overrides.discNumber),
    discTotal: normalizePositiveInteger(overrides.discTotal),
    composer: normalizeText(overrides.composer),
    lyricist: normalizeText(overrides.lyricist),
    comment: normalizeText(overrides.comment),
    lyricsPath: normalizeLyricsPath(overrides.lyricsPath),
    displayTitle:
      normalizeText(overrides.displayTitle) ||
      createDisplayTitle({
        title,
        artist,
        fileName,
      }),
    fileName,
    fileSize,
    size: fileSize,
    duration: Number.isFinite(overrides.duration) ? overrides.duration : 0,
    format: resolveFormat(fileName, overrides.format),
    bitrate: normalizeBitrate(overrides.bitrate, source),
    sampleRate: normalizeOptionalNumber(overrides.sampleRate),
    bitDepth: normalizeOptionalNumber(overrides.bitDepth),
    artwork: normalizeText(overrides.artwork),
    mimeType: overrides.mimeType ?? file?.type ?? '',
    importedAt: overrides.importedAt ?? new Date().toISOString(),
    metadataVersion: normalizePositiveInteger(overrides.metadataVersion),
    source,
    file: file ?? null,
  }
}

export function createPersistedTrackModel(
  track: TrackModelInput | null | undefined,
  options: TrackModelInput = {},
): TrackModel {
  const source = createPersistableTrackSource(track?.source ?? options.source)
  const sourcePath = normalizeSourcePath(source.path)
  const fileName = track?.fileName ?? options.fileName ?? ''
  const fileSize = Number.isFinite(track?.fileSize)
    ? track?.fileSize
    : Number.isFinite(track?.size)
      ? track?.size
      : Number.isFinite(options.fileSize)
        ? options.fileSize
        : Number.isFinite(options.size)
          ? options.size
          : 0
  const title = normalizeText(track?.title, sanitizeTrackTitle(fileName))
  const artist = normalizeText(track?.artist)

  return {
    id: track?.id ?? options.id ?? createTrackId(),
    libraryId: normalizeText(track?.libraryId ?? options.libraryId),
    libraryOrder: normalizeOrder(track?.libraryOrder ?? options.libraryOrder),
    isFavorite: normalizeBoolean(track?.isFavorite ?? options.isFavorite),
    title,
    artist,
    albumArtist: normalizeText(track?.albumArtist ?? options.albumArtist),
    album: normalizeText(track?.album ?? options.album),
    genre: normalizeText(track?.genre ?? options.genre),
    year: normalizePositiveInteger(track?.year ?? options.year),
    trackNumber: normalizePositiveInteger(track?.trackNumber ?? options.trackNumber),
    trackTotal: normalizePositiveInteger(track?.trackTotal ?? options.trackTotal),
    discNumber: normalizePositiveInteger(track?.discNumber ?? options.discNumber),
    discTotal: normalizePositiveInteger(track?.discTotal ?? options.discTotal),
    composer: normalizeText(track?.composer ?? options.composer),
    lyricist: normalizeText(track?.lyricist ?? options.lyricist),
    comment: normalizeText(track?.comment ?? options.comment),
    lyricsPath: normalizeLyricsPath(track?.lyricsPath ?? options.lyricsPath),
    displayTitle:
      normalizeText(track?.displayTitle) ||
      createDisplayTitle({
        title,
        artist,
        fileName,
      }),
    fileName,
    fileSize,
    size: fileSize,
    duration: Number.isFinite(track?.duration)
      ? track?.duration
      : Number.isFinite(options.duration)
        ? options.duration
        : 0,
    format: resolveFormat(fileName, track?.format ?? options.format),
    bitrate: normalizeBitrate(track?.bitrate ?? options.bitrate, source),
    sampleRate: normalizeOptionalNumber(track?.sampleRate ?? options.sampleRate),
    bitDepth: normalizeOptionalNumber(track?.bitDepth ?? options.bitDepth),
    artwork: normalizeText(track?.artwork ?? options.artwork),
    mimeType: track?.mimeType ?? options.mimeType ?? '',
    importedAt: track?.importedAt ?? options.importedAt ?? new Date().toISOString(),
    metadataVersion: normalizePositiveInteger(track?.metadataVersion ?? options.metadataVersion),
    source: {
      ...source,
      kind: source.kind || (sourcePath ? 'native-file' : 'unavailable'),
    },
  }
}

export function createRuntimeTrackFromPersistedTrack(record: TrackModelInput | null | undefined): TrackModel {
  const source = createPersistableTrackSource(record?.source)

  return createTrackModel(null, {
    id: record?.id,
    libraryId: record?.libraryId,
    libraryOrder: record?.libraryOrder,
    isFavorite: record?.isFavorite,
    title: record?.title,
    artist: record?.artist,
    albumArtist: record?.albumArtist,
    album: record?.album,
    genre: record?.genre,
    year: record?.year,
    trackNumber: record?.trackNumber,
    trackTotal: record?.trackTotal,
    discNumber: record?.discNumber,
    discTotal: record?.discTotal,
    composer: record?.composer,
    lyricist: record?.lyricist,
    comment: record?.comment,
    lyricsPath: record?.lyricsPath,
    displayTitle: record?.displayTitle,
    fileName: record?.fileName,
    fileSize: record?.fileSize ?? record?.size,
    size: record?.fileSize ?? record?.size,
    duration: record?.duration,
    format: record?.format,
    bitrate: record?.bitrate,
    sampleRate: record?.sampleRate,
    bitDepth: record?.bitDepth,
    artwork: record?.artwork,
    mimeType: record?.mimeType,
    importedAt: record?.importedAt,
    metadataVersion: record?.metadataVersion,
    source,
  })
}

export function createPersistableTrackMetadata(track: TrackModelInput | null | undefined): TrackModel {
  const record = createPersistedTrackModel(track)

  return {
    id: record.id,
    libraryId: record.libraryId,
    libraryOrder: record.libraryOrder,
    isFavorite: record.isFavorite,
    title: record.title,
    artist: record.artist,
    albumArtist: record.albumArtist,
    album: record.album,
    genre: record.genre,
    year: record.year,
    trackNumber: record.trackNumber,
    trackTotal: record.trackTotal,
    discNumber: record.discNumber,
    discTotal: record.discTotal,
    composer: record.composer,
    lyricist: record.lyricist,
    comment: record.comment,
    lyricsPath: record.lyricsPath,
    displayTitle: record.displayTitle,
    fileName: record.fileName,
    fileSize: record.fileSize,
    size: record.size,
    duration: record.duration,
    format: record.format,
    bitrate: record.bitrate,
    sampleRate: record.sampleRate,
    bitDepth: record.bitDepth,
    artwork: record.artwork,
    mimeType: record.mimeType,
    importedAt: record.importedAt,
    metadataVersion: record.metadataVersion,
    source: record.source,
  }
}

export function isPlayableTrack(track: TrackModelInput | null | undefined): boolean {
  return typeof track?.source?.path === 'string' && track.source.path.length > 0
}

export function updateTrackModel(
  track: TrackModelInput | null | undefined,
  patch: TrackModelInput = {},
): TrackModelInput {
  const currentTrack = track ?? {}
  const shouldRebuildDisplayTitle =
    !Object.prototype.hasOwnProperty.call(patch, 'displayTitle') &&
    (Object.prototype.hasOwnProperty.call(patch, 'title') || Object.prototype.hasOwnProperty.call(patch, 'artist'))
  const nextTrack: TrackModelInput = {
    ...currentTrack,
    ...patch,
    source: patch.source
      ? {
          ...(currentTrack.source ?? {}),
          ...patch.source,
        }
      : currentTrack.source,
  }

  const nextFileSize = Number.isFinite(patch.fileSize)
    ? patch.fileSize
    : Number.isFinite(patch.size)
      ? patch.size
      : Number.isFinite(currentTrack.fileSize)
        ? currentTrack.fileSize
        : Number.isFinite(currentTrack.size)
          ? currentTrack.size
          : 0

  nextTrack.fileSize = nextFileSize
  nextTrack.size = nextFileSize
  nextTrack.bitrate = normalizeBitrate(nextTrack.bitrate, nextTrack.source)
  nextTrack.libraryOrder = normalizeOrder(nextTrack.libraryOrder)
  nextTrack.isFavorite = normalizeBoolean(nextTrack.isFavorite)

  if (shouldRebuildDisplayTitle || !normalizeText(nextTrack.displayTitle)) {
    nextTrack.displayTitle = createDisplayTitle(nextTrack)
  }

  return nextTrack
}

export function revokeTrackResource(track: TrackModelInput | null | undefined): void {
  void track
}
