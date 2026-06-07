import { invoke, isTauri } from '@tauri-apps/api/core'
import {
  CURRENT_TRACK_METADATA_VERSION,
  createDisplayTitle,
  sanitizeTrackTitle,
  type TrackFileLike,
} from '../models/track'

export type MetadataFileLike = TrackFileLike & {
  fileName?: string
}

export interface NormalizedFileMetadata {
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
  duration: number
  fileSize: number
  format: string
  bitrate: number
  sampleRate: number
  bitDepth: number
  artwork: string
  displayTitle: string
  metadataVersion: number
}

export interface TrackMetaItemOptions {
  locale?: string
  showTechnicalMetadata?: boolean
  includeFormat?: boolean
  includeDuration?: boolean
  maxItems?: number
}

export interface TrackMetaItemTrack {
  format?: string | null
  bitrate?: number | null
  bitDepth?: number | null
  sampleRate?: number | null
  duration?: number | null
  fileSize?: number | null
  size?: number | null
  [key: string]: any
}

export interface MetadataService {
  parseFile: typeof normalizeFileMetadata
  createTrackMetaItems: typeof createTrackMetaItems
}

const DEFAULT_MAX_META_ITEMS = 3

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeOptionalNumber(value: any): number {
  return Number.isFinite(value) && value > 0 ? value : 0
}

function normalizePositiveInteger(value: any, fallback = 0): number {
  return Number.isInteger(value) && value > 0 ? value : fallback
}

function resolveFileName(file: MetadataFileLike | null | undefined): string {
  return normalizeText(file?.name ?? file?.fileName)
}

function resolveNativePath(file: MetadataFileLike | null | undefined): string {
  return normalizeText(file?.nativePath ?? file?.path)
}

function resolveFormat(fileName = '', format: unknown = ''): string {
  const normalizedFormat = normalizeText(format)

  if (normalizedFormat) {
    return normalizedFormat.toUpperCase()
  }

  const extension = String(fileName)
    .split('.')
    .pop()
    ?.trim()
    .toUpperCase()

  return extension || ''
}

function formatTime(seconds: any): string {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return ''
  }

  const totalSeconds = Math.floor(seconds)
  const minutes = Math.floor(totalSeconds / 60)
  const remainder = totalSeconds % 60

  return `${minutes}:${String(remainder).padStart(2, '0')}`
}

function formatFileSize(bytes: any, locale: string): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return ''
  }

  const mb = bytes / (1024 * 1024)
  const formatter = new Intl.NumberFormat(locale, {
    maximumFractionDigits: mb >= 10 ? 0 : 1,
  })

  return `${formatter.format(mb)} MB`
}

function formatBitrate(bitrate: any, locale: string): string {
  if (!Number.isFinite(bitrate) || bitrate <= 0) {
    return ''
  }

  const kbps = bitrate / 1000
  const formatter = new Intl.NumberFormat(locale, {
    maximumFractionDigits: kbps >= 100 ? 0 : 1,
  })

  return `${formatter.format(kbps)} kbps`
}

function formatSampleRate(sampleRate: any, locale: string): string {
  if (!Number.isFinite(sampleRate) || sampleRate <= 0) {
    return ''
  }

  const khz = sampleRate / 1000
  const formatter = new Intl.NumberFormat(locale, {
    maximumFractionDigits: Number.isInteger(khz) ? 0 : 1,
  })

  return `${formatter.format(khz)} kHz`
}

function formatBitDepthAndSampleRate(bitDepth: any, sampleRate: any, locale: string): string {
  const parts: string[] = []

  if (Number.isFinite(bitDepth) && bitDepth > 0) {
    parts.push(`${bitDepth}-bit`)
  }

  const sampleRateLabel = formatSampleRate(sampleRate, locale)

  if (sampleRateLabel) {
    parts.push(sampleRateLabel)
  }

  return parts.join(' ')
}

function createFallbackMetadata(file: MetadataFileLike | null | undefined): NormalizedFileMetadata {
  const fileName = resolveFileName(file)
  const title = sanitizeTrackTitle(fileName)
  const artist = ''

  return {
    title,
    artist,
    albumArtist: '',
    album: '',
    genre: '',
    year: 0,
    trackNumber: 0,
    trackTotal: 0,
    discNumber: 0,
    discTotal: 0,
    composer: '',
    lyricist: '',
    comment: '',
    duration: 0,
    fileSize: Number.isFinite(file?.size) ? (file?.size ?? 0) : 0,
    format: resolveFormat(fileName),
    bitrate: 0,
    sampleRate: 0,
    bitDepth: 0,
    artwork: '',
    displayTitle: createDisplayTitle({
      title,
      artist,
      fileName,
    }),
    metadataVersion: 0,
  }
}

function createResolvedMetadata(
  file: MetadataFileLike | null | undefined,
  metadata: Partial<NormalizedFileMetadata> | null | undefined,
): NormalizedFileMetadata {
  const fileName = resolveFileName(file)
  const title = normalizeText(metadata?.title, sanitizeTrackTitle(fileName))
  const artist = normalizeText(metadata?.artist)

  return {
    title,
    artist,
    albumArtist: normalizeText(metadata?.albumArtist),
    album: normalizeText(metadata?.album),
    genre: normalizeText(metadata?.genre),
    year: normalizePositiveInteger(metadata?.year),
    trackNumber: normalizePositiveInteger(metadata?.trackNumber),
    trackTotal: normalizePositiveInteger(metadata?.trackTotal),
    discNumber: normalizePositiveInteger(metadata?.discNumber),
    discTotal: normalizePositiveInteger(metadata?.discTotal),
    composer: normalizeText(metadata?.composer),
    lyricist: normalizeText(metadata?.lyricist),
    comment: normalizeText(metadata?.comment),
    duration: normalizeOptionalNumber(metadata?.duration),
    fileSize: normalizeOptionalNumber(metadata?.fileSize) || normalizeOptionalNumber(file?.size),
    format: resolveFormat(fileName, metadata?.format),
    bitrate: normalizeOptionalNumber(metadata?.bitrate),
    sampleRate: normalizeOptionalNumber(metadata?.sampleRate),
    bitDepth: normalizeOptionalNumber(metadata?.bitDepth),
    artwork: normalizeText(metadata?.artwork),
    displayTitle: createDisplayTitle({
      title,
      artist,
      fileName,
    }),
    metadataVersion: normalizePositiveInteger(
      metadata?.metadataVersion,
      CURRENT_TRACK_METADATA_VERSION,
    ),
  }
}

async function parseNativeFileMetadata(
  file: MetadataFileLike | null | undefined,
): Promise<Partial<NormalizedFileMetadata> | null> {
  const nativePath = resolveNativePath(file)

  if (!isTauri() || !nativePath) {
    return null
  }

  return invoke<Partial<NormalizedFileMetadata>>('metadata_parse_audio_file', {
    request: {
      path: nativePath,
      fileName: resolveFileName(file),
    },
  })
}

export async function normalizeFileMetadata(
  file: MetadataFileLike | null | undefined,
): Promise<NormalizedFileMetadata> {
  const fallbackMetadata = createFallbackMetadata(file)

  try {
    const metadata = await parseNativeFileMetadata(file)

    if (!metadata) {
      return fallbackMetadata
    }

    return createResolvedMetadata(file, metadata)
  } catch {
    return fallbackMetadata
  }
}

export function createTrackMetaItems(
  track: TrackMetaItemTrack | null | undefined,
  {
    locale = 'en',
    showTechnicalMetadata = true,
    includeFormat = true,
    includeDuration = true,
    maxItems = DEFAULT_MAX_META_ITEMS,
  }: TrackMetaItemOptions = {},
): string[] {
  const items: string[] = []
  const format = normalizeText(track?.format).toUpperCase()

  if (includeFormat && format) {
    items.push(format)
  }

  if (showTechnicalMetadata) {
    const bitrate = formatBitrate(track?.bitrate, locale)

    if (bitrate) {
      items.push(bitrate)
    }

    const fidelity = formatBitDepthAndSampleRate(track?.bitDepth, track?.sampleRate, locale)

    if (fidelity) {
      items.push(fidelity)
    }
  }

  if (includeDuration) {
    const duration = formatTime(track?.duration)

    if (duration) {
      items.push(duration)
    }
  }

  if (showTechnicalMetadata && items.length < maxItems) {
    const fileSize = formatFileSize(track?.fileSize ?? track?.size, locale)

    if (fileSize) {
      items.push(fileSize)
    }
  }

  return items.slice(0, Math.max(1, maxItems))
}

export function createMetadataService(): MetadataService {
  return {
    parseFile: normalizeFileMetadata,
    createTrackMetaItems,
  }
}
