const KNOWN_LYRICS_STATUS = new Set(['missing', 'resolved'])
const KNOWN_LYRICS_KIND = new Set(['synced', 'unsynced'])
const BILINGUAL_TIME_TOLERANCE_MS = 80
const MIN_EXPLICIT_LINE_DURATION_MS = BILINGUAL_TIME_TOLERANCE_MS + 1

type LyricsStatus = 'missing' | 'resolved'
type LyricsKind = 'synced' | 'unsynced' | null

interface LyricsLineInput {
  index?: unknown
  text?: unknown
  startTime?: unknown
  endTime?: unknown
  translatedLyric?: unknown
  romanLyric?: unknown
  isBG?: unknown
  isDuet?: unknown
}

export interface LyricsLineModel {
  index: number
  text: string
  startTime: number | null
  endTime: number | null
  translatedLyric: string
  romanLyric: string
  isBG: boolean
  isDuet: boolean
}

interface LyricsSnapshotInput {
  trackId?: unknown
  audioPath?: unknown
  status?: unknown
  source?: unknown
  sourcePath?: unknown
  kind?: unknown
  text?: unknown
  lines?: unknown
  title?: unknown
  artist?: unknown
  album?: unknown
  by?: unknown
  language?: unknown
  offsetMs?: unknown
  activeLineIndex?: unknown
  metadataVersion?: unknown
}

export interface LyricsSnapshotModel {
  trackId: string | null
  audioPath: string
  status: LyricsStatus
  source: string | null
  sourcePath: string | null
  kind: LyricsKind
  text: string
  lines: LyricsLineModel[]
  title: string
  artist: string
  album: string
  by: string
  language: string
  offsetMs: number
  activeLineIndex: number | null
  metadataVersion: number
  hasLyrics: boolean
  isSynced: boolean
}

interface LyricPlayerLineEntry extends LyricsLineModel {
  startMs: number
  endMs: number | null
}

interface LyricPlayerLineGroup {
  startMs: number
  entries: LyricPlayerLineEntry[]
}

export interface LyricPlayerWord {
  word: string
  startTime?: number
  endTime?: number
}

export interface LyricPlayerLineModel {
  words: LyricPlayerWord[]
  startTime: number
  endTime: number
  translatedLyric: string
  romanLyric: string
  isBG: boolean
  isDuet: boolean
  isBilingual: boolean
}

interface CreateLyricPlayerLinesOptions {
  fallbackLineMs?: number
}

function asLyricsSnapshotInput(value: unknown): LyricsSnapshotInput {
  return value && typeof value === 'object' ? value as LyricsSnapshotInput : {}
}

function asLyricsLineInput(value: unknown): LyricsLineInput {
  return value && typeof value === 'object' ? value as LyricsLineInput : {}
}

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeTime(value: unknown): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
    return null
  }

  return value
}

function normalizeActiveLineIndex(value: unknown, lines: LyricsLineModel[]): number | null {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value >= lines.length) {
    return null
  }

  return value
}

export function createLyricsLineModel(line: unknown, fallbackIndex = 0): LyricsLineModel {
  const record = asLyricsLineInput(line)

  return {
    index: Number.isInteger(record.index) && Number(record.index) >= 0 ? Number(record.index) : fallbackIndex,
    text: normalizeText(record.text),
    startTime: normalizeTime(record.startTime),
    endTime: normalizeTime(record.endTime),
    translatedLyric: normalizeText(record.translatedLyric),
    romanLyric: normalizeText(record.romanLyric),
    isBG: record.isBG === true,
    isDuet: record.isDuet === true,
  }
}

export function createLyricsSnapshotModel(snapshot: unknown = {}): LyricsSnapshotModel {
  const record = asLyricsSnapshotInput(snapshot)
  const rawLines = Array.isArray(record.lines) ? record.lines : []
  const lines = rawLines.map((line, index) => createLyricsLineModel(line, index))
  const status = KNOWN_LYRICS_STATUS.has(String(record.status)) ? record.status as LyricsStatus : 'missing'
  const kind = KNOWN_LYRICS_KIND.has(String(record.kind)) ? record.kind as Exclude<LyricsKind, null> : null

  return {
    trackId: typeof record.trackId === 'string' ? record.trackId : null,
    audioPath: normalizeText(record.audioPath),
    status,
    source: typeof record.source === 'string' ? record.source : null,
    sourcePath: typeof record.sourcePath === 'string' ? record.sourcePath : null,
    kind,
    text: typeof record.text === 'string' ? record.text : lines.map((line) => line.text).join('\n'),
    lines,
    title: normalizeText(record.title),
    artist: normalizeText(record.artist),
    album: normalizeText(record.album),
    by: normalizeText(record.by),
    language: normalizeText(record.language),
    offsetMs: Number.isInteger(record.offsetMs) ? Number(record.offsetMs) : 0,
    activeLineIndex: normalizeActiveLineIndex(record.activeLineIndex, lines),
    metadataVersion: Number.isInteger(record.metadataVersion) ? Number(record.metadataVersion) : 0,
    hasLyrics:
      status === 'resolved' &&
      (lines.length > 0 || normalizeText(record.text).length > 0),
    isSynced: kind === 'synced' && lines.some((line) => Number.isFinite(line.startTime)),
  }
}

export function findActiveLyricLineIndex(lyrics: unknown, seconds: number): number | null {
  if (!Number.isFinite(seconds) || seconds < 0) {
    return null
  }

  const snapshot = createLyricsSnapshotModel(lyrics)

  if (!snapshot.isSynced) {
    return null
  }

  let activeIndex = null

  for (const line of snapshot.lines) {
    const startTime = line.startTime
    if (typeof startTime !== 'number' || !Number.isFinite(startTime)) {
      continue
    }

    if (seconds < startTime) {
      break
    }

    activeIndex = line.index
  }

  return activeIndex
}

export function toLyricPlayerTimeMs(seconds: number): number {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return 0
  }

  return Math.floor(seconds * 1000 + 0.000001)
}

export function findActiveLyricPlayerLineIndex(
  lines: readonly Pick<LyricPlayerLineModel, 'startTime'>[],
  currentTimeMs: number,
): number {
  if (!Array.isArray(lines) || !Number.isFinite(currentTimeMs) || currentTimeMs < 0) {
    return -1
  }

  let index = -1

  for (let i = 0; i < lines.length; i += 1) {
    if (lines[i].startTime <= currentTimeMs) {
      index = i
    } else {
      break
    }
  }

  return index
}

export function findUpcomingLyricPlayerLineIndex(
  lines: readonly Pick<LyricPlayerLineModel, 'startTime'>[],
  currentTimeMs: number,
): number {
  if (!Array.isArray(lines) || !Number.isFinite(currentTimeMs) || currentTimeMs < 0) {
    return -1
  }

  for (let i = 0; i < lines.length; i += 1) {
    if (lines[i].startTime > currentTimeMs) {
      return i
    }
  }

  return -1
}

export function createLyricPlayerLines(
  lyrics: unknown,
  { fallbackLineMs = 5000 }: CreateLyricPlayerLinesOptions = {},
): LyricPlayerLineModel[] {
  const snapshot = createLyricsSnapshotModel(lyrics)
  const entries = snapshot.lines.map((line, index) => {
    const startTime = line.startTime
    const endTime = line.endTime
    const startMs = typeof startTime === 'number' && Number.isFinite(startTime)
      ? Math.round(startTime * 1000)
      : index * fallbackLineMs
    const endMs = typeof endTime === 'number' && Number.isFinite(endTime)
      ? Math.max(startMs + 1, Math.round(endTime * 1000))
      : null

    return {
      ...line,
      text: line.text || ' ',
      startMs,
      endMs,
    }
  })
  const groups: LyricPlayerLineGroup[] = []

  for (const entry of entries) {
    const previousGroup = groups[groups.length - 1]

    if (
      previousGroup &&
      Number.isFinite(entry.startMs) &&
      Math.abs(entry.startMs - previousGroup.startMs) <= BILINGUAL_TIME_TOLERANCE_MS
    ) {
      previousGroup.entries.push(entry)
      continue
    }

    groups.push({
      startMs: entry.startMs,
      entries: [entry],
    })
  }

  return groups.map((group, index) => {
    const nextGroup = groups[index + 1] ?? null
    const primary = group.entries.find((entry) => entry.text.trim().length > 0) ?? group.entries[0]
    const secondaryEntries = group.entries.filter((entry) => entry !== primary && entry.text.trim().length > 0)
    const translatedLyrics: string[] = []
    const romanLyrics: string[] = []

    if (primary.translatedLyric) {
      translatedLyrics.push(primary.translatedLyric)
    }

    if (primary.romanLyric) {
      romanLyrics.push(primary.romanLyric)
    }

    for (const entry of secondaryEntries) {
      translatedLyrics.push(entry.text)

      if (entry.translatedLyric) {
        translatedLyrics.push(entry.translatedLyric)
      }

      if (entry.romanLyric) {
        romanLyrics.push(entry.romanLyric)
      }
    }

    const nextStartMs = Number.isFinite(nextGroup?.startMs) ? nextGroup.startMs : null
    const explicitEndMs = group.entries
      .map((entry) => entry.endMs)
      .filter((value): value is number => {
        if (typeof value !== 'number' || !Number.isFinite(value) || value < group.startMs + MIN_EXPLICIT_LINE_DURATION_MS) {
          return false
        }

        return nextStartMs === null || value <= nextStartMs
      })
      .sort((left, right) => left - right)[0]
    const endMs = explicitEndMs ?? (nextStartMs !== null ? Math.max(group.startMs + 1, nextStartMs) : group.startMs + fallbackLineMs)

    return {
      words: [
        {
          word: primary.text || ' ',
          startTime: group.startMs,
          endTime: endMs,
        },
      ],
      startTime: group.startMs,
      endTime: endMs,
      translatedLyric: uniqueNonEmptyText(translatedLyrics).join('\n'),
      romanLyric: uniqueNonEmptyText(romanLyrics).join('\n'),
      isBG: primary.isBG,
      isDuet: primary.isDuet,
      isBilingual: translatedLyrics.length > 0 || romanLyrics.length > 0,
    }
  })
}

function uniqueNonEmptyText(items: string[] = []) {
  const seen = new Set<string>()
  const result: string[] = []

  for (const item of items) {
    const value = normalizeText(item)

    if (!value || seen.has(value)) {
      continue
    }

    seen.add(value)
    result.push(value)
  }

  return result
}
