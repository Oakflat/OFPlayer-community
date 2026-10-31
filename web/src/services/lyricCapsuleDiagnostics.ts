import {
  logDiagnosticsError,
  logDiagnosticsInfo,
  logDiagnosticsWarn,
} from './diagnosticsLogger'

export type LyricCapsuleAttemptId = string
export type LyricCapsuleDiagnosticPayload = unknown

export interface LyricCapsuleTransportByteOptions {
  includeArtwork?: boolean
}

export interface LyricCapsuleSnapshotSummary {
  trackId: unknown
  hasTrack: boolean
  isPlaying: boolean
  isLoading: boolean
  progress: number
  artworkLength: number
  artworkKind: 'data-url' | 'url-or-path' | 'empty'
  lyricTextLength: number
  metaTextLength: number
  audioLevelCount: number
  updatedAt: number | null
}

export const LYRIC_CAPSULE_DIAGNOSTICS_VERSION = '2026-05-09.3'
export const LYRIC_CAPSULE_DIAGNOSTICS_CATEGORY = 'lyric-capsule'

let lyricCapsuleAttemptCounter = 0

export function nowMs(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

export function elapsedMs(startedAt: number): number {
  return Math.max(0, Math.round(nowMs() - startedAt))
}

export function createLyricCapsuleAttemptId(reason = 'open'): LyricCapsuleAttemptId {
  lyricCapsuleAttemptCounter += 1
  return `${reason}-${Date.now()}-${lyricCapsuleAttemptCounter}`
}

export function estimateJsonBytes(value: unknown): number {
  try {
    return new Blob([JSON.stringify(value)]).size
  } catch {
    try {
      return JSON.stringify(value).length
    } catch {
      return 0
    }
  }
}

export function estimateLyricCapsuleTransportBytes(
  snapshot: Record<string, unknown> = {},
  { includeArtwork = false }: LyricCapsuleTransportByteOptions = {},
): number {
  if (includeArtwork) {
    return estimateJsonBytes(snapshot)
  }

  return estimateJsonBytes({
    ...snapshot,
    artworkUrl: undefined,
  })
}

export function summarizeLyricCapsuleSnapshot(
  snapshot: Record<string, unknown> = {},
): LyricCapsuleSnapshotSummary {
  const artworkUrl = typeof snapshot.artworkUrl === 'string' ? snapshot.artworkUrl : ''
  const lyricText = typeof snapshot.lyricText === 'string' ? snapshot.lyricText : ''
  const metaText = typeof snapshot.metaText === 'string' ? snapshot.metaText : ''
  const audioLevels = Array.isArray(snapshot.audioLevels) ? snapshot.audioLevels : []
  const progress = typeof snapshot.progress === 'number' && Number.isFinite(snapshot.progress) ? snapshot.progress : 0
  const updatedAt =
    typeof snapshot.updatedAt === 'number' && Number.isFinite(snapshot.updatedAt) ? snapshot.updatedAt : null

  return {
    trackId: snapshot.trackId ?? null,
    hasTrack: snapshot.hasTrack === true,
    isPlaying: snapshot.isPlaying === true,
    isLoading: snapshot.isLoading === true,
    progress,
    artworkLength: artworkUrl.length,
    artworkKind: artworkUrl.startsWith('data:')
      ? 'data-url'
      : artworkUrl
        ? 'url-or-path'
        : 'empty',
    lyricTextLength: lyricText.length,
    metaTextLength: metaText.length,
    audioLevelCount: audioLevels.length,
    updatedAt,
  }
}

function withBasePayload(payload: LyricCapsuleDiagnosticPayload | undefined | null = null): {
  diagnosticsVersion: string
} & Record<string, unknown> {
  return {
    diagnosticsVersion: LYRIC_CAPSULE_DIAGNOSTICS_VERSION,
    ...(payload && typeof payload === 'object' ? payload : { value: payload }),
  }
}

export function logLyricCapsuleInfo(
  event: string,
  payload: LyricCapsuleDiagnosticPayload | undefined | null = null,
): any {
  return logDiagnosticsInfo(
    '[OFPlayer lyric capsule]',
    LYRIC_CAPSULE_DIAGNOSTICS_CATEGORY,
    event,
    withBasePayload(payload),
  )
}

export function logLyricCapsuleWarn(
  event: string,
  payload: LyricCapsuleDiagnosticPayload | undefined | null = null,
): any {
  return logDiagnosticsWarn(
    '[OFPlayer lyric capsule]',
    LYRIC_CAPSULE_DIAGNOSTICS_CATEGORY,
    event,
    withBasePayload(payload),
  )
}

export function logLyricCapsuleError(
  event: string,
  payload: LyricCapsuleDiagnosticPayload | undefined | null = null,
): any {
  return logDiagnosticsError(
    '[OFPlayer lyric capsule]',
    LYRIC_CAPSULE_DIAGNOSTICS_CATEGORY,
    event,
    withBasePayload(payload),
  )
}
