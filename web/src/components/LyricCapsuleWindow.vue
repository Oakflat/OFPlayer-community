<script setup lang="ts">
import '../styles/capsuleTextMotion.css'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CSSProperties } from 'vue'
import { lyricWordProgress } from '../models/lyrics'
import { LogicalPosition, getCurrentWindow, primaryMonitor } from '../services/capsuleWindowHost'
import { useWebLocale } from '../services/useWebLocale'
import {
  applyLyricCapsuleHitRegion,
  createLyricCapsuleSnapshot,
  findCapsuleTimelineLine,
  getLyricCapsuleAppearance,
  getLyricCapsuleBootState,
  listenForCapsuleAppearance,
  listenForCapsuleMeter,
  listenForCapsulePositionReset,
  listenForCapsuleProgressAnchor,
  listenForCapsuleState,
  openLyricCapsuleSettings,
  releaseLyricCapsule,
  closeLyricCapsule,
  requestLyricCapsuleControl,
} from '../services/lyricCapsuleBridge'
import {
  CAPSULE_CONTROL_ROW_HEIGHT,
  CAPSULE_ECHO_BARS,
  CAPSULE_ECHO_STEP_MS,
  CAPSULE_EXPANDED_MIN_WIDTH,
  CAPSULE_EXPANDED_RADIUS,
  CAPSULE_MINI_SIZE,
  CAPSULE_SPECTRUM_ORDER,
  DEFAULT_CAPSULE_APPEARANCE,
  capsuleAlpha,
  capsuleEchoSample,
  capsuleLighten,
  capsuleMetrics,
  capsuleOnAccent,
  capsulePalette,
  capsuleLineWidth,
  pickArtworkAccent,
  resolveCapsuleAccent,
  type LyricCapsuleAppearance,
} from '../services/lyricCapsuleAppearance'
import {
  elapsedMs,
  logLyricCapsuleError as rawLogLyricCapsuleError,
  logLyricCapsuleInfo as rawLogLyricCapsuleInfo,
  logLyricCapsuleWarn as rawLogLyricCapsuleWarn,
  nowMs,
} from '../services/lyricCapsuleDiagnostics'
import {
  LYRIC_CAPSULE_HIT_REGION_COLLAPSE_DELAY_MS,
  LYRIC_CAPSULE_HEIGHT,
  LYRIC_CAPSULE_TOP_MARGIN,
  LYRIC_CAPSULE_WIDTH,
} from '../services/lyricCapsuleWindowBounds'

type TimerHandle = number | null
type AnimationFrameHandle = ReturnType<typeof requestAnimationFrame> | null
type UnlistenFn = () => void

type CapsuleControlAction = 'previous' | 'toggle-playback' | 'next'
type LyricCapsuleTimelineLine = {
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

type CapsuleWindowPosition = {
  x: number
  y: number
}

type LyricCapsuleSnapshot = {
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
  lyricTimeline: Array<LyricCapsuleTimelineLine | null>
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

type LyricCapsuleStatePatch = Record<string, unknown>

type LyricCapsuleMeterFrame = {
  seq: number
  trackId: string | null
  isPlaying: boolean
  sentAtMs: number
  levels: number[]
}

type ManualCapsuleDragState = {
  pointerId: number
  startScreenX: number
  startScreenY: number
  startWindowX: number
  startWindowY: number
}

type ReadonlyNumericTuple8 = readonly [number, number, number, number, number, number, number, number]

type LyricCapsuleDiagnosticPayload = unknown

const logLyricCapsuleInfo = (event: string, payload?: LyricCapsuleDiagnosticPayload) =>
  rawLogLyricCapsuleInfo(event, payload as never)
const logLyricCapsuleWarn = (event: string, payload?: LyricCapsuleDiagnosticPayload) =>
  rawLogLyricCapsuleWarn(event, payload as never)
const logLyricCapsuleError = (event: string, payload?: LyricCapsuleDiagnosticPayload) =>
  rawLogLyricCapsuleError(event, payload as never)

const EMPTY_AUDIO_LEVELS: ReadonlyNumericTuple8 = [0, 0, 0, 0, 0, 0, 0, 0]
const CAPSULE_POSITION_STORAGE_KEY = 'ofplayer:lyric-capsule-window-position:v1'
const CAPSULE_POSITION_LOCK_STORAGE_KEY = 'ofplayer:lyric-capsule-window-position-locked:v1'
const CAPSULE_MAX_VISIBLE_ARTISTS = 2
const CAPSULE_LYRIC_RENDER_LEAD_MS = 90
const ECHO_IDLE_LEVEL = 0.04
// Segoe Fluent Icons, the same glyphs as the main window's playback bar.
const GLYPH_PLAY = ''
const GLYPH_PAUSE = ''
const GLYPH_LOCK = ''
const GLYPH_UNLOCK = ''
const GLYPH_RESET = ''
const scriptStartedAt = nowMs()
const { t } = useWebLocale('capsuleTitle')
const capsuleState = ref<LyricCapsuleSnapshot>(createLyricCapsuleSnapshot() as LyricCapsuleSnapshot)
const appearance = ref<LyricCapsuleAppearance>({ ...DEFAULT_CAPSULE_APPEARANCE })
const artworkAccent = ref<string | null>(null)
const progressRatio = ref<number>(capsuleState.value.progress)
const progressSliderRef = ref<HTMLElement | null>(null)
const progressScrubbing = ref<boolean>(false)
const progressScrubPositionMs = ref<number>(0)
const displayedLyricText = ref<string>(capsuleState.value.lyricText)
const displayedLyricIndex = ref<number | null>(capsuleState.value.lyricIndex)
const meterLevels = ref<number[]>([...EMPTY_AUDIO_LEVELS])
const echoHistory = ref<number[]>(Array.from({ length: CAPSULE_ECHO_BARS }, () => ECHO_IDLE_LEVEL))
const echoTrackElement = ref<HTMLElement | null>(null)
const displayedArtworkUrl = ref<string>('')
const capsuleExpanded = ref<boolean>(false)
const capsuleElement = ref<HTMLElement | null>(null)
let capsuleResizeObserver: ResizeObserver | null = null
let hitRegionFrameId: number | null = null
const capsulePositionLocked = ref<boolean>(readStoredCapsulePositionLocked())
const capsuleDragActive = ref<boolean>(false)
let unlistenState: UnlistenFn | null = null
let unlistenProgressAnchor: UnlistenFn | null = null
let unlistenMeter: UnlistenFn | null = null
let unlistenAppearance: UnlistenFn | null = null
let unlistenPositionReset: UnlistenFn | null = null
let mountedAt: number | null = null
let progressFrameId: AnimationFrameHandle = null
let artworkLoadStartedAt: number | null = null
let lastArtworkUrl = ''
let artworkPreloadToken = 0
let artworkAccentToken = 0
let artworkAccentPending: Promise<void> = Promise.resolve()
const artworkAccentCache = new Map<string, string | null>()
let echoLastPushAt = 0
let controlRequestLocked = false
let hitRegionTimerId: TimerHandle = null
let positionSaveTimerId: TimerHandle = null
let manualDragState: ManualCapsuleDragState | null = null

void logLyricCapsuleInfo('capsule_script_loaded', {
  scriptStartedAtMs: Math.round(scriptStartedAt),
})

const progressDurationMs = computed(() => {
  const durationMs = Number(capsuleState.value.durationMs)
  return Number.isFinite(durationMs) && durationMs > 0 ? durationMs : 0
})

const liveProgressPositionMs = computed(() => {
  return clampProgressPositionMs(progressRatio.value * progressDurationMs.value)
})

const displayProgressPositionMs = computed(() => {
  return progressScrubbing.value ? progressScrubPositionMs.value : liveProgressPositionMs.value
})

const displayProgressRatio = computed(() => {
  const durationMs = progressDurationMs.value

  if (durationMs <= 0) {
    return 0
  }

  return Math.max(0, Math.min(1, displayProgressPositionMs.value / durationMs))
})

const progressSliderDisabled = computed(() => {
  return !capsuleState.value.hasTrack || progressDurationMs.value <= 0
})

const progressAriaValueNow = computed(() => Math.round(displayProgressPositionMs.value / 1_000))
const progressAriaValueMax = computed(() => Math.max(0, Math.round(progressDurationMs.value / 1_000)))
const progressAriaText = computed(() => formatCapsuleTime(displayProgressPositionMs.value))
const positionText = computed(() => formatCapsuleTime(displayProgressPositionMs.value))
const durationText = computed(() => formatCapsuleTime(progressDurationMs.value))

const progressStyle = computed(() => ({
  transform: `scaleX(${displayProgressRatio.value})`,
}))

const rawLyricText = computed(() => displayedLyricText.value || capsuleState.value.lyricText || capsuleState.value.title || t('musicReady'))
const timelineLines = computed(() =>
  (Array.isArray(capsuleState.value.lyricTimeline) ? capsuleState.value.lyricTimeline : [])
    .filter((line): line is LyricCapsuleTimelineLine => Boolean(line)),
)
const timedCapsuleLine = computed(() => {
  const line = findTimelineLyric(liveProgressPositionMs.value)
  const completeText = line?.words.map((word) => word.text).join('').replace(/\s+/g, ' ').trim()
  return line?.index === displayedLyricIndex.value && line.words.length > 0 &&
    completeText === line.text.replace(/\s+/g, ' ').trim() ? line : null
})

function capsuleWordStyle(word: { startMs: number; endMs: number }): CSSProperties {
  const progress = lyricWordProgress(
    { startTime: word.startMs, endTime: word.endMs },
    liveProgressPositionMs.value,
  )
  return { '--capsule-word-progress': `${(progress * 100).toFixed(2)}%` } as CSSProperties
}

const songInfoText = computed(() => {
  const title = capsuleState.value.title || ''
  const artist = compactArtistList(capsuleState.value.artist || '')

  if (artist && title) {
    return `${artist} · ${title}`
  }

  return capsuleState.value.metaText || artist || title || 'OFPlayer'
})

const hasTranslations = computed(() => timelineLines.value.some((line) => line.translation))

const secondLineText = computed(() => {
  const mode = appearance.value.secondLine
  if (mode === 'none') return ''
  if (mode === 'meta' || timelineLines.value.length === 0) return songInfoText.value

  const lines = timelineLines.value
  const current = lines.findIndex((line) => line.index !== null && line.index === displayedLyricIndex.value)
  if (mode === 'translation') {
    return hasTranslations.value ? (current >= 0 ? lines[current].translation : '') : songInfoText.value
  }

  const currentText = current >= 0 ? lines[current].text : ''
  const next = lines.slice(current + 1).find((line) => !line.isBG && line.text !== currentText)
  return next?.text || songInfoText.value
})

const lyricTransitionKey = computed(() => {
  return `${capsuleState.value.trackId || '_'}:${capsuleState.value.lyricVersion}:${displayedLyricIndex.value ?? '_'}:${rawLyricText.value}`
})

const secondLineTransitionKey = computed(() => `${appearance.value.secondLine}:${secondLineText.value}`)

const playbackControlLabel = computed(() => t(capsuleState.value.isPlaying ? 'pause' : 'play'))
const playbackGlyph = computed(() => (capsuleState.value.isPlaying ? GLYPH_PAUSE : GLYPH_PLAY))
const capsulePositionLockLabel = computed(() =>
  t(capsulePositionLocked.value ? 'unlockPosition' : 'lockPosition'),
)
const capsulePositionLockGlyph = computed(() => (capsulePositionLocked.value ? GLYPH_LOCK : GLYPH_UNLOCK))

// --- Appearance ------------------------------------------------------------------------

const metrics = computed(() => capsuleMetrics(appearance.value))
const accentColor = computed(() => resolveCapsuleAccent(appearance.value, artworkAccent.value))
const palette = computed(() => capsulePalette(appearance.value.theme, appearance.value.opacity, artworkAccent.value))
const needsArtworkColour = computed(() => appearance.value.accent === 'cover' || appearance.value.theme === 'cover')

const controlsVisible = computed(() => appearance.value.controls === 'always' || capsuleExpanded.value)
const capsuleMini = computed(() =>
  appearance.value.whenPaused === 'mini' &&
  capsuleState.value.hasTrack &&
  !capsuleState.value.isPlaying &&
  !capsuleExpanded.value &&
  !progressScrubbing.value,
)

// Measure an unconstrained copy using the same resolved font as the visible lyric.
// Measuring the clipped text container would feed the capsule's width back into itself.
const lyricMeasureElement = ref<HTMLElement | null>(null)
const measuredLineWidth = ref<number>()
function measureCurrentLyric(): void {
  const width = lyricMeasureElement.value?.getBoundingClientRect().width
  if (width !== undefined && Number.isFinite(width) && width >= 0) measuredLineWidth.value = width
}
watch([rawLyricText, () => metrics.value.fontSize], measureCurrentLyric, { flush: 'post' })
const lineWidth = computed(() => capsuleLineWidth(appearance.value, rawLyricText.value, measuredLineWidth.value))

const capsuleWidth = computed(() => {
  if (capsuleMini.value) return CAPSULE_MINI_SIZE
  return controlsVisible.value ? Math.max(lineWidth.value, CAPSULE_EXPANDED_MIN_WIDTH) : lineWidth.value
})

const capsuleHeight = computed(() => {
  if (capsuleMini.value) return CAPSULE_MINI_SIZE
  return metrics.value.rowHeight + (controlsVisible.value ? CAPSULE_CONTROL_ROW_HEIGHT : 0)
})

const capsuleRadius = computed(() => {
  if (capsuleMini.value) return CAPSULE_MINI_SIZE / 2
  return controlsVisible.value ? CAPSULE_EXPANDED_RADIUS : metrics.value.rowHeight / 2
})

const capsuleStyle = computed(() => {
  const accent = accentColor.value
  const colours = palette.value
  return {
    '--capsule-width': `${capsuleWidth.value}px`,
    '--capsule-height': `${capsuleHeight.value}px`,
    '--capsule-radius': `${capsuleRadius.value}px`,
    '--capsule-row': `${metrics.value.rowHeight}px`,
    '--capsule-font': `${metrics.value.fontSize}px`,
    '--capsule-font-second': `${metrics.value.secondFontSize}px`,
    '--capsule-wave-width': `${metrics.value.waveWidth}px`,
    '--capsule-align': appearance.value.align,
    '--capsule-progress-percent': `${(displayProgressRatio.value * 100).toFixed(3)}%`,
    '--cap-bg': colours.background,
    '--cap-solid': colours.solid,
    '--cap-fg': colours.foreground,
    '--cap-fg2': colours.secondary,
    '--cap-unsung': colours.unsung,
    '--cap-border': colours.border,
    '--cap-track': colours.track,
    '--cap-hover': colours.hover,
    '--cap-accent': accent,
    '--cap-accent-soft': capsuleAlpha(accent, 0.18),
    '--cap-accent-high': capsuleLighten(accent, 0.35),
    '--cap-on-accent': capsuleOnAccent(accent),
  } as CSSProperties
})

const spectrumBars = computed<number[]>(() => {
  const levels = Array.isArray(meterLevels.value) ? meterLevels.value : [...EMPTY_AUDIO_LEVELS]
  return CAPSULE_SPECTRUM_ORDER.map((sourceIndex) => {
    const level = Number(levels[sourceIndex])
    return Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0
  })
})

function spectrumBarStyle(level: number): CSSProperties {
  const height = 3 + Math.pow(level, 0.58) * 21
  return { transform: `scaleY(${(height / 24).toFixed(3)})` }
}

function echoBarStyle(level: number, index: number): CSSProperties {
  return {
    height: `${(3 + level * 19).toFixed(1)}px`,
    opacity: (0.3 + 0.7 * (index / (CAPSULE_ECHO_BARS - 1))).toFixed(2),
  }
}

// Recent loudness flows left: one sample per step, slid smoothly in between.
function updateEchoWave(now: number): void {
  if (appearance.value.wave !== 'echo') return
  const track = echoTrackElement.value
  const playing = capsuleState.value.isPlaying
  const idle = !playing && echoHistory.value.every((level) => level <= ECHO_IDLE_LEVEL)

  if (idle) {
    echoLastPushAt = now
    if (track) track.style.transform = 'translate3d(0, 0, 0)'
    return
  }

  if (now - echoLastPushAt >= CAPSULE_ECHO_STEP_MS) {
    echoHistory.value = [...echoHistory.value.slice(1), capsuleEchoSample(meterLevels.value, playing)]
    echoLastPushAt = now - echoLastPushAt > CAPSULE_ECHO_STEP_MS * 3 ? now : echoLastPushAt + CAPSULE_ECHO_STEP_MS
  }

  const fraction = Math.min(1, (now - echoLastPushAt) / CAPSULE_ECHO_STEP_MS)
  if (track) track.style.transform = `translate3d(${(5 - fraction * 5).toFixed(2)}px, 0, 0)`
}

function readArtworkAccent(url: string): Promise<string | null> {
  return new Promise((resolve) => {
    const image = new Image()
    image.crossOrigin = 'anonymous'
    image.decoding = 'async'
    image.onload = () => {
      try {
        const canvas = document.createElement('canvas')
        canvas.width = 24
        canvas.height = 24
        const context = canvas.getContext('2d', { willReadFrequently: true })
        if (!context) return resolve(null)
        context.drawImage(image, 0, 0, 24, 24)
        resolve(pickArtworkAccent(context.getImageData(0, 0, 24, 24).data))
      } catch (error) {
        void logLyricCapsuleWarn('capsule_artwork_accent_failed', { error })
        resolve(null)
      }
    }
    image.onerror = () => resolve(null)
    image.src = url
  })
}

function syncArtworkAccent(url: string, needed: boolean): void {
  const token = ++artworkAccentToken
  if (!needed || !url) {
    artworkAccent.value = null
    artworkAccentPending = Promise.resolve()
    return
  }

  if (artworkAccentCache.has(url)) {
    artworkAccent.value = artworkAccentCache.get(url) ?? null
    artworkAccentPending = Promise.resolve()
    return
  }

  artworkAccentPending = readArtworkAccent(url).then((colour) => {
    if (artworkAccentCache.size >= 32) artworkAccentCache.clear()
    artworkAccentCache.set(url, colour)
    if (token === artworkAccentToken) artworkAccent.value = colour
  })
}

function applyAppearance(next: LyricCapsuleAppearance, reason: string): void {
  appearance.value = next
  void logLyricCapsuleInfo('capsule_appearance_applied', { reason, ...next })
}

// --- Position ----------------------------------------------------------------------------

function readStoredJson<T = unknown>(key: string): T | null {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return null
  }

  try {
    return JSON.parse(window.localStorage.getItem(key) || 'null')
  } catch {
    return null
  }
}

function writeStoredJson(key: string, value: unknown): boolean {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return false
  }

  try {
    window.localStorage.setItem(key, JSON.stringify(value))
    return true
  } catch {
    return false
  }
}

function removeStoredItem(key: string): boolean {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return false
  }

  try {
    window.localStorage.removeItem(key)
    return true
  } catch {
    return false
  }
}

function readStoredCapsulePositionLocked(): boolean {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return false
  }

  try {
    return window.localStorage.getItem(CAPSULE_POSITION_LOCK_STORAGE_KEY) === '1'
  } catch {
    return false
  }
}

function writeStoredCapsulePositionLocked(locked: boolean): boolean {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return false
  }

  try {
    window.localStorage.setItem(CAPSULE_POSITION_LOCK_STORAGE_KEY, locked ? '1' : '0')
    return true
  } catch {
    return false
  }
}

function readStoredCapsulePosition(): CapsuleWindowPosition | null {
  const position = readStoredJson<CapsuleWindowPosition>(CAPSULE_POSITION_STORAGE_KEY)
  const x = Number(position?.x)
  const y = Number(position?.y)

  if (!Number.isFinite(x) || !Number.isFinite(y)) {
    return null
  }

  return {
    x,
    y,
  }
}

function clampNumber(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max)
}

function clampProgressPositionMs(value: number): number {
  const durationMs = progressDurationMs.value
  const numericValue = Number(value)

  if (!Number.isFinite(numericValue) || numericValue <= 0) {
    return 0
  }

  if (durationMs <= 0) {
    return numericValue
  }

  return Math.min(numericValue, durationMs)
}

function formatCapsuleTime(positionMs: number): string {
  const totalSeconds = Math.max(0, Math.round(positionMs / 1_000))
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60

  return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

// The window is 560x112 CSS px. Windows text size draws CSS px larger than the
// monitor's logical px, so the window is that much wider in position units.
function capsuleWindowLogicalScale(scaleFactor: number): number {
  const ratio = (window.devicePixelRatio || scaleFactor) / scaleFactor
  return Number.isFinite(ratio) && ratio >= 1 ? ratio : 1
}

async function clampCapsulePositionToPrimaryMonitor(position: CapsuleWindowPosition): Promise<CapsuleWindowPosition> {
  try {
    const monitor = await primaryMonitor()
    if (!monitor) {
      return position
    }

    const scaleFactor = monitor.scaleFactor || 1
    const monitorX = monitor.position.x / scaleFactor
    const monitorY = monitor.position.y / scaleFactor
    const monitorWidth = monitor.size.width / scaleFactor
    const monitorHeight = monitor.size.height / scaleFactor
    const windowSize = capsuleWindowLogicalScale(scaleFactor)
    const maxX = monitorX + Math.max(0, monitorWidth - LYRIC_CAPSULE_WIDTH * windowSize)
    const maxY = monitorY + Math.max(0, monitorHeight - LYRIC_CAPSULE_HEIGHT * windowSize)

    return {
      x: Math.round(clampNumber(position.x, monitorX, maxX)),
      y: Math.round(clampNumber(position.y, monitorY, maxY)),
    }
  } catch {
    return position
  }
}

async function restoreCapsuleWindowPosition(): Promise<void> {
  const position = readStoredCapsulePosition()

  if (!position) {
    return
  }

  const clampedPosition = await clampCapsulePositionToPrimaryMonitor(position)

  try {
    await getCurrentWindow().setPosition(new LogicalPosition(clampedPosition.x, clampedPosition.y))
  } catch (error) {
    void logLyricCapsuleWarn('capsule_position_restore_failed', {
      x: clampedPosition.x,
      y: clampedPosition.y,
      error,
    })
  }
}

async function resolveDefaultCapsuleWindowPosition(): Promise<CapsuleWindowPosition> {
  try {
    const monitor = await primaryMonitor()

    if (!monitor) {
      return {
        x: 120,
        y: LYRIC_CAPSULE_TOP_MARGIN,
      }
    }

    const scaleFactor = monitor.scaleFactor || 1
    const monitorX = monitor.position.x / scaleFactor
    const monitorY = monitor.position.y / scaleFactor
    const monitorWidth = monitor.size.width / scaleFactor

    return {
      x: Math.round(monitorX + Math.max(0, (monitorWidth - LYRIC_CAPSULE_WIDTH * capsuleWindowLogicalScale(scaleFactor)) / 2)),
      y: Math.round(monitorY + LYRIC_CAPSULE_TOP_MARGIN),
    }
  } catch {
    return {
      x: 120,
      y: LYRIC_CAPSULE_TOP_MARGIN,
    }
  }
}

async function captureCurrentCapsuleWindowPosition(): Promise<CapsuleWindowPosition> {
  const appWindow = getCurrentWindow()
  const [position, scaleFactor] = await Promise.all([
    appWindow.outerPosition(),
    appWindow.scaleFactor(),
  ])
  const scale = Number(scaleFactor) || 1

  return {
    x: Math.round(position.x / scale),
    y: Math.round(position.y / scale),
  }
}

async function saveCapsuleWindowPosition(reason: string): Promise<void> {
  try {
    const position = await captureCurrentCapsuleWindowPosition()
    writeStoredJson(CAPSULE_POSITION_STORAGE_KEY, position)
    void logLyricCapsuleInfo('capsule_position_saved', {
      reason,
      ...position,
      locked: capsulePositionLocked.value,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_position_save_failed', {
      reason,
      error,
    })
  }
}

function scheduleCapsulePositionSave(reason: string, delayMs = 180): void {
  if (positionSaveTimerId !== null) {
    window.clearTimeout(positionSaveTimerId)
    positionSaveTimerId = null
  }

  positionSaveTimerId = window.setTimeout(() => {
    positionSaveTimerId = null
    void saveCapsuleWindowPosition(reason)
  }, delayMs)
}

// Asked for from Settings; the pointer is elsewhere, so the capsule stays as it is.
async function resetCapsuleWindowPosition(): Promise<void> {
  if (positionSaveTimerId !== null) {
    window.clearTimeout(positionSaveTimerId)
    positionSaveTimerId = null
  }

  removeStoredItem(CAPSULE_POSITION_STORAGE_KEY)
  const position = await resolveDefaultCapsuleWindowPosition()

  try {
    await getCurrentWindow().setPosition(new LogicalPosition(position.x, position.y))
    void logLyricCapsuleInfo('capsule_position_reset', {
      ...position,
      locked: capsulePositionLocked.value,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_position_reset_failed', {
      ...position,
      error,
    })
  }
}

function compactArtistList(artistText: string): string {
  const normalized = String(artistText || '').replace(/\s+/g, ' ').trim()
  if (!normalized) {
    return ''
  }

  const artists = normalized
    .split(/\s*(?:\/|、|,|，|;|；|\s+&\s+|\s+and\s+)\s*/i)
    .map((artist) => artist.trim())
    .filter(Boolean)

  if (artists.length <= CAPSULE_MAX_VISIBLE_ARTISTS) {
    return normalized
  }

  const visibleArtists = artists.slice(0, CAPSULE_MAX_VISIBLE_ARTISTS).join('/')
  return `${visibleArtists} +${artists.length - CAPSULE_MAX_VISIBLE_ARTISTS}`
}

// --- Playback state ----------------------------------------------------------------------

function fallbackLyricText(): string {
  return capsuleState.value.lyricText || capsuleState.value.title || ''
}

function findTimelineLyric(positionMs: number | null | undefined): LyricCapsuleTimelineLine | null {
  const timeline = timelineLines.value
  const safePositionMs = Number(positionMs)

  if (!Number.isFinite(safePositionMs) || timeline.length === 0) {
    return null
  }
  return findCapsuleTimelineLine(
    timeline,
    safePositionMs,
    capsuleState.value.lyricSource,
    capsuleState.value.lyricSource === 'sidecar-ttml' ? 0 : CAPSULE_LYRIC_RENDER_LEAD_MS,
  )
}

function syncDisplayedLyric(positionMs: number, force = false): void {
  const timelineLine = findTimelineLyric(positionMs)
  const nextText = timelineLine?.text || fallbackLyricText()
  const nextIndex = timelineLine?.index ?? capsuleState.value.lyricIndex ?? null

  if (force || nextText !== displayedLyricText.value || nextIndex !== displayedLyricIndex.value) {
    displayedLyricText.value = nextText
    displayedLyricIndex.value = nextIndex
  }
}

function updateInterpolatedProgress(): void {
  const durationMs = Number(capsuleState.value.durationMs)
  const positionMs = Number(capsuleState.value.positionMs)

  if (!Number.isFinite(durationMs) || durationMs <= 0) {
    progressRatio.value = 0
    syncDisplayedLyric(0)
    return
  }

  const sentAtMs = Number(capsuleState.value.sentAtMs)
  const elapsedSinceAnchorMs =
    capsuleState.value.isPlaying && Number.isFinite(sentAtMs) ? Math.max(0, Date.now() - sentAtMs) : 0
  const interpolatedPositionMs = Math.min(durationMs, Math.max(0, positionMs + elapsedSinceAnchorMs))
  progressRatio.value = Math.max(0, Math.min(1, interpolatedPositionMs / durationMs))
  syncDisplayedLyric(interpolatedPositionMs)
}

function startProgressInterpolation(): void {
  if (progressFrameId) {
    cancelAnimationFrame(progressFrameId)
    progressFrameId = null
  }

  // The retained capsule may be hidden for hours. Resume the same page when it
  // becomes visible instead of spending a frame loop on an invisible surface.
  if (document.hidden) return

  const tick = (now: number) => {
    updateInterpolatedProgress()
    updateEchoWave(now)
    progressFrameId = requestAnimationFrame(tick)
  }

  progressFrameId = requestAnimationFrame(tick)
}

function displayArtworkWhenReady(nextArtworkUrl: string): void {
  const token = ++artworkPreloadToken
  if (!nextArtworkUrl) {
    displayedArtworkUrl.value = ''
    return
  }
  if (nextArtworkUrl === displayedArtworkUrl.value) {
    return
  }

  const image = new Image()
  image.onload = () => {
    if (token === artworkPreloadToken) {
      displayedArtworkUrl.value = nextArtworkUrl
    }
  }
  image.onerror = () => {
    if (token === artworkPreloadToken) {
      displayedArtworkUrl.value = ''
      void logLyricCapsuleWarn('capsule_artwork_preload_failed', {
        elapsedSinceArtworkSetMs: artworkLoadStartedAt ? elapsedMs(artworkLoadStartedAt) : null,
        artworkLength: nextArtworkUrl.length,
      })
    }
  }
  image.src = nextArtworkUrl
}

function trackArtworkLoad(nextArtworkUrl: string, reason: string): void {
  if (nextArtworkUrl === lastArtworkUrl) {
    return
  }

  lastArtworkUrl = nextArtworkUrl
  artworkLoadStartedAt = nextArtworkUrl ? nowMs() : null
  displayArtworkWhenReady(nextArtworkUrl)

  if (!nextArtworkUrl) {
    return
  }

  void logLyricCapsuleInfo('capsule_artwork_set', {
    elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
    reason,
    artworkLength: nextArtworkUrl.length,
    artworkKind: nextArtworkUrl.startsWith('data:') ? 'blocked-data-url' : 'cache-or-url',
  })
}

function applyCapsuleState(nextState: LyricCapsuleStatePatch, reason: string): void {
  const mergedState = createLyricCapsuleSnapshot({
    ...capsuleState.value,
    ...nextState,
    audioLevels: meterLevels.value,
  } as LyricCapsuleStatePatch) as LyricCapsuleSnapshot

  trackArtworkLoad(mergedState.artworkUrl, reason)
  capsuleState.value = mergedState
  if (!mergedState.isPlaying) meterLevels.value = [...EMPTY_AUDIO_LEVELS]
  syncDisplayedLyric(mergedState.positionMs, true)
  updateInterpolatedProgress()
}

function applyProgressAnchor(anchor: LyricCapsuleStatePatch): void {
  capsuleState.value = createLyricCapsuleSnapshot({
    ...capsuleState.value,
    ...anchor,
    audioLevels: meterLevels.value,
  } as LyricCapsuleStatePatch) as LyricCapsuleSnapshot
  if (!capsuleState.value.isPlaying) meterLevels.value = [...EMPTY_AUDIO_LEVELS]
  syncDisplayedLyric(capsuleState.value.positionMs, true)
  updateInterpolatedProgress()
}

function applyMeterFrame(frame: LyricCapsuleMeterFrame): void {
  const currentTrackId = capsuleState.value.trackId

  if (frame.trackId && currentTrackId && frame.trackId !== currentTrackId) {
    return
  }

  meterLevels.value = frame.isPlaying && capsuleState.value.isPlaying ? frame.levels : [...EMPTY_AUDIO_LEVELS]
}

// --- Hit region and expansion --------------------------------------------------------------

async function applyCapsuleProtectionRegion(reason: string): Promise<void> {
  const rowHeight = metrics.value.rowHeight
  const expandedHeight = rowHeight + CAPSULE_CONTROL_ROW_HEIGHT
  const collapsedHeight = capsuleMini.value
    ? CAPSULE_MINI_SIZE
    : appearance.value.controls === 'always' ? expandedHeight : rowHeight

  try {
    await nextTick()
    await applyLyricCapsuleHitRegion({
      capsuleWidth: capsuleElement.value?.getBoundingClientRect().width ?? capsuleWidth.value,
      expanded: controlsVisible.value,
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      collapsedHeight,
      expandedHeight,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_hit_region_update_failed', {
      reason,
      capsuleWidth: capsuleWidth.value,
      expanded: controlsVisible.value,
      error,
    })
  }
}

function scheduleCapsuleProtectionRegion(reason: string, delayMs = 0): void {
  if (hitRegionTimerId !== null) {
    window.clearTimeout(hitRegionTimerId)
    hitRegionTimerId = null
  }

  if (delayMs <= 0) {
    if (hitRegionFrameId === null) hitRegionFrameId = requestAnimationFrame(() => {
      hitRegionFrameId = null
      void applyCapsuleProtectionRegion(reason)
    })
    return
  }

  hitRegionTimerId = window.setTimeout(() => {
    hitRegionTimerId = null
    void applyCapsuleProtectionRegion(reason)
  }, delayMs)
}

function setCapsuleExpanded(expanded: boolean, reason: string): void {
  if (!expanded && progressScrubbing.value) {
    return
  }

  const delayMs = expanded ? 0 : LYRIC_CAPSULE_HIT_REGION_COLLAPSE_DELAY_MS
  if (capsuleExpanded.value !== expanded) {
    capsuleExpanded.value = expanded
  }

  scheduleCapsuleProtectionRegion(reason, delayMs)
}

function handleCapsuleFocusOut(event: FocusEvent): void {
  const currentTarget = event.currentTarget as HTMLElement | null
  if (currentTarget?.contains(event.relatedTarget as Node | null)) {
    return
  }

  setCapsuleExpanded(false, 'focus-out')
}

// --- Controls -----------------------------------------------------------------------------

async function handleCapsuleControl(action: CapsuleControlAction, event: MouseEvent | null = null): Promise<void> {
  if (!capsuleState.value.hasTrack || controlRequestLocked) {
    return
  }

  const currentTarget = event?.currentTarget as HTMLElement | null
  currentTarget?.blur()
  controlRequestLocked = true
  const startedAt = nowMs()

  try {
    await requestLyricCapsuleControl(action)
    void logLyricCapsuleInfo('capsule_control_requested', {
      action,
      elapsedMs: elapsedMs(startedAt),
      trackId: capsuleState.value.trackId,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_control_request_failed', {
      action,
      elapsedMs: elapsedMs(startedAt),
      error,
    })
  } finally {
    window.setTimeout(() => {
      controlRequestLocked = false
    }, 220)
  }
}

// From the capsule itself: it moves away from the pointer, so keep the controls open there.
function handleCapsulePositionReset(event: MouseEvent): void {
  ;(event.currentTarget as HTMLElement | null)?.blur()
  setCapsuleExpanded(true, 'position-reset')
  void resetCapsuleWindowPosition()
}

function openCapsuleSettings(event: MouseEvent): void {
  ;(event.currentTarget as HTMLElement | null)?.blur()
  void openLyricCapsuleSettings().catch((error: unknown) => {
    void logLyricCapsuleWarn('capsule_open_settings_failed', { error })
  })
}

function captureProgressPointer(event: PointerEvent): void {
  const target = event.currentTarget as HTMLElement | null

  if (!target || typeof target.setPointerCapture !== 'function') {
    return
  }

  try {
    target.setPointerCapture(event.pointerId)
  } catch {
    // Pointer capture can fail if the webview has already released this pointer.
  }
}

function releaseProgressPointer(event: PointerEvent): void {
  const target = event.currentTarget as HTMLElement | null

  if (!target || typeof target.releasePointerCapture !== 'function') {
    return
  }

  try {
    target.releasePointerCapture(event.pointerId)
  } catch {
    // Pointer capture can already be gone after cancel/blur.
  }
}

function resolveProgressPositionFromPointer(event: PointerEvent): number {
  const rect = progressSliderRef.value?.getBoundingClientRect?.()
  const clientX = event.clientX

  if (!rect || !Number.isFinite(clientX) || rect.width <= 0) {
    return progressScrubPositionMs.value
  }

  const ratio = clampNumber((clientX - rect.left) / rect.width, 0, 1)
  return clampProgressPositionMs(ratio * progressDurationMs.value)
}

function updateProgressScrubFromPointer(event: PointerEvent): void {
  if (progressSliderDisabled.value) {
    return
  }

  event.preventDefault()
  progressScrubbing.value = true
  progressScrubPositionMs.value = resolveProgressPositionFromPointer(event)
  syncDisplayedLyric(progressScrubPositionMs.value)
}

function beginProgressScrub(event: PointerEvent): void {
  if (progressSliderDisabled.value) {
    return
  }

  setCapsuleExpanded(true, 'progress-scrub-start')
  captureProgressPointer(event)
  updateProgressScrubFromPointer(event)
}

function handleProgressPointerMove(event: PointerEvent): void {
  if (!progressScrubbing.value) {
    return
  }

  updateProgressScrubFromPointer(event)
}

function cancelProgressScrub(event: PointerEvent): void {
  releaseProgressPointer(event)
  progressScrubbing.value = false
  progressScrubPositionMs.value = liveProgressPositionMs.value
  syncDisplayedLyric(liveProgressPositionMs.value, true)
}

async function commitProgressSeek(event: PointerEvent): Promise<void> {
  if (progressSliderDisabled.value) {
    progressScrubbing.value = false
    return
  }

  releaseProgressPointer(event)

  if (progressScrubbing.value && Number.isFinite(event.clientX)) {
    updateProgressScrubFromPointer(event)
  }

  const nextPositionMs = clampProgressPositionMs(progressScrubPositionMs.value)
  const startedAt = nowMs()
  progressScrubPositionMs.value = nextPositionMs
  progressScrubbing.value = false
  progressRatio.value = progressDurationMs.value > 0 ? nextPositionMs / progressDurationMs.value : 0
  syncDisplayedLyric(nextPositionMs, true)

  try {
    await requestLyricCapsuleControl('seek', {
      seconds: nextPositionMs / 1_000,
    })
    void logLyricCapsuleInfo('capsule_control_requested', {
      action: 'seek',
      elapsedMs: elapsedMs(startedAt),
      positionMs: Math.round(nextPositionMs),
      trackId: capsuleState.value.trackId,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_control_request_failed', {
      action: 'seek',
      elapsedMs: elapsedMs(startedAt),
      positionMs: Math.round(nextPositionMs),
      error,
    })
  }
}

function handleProgressKeydown(event: KeyboardEvent): void {
  if (progressSliderDisabled.value) {
    return
  }

  const durationMs = progressDurationMs.value
  const smallStepMs = Math.max(1_000, Math.min(durationMs / 100, 5_000))
  const largeStepMs = Math.max(smallStepMs, Math.min(durationMs / 10, 30_000))
  let nextPositionMs: number | null = null

  switch (event.key) {
    case 'ArrowLeft':
    case 'ArrowDown':
      nextPositionMs = displayProgressPositionMs.value - smallStepMs
      break
    case 'ArrowRight':
    case 'ArrowUp':
      nextPositionMs = displayProgressPositionMs.value + smallStepMs
      break
    case 'PageDown':
      nextPositionMs = displayProgressPositionMs.value - largeStepMs
      break
    case 'PageUp':
      nextPositionMs = displayProgressPositionMs.value + largeStepMs
      break
    case 'Home':
      nextPositionMs = 0
      break
    case 'End':
      nextPositionMs = durationMs
      break
    default:
      return
  }

  event.preventDefault()
  event.stopPropagation()
  nextPositionMs = clampProgressPositionMs(nextPositionMs)
  progressScrubPositionMs.value = nextPositionMs
  progressRatio.value = durationMs > 0 ? nextPositionMs / durationMs : 0
  syncDisplayedLyric(nextPositionMs, true)
  void requestLyricCapsuleControl('seek', {
    seconds: nextPositionMs / 1_000,
  })
}

function releaseManualCapsuleDrag(): void {
  manualDragState = null
  capsuleDragActive.value = false
  scheduleCapsulePositionSave('drag-end')
}

async function beginManualCapsuleDrag(event: PointerEvent): Promise<void> {
  const dragTarget = event.currentTarget as Element | null
  const pointerId = event.pointerId
  const startScreenX = event.screenX
  const startScreenY = event.screenY

  try {
    const position = await captureCurrentCapsuleWindowPosition()
    manualDragState = {
      pointerId,
      startScreenX,
      startScreenY,
      startWindowX: position.x,
      startWindowY: position.y,
    }
    dragTarget?.setPointerCapture?.(pointerId)
  } catch (error) {
    capsuleDragActive.value = false
    void logLyricCapsuleWarn('capsule_drag_fallback_failed', { error })
  }
}

function handleCapsulePointerDown(event: PointerEvent): void {
  if (
    capsulePositionLocked.value ||
    event.button !== 0 ||
    (event.target as Element | null)?.closest?.('button')
  ) {
    return
  }

  event.preventDefault()
  capsuleDragActive.value = true
  setCapsuleExpanded(true, 'drag-start')

  const appWindow = getCurrentWindow()
  void appWindow
    .startDragging()
    .then(() => {
      capsuleDragActive.value = false
      scheduleCapsulePositionSave('drag-start', 900)
    })
    .catch((error) => {
      void logLyricCapsuleWarn('capsule_drag_native_failed', { error })
      void beginManualCapsuleDrag(event)
    })
}

function handleCapsulePointerMove(event: PointerEvent): void {
  if (!manualDragState || manualDragState.pointerId !== event.pointerId) {
    return
  }

  const nextX = Math.round(manualDragState.startWindowX + event.screenX - manualDragState.startScreenX)
  const nextY = Math.round(manualDragState.startWindowY + event.screenY - manualDragState.startScreenY)

  void getCurrentWindow()
    .setPosition(new LogicalPosition(nextX, nextY))
    .catch((error) => {
      void logLyricCapsuleWarn('capsule_drag_position_failed', { error })
    })
}

function handleCapsulePointerRelease(event: PointerEvent): void {
  const currentTarget = event.currentTarget as Element | null
  if (manualDragState?.pointerId === event.pointerId) {
    currentTarget?.releasePointerCapture?.(event.pointerId)
  }

  if (capsuleDragActive.value || manualDragState) {
    releaseManualCapsuleDrag()
  }
}

function toggleCapsulePositionLock(event: MouseEvent | null = null): void {
  const currentTarget = event?.currentTarget as HTMLElement | null
  currentTarget?.blur()
  capsulePositionLocked.value = !capsulePositionLocked.value
  writeStoredCapsulePositionLocked(capsulePositionLocked.value)
  setCapsuleExpanded(true, 'position-lock-toggle')

  if (capsulePositionLocked.value) {
    void saveCapsuleWindowPosition('position-locked')
  }

  void logLyricCapsuleInfo('capsule_position_lock_changed', {
    locked: capsulePositionLocked.value,
  })
}

function handleArtworkLoad(event: Event): void {
  const image = event.currentTarget as HTMLImageElement | null
  if (!image) {
    return
  }
  const loadMs = artworkLoadStartedAt ? elapsedMs(artworkLoadStartedAt) : null

  if (loadMs === null || loadMs >= 100) {
    void logLyricCapsuleInfo('capsule_artwork_load_complete', {
      elapsedSinceArtworkSetMs: loadMs,
      elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
      naturalWidth: Number.isFinite(image?.naturalWidth) ? image.naturalWidth : 0,
      naturalHeight: Number.isFinite(image?.naturalHeight) ? image.naturalHeight : 0,
      artworkLength: lastArtworkUrl.length,
    })
  }

  artworkLoadStartedAt = null
}

function handleArtworkError(event: Event): void {
  // Never leave a broken-image glyph (or the previous track's cover) on screen.
  displayedArtworkUrl.value = ''
  void logLyricCapsuleWarn('capsule_artwork_load_failed', {
    elapsedSinceArtworkSetMs: artworkLoadStartedAt ? elapsedMs(artworkLoadStartedAt) : null,
    elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
    artworkLength: lastArtworkUrl.length,
    errorType: event.type,
  })

  artworkLoadStartedAt = null
}

watch([capsuleWidth, capsuleHeight, controlsVisible, capsuleMini], () => {
  scheduleCapsuleProtectionRegion('shape-change')
})

watch([displayedArtworkUrl, needsArtworkColour], ([url, needed]) => syncArtworkAccent(url, needed), { immediate: true })

function handleCapsuleViewportResize(): void {
  scheduleCapsuleProtectionRegion('viewport-resize')
}

onMounted(async () => {
  document.addEventListener('visibilitychange', startProgressInterpolation)
  mountedAt = nowMs()
  void logLyricCapsuleInfo('capsule_mount_start', {
    elapsedSinceScriptMs: elapsedMs(scriptStartedAt),
  })

  document.documentElement.dataset.appView = 'lyric-capsule'
  capsuleResizeObserver = new ResizeObserver((entries) => {
    if (entries.some((entry) => entry.target === lyricMeasureElement.value)) measureCurrentLyric()
    if (entries.some((entry) => entry.target === capsuleElement.value)) scheduleCapsuleProtectionRegion('layout-resize')
  })
  if (capsuleElement.value) capsuleResizeObserver.observe(capsuleElement.value)
  if (lyricMeasureElement.value) capsuleResizeObserver.observe(lyricMeasureElement.value)
  measureCurrentLyric()
  window.addEventListener('resize', handleCapsuleViewportResize)
  window.visualViewport?.addEventListener('resize', handleCapsuleViewportResize)

  await restoreCapsuleWindowPosition()
  scheduleCapsuleProtectionRegion('mount')

  const listenStartedAt = nowMs()
  try {
    ;[unlistenState, unlistenProgressAnchor, unlistenMeter, unlistenAppearance, unlistenPositionReset] = await Promise.all([
      listenForCapsuleState((state: LyricCapsuleStatePatch) => applyCapsuleState(state, 'state-event')),
      listenForCapsuleProgressAnchor(applyProgressAnchor),
      listenForCapsuleMeter(applyMeterFrame),
      listenForCapsuleAppearance((next) => applyAppearance(next, 'appearance-event')),
      listenForCapsulePositionReset(() => void resetCapsuleWindowPosition()),
    ])

    void logLyricCapsuleInfo('capsule_listeners_ready', {
      elapsedMs: elapsedMs(listenStartedAt),
      elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
      dataPath: 'capsule-direct-rust-events',
    })
  } catch (error) {
    void logLyricCapsuleError('capsule_listeners_failed', {
      elapsedMs: elapsedMs(listenStartedAt),
      elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
      error,
    })
    return
  }

  try {
    applyAppearance(await getLyricCapsuleAppearance(), 'boot-appearance')
  } catch (error) {
    void logLyricCapsuleWarn('capsule_appearance_boot_failed', { error })
  }

  const bootStartedAt = nowMs()
  try {
    const bootState = (await getLyricCapsuleBootState()) as LyricCapsuleSnapshot
    applyCapsuleState(bootState, 'boot-state')
    void logLyricCapsuleInfo('capsule_boot_state_complete', {
      elapsedMs: elapsedMs(bootStartedAt),
      elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
      seq: bootState.seq,
      hasTrack: bootState.hasTrack,
      artworkKey: bootState.artworkKey || null,
      artworkSrcLength: bootState.artworkSrc?.length ?? 0,
    })
  } catch (error) {
    void logLyricCapsuleError('capsule_boot_state_failed', {
      elapsedMs: elapsedMs(bootStartedAt),
      elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
      error,
    })
  }

  startProgressInterpolation()

  // The WinUI host remains hidden until boot state, its first images and the cover colour are ready.
  const nativeBridge = (window as unknown as { chrome?: { webview?: { postMessage: (message: unknown) => void } } }).chrome?.webview
  if (nativeBridge) {
    await nextTick()
    await Promise.race([
      Promise.all([
        ...Array.from(document.images, image => image.decode().catch(() => undefined)),
        artworkAccentPending,
      ]),
      new Promise(resolve => window.setTimeout(resolve, 1000)),
    ])
    nativeBridge.postMessage({ kind: 'capsule.ready' })
  }

  void logLyricCapsuleInfo('capsule_mount_complete', {
    elapsedSinceScriptMs: elapsedMs(scriptStartedAt),
    elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
  })
})

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', startProgressInterpolation)
  capsuleResizeObserver?.disconnect()
  window.removeEventListener('resize', handleCapsuleViewportResize)
  window.visualViewport?.removeEventListener('resize', handleCapsuleViewportResize)
  if (hitRegionFrameId !== null) cancelAnimationFrame(hitRegionFrameId)
  if (hitRegionTimerId !== null) {
    window.clearTimeout(hitRegionTimerId)
    hitRegionTimerId = null
  }

  if (positionSaveTimerId !== null) {
    window.clearTimeout(positionSaveTimerId)
    positionSaveTimerId = null
  }

  if (progressFrameId) {
    cancelAnimationFrame(progressFrameId)
    progressFrameId = 0
  }

  for (const unlisten of [unlistenState, unlistenProgressAnchor, unlistenMeter, unlistenAppearance, unlistenPositionReset]) {
    if (typeof unlisten === 'function') {
      unlisten()
    }
  }

  unlistenState = null
  unlistenProgressAnchor = null
  unlistenMeter = null
  unlistenAppearance = null
  unlistenPositionReset = null
  void releaseLyricCapsule().catch((error: unknown) => {
    void logLyricCapsuleWarn('capsule_release_failed', { error })
  })
})
</script>

<template>
  <main class="lyric-capsule-window" :class="{ 'is-playing': capsuleState.isPlaying }">
    <section
      ref="capsuleElement"
      class="lyric-capsule"
      :class="[
        `is-${palette.tone}`,
        {
          'is-expanded': controlsVisible,
          'is-mini': capsuleMini,
          'is-playing': capsuleState.isPlaying,
          'is-spinning': appearance.spin,
          'is-position-locked': capsulePositionLocked,
          'is-dragging': capsuleDragActive,
        },
      ]"
      :style="capsuleStyle"
      @pointerenter="setCapsuleExpanded(true, 'pointer-enter')"
      @pointerleave="setCapsuleExpanded(false, 'pointer-leave')"
      @pointermove="handleCapsulePointerMove"
      @pointerup="handleCapsulePointerRelease"
      @pointercancel="handleCapsulePointerRelease"
      @focusin="setCapsuleExpanded(true, 'focus-in')"
      @focusout="handleCapsuleFocusOut"
    >
      <span ref="lyricMeasureElement" class="lyric-capsule__line lyric-capsule__measure" aria-hidden="true">{{ rawLyricText }}</span>
      <div
        class="lyric-capsule__primary"
        @pointerdown="handleCapsulePointerDown"
      >
        <span class="lyric-capsule__disc" aria-hidden="true">
          <span class="lyric-capsule__ring" />
          <span class="lyric-capsule__record">
            <img
              v-if="displayedArtworkUrl"
              :src="displayedArtworkUrl"
              :alt="capsuleState.title"
              @load="handleArtworkLoad"
              @error="handleArtworkError"
            />
            <span v-else class="lyric-capsule__placeholder">&#xE8D6;</span>
            <span v-if="displayedArtworkUrl && appearance.spin" class="lyric-capsule__spindle" />
          </span>
        </span>

        <div class="lyric-capsule__copy" aria-live="polite">
          <span class="lyric-capsule__slot lyric-capsule__slot--line">
            <Transition name="capsule-lyric-flow">
              <p :key="lyricTransitionKey" class="lyric-capsule__line" :class="{ 'is-bg': timedCapsuleLine?.isBG }">
                <template v-if="timedCapsuleLine">
                  <span v-for="(word, wordIndex) in timedCapsuleLine.words" :key="wordIndex" class="lyric-capsule__word" :style="capsuleWordStyle(word)">{{ word.text }}</span>
                </template>
                <template v-else>{{ rawLyricText }}</template>
              </p>
            </Transition>
          </span>
          <span v-if="appearance.secondLine !== 'none'" class="lyric-capsule__slot lyric-capsule__slot--second">
            <Transition name="capsule-lyric-flow">
              <p :key="secondLineTransitionKey" class="lyric-capsule__second">{{ secondLineText }}</p>
            </Transition>
          </span>
        </div>

        <span v-if="appearance.wave === 'echo'" class="lyric-capsule__wave lyric-capsule__wave--echo" aria-hidden="true">
          <span ref="echoTrackElement" class="lyric-capsule__echo-track">
            <span
              v-for="(level, index) in echoHistory"
              :key="index"
              :style="echoBarStyle(level, index)"
            />
          </span>
        </span>
        <span v-else-if="appearance.wave === 'spectrum'" class="lyric-capsule__wave lyric-capsule__wave--spectrum" aria-hidden="true">
          <span
            v-for="(level, index) in spectrumBars"
            :key="index"
            :style="spectrumBarStyle(level)"
          />
        </span>
      </div>

      <div class="lyric-capsule__toolbar">
        <button
          class="lyric-capsule__icon"
          type="button"
          :aria-label="t('previous')"
          :title="t('previous')"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('previous', $event)"
        >&#xE892;</button>
        <button
          class="lyric-capsule__play"
          type="button"
          :aria-label="playbackControlLabel"
          :title="playbackControlLabel"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('toggle-playback', $event)"
        >{{ playbackGlyph }}</button>
        <button
          class="lyric-capsule__icon"
          type="button"
          :aria-label="t('next')"
          :title="t('next')"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('next', $event)"
        >&#xE893;</button>
        <span class="lyric-capsule__time lyric-capsule__time--position">{{ positionText }}</span>
        <span
          ref="progressSliderRef"
          class="lyric-capsule__seek"
          :class="{
            'is-disabled': progressSliderDisabled,
            'is-scrubbing': progressScrubbing,
          }"
          role="slider"
          :tabindex="progressSliderDisabled ? -1 : 0"
          :aria-label="t('progress')"
          aria-valuemin="0"
          :aria-valuemax="progressAriaValueMax"
          :aria-valuenow="progressAriaValueNow"
          :aria-valuetext="progressAriaText"
          :aria-disabled="progressSliderDisabled"
          @pointerdown.stop="beginProgressScrub"
          @pointermove.stop="handleProgressPointerMove"
          @pointerup.stop="commitProgressSeek"
          @pointercancel.stop="cancelProgressScrub"
          @keydown="handleProgressKeydown"
        >
          <span class="lyric-capsule__seek-track">
            <span :style="progressStyle" />
          </span>
          <span class="lyric-capsule__seek-thumb" aria-hidden="true" />
        </span>
        <span class="lyric-capsule__time lyric-capsule__time--duration">{{ durationText }}</span>
        <button
          class="lyric-capsule__icon lyric-capsule__lock"
          :class="{ 'is-locked': capsulePositionLocked }"
          type="button"
          :aria-label="capsulePositionLockLabel"
          :aria-pressed="capsulePositionLocked"
          :title="capsulePositionLockLabel"
          @mousedown.prevent
          @click.stop="toggleCapsulePositionLock($event)"
        >{{ capsulePositionLockGlyph }}</button>
        <button
          class="lyric-capsule__icon"
          type="button"
          :aria-label="t('resetPosition')"
          :title="t('resetPosition')"
          @mousedown.prevent
          @click.stop="handleCapsulePositionReset($event)"
        >{{ GLYPH_RESET }}</button>
        <button
          class="lyric-capsule__icon"
          type="button"
          :aria-label="t('capsuleSettings')"
          :title="t('capsuleSettings')"
          @mousedown.prevent
          @click.stop="openCapsuleSettings($event)"
        >&#xE713;</button>
        <button
          class="lyric-capsule__icon"
          type="button"
          :aria-label="t('closeCapsule')"
          :title="t('closeCapsule')"
          @mousedown.prevent
          @click.stop="closeLyricCapsule()"
        >&#xE711;</button>
      </div>
    </section>
  </main>
</template>

<style scoped>
:global(html[data-app-view='lyric-capsule']),
:global(html[data-app-view='lyric-capsule'] body),
:global(html[data-app-view='lyric-capsule'] #app) {
  width: 100%;
  height: 100%;
  margin: 0;
  overflow: hidden;
  background: transparent !important;
}

:global(html[data-app-view='lyric-capsule'] body) {
  font-family: var(--font-family);
  user-select: none;
}

@keyframes lyric-capsule-spin {
  to {
    transform: rotate(360deg);
  }
}

.lyric-capsule-window {
  box-sizing: border-box;
  display: grid;
  width: 100vw;
  height: 100vh;
  place-items: start center;
  padding: 4px;
  pointer-events: none;
  background: transparent;
}

.lyric-capsule {
  --capsule-ease: cubic-bezier(0.22, 1, 0.36, 1);
  --capsule-glyphs: 'Segoe Fluent Icons', 'Segoe MDL2 Assets';
  box-sizing: border-box;
  position: relative;
  display: flex;
  flex-direction: column;
  width: min(var(--capsule-width, 440px), calc(100vw - 8px));
  height: var(--capsule-height, 48px);
  overflow: hidden;
  border: 1px solid var(--cap-border);
  border-radius: var(--capsule-radius, 24px);
  background-color: var(--cap-bg);
  /* Stays inside the window's 4px top and 10px side margins, so no edge of it is cut. */
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.18);
  color: var(--cap-fg);
  font-family: 'Segoe UI Variable Text', 'Segoe UI', 'Microsoft YaHei UI', 'Microsoft YaHei', system-ui, sans-serif;
  isolation: isolate;
  transform: translateZ(0);
  transition:
    width 320ms var(--capsule-ease),
    height 280ms var(--capsule-ease),
    border-radius 280ms var(--capsule-ease),
    background-color 350ms ease,
    border-color 350ms ease,
    color 350ms ease;
  will-change: width, height;
  pointer-events: auto;
}

.lyric-capsule.is-dragging {
  cursor: grabbing;
}

/* --- First row: disc, lyric, waveform --- */

.lyric-capsule__primary {
  box-sizing: border-box;
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
  height: calc(var(--capsule-row, 48px) - 2px);
  padding: 0 14px 0 5px;
  min-width: 0;
  cursor: grab;
}

.lyric-capsule__primary:active {
  cursor: grabbing;
}

.lyric-capsule.is-position-locked .lyric-capsule__primary,
.lyric-capsule.is-position-locked .lyric-capsule__primary:active {
  cursor: default;
}

.lyric-capsule__disc {
  position: relative;
  flex: none;
  width: 36px;
  height: 36px;
}

.lyric-capsule__ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  background: conic-gradient(var(--cap-accent) var(--capsule-progress-percent, 0%), var(--cap-track) 0);
  -webkit-mask: radial-gradient(circle, transparent 16px, #000 16.5px);
  mask: radial-gradient(circle, transparent 16px, #000 16.5px);
}

.lyric-capsule__record {
  position: absolute;
  top: 3px;
  left: 3px;
  display: grid;
  width: 30px;
  height: 30px;
  place-items: center;
  overflow: hidden;
  border-radius: 50%;
  background: radial-gradient(circle at 35% 30%, var(--cap-accent-high), var(--cap-accent));
  contain: paint;
}

.lyric-capsule.is-spinning .lyric-capsule__record {
  animation: lyric-capsule-spin 18s linear infinite;
  animation-play-state: paused;
}

.lyric-capsule.is-spinning.is-playing .lyric-capsule__record {
  animation-play-state: running;
}

.lyric-capsule__record img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.lyric-capsule__placeholder {
  color: var(--cap-on-accent);
  font-family: var(--capsule-glyphs);
  font-size: 13px;
  line-height: 1;
}

.lyric-capsule__spindle {
  position: absolute;
  top: 13px;
  left: 13px;
  width: 4px;
  height: 4px;
  border-radius: 50%;
  background: var(--cap-solid);
  box-shadow: 0 0 0 1.5px rgba(255, 255, 255, 0.4);
}

.lyric-capsule__copy {
  flex: 1 1 auto;
  min-width: 0;
  text-align: var(--capsule-align, left);
  transition: opacity 200ms ease;
}

.lyric-capsule__slot {
  position: relative;
  display: block;
}

.lyric-capsule__line,
.lyric-capsule__second {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  letter-spacing: 0;
}

.lyric-capsule__line {
  font-size: var(--capsule-font, 15px);
  font-weight: 600;
  line-height: 1.3;
  will-change: opacity, transform, filter;
}

.lyric-capsule__word {
  white-space: pre-wrap;
  color: transparent;
  background: linear-gradient(90deg, var(--cap-fg) var(--capsule-word-progress), var(--cap-unsung) var(--capsule-word-progress));
  background-clip: text;
  -webkit-background-clip: text;
}

.lyric-capsule__measure {
  position: absolute;
  width: max-content;
  max-width: none;
  visibility: hidden;
  pointer-events: none;
  white-space: pre;
  overflow: visible;
  will-change: auto;
}

.lyric-capsule__slot--second {
  margin-top: 1px;
}

.lyric-capsule__second {
  color: var(--cap-fg2);
  font-size: var(--capsule-font-second, 11px);
  font-weight: 400;
  line-height: 1.3;
}

.lyric-capsule__wave {
  display: flex;
  flex: none;
  align-items: center;
  height: 24px;
  transition: opacity 200ms ease;
}

.lyric-capsule__wave--echo {
  position: relative;
  width: var(--capsule-wave-width, 66px);
  overflow: hidden;
  -webkit-mask-image: linear-gradient(90deg, transparent, #000 45%);
  mask-image: linear-gradient(90deg, transparent, #000 45%);
}

.lyric-capsule__echo-track {
  position: absolute;
  top: 0;
  right: 0;
  display: flex;
  align-items: center;
  height: 24px;
  gap: 2px;
  will-change: transform;
}

.lyric-capsule__echo-track span {
  flex: none;
  width: 3px;
  border-radius: 2px;
  background: var(--cap-accent);
}

.lyric-capsule__wave--spectrum {
  gap: 3px;
}

.lyric-capsule__wave--spectrum span {
  flex: none;
  width: 3px;
  height: 24px;
  border-radius: 2px;
  background: linear-gradient(180deg, var(--cap-accent-high), var(--cap-accent));
  transform-origin: center;
  will-change: transform;
  transition: transform 72ms cubic-bezier(0.2, 0.82, 0.25, 1);
}

/* --- Second row: playback controls --- */

.lyric-capsule__toolbar {
  box-sizing: border-box;
  display: flex;
  flex: none;
  align-items: center;
  gap: 4px;
  height: 40px;
  padding: 0 8px 4px 7px;
  min-width: 0;
  opacity: 0;
  transform: translate3d(0, -6px, 0);
  pointer-events: none;
  transition:
    opacity 200ms ease,
    transform 260ms var(--capsule-ease);
}

.lyric-capsule.is-expanded:not(.is-mini) .lyric-capsule__toolbar {
  opacity: 1;
  transform: translate3d(0, 0, 0);
  pointer-events: auto;
}

.lyric-capsule__icon,
.lyric-capsule__play {
  flex: none;
  display: grid;
  place-items: center;
  padding: 0;
  border: 0;
  border-radius: 50%;
  font-family: var(--capsule-glyphs);
  font-size: 14px;
  line-height: 1;
  cursor: pointer;
}

.lyric-capsule__icon {
  width: 28px;
  height: 28px;
  background-color: transparent;
  color: var(--cap-fg);
  transition: background-color 140ms ease, color 140ms ease;
}

.lyric-capsule__icon:hover:not(:disabled),
.lyric-capsule__icon:focus-visible {
  background-color: var(--cap-hover);
}

.lyric-capsule__lock.is-locked {
  background-color: var(--cap-accent-soft);
  color: var(--cap-accent);
}

.lyric-capsule__play {
  width: 32px;
  height: 32px;
  background: var(--cap-accent);
  color: var(--cap-on-accent);
  transition: transform 140ms ease, filter 140ms ease;
}

.lyric-capsule__play:hover:not(:disabled) {
  filter: brightness(1.08);
}

.lyric-capsule__play:active:not(:disabled) {
  transform: scale(0.94);
}

.lyric-capsule__icon:disabled,
.lyric-capsule__play:disabled {
  cursor: default;
  opacity: 0.4;
}

.lyric-capsule__icon:focus-visible,
.lyric-capsule__play:focus-visible,
.lyric-capsule__seek:focus-visible {
  outline: 2px solid var(--cap-accent);
  outline-offset: 1px;
}

.lyric-capsule__time {
  flex: none;
  color: var(--cap-fg2);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  line-height: 1;
}

.lyric-capsule__time--position {
  margin-left: 8px;
}

.lyric-capsule__time--duration {
  margin-right: 6px;
}

.lyric-capsule__seek {
  position: relative;
  display: grid;
  flex: 1 1 auto;
  align-items: center;
  min-width: 40px;
  height: 20px;
  margin: 0 4px;
  border-radius: 10px;
  cursor: pointer;
  outline: none;
}

.lyric-capsule__seek.is-disabled {
  cursor: default;
  opacity: 0.44;
}

.lyric-capsule__seek-track {
  display: block;
  height: 4px;
  overflow: hidden;
  border-radius: 2px;
  background: var(--cap-track);
}

.lyric-capsule__seek-track span {
  display: block;
  width: 100%;
  height: 100%;
  border-radius: inherit;
  background: var(--cap-accent);
  transform-origin: left center;
  will-change: transform;
}

.lyric-capsule__seek-thumb {
  position: absolute;
  top: 50%;
  left: var(--capsule-progress-percent, 0%);
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: var(--cap-accent);
  box-shadow: 0 0 0 2px var(--cap-solid);
  opacity: 0;
  transform: translate3d(-50%, -50%, 0) scale(0.7);
  transition:
    opacity 140ms ease,
    transform 180ms var(--capsule-ease);
  pointer-events: none;
}

.lyric-capsule__seek:hover:not(.is-disabled) .lyric-capsule__seek-thumb,
.lyric-capsule__seek:focus-visible .lyric-capsule__seek-thumb,
.lyric-capsule__seek.is-scrubbing .lyric-capsule__seek-thumb {
  opacity: 1;
  transform: translate3d(-50%, -50%, 0) scale(1);
}

/* --- Paused mini form: only the disc --- */

.lyric-capsule.is-mini .lyric-capsule__primary {
  height: 46px;
}

.lyric-capsule.is-mini .lyric-capsule__copy,
.lyric-capsule.is-mini .lyric-capsule__wave {
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .lyric-capsule,
  .lyric-capsule__copy,
  .lyric-capsule__wave,
  .lyric-capsule__toolbar,
  .lyric-capsule__icon,
  .lyric-capsule__play,
  .lyric-capsule__seek-thumb,
  .lyric-capsule__wave--spectrum span {
    transition: none;
  }

  .lyric-capsule.is-spinning .lyric-capsule__record {
    animation: none;
  }

  .capsule-lyric-flow-enter-active,
  .capsule-lyric-flow-leave-active {
    transition: none;
  }
}
</style>
