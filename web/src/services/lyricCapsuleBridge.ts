import { hostRequest, isHostBridgeAvailable, onHostEvent } from '../immersiveWeb/hostBridge.ts'
import { DEFAULT_CAPSULE_APPEARANCE, normalizeCapsuleAppearance, type LyricCapsuleAppearance } from './lyricCapsuleAppearance.ts'

type UnlistenFn = () => void

export type LyricCapsuleControlAction = 'previous' | 'toggle-playback' | 'next' | 'seek'

export interface LyricCapsuleControlEventPayload {
  action: LyricCapsuleControlAction
  sentAtMs: number
  seconds?: number
}

export interface LyricCapsuleControlRequestOptions {
  seconds?: unknown
}

export interface LyricCapsuleTimelineLine {
  index: number | null
  text: string
  translation: string
  startMs: number
  endMs: number | null
  words: Array<{ text: string; startMs: number; endMs: number }>
  isBG: boolean
  isDuet: boolean
  agent: string
}

export interface LyricCapsuleSnapshot {
  [key: string]: unknown
  seq: number
  hasTrack: boolean
  trackId: string | null
  artworkKey: string
  artworkSrc: string
  artworkUrl: string
  lyricLine: string
  lyricText: string
  lyricVersion: number
  lyricSource: string
  lyricIndex: number | null
  lyricTimeline: LyricCapsuleTimelineLine[]
  title: string
  artist: string
  metaText: string
  isPlaying: boolean
  isLoading: boolean
  progress: number
  audioLevels: number[]
  positionMs: number
  durationMs: number
  sentAtMs: number
  updatedAt: number
}

export interface LyricCapsuleSnapshotInput {
  seq?: unknown
  trackId?: unknown
  hasTrack?: unknown
  lyricLine?: unknown
  lyricText?: unknown
  lyricVersion?: unknown
  lyricSource?: unknown
  lyricIndex?: unknown
  lyricTimeline?: unknown
  title?: unknown
  artist?: unknown
  metaText?: unknown
  isPlaying?: unknown
  durationMs?: unknown
  positionMs?: unknown
  sentAtMs?: unknown
  artworkKey?: unknown
  artworkSrc?: unknown
  artworkUrl?: unknown
  audioLevels?: unknown
  isLoading?: unknown
}

export interface LyricCapsuleProgressAnchor {
  [key: string]: unknown
  seq: number
  trackId: string | null
  isPlaying: boolean
  durationMs: number
  positionMs: number
  sentAtMs: number
}

export interface LyricCapsuleProgressAnchorInput {
  seq?: unknown
  trackId?: unknown
  isPlaying?: unknown
  durationMs?: unknown
  positionMs?: unknown
  sentAtMs?: unknown
}

export interface LyricCapsuleMeterFrame {
  [key: string]: unknown
  seq: number
  trackId: string | null
  isPlaying: boolean
  sentAtMs: number
  levels: number[]
}

export interface LyricCapsuleMeterFrameInput {
  seq?: unknown
  trackId?: unknown
  isPlaying?: unknown
  sentAtMs?: unknown
  levels?: unknown
}

export interface LyricCapsuleHitRegionRequest {
  capsuleWidth?: unknown
  expanded?: unknown
  viewportWidth?: unknown
  viewportHeight?: unknown
  collapsedHeight?: unknown
  expandedHeight?: unknown
}

export const LYRIC_CAPSULE_LABEL = 'lyric-capsule'
export const LYRIC_CAPSULE_ROUTE = '/lyric-capsule.html'
export const LYRIC_CAPSULE_STATE_EVENT = 'capsule://state'
export const LYRIC_CAPSULE_PROGRESS_ANCHOR_EVENT = 'capsule://progress-anchor'
export const LYRIC_CAPSULE_METER_EVENT = 'capsule://meter'
export const LYRIC_CAPSULE_CONTROL_EVENT = 'capsule://control'
export const LYRIC_CAPSULE_APPEARANCE_EVENT = 'capsule://appearance'
export const LYRIC_CAPSULE_POSITION_RESET_EVENT = 'capsule://position-reset'

const DEFAULT_AUDIO_LEVELS = Object.freeze([0, 0, 0, 0, 0, 0, 0, 0])
const STRUCTURED_LYRIC_TEXT_KEYS = Object.freeze(['tx', 'text', 'lyric'])
const STRUCTURED_LYRIC_CHILD_KEYS = Object.freeze(['c', 'children', 'segments'])

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeDisplayWhitespace(value: string): string {
  return value.replace(/\s+/g, ' ').trim()
}

function collectStructuredLyricText(node: unknown, parts: string[]): void {
  if (Array.isArray(node)) {
    node.forEach((item) => collectStructuredLyricText(item, parts))
    return
  }

  if (!node || typeof node !== 'object') {
    return
  }

  const record = node as Record<string, unknown>
  STRUCTURED_LYRIC_TEXT_KEYS.forEach((key) => {
    if (typeof record[key] === 'string') {
      parts.push(record[key])
    }
  })

  STRUCTURED_LYRIC_CHILD_KEYS.forEach((key) => {
    if (Array.isArray(record[key])) {
      collectStructuredLyricText(record[key], parts)
    }
  })
}

function parseStructuredLyricLine(value: string): string {
  const trimmed = value.trim()

  if (!trimmed || !/^[\[{]/.test(trimmed)) {
    return ''
  }

  try {
    const parsed = JSON.parse(trimmed)
    const parts: string[] = []
    collectStructuredLyricText(parsed, parts)
    return normalizeDisplayWhitespace(parts.join(''))
  } catch {
    return ''
  }
}

function normalizeLyricDisplayText(value: unknown): string {
  const normalized = normalizeText(value)

  if (!normalized) {
    return ''
  }

  return normalized
    .split(/\r?\n+/)
    .map((line) => parseStructuredLyricLine(line) || normalizeDisplayWhitespace(line))
    .filter(Boolean)
    .join('\n')
}

function normalizeNumber(value: unknown, fallback = 0): number {
  const numericValue = Number(value)
  return Number.isFinite(numericValue) ? numericValue : fallback
}

function normalizeDurationMs(value: unknown): number {
  return Math.max(0, Math.round(normalizeNumber(value)))
}

function normalizeTrackId(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

function normalizeArtworkSrc(value: unknown): string {
  const artworkSrc = normalizeText(value)

  if (!artworkSrc || artworkSrc.startsWith('data:image/')) {
    return ''
  }

  return isLocalArtworkPath(artworkSrc) ? '' : artworkSrc
}

function isLocalArtworkPath(value: string): boolean {
  return /^[a-zA-Z]:[\\/]/.test(value) || value.startsWith('/') || value.startsWith('\\\\')
}

function normalizeAudioLevels(levels: unknown): number[] {
  if (!Array.isArray(levels)) {
    return [...DEFAULT_AUDIO_LEVELS]
  }

  return Array.from({ length: 8 }, (_, index) => {
    const numericLevel = Number(levels[index])
    return Number.isFinite(numericLevel) ? Math.max(0, Math.min(1, numericLevel)) : 0
  })
}

function normalizeOptionalIndex(value: unknown): number | null {
  if (value === null || value === undefined) {
    return null
  }

  const numericValue = Number(value)
  return Number.isFinite(numericValue) && numericValue >= 0 ? Math.round(numericValue) : null
}

function normalizeOptionalTimestampMs(value: unknown): number | null {
  if (value === null || value === undefined) {
    return null
  }

  const numericValue = Number(value)
  return Number.isFinite(numericValue) && numericValue >= 0 ? Math.round(numericValue) : null
}

function normalizeWordTimeMs(value: unknown, scale: number): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value * scale : null
}

function normalizeLyricTimeline(lines: unknown): LyricCapsuleTimelineLine[] {
  if (!Array.isArray(lines)) {
    return []
  }

  return lines
    .map((line) => {
      const record = line && typeof line === 'object' ? line as Record<string, unknown> : {}
      const text = normalizeLyricDisplayText(record.text)
      const startMs = normalizeOptionalTimestampMs(record.startMs)

      if (!text || startMs === null) {
        return null
      }

      return {
        index: normalizeOptionalIndex(record.index),
        text,
        // The page merges every update into a snapshot that was already normalized, so this
        // also reads its own output: `translation`, and words in milliseconds.
        translation: normalizeLyricDisplayText(record.translatedLyric ?? record.translation).replace(/\n+/g, ' '),
        startMs,
        endMs: normalizeOptionalTimestampMs(record.endMs),
        words: Array.isArray(record.words) ? record.words.flatMap((item) => {
          const word = item && typeof item === 'object' ? item as Record<string, unknown> : {}
          const hostSeconds = 'startTime' in word || 'endTime' in word
          const start = hostSeconds ? normalizeWordTimeMs(word.startTime, 1000) : normalizeWordTimeMs(word.startMs, 1)
          const end = hostSeconds ? normalizeWordTimeMs(word.endTime, 1000) : normalizeWordTimeMs(word.endMs, 1)
          return typeof word.text === 'string' && word.text.length > 0 && start !== null && end !== null && end > start
            ? [{ text: word.text, startMs: Math.round(start), endMs: Math.max(Math.round(start) + 1, Math.round(end)) }] : []
        }) : [],
        isBG: record.isBG === true,
        isDuet: record.isDuet === true,
        agent: typeof record.agent === 'string' ? record.agent : '',
      }
    })
    .filter((line) => line !== null)
    .sort((left, right) => left.startMs - right.startMs || (left.index ?? 0) - (right.index ?? 0))
}

export function findCapsuleTimelineLine(
  lines: readonly LyricCapsuleTimelineLine[],
  positionMs: number,
  source: string,
  renderLeadMs = 0,
): LyricCapsuleTimelineLine | null {
  if (!Number.isFinite(positionMs)) return null
  const at = Math.max(0, positionMs + renderLeadMs)
  let active: LyricCapsuleTimelineLine | null = null
  let fallback: LyricCapsuleTimelineLine | null = null
  for (const line of lines) {
    if (at < line.startMs) break
    fallback = line
    if (line.endMs !== null && at >= line.endMs) continue
    const backing = line.isBG || line.isDuet
    const activeBacking = active !== null && (active.isBG || active.isDuet)
    if (!active || (!backing && activeBacking) ||
      (backing === activeBacking && line.startMs > active.startMs)) active = line
  }
  return active ?? (source === 'sidecar-ttml' ? null : fallback)
}

export function createLyricCapsuleSnapshot({
  seq = 0,
  trackId = null,
  hasTrack = null,
  lyricLine = '',
  lyricText = '',
  lyricVersion = 0,
  lyricSource = '',
  lyricIndex = null,
  lyricTimeline = [],
  title = '',
  artist = '',
  metaText = '',
  isPlaying = false,
  durationMs = 0,
  positionMs = 0,
  sentAtMs = Date.now(),
  artworkKey = '',
  artworkSrc = '',
  artworkUrl = '',
  audioLevels = DEFAULT_AUDIO_LEVELS,
  isLoading = false,
}: LyricCapsuleSnapshotInput = {}): LyricCapsuleSnapshot {
  const safeTrackId = normalizeTrackId(trackId)
  // Keep generated UI copy out of track data so it follows live language changes.
  const safeTitle = normalizeText(title)
  const safeArtist = normalizeText(artist, 'OFPlayer')
  const safeLyric = normalizeLyricDisplayText(lyricLine) || normalizeLyricDisplayText(lyricText) || safeTitle
  const safeDurationMs = normalizeDurationMs(durationMs)
  const safePositionMs = Math.max(0, Math.min(safeDurationMs || Number.MAX_SAFE_INTEGER, normalizeDurationMs(positionMs)))
  const safeSentAtMs = normalizeDurationMs(sentAtMs) || Date.now()
  const progress = safeDurationMs > 0 ? Math.max(0, Math.min(1, safePositionMs / safeDurationMs)) : 0
  const safeArtworkSrc = normalizeArtworkSrc(artworkSrc) || normalizeArtworkSrc(artworkUrl)
  const safeLyricTimeline = normalizeLyricTimeline(lyricTimeline)

  return {
    seq: normalizeDurationMs(seq),
    hasTrack: hasTrack === null ? Boolean(safeTrackId || safeTitle) : Boolean(hasTrack),
    trackId: safeTrackId,
    artworkKey: normalizeText(artworkKey),
    artworkSrc: safeArtworkSrc,
    artworkUrl: safeArtworkSrc,
    lyricLine: safeLyric,
    lyricText: safeLyric,
    lyricVersion: normalizeDurationMs(lyricVersion),
    lyricSource: normalizeText(lyricSource),
    lyricIndex: normalizeOptionalIndex(lyricIndex),
    lyricTimeline: safeLyricTimeline,
    title: safeTitle,
    artist: safeArtist,
    metaText:
      normalizeText(metaText) ||
      (safeArtist && safeTitle && safeLyric !== safeTitle ? `${safeArtist} / ${safeTitle}` : safeArtist),
    isPlaying: Boolean(isPlaying),
    isLoading: Boolean(isLoading),
    progress,
    audioLevels: normalizeAudioLevels(audioLevels),
    positionMs: safePositionMs,
    durationMs: safeDurationMs,
    sentAtMs: safeSentAtMs,
    updatedAt: Date.now(),
  }
}

export function normalizeCapsuleProgressAnchor(
  payload: LyricCapsuleProgressAnchorInput = {},
): LyricCapsuleProgressAnchor {
  return {
    seq: normalizeDurationMs(payload.seq),
    trackId: normalizeTrackId(payload.trackId),
    isPlaying: Boolean(payload.isPlaying),
    durationMs: normalizeDurationMs(payload.durationMs),
    positionMs: normalizeDurationMs(payload.positionMs),
    sentAtMs: normalizeDurationMs(payload.sentAtMs) || Date.now(),
  }
}

export function normalizeCapsuleMeterFrame(payload: LyricCapsuleMeterFrameInput = {}): LyricCapsuleMeterFrame {
  const rawLevels = Array.isArray(payload.levels) ? payload.levels : []
  const levels = Array.from({ length: 8 }, (_, index) => {
    const level = Number(rawLevels[index])
    return Number.isFinite(level) ? Math.max(0, Math.min(1, level / 255)) : 0
  })

  return {
    seq: normalizeDurationMs(payload.seq),
    trackId: normalizeTrackId(payload.trackId),
    isPlaying: Boolean(payload.isPlaying),
    sentAtMs: normalizeDurationMs(payload.sentAtMs) || Date.now(),
    levels,
  }
}

export async function getLyricCapsuleBootState(): Promise<LyricCapsuleSnapshot> {
  if (!isHostBridgeAvailable()) {
    return createLyricCapsuleSnapshot()
  }

  const payload = await hostRequest<LyricCapsuleSnapshotInput>('capsule_get_boot_state')
  return createLyricCapsuleSnapshot(payload)
}

export async function releaseLyricCapsule(): Promise<void> {
  if (!isHostBridgeAvailable()) {
    return
  }

  await hostRequest('capsule_release')
}

export async function applyLyricCapsuleHitRegion({
  capsuleWidth,
  expanded,
  viewportWidth,
  viewportHeight,
  collapsedHeight,
  expandedHeight,
}: LyricCapsuleHitRegionRequest = {}): Promise<boolean> {
  if (!isHostBridgeAvailable()) {
    return false
  }

  await hostRequest('capsule_apply_hit_region', {
    request: {
      capsuleWidth: normalizeNumber(capsuleWidth),
      expanded: Boolean(expanded),
      viewportWidth: normalizeNumber(viewportWidth),
      viewportHeight: normalizeNumber(viewportHeight),
      collapsedHeight: normalizeNumber(collapsedHeight),
      expandedHeight: normalizeNumber(expandedHeight),
    },
  })
  return true
}

export async function getLyricCapsuleAppearance(): Promise<LyricCapsuleAppearance> {
  if (!isHostBridgeAvailable()) {
    return { ...DEFAULT_CAPSULE_APPEARANCE }
  }

  return normalizeCapsuleAppearance(await hostRequest<unknown>('capsule_appearance_get'))
}

export function listenForCapsuleAppearance(handler: (appearance: LyricCapsuleAppearance) => void): Promise<UnlistenFn> {
  if (!isHostBridgeAvailable() || typeof handler !== 'function') {
    return Promise.resolve(() => {})
  }

  return Promise.resolve(onHostEvent((event) => {
    if (event.event === LYRIC_CAPSULE_APPEARANCE_EVENT) handler(normalizeCapsuleAppearance(event.data))
  }))
}

/** Settings asked for the default position; the page owns where the capsule sits. */
export function listenForCapsulePositionReset(handler: () => void): Promise<UnlistenFn> {
  if (!isHostBridgeAvailable() || typeof handler !== 'function') {
    return Promise.resolve(() => {})
  }

  return Promise.resolve(onHostEvent((event) => {
    if (event.event === LYRIC_CAPSULE_POSITION_RESET_EVENT) handler()
  }))
}

export async function openLyricCapsuleSettings(): Promise<void> {
  if (isHostBridgeAvailable()) await hostRequest('capsule_open_settings')
}

export function listenForCapsuleState(handler: (snapshot: LyricCapsuleSnapshot) => void): Promise<UnlistenFn> {
  if (!isHostBridgeAvailable() || typeof handler !== 'function') {
    return Promise.resolve(() => {})
  }

  return Promise.resolve(onHostEvent((event) => {
    if (event.event === LYRIC_CAPSULE_STATE_EVENT)
      handler(createLyricCapsuleSnapshot((event.data ?? {}) as LyricCapsuleSnapshotInput))
  }))
}

export function listenForCapsuleProgressAnchor(
  handler: (anchor: LyricCapsuleProgressAnchor) => void,
): Promise<UnlistenFn> {
  if (!isHostBridgeAvailable() || typeof handler !== 'function') {
    return Promise.resolve(() => {})
  }

  return Promise.resolve(onHostEvent((event) => {
    if (event.event === LYRIC_CAPSULE_PROGRESS_ANCHOR_EVENT)
      handler(normalizeCapsuleProgressAnchor((event.data ?? {}) as LyricCapsuleProgressAnchorInput))
  }))
}

export function listenForCapsuleMeter(handler: (frame: LyricCapsuleMeterFrame) => void): Promise<UnlistenFn> {
  if (!isHostBridgeAvailable() || typeof handler !== 'function') {
    return Promise.resolve(() => {})
  }

  return Promise.resolve(onHostEvent((event) => {
    if (event.event === LYRIC_CAPSULE_METER_EVENT)
      handler(normalizeCapsuleMeterFrame((event.data ?? {}) as LyricCapsuleMeterFrameInput))
  }))
}

function isLyricCapsuleControlAction(action: unknown): action is LyricCapsuleControlAction {
  return ['previous', 'toggle-playback', 'next', 'seek'].includes(action as string)
}

function normalizeSeekSeconds(value: unknown): number | null {
  const seconds = Number(value)

  if (!Number.isFinite(seconds) || seconds < 0) {
    return null
  }

  return seconds
}

export async function closeLyricCapsule(): Promise<void> {
  if (isHostBridgeAvailable()) await hostRequest('capsule_close')
}

export async function requestLyricCapsuleControl(
  action: unknown,
  options: LyricCapsuleControlRequestOptions = {},
): Promise<boolean> {
  if (!isHostBridgeAvailable() || !isLyricCapsuleControlAction(action)) {
    return false
  }

  const payload: LyricCapsuleControlEventPayload = {
    action,
    sentAtMs: Date.now(),
  }

  if (action === 'seek') {
    const seconds = normalizeSeekSeconds(options.seconds)

    if (seconds === null) {
      return false
    }

    payload.seconds = seconds
  }

  await hostRequest('capsule_control', { payload })
  return true
}
