<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CSSProperties } from 'vue'
import { LogicalPosition } from '@tauri-apps/api/dpi'
import { getCurrentWindow, primaryMonitor } from '@tauri-apps/api/window'
import { Lock, RotateCcw, Unlock } from 'lucide-vue-next'
import {
  applyLyricCapsuleHitRegion,
  createLyricCapsuleSnapshot,
  getLyricCapsuleBootState,
  listenForCapsuleMeter,
  listenForCapsuleProgressAnchor,
  listenForCapsuleState,
  releaseLyricCapsule,
  requestLyricCapsuleControl,
} from '../services/lyricCapsuleBridge'
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
  startMs: number
  endMs: number | null
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

type LyricCapsuleProgressAnchor = {
  seq: number
  trackId: string | null
  isPlaying: boolean
  durationMs: number
  positionMs: number
  sentAtMs: number
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
const CAPSULE_MIN_WIDTH = 320
const CAPSULE_MAX_WIDTH = 540
const CAPSULE_LYRIC_MAX_UNITS = 25
const CAPSULE_META_MAX_UNITS = 44
const CAPSULE_MAX_VISIBLE_ARTISTS = 2
const CAPSULE_LYRIC_RENDER_LEAD_MS = 90
const scriptStartedAt = nowMs()
const capsuleState = ref<LyricCapsuleSnapshot>(createLyricCapsuleSnapshot() as LyricCapsuleSnapshot)
const progressRatio = ref<number>(capsuleState.value.progress)
const displayedLyricText = ref<string>(capsuleState.value.lyricText)
const displayedLyricIndex = ref<number | null>(capsuleState.value.lyricIndex)
const meterLevels = ref<number[]>([...EMPTY_AUDIO_LEVELS])
const displayedArtworkUrl = ref<string>('')
const capsuleExpanded = ref<boolean>(false)
const capsulePositionLocked = ref<boolean>(readStoredCapsulePositionLocked())
const capsuleDragActive = ref<boolean>(false)
let unlistenState: UnlistenFn | null = null
let unlistenProgressAnchor: UnlistenFn | null = null
let unlistenMeter: UnlistenFn | null = null
let mountedAt: number | null = null
let progressFrameId: AnimationFrameHandle = null
let artworkLoadStartedAt: number | null = null
let lastArtworkUrl = ''
let artworkPreloadToken = 0
let controlRequestLocked = false
let hitRegionTimerId: TimerHandle = null
let positionSaveTimerId: TimerHandle = null
let manualDragState: ManualCapsuleDragState | null = null

void logLyricCapsuleInfo('capsule_script_loaded', {
  scriptStartedAtMs: Math.round(scriptStartedAt),
})

const statusText = computed(() => {
  if (capsuleState.value.isLoading) {
    return 'SYNC'
  }

  if (!capsuleState.value.hasTrack) {
    return 'READY'
  }

  return capsuleState.value.isPlaying ? 'LIVE' : 'PAUSED'
})

const progressStyle = computed(() => ({
  transform: `scaleX(${Math.max(0.04, progressRatio.value || 0.04)})`,
}))

const rawLyricText = computed(() => displayedLyricText.value || capsuleState.value.lyricText || capsuleState.value.title || 'Music is ready')
const lyricText = computed(() => truncateTextByUnits(rawLyricText.value, CAPSULE_LYRIC_MAX_UNITS))

const songInfoText = computed(() => {
  const title = capsuleState.value.title || ''
  const artist = compactArtistList(capsuleState.value.artist || '')

  if (artist && title) {
    return truncateTextByUnits(`${artist} / ${title}`, CAPSULE_META_MAX_UNITS)
  }

  return truncateTextByUnits(capsuleState.value.metaText || artist || title || 'OFPlayer', CAPSULE_META_MAX_UNITS)
})

const lyricTransitionKey = computed(() => {
  return `${capsuleState.value.trackId || '_'}:${capsuleState.value.lyricVersion}:${displayedLyricIndex.value ?? '_'}:${rawLyricText.value}`
})

const playbackControlLabel = computed(() => (capsuleState.value.isPlaying ? 'Pause' : 'Play'))
const capsulePositionLockLabel = computed(() =>
  capsulePositionLocked.value ? 'Unlock capsule position' : 'Lock capsule position',
)
const capsulePositionLockIcon = computed(() => (capsulePositionLocked.value ? Lock : Unlock))
const capsulePositionResetLabel = 'Restore default capsule position'

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
    const maxX = monitorX + Math.max(0, monitorWidth - LYRIC_CAPSULE_WIDTH)
    const maxY = monitorY + Math.max(0, monitorHeight - LYRIC_CAPSULE_HEIGHT)

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
      x: Math.round(monitorX + Math.max(0, (monitorWidth - LYRIC_CAPSULE_WIDTH) / 2)),
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

async function resetCapsuleWindowPosition(event: MouseEvent | null = null): Promise<void> {
  const currentTarget = event?.currentTarget as HTMLElement | null
  currentTarget?.blur()
  setCapsuleExpanded(true, 'position-reset')

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

function estimateTextUnits(text: string): number {
  return Array.from(text || '').reduce((total, char) => {
    if (/\s/.test(char)) return total + 0.34
    if (/[\u3000-\u9fff\uac00-\ud7af\u3040-\u30ff]/.test(char)) return total + 1.04
    if (/[A-Z0-9]/.test(char)) return total + 0.68
    if (/[il.,:;'|!]/.test(char)) return total + 0.36
    return total + 0.58
  }, 0)
}

function truncateTextByUnits(text: string, maxUnits: number): string {
  const normalized = String(text || '').replace(/\s+/g, ' ').trim()
  if (!normalized || estimateTextUnits(normalized) <= maxUnits) {
    return normalized
  }

  const ellipsis = '...'
  const ellipsisUnits = estimateTextUnits(ellipsis)
  let usedUnits = 0
  let output = ''

  for (const char of normalized) {
    const charUnits = estimateTextUnits(char)
    if (usedUnits + charUnits + ellipsisUnits > maxUnits) {
      break
    }
    output += char
    usedUnits += charUnits
  }

  return `${output.trimEnd()}${ellipsis}`
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

const capsuleWidth = computed(() => {
  const lyricWidth = estimateTextUnits(lyricText.value) * 15.2
  const infoWidth = estimateTextUnits(songInfoText.value) * 5.8
  const desiredWidth = Math.round(158 + Math.max(lyricWidth, infoWidth))
  return Math.max(CAPSULE_MIN_WIDTH, Math.min(CAPSULE_MAX_WIDTH, desiredWidth))
})

const capsuleStyle = computed(() => {
  return {
    '--capsule-width': `${capsuleWidth.value}px`,
  }
})

const audioBars = computed<number[]>(() => {
  return Array.from({ length: 8 }, (_, index) => {
    const levels = Array.isArray(meterLevels.value) ? meterLevels.value : [...EMPTY_AUDIO_LEVELS]
    const level = Number(levels[index])
    return Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0
  })
})

function audioBarStyle(level: number): CSSProperties {
  const easedLevel = Math.pow(level, 0.54)
  const scale = 0.14 + easedLevel * 1.86

  return {
    opacity: 0.3 + easedLevel * 0.68,
    transform: `scaleY(${scale.toFixed(3)})`,
  }
}

function fallbackLyricText(): string {
  return capsuleState.value.lyricText || capsuleState.value.title || 'Music is ready'
}

function findTimelineLyric(positionMs: number | null | undefined): LyricCapsuleTimelineLine | null {
  const timeline = Array.isArray(capsuleState.value.lyricTimeline) ? capsuleState.value.lyricTimeline : []
  const safePositionMs = Number(positionMs)

  if (!Number.isFinite(safePositionMs) || timeline.length === 0) {
    return null
  }

  const effectivePositionMs = Math.max(0, safePositionMs + CAPSULE_LYRIC_RENDER_LEAD_MS)
  let activeLine = null

  for (const line of timeline) {
    if (!line || !Number.isFinite(line.startMs)) {
      continue
    }

    if (effectivePositionMs < line.startMs) {
      break
    }

    activeLine = line
  }

  return activeLine
}

function syncDisplayedLyric(positionMs: number, force = false): void {
  const timelineLine = findTimelineLyric(positionMs)
  const nextText = timelineLine?.text || fallbackLyricText()
  const nextIndex = timelineLine?.index ?? capsuleState.value.lyricIndex ?? null

  if (!nextText) {
    return
  }

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
  }

  const tick = () => {
    updateInterpolatedProgress()
    progressFrameId = requestAnimationFrame(tick)
  }

  progressFrameId = requestAnimationFrame(tick)
}

function displayArtworkWhenReady(nextArtworkUrl: string): void {
  if (!nextArtworkUrl || nextArtworkUrl === displayedArtworkUrl.value) {
    return
  }

  const token = ++artworkPreloadToken

  if (!displayedArtworkUrl.value) {
    displayedArtworkUrl.value = nextArtworkUrl
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
  syncDisplayedLyric(mergedState.positionMs, true)
  updateInterpolatedProgress()
}

function applyProgressAnchor(anchor: LyricCapsuleStatePatch): void {
  capsuleState.value = createLyricCapsuleSnapshot({
    ...capsuleState.value,
    ...anchor,
    audioLevels: meterLevels.value,
  } as LyricCapsuleStatePatch) as LyricCapsuleSnapshot
  syncDisplayedLyric(capsuleState.value.positionMs, true)
  updateInterpolatedProgress()
}

function applyMeterFrame(frame: LyricCapsuleMeterFrame): void {
  const currentTrackId = capsuleState.value.trackId

  if (frame.trackId && currentTrackId && frame.trackId !== currentTrackId) {
    return
  }

  meterLevels.value = frame.isPlaying ? frame.levels : [...EMPTY_AUDIO_LEVELS]
}

async function applyCapsuleProtectionRegion(reason: string): Promise<void> {
  try {
    await applyLyricCapsuleHitRegion({
      capsuleWidth: capsuleWidth.value,
      expanded: capsuleExpanded.value,
    })
  } catch (error) {
    void logLyricCapsuleWarn('capsule_hit_region_update_failed', {
      reason,
      capsuleWidth: capsuleWidth.value,
      expanded: capsuleExpanded.value,
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
    void applyCapsuleProtectionRegion(reason)
    return
  }

  hitRegionTimerId = window.setTimeout(() => {
    hitRegionTimerId = null
    void applyCapsuleProtectionRegion(reason)
  }, delayMs)
}

function setCapsuleExpanded(expanded: boolean, reason: string): void {
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
  void logLyricCapsuleWarn('capsule_artwork_load_failed', {
    elapsedSinceArtworkSetMs: artworkLoadStartedAt ? elapsedMs(artworkLoadStartedAt) : null,
    elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
    artworkLength: lastArtworkUrl.length,
    errorType: event.type,
  })

  artworkLoadStartedAt = null
}

watch(capsuleWidth, () => {
  scheduleCapsuleProtectionRegion('width-change')
})

onMounted(async () => {
  mountedAt = nowMs()
  void logLyricCapsuleInfo('capsule_mount_start', {
    elapsedSinceScriptMs: elapsedMs(scriptStartedAt),
  })

  document.documentElement.dataset.appView = 'lyric-capsule'

  await restoreCapsuleWindowPosition()
  scheduleCapsuleProtectionRegion('mount')

  const listenStartedAt = nowMs()
  try {
    ;[unlistenState, unlistenProgressAnchor, unlistenMeter] = await Promise.all([
      listenForCapsuleState((state: LyricCapsuleStatePatch) => applyCapsuleState(state, 'state-event')),
      listenForCapsuleProgressAnchor(applyProgressAnchor),
      listenForCapsuleMeter(applyMeterFrame),
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

  void logLyricCapsuleInfo('capsule_mount_complete', {
    elapsedSinceScriptMs: elapsedMs(scriptStartedAt),
    elapsedSinceMountMs: mountedAt ? elapsedMs(mountedAt) : null,
  })
})

onBeforeUnmount(() => {
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

  for (const unlisten of [unlistenState, unlistenProgressAnchor, unlistenMeter]) {
    if (typeof unlisten === 'function') {
      unlisten()
    }
  }

  unlistenState = null
  unlistenProgressAnchor = null
  unlistenMeter = null
  void releaseLyricCapsule().catch((error: unknown) => {
    void logLyricCapsuleWarn('capsule_release_failed', { error })
  })
})
</script>

<template>
  <main class="lyric-capsule-window" :class="{ 'is-playing': capsuleState.isPlaying }">
    <section
      class="lyric-capsule"
      :class="{
        'is-expanded': capsuleExpanded,
        'is-position-locked': capsulePositionLocked,
        'is-dragging': capsuleDragActive,
      }"
      :style="capsuleStyle"
      aria-live="polite"
      @pointerenter="setCapsuleExpanded(true, 'pointer-enter')"
      @pointerleave="setCapsuleExpanded(false, 'pointer-leave')"
      @pointermove="handleCapsulePointerMove"
      @pointerup="handleCapsulePointerRelease"
      @pointercancel="handleCapsulePointerRelease"
      @focusin="setCapsuleExpanded(true, 'focus-in')"
      @focusout="handleCapsuleFocusOut"
    >
      <div
        class="lyric-capsule__primary"
        @pointerdown="handleCapsulePointerDown"
      >
        <span class="lyric-capsule__artwork" aria-hidden="true">
          <img
            v-if="displayedArtworkUrl"
            :src="displayedArtworkUrl"
            :alt="capsuleState.title"
            @load="handleArtworkLoad"
            @error="handleArtworkError"
          />
          <span v-else class="lyric-capsule__sig">
            <span />
            <span />
            <span />
          </span>
        </span>

        <div class="lyric-capsule__copy">
          <Transition name="capsule-lyric-flow">
            <p :key="lyricTransitionKey" class="lyric-capsule__line">{{ lyricText }}</p>
          </Transition>
          <p class="lyric-capsule__meta">{{ songInfoText }}</p>
        </div>

        <div class="lyric-capsule__status" aria-hidden="true">
          <span class="lyric-capsule__label">{{ statusText }}</span>
          <span class="lyric-capsule__bars">
            <span
              v-for="(level, index) in audioBars"
              :key="index"
              :style="audioBarStyle(level)"
            />
          </span>
          <span class="lyric-capsule__progress">
            <span :style="progressStyle" />
          </span>
        </div>
      </div>

      <div class="lyric-capsule__controls" aria-hidden="false">
        <button
          class="lyric-capsule__control lyric-capsule__control--side"
          type="button"
          aria-label="Previous track"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('previous', $event)"
        >
          <span class="lyric-capsule__glyph lyric-capsule__glyph--previous" aria-hidden="true" />
        </button>
        <button
          class="lyric-capsule__control lyric-capsule__control--toggle"
          :class="capsuleState.isPlaying ? 'is-playing' : 'is-paused'"
          type="button"
          :aria-label="playbackControlLabel"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('toggle-playback', $event)"
        >
          <span
            class="lyric-capsule__glyph"
            :class="capsuleState.isPlaying ? 'lyric-capsule__glyph--pause' : 'lyric-capsule__glyph--play'"
            aria-hidden="true"
          />
        </button>
        <button
          class="lyric-capsule__control lyric-capsule__control--side"
          type="button"
          aria-label="Next track"
          :disabled="!capsuleState.hasTrack"
          @mousedown.prevent
          @click.stop="handleCapsuleControl('next', $event)"
        >
          <span class="lyric-capsule__glyph lyric-capsule__glyph--next" aria-hidden="true" />
        </button>
      </div>

      <button
        class="lyric-capsule__reset"
        type="button"
        :aria-label="capsulePositionResetLabel"
        :title="capsulePositionResetLabel"
        @mousedown.prevent
        @click.stop="resetCapsuleWindowPosition($event)"
      >
        <RotateCcw aria-hidden="true" />
      </button>

      <button
        class="lyric-capsule__lock"
        :class="{ 'is-locked': capsulePositionLocked }"
        type="button"
        :aria-label="capsulePositionLockLabel"
        :aria-pressed="capsulePositionLocked"
        :title="capsulePositionLockLabel"
        @mousedown.prevent
        @click.stop="toggleCapsulePositionLock($event)"
      >
        <component :is="capsulePositionLockIcon" aria-hidden="true" />
      </button>
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
  font-family:
    Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  user-select: none;
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
  box-sizing: border-box;
  position: relative;
  display: grid;
  grid-template-rows: 34px 24px;
  align-content: start;
  width: min(var(--capsule-width, 500px), calc(100vw - 8px));
  height: 74px;
  gap: 5px;
  padding: 5px 10px 5px 11px;
  overflow: visible;
  border-radius: 24px;
  background: transparent;
  box-shadow: none;
  color: #f7f7f3;
  isolation: isolate;
  transform: translateZ(0);
  transition:
    width 260ms cubic-bezier(0.22, 1, 0.36, 1);
  will-change: width;
  pointer-events: auto;
}

.lyric-capsule.is-dragging {
  cursor: grabbing;
}

.lyric-capsule::before,
.lyric-capsule::after {
  position: absolute;
  inset: 0 0 auto;
  height: 44px;
  border-radius: 22px;
  content: '';
  pointer-events: none;
  transition:
    height 260ms cubic-bezier(0.22, 1, 0.36, 1),
    border-radius 260ms cubic-bezier(0.22, 1, 0.36, 1);
}

.lyric-capsule::before {
  z-index: 0;
  background: linear-gradient(180deg, #111214 0%, #0c0d0f 100%);
}

.lyric-capsule::after {
  z-index: 1;
  box-shadow:
    inset 0 0 0 1px rgba(255, 255, 255, 0.06),
    inset 0 -1px 0 rgba(255, 255, 255, 0.03);
}

.lyric-capsule:hover::before,
.lyric-capsule:hover::after,
.lyric-capsule.is-expanded::before,
.lyric-capsule.is-expanded::after {
  height: 74px;
  border-radius: 24px;
}

.lyric-capsule__primary {
  display: grid;
  grid-template-columns: 28px minmax(118px, 1fr) 76px;
  align-items: center;
  min-width: 0;
  gap: 10px;
  position: relative;
  z-index: 2;
  cursor: grab;
}

.lyric-capsule__primary:active {
  cursor: grabbing;
}

.lyric-capsule.is-position-locked .lyric-capsule__primary,
.lyric-capsule.is-position-locked .lyric-capsule__primary:active {
  cursor: default;
}

.lyric-capsule__artwork {
  display: grid;
  width: 28px;
  height: 28px;
  place-items: center;
  overflow: hidden;
  border-radius: 10px;
  background: rgba(255, 105, 105, 0.12);
  contain: paint;
}

.lyric-capsule__artwork img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
  transform: translateZ(0);
}

.lyric-capsule__sig {
  display: flex;
  align-items: flex-end;
  justify-content: center;
  width: 15px;
  height: 18px;
  gap: 2px;
}

.lyric-capsule__sig span {
  width: 3px;
  border-radius: 999px;
  background: #ff6969;
  box-shadow: 0 0 8px rgba(255, 105, 105, 0.38);
}

.lyric-capsule__sig span:nth-child(1) {
  height: 8px;
}

.lyric-capsule__sig span:nth-child(2) {
  height: 15px;
}

.lyric-capsule__sig span:nth-child(3) {
  height: 11px;
}

.lyric-capsule__copy {
  display: grid;
  grid-template-rows: 18px 13px;
  align-content: center;
  position: relative;
  min-width: 0;
  min-height: 0;
  overflow: visible;
}

.lyric-capsule__line,
.lyric-capsule__meta,
.lyric-capsule__label {
  min-width: 0;
  overflow: hidden;
  margin: 0;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.lyric-capsule__line {
  align-self: end;
  grid-row: 1;
  font-size: 14px;
  font-weight: 780;
  line-height: 1.18;
  letter-spacing: 0;
  transform-origin: left center;
  will-change: opacity, transform, filter;
}

.lyric-capsule__meta {
  grid-row: 2;
  margin-top: 1px;
  color: rgba(232, 234, 238, 0.58);
  font-size: 9px;
  font-weight: 650;
  line-height: 1.2;
  letter-spacing: 0;
  transform: translateZ(0);
}

.capsule-lyric-flow-enter-active {
  transition:
    opacity 155ms cubic-bezier(0.22, 1, 0.36, 1),
    transform 175ms cubic-bezier(0.22, 1, 0.36, 1),
    filter 175ms cubic-bezier(0.22, 1, 0.36, 1);
}

.capsule-lyric-flow-leave-active {
  position: absolute;
  top: 0;
  right: 0;
  left: 0;
  transition:
    opacity 100ms cubic-bezier(0.4, 0, 1, 1),
    transform 115ms cubic-bezier(0.4, 0, 1, 1),
    filter 115ms cubic-bezier(0.4, 0, 1, 1);
}

.capsule-lyric-flow-enter-from {
  opacity: 0;
  filter: blur(2px);
  transform: translate3d(0, 0.36em, 0);
}

.capsule-lyric-flow-leave-to {
  opacity: 0;
  filter: blur(1.5px);
  transform: translate3d(0, -0.28em, 0);
}

.capsule-lyric-flow-enter-to,
.capsule-lyric-flow-leave-from {
  opacity: 1;
  filter: blur(0);
  transform: translate3d(0, 0, 0);
}

.lyric-capsule__status {
  display: grid;
  align-items: center;
  justify-items: end;
  min-width: 0;
  gap: 2px;
}

.lyric-capsule__controls {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 0;
  gap: 8px;
  position: relative;
  z-index: 2;
  opacity: 0;
  transform: translate3d(0, -5px, 0);
  pointer-events: none;
  transition:
    opacity 180ms ease,
    transform 220ms cubic-bezier(0.22, 1, 0.36, 1);
}

.lyric-capsule:hover .lyric-capsule__controls,
.lyric-capsule.is-expanded .lyric-capsule__controls {
  opacity: 1;
  transform: translate3d(0, 0, 0);
  pointer-events: auto;
}

.lyric-capsule__control {
  display: inline-grid;
  place-items: center;
  width: 30px;
  height: 22px;
  padding: 0;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.065);
  color: rgba(245, 246, 250, 0.82);
  cursor: pointer;
  box-shadow:
    inset 0 1px 0 rgba(255, 255, 255, 0.06),
    inset 0 -1px 0 rgba(0, 0, 0, 0.18);
  transition:
    background 140ms ease,
    color 140ms ease,
    border-color 140ms ease,
    box-shadow 140ms ease;
}

.lyric-capsule__control--toggle {
  width: 34px;
  background: rgba(255, 105, 105, 0.18);
  border-color: rgba(255, 105, 105, 0.24);
  color: #fff;
}

.lyric-capsule__control:hover:not(:disabled) {
  border-color: rgba(255, 105, 105, 0.34);
  background: rgba(255, 105, 105, 0.18);
  color: #fff;
}

.lyric-capsule__control:active:not(:disabled) {
  background: rgba(255, 105, 105, 0.24);
  box-shadow:
    inset 0 1px 2px rgba(0, 0, 0, 0.22),
    inset 0 -1px 0 rgba(255, 255, 255, 0.04);
}

.lyric-capsule__control:disabled {
  cursor: default;
  opacity: 0.42;
}

.lyric-capsule__reset,
.lyric-capsule__lock {
  position: absolute;
  bottom: 5px;
  z-index: 3;
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.055);
  color: rgba(245, 246, 250, 0.66);
  cursor: pointer;
  opacity: 0;
  transform: translate3d(0, -5px, 0) scale(0.96);
  pointer-events: none;
  box-shadow:
    inset 0 1px 0 rgba(255, 255, 255, 0.06),
    inset 0 -1px 0 rgba(0, 0, 0, 0.18);
  transition:
    opacity 180ms ease,
    transform 220ms cubic-bezier(0.22, 1, 0.36, 1),
    border-color 140ms ease,
    background 140ms ease,
    color 140ms ease;
}

.lyric-capsule__reset {
  left: 10px;
}

.lyric-capsule__lock {
  right: 10px;
}

.lyric-capsule:hover .lyric-capsule__reset,
.lyric-capsule.is-expanded .lyric-capsule__reset,
.lyric-capsule__reset:focus-visible,
.lyric-capsule:hover .lyric-capsule__lock,
.lyric-capsule.is-expanded .lyric-capsule__lock,
.lyric-capsule__lock:focus-visible {
  opacity: 1;
  transform: translate3d(0, 0, 0) scale(1);
  pointer-events: auto;
}

.lyric-capsule__reset:hover,
.lyric-capsule__lock:hover {
  border-color: rgba(255, 105, 105, 0.34);
  background: rgba(255, 105, 105, 0.16);
  color: #fff;
}

.lyric-capsule__lock.is-locked {
  border-color: rgba(255, 105, 105, 0.38);
  background: rgba(255, 105, 105, 0.18);
  color: #fff;
}

.lyric-capsule__reset svg,
.lyric-capsule__lock svg {
  width: 12px;
  height: 12px;
  stroke-width: 2.35;
}

.lyric-capsule__glyph {
  position: relative;
  display: block;
  width: 14px;
  height: 14px;
  color: currentColor;
}

.lyric-capsule__glyph--previous::before,
.lyric-capsule__glyph--previous::after,
.lyric-capsule__glyph--next::before,
.lyric-capsule__glyph--next::after,
.lyric-capsule__glyph--play::before,
.lyric-capsule__glyph--pause::before,
.lyric-capsule__glyph--pause::after {
  position: absolute;
  top: 50%;
  content: '';
  transform: translateY(-50%);
}

.lyric-capsule__glyph--previous::before,
.lyric-capsule__glyph--next::before {
  width: 2px;
  height: 11px;
  border-radius: 999px;
  background: currentColor;
}

.lyric-capsule__glyph--previous::before {
  left: 1px;
}

.lyric-capsule__glyph--next::before {
  right: 1px;
}

.lyric-capsule__glyph--previous::after,
.lyric-capsule__glyph--next::after {
  width: 9px;
  height: 10px;
  background: currentColor;
}

.lyric-capsule__glyph--previous::after {
  left: 4px;
  clip-path: polygon(100% 0, 0 50%, 100% 100%);
}

.lyric-capsule__glyph--next::after {
  right: 4px;
  clip-path: polygon(0 0, 100% 50%, 0 100%);
}

.lyric-capsule__glyph--play::before {
  left: 4px;
  width: 9px;
  height: 11px;
  background: currentColor;
  clip-path: polygon(0 0, 100% 50%, 0 100%);
}

.lyric-capsule__glyph--pause::before,
.lyric-capsule__glyph--pause::after {
  width: 3px;
  height: 11px;
  border-radius: 999px;
  background: currentColor;
}

.lyric-capsule__glyph--pause::before {
  left: 3px;
}

.lyric-capsule__glyph--pause::after {
  right: 3px;
}

.lyric-capsule__label {
  max-width: 100%;
  color: rgba(232, 234, 238, 0.72);
  font-size: 8px;
  font-weight: 800;
  line-height: 1.1;
}

.lyric-capsule__bars {
  display: inline-flex;
  align-items: flex-end;
  height: 15px;
  gap: 3px;
}

.lyric-capsule__bars span {
  width: 4px;
  height: 7px;
  border-radius: 999px;
  background: rgba(255, 105, 105, 0.72);
  transform-origin: bottom;
  will-change: transform, opacity;
  transition:
    opacity 72ms ease,
    transform 72ms cubic-bezier(0.2, 0.82, 0.25, 1);
}

.lyric-capsule__progress {
  display: block;
  width: 54px;
  height: 2px;
  overflow: hidden;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.16);
}

.lyric-capsule__progress span {
  display: block;
  width: 100%;
  height: 100%;
  border-radius: inherit;
  background: #ff6969;
  transform-origin: left center;
  will-change: transform;
}

@media (prefers-reduced-motion: reduce) {
  .lyric-capsule,
  .lyric-capsule::before,
  .lyric-capsule::after,
  .lyric-capsule__controls,
  .lyric-capsule__control,
  .lyric-capsule__reset,
  .lyric-capsule__lock,
  .lyric-capsule__bars span {
    transition: none;
  }

  .capsule-lyric-flow-enter-active,
  .capsule-lyric-flow-leave-active {
    transition: none;
  }
}
</style>
