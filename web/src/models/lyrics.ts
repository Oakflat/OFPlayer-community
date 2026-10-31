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
  agent?: unknown
  words?: unknown
}

export interface LyricsWordModel {
  text: string
  startTime: number
  endTime: number
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
  agent: string
  words: LyricsWordModel[]
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
  hasWordTiming: boolean
  agent: string
  strictTiming: boolean
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

function normalizeWords(value: unknown): LyricsWordModel[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const word = item && typeof item === 'object' ? item as Record<string, unknown> : {}
    const startTime = normalizeTime(word.startTime)
    const endTime = normalizeTime(word.endTime)
    // Do not trim TTML text: inline spaces belong to their original word.
    return typeof word.text === 'string' && word.text.length > 0 &&
      startTime !== null && endTime !== null && endTime > startTime
      ? [{ text: word.text, startTime, endTime }]
      : []
  })
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
    agent: typeof record.agent === 'string' ? record.agent : '',
    words: normalizeWords(record.words),
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
  lines: readonly (Pick<LyricPlayerLineModel, 'startTime'> & Partial<Pick<LyricPlayerLineModel, 'endTime' | 'isBG' | 'isDuet' | 'strictTiming'>>)[],
  currentTimeMs: number,
): number {
  if (!Array.isArray(lines) || !Number.isFinite(currentTimeMs) || currentTimeMs < 0) {
    return -1
  }

  let index = -1
  let fallback = -1
  const strictTiming = lines.some((line) => line.strictTiming)

  for (let i = 0; i < lines.length; i += 1) {
    if (lines[i].startTime <= currentTimeMs) {
      fallback = i
      if (typeof lines[i].endTime === 'number' && currentTimeMs >= lines[i].endTime!) continue
      // Active lead vocals outrank a later-starting background phrase. Once the
      // lead ends, the still-active background phrase may take focus.
      const backing = Boolean(lines[i].isBG || lines[i].isDuet)
      const currentBacking = index >= 0 && Boolean(lines[index].isBG || lines[index].isDuet)
      if (index < 0 || (!backing && currentBacking) ||
        (backing === currentBacking && lines[i].startTime > lines[index].startTime)) {
        index = i
      }
    } else {
      break
    }
  }

  return index >= 0 ? index : (strictTiming ? -1 : fallback)
}

export function lyricWordProgress(word: Pick<LyricPlayerWord, 'startTime' | 'endTime'>, currentTimeMs: number): number {
  const start = word.startTime ?? 0
  const end = word.endTime ?? start
  if (!Number.isFinite(currentTimeMs) || end <= start) return 0
  return Math.max(0, Math.min(1, (currentTimeMs - start) / (end - start)))
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
      snapshot.source !== 'sidecar-ttml' &&
      Number.isFinite(entry.startMs) &&
      Math.abs(entry.startMs - previousGroup.startMs) <= BILINGUAL_TIME_TOLERANCE_MS &&
      !entry.isBG && !entry.isDuet && !entry.agent && entry.words.length === 0 &&
      previousGroup.entries.every((other) => !other.isBG && !other.isDuet && !other.agent && other.words.length === 0)
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

    const nextLaterGroup = groups.slice(index + 1).find((candidate) => candidate.startMs > group.startMs)
    const nextStartMs = Number.isFinite(nextLaterGroup?.startMs) ? nextLaterGroup!.startMs : null
    const explicitEndMs = group.entries
      .map((entry) => entry.endMs)
      .filter((value): value is number => {
        const minimum = snapshot.source === 'sidecar-ttml' ? group.startMs + 1 : group.startMs + MIN_EXPLICIT_LINE_DURATION_MS
        if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum) {
          return false
        }

        return snapshot.source === 'sidecar-ttml' || nextStartMs === null || value <= nextStartMs
      })
      .sort((left, right) => left - right)[0]
    const endMs = explicitEndMs ?? (nextStartMs !== null ? Math.max(group.startMs + 1, nextStartMs) : group.startMs + fallbackLineMs)
    const timedWordsCoverText = primary.words.length > 0 &&
      primary.words.map((word) => word.text).join('').trim() === primary.text.trim()

    return {
      words: timedWordsCoverText
        ? primary.words.map((word) => ({
          word: word.text,
          startTime: Math.round(word.startTime * 1000),
          endTime: Math.max(Math.round(word.startTime * 1000) + 1, Math.round(word.endTime * 1000)),
        }))
        : [{ word: primary.text || ' ', startTime: group.startMs, endTime: endMs }],
      startTime: group.startMs,
      endTime: endMs,
      translatedLyric: uniqueNonEmptyText(translatedLyrics).join('\n'),
      romanLyric: uniqueNonEmptyText(romanLyrics).join('\n'),
      isBG: primary.isBG,
      isDuet: primary.isDuet,
      isBilingual: translatedLyrics.length > 0 || romanLyrics.length > 0,
      hasWordTiming: timedWordsCoverText,
      agent: primary.agent,
      strictTiming: snapshot.source === 'sidecar-ttml',
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
