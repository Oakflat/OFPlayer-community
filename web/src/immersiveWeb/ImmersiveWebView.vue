<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Pin, PinOff, Maximize2, Minimize2, ChevronDown, SkipBack, SkipForward, Play, Pause } from 'lucide-vue-next'
import LyricsPlayerView from '../components/LyricsPlayerView.vue'
import {
  createLyricPlayerLines,
  toLyricPlayerTimeMs,
  type LyricPlayerLineModel,
} from '../models/lyrics'
import { hostRequest, initHostBridge, notifyHostReady, onHostEvent } from './hostBridge'
import { createImmersiveFrameLoop } from './frameLoop'
import { readPlaybackTrackUpdate } from './playbackMetadata'
import { createArtworkBackdropController } from './artworkBackdrop'
import { createBackdrop } from './backdrop'
import { BACKDROP_SIDE, renderBackdropField } from './backdropField'
import { createBackdropRenderer } from './backdropRenderer'
import { useWebLocale } from '../services/useWebLocale'
import type { WebTextKey } from '../services/webLocale'

type TrackDetail = {
  id: string
  displayTitle?: string | null
  title?: string | null
  artist?: string | null
  album?: string | null
  artwork?: string | null
}

type PlaybackPayload = {
  status?: string
  activeTrackId?: string | null
  positionSeconds?: number
  durationSeconds?: number
  currentTime?: number
  duration?: number
  volume?: number
}

const LOST_TRACK_POLL_MS = 1500
// Old front-end constants (ImmersivePlayerView.vue).
const FOOTER_AUTO_HIDE_DELAY_MS = 5000
const FOOTER_ACTIVITY_THROTTLE_MS = 250

const bridgeReady = ref(initHostBridge())
const { t } = useWebLocale('immersiveTitle')
const statusKey = ref<WebTextKey | null>('connecting')
const statusError = ref('')
const statusText = computed(() => statusKey.value ? t(statusKey.value, { error: statusError.value }) : '')
function setStatus(key: WebTextKey | null, error?: unknown) {
  statusKey.value = key
  statusError.value = error instanceof Error ? error.message : ''
}
const title = ref('')
const artist = ref('')
const artwork = ref<string | null>(null)
const lyricLines = ref<LyricPlayerLineModel[]>([])
const activeTrackId = ref<string | null>(null)
// R4-3: monotonic token for lyric lookups. A trackId check alone cannot discard a
// stale response when playback returns to the same track (A -> B -> A): the late A
// response would match the current trackId yet describe an older position/edit.
let lyricsRequestToken = 0
// Last (trackId, version) applied, so a same-track lyrics.changed broadcast is not
// re-fetched when nothing actually changed.
let appliedLyricsKey = ''
const playing = ref(false)
// The native shell keeps this WebView warm at opacity zero. DOM visibility alone
// cannot tell us whether the immersive surface is actually being presented.
const surfaceVisible = ref(false)
let trackNeedsRefresh = true
let trackRequestToken = 0
let trackMetadataRevision = 0
let snapshotRequestToken = 0
let playbackRevision = 0
const duration = ref(0)
const volumePercent = ref(80)
const eventPosition = ref(0)
const eventAt = ref(performance.now())
const displayPosition = ref(0)
const artworkFailed = ref(false)

// The ground behind everything: the cover's colours, flowing while the music plays.
const backdropCanvas = ref<HTMLCanvasElement | null>(null)
let ground: ReturnType<typeof createBackdrop> | null = null
let groundRenderer: ReturnType<typeof createBackdropRenderer> | null = null
let groundObserver: ResizeObserver | null = null

/** The cover's middle square, reduced and worked into the ground it gives. */
async function loadBackdropField(source: string): Promise<Uint8ClampedArray> {
  const image = new Image()
  image.src = source
  await image.decode()
  const crop = Math.min(image.naturalWidth, image.naturalHeight)
  const square = document.createElement('canvas')
  square.width = square.height = BACKDROP_SIDE
  const context = square.getContext('2d', { willReadFrequently: true })
  if (!context || crop <= 0) throw new Error('Artwork could not be read')
  context.imageSmoothingQuality = 'high'
  context.drawImage(
    image,
    (image.naturalWidth - crop) / 2, (image.naturalHeight - crop) / 2, crop, crop,
    0, 0, BACKDROP_SIDE, BACKDROP_SIDE,
  )
  return renderBackdropField(context.getImageData(0, 0, BACKDROP_SIDE, BACKDROP_SIDE).data)
}

function motionReduced() {
  return reducedMotionPreference.value || window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

const backdrop = createArtworkBackdropController(loadBackdropField, (field) => {
  // Mixed in where it can be seen; simply there when hidden or when motion is reduced.
  const animate = surfaceVisible.value && !motionReduced()
  ground?.show(field, animate)
  if (animate) frameLoop.wake()
})

function setArtwork(source: string | null) {
  artwork.value = source
  artworkFailed.value = false
  backdrop.update(source)
}

function handleArtworkError(event: Event) {
  if ((event.target as HTMLImageElement).getAttribute('src') === artwork.value) {
    artworkFailed.value = true
    backdrop.update(null)
  }
}

// Footer auto-hide (old behavior): idle collapses after 5s, pin keeps it open.
const isFooterCollapsed = ref(false)
const isFooterPinned = ref(false)
let footerAutoHideTimer = 0
let lastFooterActivityAt = 0
let footerHovering = false

// Taskbar mode mirrors the old top-actions toggle (visual state flips locally;
// the host applies the real window mode).
const taskbarHidden = ref(false)

// Entry/exit motion phases driven by the host (old immersive-enter/leave animations).
const enterPhase = ref<'idle' | 'entering' | 'leaving'>('idle')
let phaseTimer = 0
let enterFrame = 0

function playEnterAnimation() {
  window.cancelAnimationFrame(enterFrame)
  window.clearTimeout(phaseTimer)
  enterPhase.value = 'idle'
  if (reducedMotionPreference.value || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    revealFooter()
    return
  }
  // Restart the CSS animation on the next frame.
  enterFrame = window.requestAnimationFrame(() => {
    enterFrame = 0
    if (!surfaceVisible.value) return
    enterPhase.value = 'entering'
    revealFooter()
    phaseTimer = window.setTimeout(() => {
      enterPhase.value = 'idle'
    }, 560)
  })
}

function playLeaveAnimation() {
  window.clearTimeout(phaseTimer)
  if (reducedMotionPreference.value || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    enterPhase.value = 'idle'
    return
  }
  enterPhase.value = 'leaving'
  phaseTimer = window.setTimeout(() => {
    enterPhase.value = 'idle'
  }, 280)
}

function notifyHostEvent(name: string, data: Record<string, unknown> = {}) {
  try {
    const hostWebView = (
      window as unknown as { chrome?: { webview?: { postMessage: (message: unknown) => void } } }
    ).chrome?.webview
    hostWebView?.postMessage({ event: name, data })
  } catch {
    // Diagnostics must never throw.
  }
}

// Surface page-side crashes to the host log via the bridge (D0 diagnostics).
function reportPageError(data: Record<string, unknown>) {
  notifyHostEvent('web.error', data)
}

window.addEventListener('error', (event) => {
  reportPageError({ message: event.message, source: event.filename, line: event.lineno })
})
window.addEventListener('unhandledrejection', (event) => {
  reportPageError({ message: String((event as PromiseRejectionEvent).reason) })
})

let detachEvents: (() => void) | null = null
let lostTrackTimer = 0

const artworkMonogram = computed(() => {
  const source = (title.value || '').trim()

  if (source.length === 0) {
    return '♪'
  }

  const first = source[0]
  return /[a-z]/i.test(first) ? first.toUpperCase() : first
})

const positionText = computed(() => `${formatTime(displayPosition.value)} / ${formatTime(duration.value)}`)
const lyricPositionMs = computed(() => toLyricPlayerTimeMs(displayPosition.value))

const seekPercent = computed(() =>
  duration.value > 0 ? Math.min(100, (displayPosition.value / duration.value) * 100) : 0,
)

function formatTime(seconds: number) {
  const safe = Number.isFinite(seconds) && seconds > 0 ? seconds : 0
  const minutes = Math.floor(safe / 60)
  const remainder = Math.floor(safe % 60)
  return `${minutes}:${String(remainder).padStart(2, '0')}`
}

// ---- footer auto-hide (old front-end logic) ----

function clearFooterAutoHideTimer() {
  if (footerAutoHideTimer !== 0) {
    window.clearTimeout(footerAutoHideTimer)
    footerAutoHideTimer = 0
  }
}

function scheduleFooterAutoHide() {
  clearFooterAutoHideTimer()

  if (!surfaceVisible.value || isFooterPinned.value || footerHovering) {
    return
  }

  footerAutoHideTimer = window.setTimeout(() => {
    footerAutoHideTimer = 0

    if (!isFooterPinned.value && !footerHovering) {
      isFooterCollapsed.value = true
    }
  }, FOOTER_AUTO_HIDE_DELAY_MS)
}

function revealFooter(restartTimer = true) {
  isFooterCollapsed.value = false

  if (restartTimer) {
    scheduleFooterAutoHide()
  }
}

function handleImmersiveActivity() {
  if (isFooterPinned.value) {
    return
  }

  const currentTime = window.performance.now()

  if (!isFooterCollapsed.value && currentTime - lastFooterActivityAt < FOOTER_ACTIVITY_THROTTLE_MS) {
    return
  }

  lastFooterActivityAt = currentTime
  revealFooter()
}

function handleFooterEnter() {
  footerHovering = true
  revealFooter(false)
  clearFooterAutoHideTimer()
}

function handleFooterLeave() {
  footerHovering = false
  scheduleFooterAutoHide()
}

function toggleFooterPinned() {
  isFooterPinned.value = !isFooterPinned.value
  revealFooter(!isFooterPinned.value)

  if (isFooterPinned.value) {
    clearFooterAutoHideTimer()
  }
}

/// App-level motion preference pushed from the native shell (R4-2).
const reducedMotionPreference = ref(false)

const frameLoop = createImmersiveFrameLoop({
  requestFrame: (callback) => window.requestAnimationFrame(callback),
  cancelFrame: (id) => window.cancelAnimationFrame(id),
  updateClock: () => { displayPosition.value = currentPosition() },
  drawBackground: (timestamp) => ground?.frame(timestamp, playing.value) ?? false,
})

function syncFrameLoop() {
  frameLoop.setState({
    visible: surfaceVisible.value,
    playing: playing.value,
    reducedMotion: reducedMotionPreference.value || window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  })
}

// Windows' animation switch reaches the page only as this query, and it can be thrown while
// the ground is flowing: nothing else would stop it before the next pause.
const systemMotion = window.matchMedia('(prefers-reduced-motion: reduce)')

function handleMotionChange() {
  if (motionReduced()) ground?.settle()
  syncFrameLoop()
}

function setSurfaceVisible(visible: boolean) {
  if (surfaceVisible.value === visible) return
  surfaceVisible.value = visible
  // In-flight replies belong to the previous presentation. Reopening always
  // requests a fresh snapshot, including same-track metadata/lyrics edits.
  snapshotRequestToken++
  trackRequestToken++
  lyricsRequestToken++
  trackNeedsRefresh = true
  appliedLyricsKey = ''
  window.clearTimeout(lostTrackTimer)
  lostTrackTimer = 0
  syncFrameLoop()
  if (visible) {
    displayPosition.value = currentPosition()
    ground?.draw()
    void pollForSnapshot()
  } else {
    // No frames come while hidden, so a cover still mixing in arrives now.
    ground?.settle()
    window.cancelAnimationFrame(enterFrame)
    enterFrame = 0
    clearFooterAutoHideTimer()
    footerHovering = false
  }
  notifyHostEvent('web.activity', { visible, frameLoopActive: visible && playing.value })
}

function currentPosition() {
  if (!playing.value) {
    return eventPosition.value
  }

  return eventPosition.value + (performance.now() - eventAt.value) / 1000
}

async function refreshTrack(trackId: string, positionSeconds: number) {
  const token = ++trackRequestToken
  activeTrackId.value = trackId

  try {
    const detail = (await hostRequest<TrackDetail | null>('desktop_catalog_get_track', {
      trackId,
      includeArtwork: true,
    })) as TrackDetail | null

    if (!surfaceVisible.value || token !== trackRequestToken || activeTrackId.value !== trackId) return
    if (detail) {
      title.value = detail.displayTitle || detail.title || ''
      artist.value = detail.artist || ''
      setArtwork(detail.artwork ?? null)
    } else {
      title.value = ''
      artist.value = ''
      setArtwork(null)
    }
  } catch (error) {
    if (!surfaceVisible.value || token !== trackRequestToken || activeTrackId.value !== trackId) return
    setArtwork(null)
    setStatus('trackError', error)
  }

  await refreshLyrics(trackId, positionSeconds)
}

/// R4-3: fetches lyrics with a monotonic token so a response that lost its race is
/// discarded — including A -> B -> A, where a trackId comparison would wrongly accept
/// the stale reply. `key` dedupes same-track refreshes pushed by the shell.
async function refreshLyrics(trackId: string, positionSeconds: number, key?: string) {
  if (!surfaceVisible.value) return
  if (key && key === appliedLyricsKey) return

  const token = ++lyricsRequestToken

  try {
    const resolved = await hostRequest('lyrics_resolve_track', {
      trackId,
      positionSeconds,
    })

    if (token !== lyricsRequestToken || activeTrackId.value !== trackId) return
    if (key) appliedLyricsKey = key
    lyricLines.value = createLyricPlayerLines(resolved)
    setStatus((resolved as { reason?: string } | null)?.reason === 'together-lyrics-unavailable'
      ? 'togetherLyricsUnavailable' : lyricLines.value.length === 0 ? 'noLyrics' : null)
  } catch (error) {
    if (token !== lyricsRequestToken || activeTrackId.value !== trackId) return
    if (key) appliedLyricsKey = key
    lyricLines.value = []
    setStatus('lyricError', error)
  }
}

function applyPlayback(payload: PlaybackPayload) {
  if (!surfaceVisible.value) return
  playbackRevision++
  // PlaybackStatus serializes as PascalCase (Idle/Paused/Playing); compare case-insensitively.
  playing.value = (payload.status ?? '').toLowerCase() === 'playing'
  duration.value = Number(payload.duration ?? payload.durationSeconds) || 0
  eventPosition.value = Number(payload.currentTime ?? payload.positionSeconds) || 0
  if (typeof payload.volume === 'number') volumePercent.value = Math.max(0, Math.min(100, payload.volume * 100))
  eventAt.value = performance.now()
  // Paused/idle views have no animation loop, so seek and pause still update now.
  displayPosition.value = eventPosition.value

  const nextTrackId = payload.activeTrackId ?? null

  if (nextTrackId && (nextTrackId !== activeTrackId.value || trackNeedsRefresh)) {
    if (nextTrackId !== activeTrackId.value) lyricLines.value = []
    trackNeedsRefresh = false
    activeTrackId.value = nextTrackId
    // A new track invalidates the previous dedupe key: the same track may come back
    // later (A -> B -> A) and must be re-fetched rather than suppressed as a no-op.
    appliedLyricsKey = ''
    void refreshTrack(nextTrackId, eventPosition.value)
  } else if (!nextTrackId && activeTrackId.value) {
    activeTrackId.value = null
    appliedLyricsKey = ''
    lyricLines.value = []
    title.value = ''
    artist.value = ''
    setArtwork(null)
    setStatus('chooseTrack')
  }
}

async function pollForSnapshot() {
  if (!surfaceVisible.value) return
  const token = ++snapshotRequestToken
  const revision = playbackRevision
  try {
    const snapshot = await hostRequest<PlaybackPayload>('playback_snapshot', {})
    if (!surfaceVisible.value || token !== snapshotRequestToken) return
    // A newer pushed playback event wins over an older request reply.
    if (revision !== playbackRevision) return
    applyPlayback(snapshot)

    if (snapshot.activeTrackId) {
      return
    }

    setStatus('chooseTrack')
    lostTrackTimer = window.setTimeout(() => void pollForSnapshot(), LOST_TRACK_POLL_MS)
  } catch (error) {
    if (!surfaceVisible.value || token !== snapshotRequestToken || revision !== playbackRevision) return
    setStatus('stateError', error)
    lostTrackTimer = window.setTimeout(() => void pollForSnapshot(), LOST_TRACK_POLL_MS)
  }
}

async function seekTo(seconds: number) {
  try {
    await hostRequest('playback_seek', { seconds })
    eventPosition.value = seconds
    eventAt.value = performance.now()
    displayPosition.value = seconds
  } catch (error) {
    setStatus('seekError', error)
  }
}

function handleLyricLineClick(payload: { startTime: number } | null) {
  if (!payload || !Number.isFinite(payload.startTime)) {
    return
  }

  void seekTo(payload.startTime / 1000)
}

async function togglePlay() {
  try {
    await hostRequest('ui.toggle_play', {})
  } catch (error) {
    setStatus('controlError', error)
  }
}

async function playNext() {
  try {
    await hostRequest('ui.play_next', {})
  } catch (error) {
    setStatus('controlError', error)
  }
}

async function playPrevious() {
  try {
    await hostRequest('ui.play_previous', {})
  } catch (error) {
    setStatus('controlError', error)
  }
}

async function applyVolume(percent: number) {
  try {
    await hostRequest('ui.set_volume', { volume: percent / 100 })
  } catch (error) {
    setStatus('volumeError', error)
  }
}

function handleSeekClick(event: MouseEvent) {
  if (duration.value <= 0) {
    return
  }

  const target = event.currentTarget as HTMLElement
  const rect = target.getBoundingClientRect()
  const percent = ((event.clientX - rect.left) / rect.width) * 100
  void seekTo(Math.max(0, Math.min(duration.value, (percent / 100) * duration.value)))
}

function requestClose() {
  notifyHostEvent('web.close-immersive')
}

function toggleTaskbarMode() {
  taskbarHidden.value = !taskbarHidden.value
  notifyHostEvent('web.toggle-taskbar')
}

function handleKeydown(event: KeyboardEvent) {
  if (!surfaceVisible.value) return
  handleImmersiveActivity()

  if (event.code === 'Escape') {
    requestClose()
    return
  }

  if (event.code === 'Space') {
    event.preventDefault()
    void togglePlay()
  } else if (event.code === 'ArrowRight') {
    void seekTo(Math.min(duration.value || Infinity, displayPosition.value + 5))
  } else if (event.code === 'ArrowLeft') {
    void seekTo(Math.max(0, displayPosition.value - 5))
  }
}

watch(playing, syncFrameLoop)

onMounted(() => {
  const canvas = backdropCanvas.value
  if (canvas) {
    try {
      groundRenderer = createBackdropRenderer(canvas)
      ground = createBackdrop(groundRenderer)
      ground.draw()
      groundObserver = new ResizeObserver(() => groundRenderer?.resize())
      groundObserver.observe(canvas)
    } catch {
      // The page's own dark background stands in for a ground that cannot be painted.
      ground = null
    }
  }

  bridgeReady.value = initHostBridge()

  if (!bridgeReady.value) {
    setStatus('unavailable')
    return
  }

  detachEvents = onHostEvent((event) => {
    if (event.event === 'playback' && event.data && typeof event.data === 'object') {
      applyPlayback(event.data as PlaybackPayload)
      return
    }

    if (event.event === 'track.metadata') {
      if (!surfaceVisible.value) return
      const update = readPlaybackTrackUpdate(event.data, activeTrackId.value, trackMetadataRevision)
      if (!update) return
      trackMetadataRevision = update.revision
      // Invalidate an older catalog reply even when both songs use "external".
      trackRequestToken++
      title.value = update.title
      artist.value = update.artist
      setArtwork(update.artwork)
      return
    }

    if (event.event === 'web.enter') {
      setSurfaceVisible(true)
      playEnterAnimation()
      return
    }

    if (event.event === 'web.exit') {
      setSurfaceVisible(false)
      playLeaveAnimation()
      return
    }

    if (event.event === 'web.hidden') {
      setSurfaceVisible(false)
      return
    }

    if (event.event === 'web.taskbar' && event.data && typeof event.data === 'object') {
      taskbarHidden.value = (event.data as { hidden?: boolean }).hidden === true
      return
    }

    if (event.event === 'web.motion' && event.data && typeof event.data === 'object') {
      reducedMotionPreference.value = (event.data as { reduced?: boolean }).reduced === true
      handleMotionChange()
      return
    }

    // R4-3: the shell pushes this after a same-track lyric edit (bind / clear /
    // reparse). Without it the panel would keep showing stale lyrics until the
    // next track change, because the playback event only carries trackId.
    if (event.event === 'lyrics.changed' && event.data && typeof event.data === 'object') {
      const data = event.data as { trackId?: string | null; version?: number }
      const trackId = data.trackId ?? activeTrackId.value

      if (!trackId || trackId !== activeTrackId.value) return

      void refreshLyrics(trackId, currentPosition(), `${trackId}#${data.version ?? 0}`)
    }
  })

  notifyHostReady()
  window.addEventListener('keydown', handleKeydown)
  systemMotion.addEventListener('change', handleMotionChange)
  // web.enter starts work; web.ready during hidden prewarm must remain idle.
})

onBeforeUnmount(() => {
  detachEvents?.()
  window.removeEventListener('keydown', handleKeydown)
  systemMotion.removeEventListener('change', handleMotionChange)
  backdrop.cancel()
  setSurfaceVisible(false)
  frameLoop.stop()
  groundObserver?.disconnect()
  groundRenderer?.dispose()
  window.cancelAnimationFrame(enterFrame)
  window.clearTimeout(lostTrackTimer)
  window.clearTimeout(phaseTimer)
  clearFooterAutoHideTimer()
})
</script>

<template>
  <div
    class="ip-root"
    :class="{
      'is-playing': playing && surfaceVisible,
      'is-footer-collapsed': isFooterCollapsed,
      'is-entering': enterPhase === 'entering',
      'is-leaving': enterPhase === 'leaving',
    }"
    @pointermove.passive="handleImmersiveActivity"
    @pointerdown="handleImmersiveActivity"
  >
    <canvas ref="backdropCanvas" class="ip-backdrop" aria-hidden="true"></canvas>

    <header class="ip-header">
      <div class="ip-meta">
        <h1 class="ip-title">{{ title || (activeTrackId ? t('unknownTrack') : 'OFPlayer') }}</h1>
        <p class="ip-artist">{{ artist }}</p>
      </div>
      <div class="ip-actions">
        <button
          class="ip-action"
          :class="{ 'is-active': isFooterPinned }"
          type="button"
          :aria-pressed="isFooterPinned"
          :aria-label="t(isFooterPinned ? 'unpinControls' : 'pinControls')"
          :title="t(isFooterPinned ? 'unpinControls' : 'pinControls')"
          @click="toggleFooterPinned"
        ><Pin v-if="isFooterPinned" aria-hidden="true"/><PinOff v-else aria-hidden="true"/></button>
        <button
          class="ip-action"
          type="button"
          :aria-label="t(taskbarHidden ? 'showTaskbar' : 'hideTaskbar')"
          :title="t(taskbarHidden ? 'showTaskbar' : 'hideTaskbar')"
          @click="toggleTaskbarMode"
        ><Minimize2 v-if="taskbarHidden" aria-hidden="true"/><Maximize2 v-else aria-hidden="true"/></button>
        <button
          class="ip-action ip-action--close"
          type="button"
          :aria-label="t('closeImmersive')"
          :title="t('closeImmersiveTip')"
          @click="requestClose"
        ><ChevronDown aria-hidden="true"/></button>
      </div>
    </header>

    <main class="ip-main" :class="{ 'has-lyrics': lyricLines.length > 0 }">
      <figure class="ip-artwork">
        <Transition name="track-art" mode="out-in">
          <div :key="activeTrackId ?? '_'" class="ip-artwork-frame">
            <img v-if="artwork && !artworkFailed" :src="artwork" alt="" class="ip-artwork-image" @error="handleArtworkError" />
            <div v-else class="ip-artwork-placeholder"><span>{{ artworkMonogram }}</span></div>
          </div>
        </Transition>
        <div class="ip-artwork-reflection" aria-hidden="true">
          <img v-if="artwork && !artworkFailed" :src="artwork" alt="" @error="handleArtworkError" />
          <div v-else class="ip-artwork-placeholder"><span>{{ artworkMonogram }}</span></div>
        </div>
      </figure>

      <div v-if="lyricLines.length > 0" class="ip-lyric-stage">
        <LyricsPlayerView
          v-if="lyricLines.length > 0"
          class="ip-lyrics"
          :lyric-lines="lyricLines"
          :current-time="lyricPositionMs"
          :playing="playing && surfaceVisible"
          :align-position="0.42"
          :enable-blur="true"
          @line-click="handleLyricLineClick"
        />
        <p v-else class="ip-empty">{{ statusText || t('noLyrics') }}</p>
      </div>
    </main>

    <footer
      class="ip-footer"
      @pointerenter="handleFooterEnter"
      @pointerleave="handleFooterLeave"
      @focusout="handleFooterLeave"
    >
      <button class="ip-button" type="button" :aria-label="t('previous')" @click="playPrevious"><SkipBack aria-hidden="true"/></button>
      <button class="ip-button ip-button--primary" type="button" :aria-label="t(playing ? 'pause' : 'play')" @click="togglePlay">
        <Pause v-if="playing" aria-hidden="true"/><Play v-else aria-hidden="true"/>
      </button>
      <button class="ip-button" type="button" :aria-label="t('next')" @click="playNext"><SkipForward aria-hidden="true"/></button>
      <span class="ip-time">{{ positionText }}</span>
      <div class="ip-seek" role="slider" :aria-label="t('progress')" @click="handleSeekClick">
        <div class="ip-seek-fill" :style="{ width: seekPercent + '%' }"></div>
      </div>
      <span class="ip-volume-label" aria-hidden="true">{{ t('volume') }}</span>
      <input
        class="ip-volume"
        type="range"
        min="0"
        max="100"
        :value="volumePercent"
        :aria-label="t('volume')"
        @input="applyVolume(Number(($event.target as HTMLInputElement).value))"
      />
    </footer>
  </div>
</template>

<style scoped>
.ip-root {
  --enter-ease: cubic-bezier(0.22, 1, 0.36, 1);
  --exit-ease: cubic-bezier(0.4, 0, 1, 1);
  position: fixed;
  inset: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  color: #f5f5f7;
  background-color: #0b0b0f;
  font-family: 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif;
}

/* Painted by backdropRenderer: the blurred cover under its scrim, already in the pixels. */
.ip-backdrop {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
}

.ip-header {
  position: relative;
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  padding: 22px 34px 0;
}

.ip-title {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
}

.ip-artist {
  margin: 4px 0 0;
  font-size: 13px;
  color: rgba(245, 245, 247, 0.62);
}

.ip-status {
  margin: 6px 0 0;
  font-size: 13px;
  color: rgba(245, 245, 247, 0.5);
}

.ip-actions {
  display: flex;
  gap: 8px;
}

.ip-action {
  min-width: 38px;
  height: 34px;
  padding: 0 10px;
  border: 0;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.1);
  color: inherit;
  font-size: 15px;
  line-height: 1;
  cursor: pointer;
  transition: background 140ms ease, transform 140ms ease;
}

.ip-action:hover {
  background: rgba(255, 255, 255, 0.2);
}

.ip-action.is-active {
  background: rgba(150, 190, 255, 0.35);
}

.ip-action--close {
  font-size: 20px;
  padding-bottom: 8px;
}

.ip-main {
  position: relative;
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: minmax(0, 0.9fr) minmax(0, 1.1fr);
  gap: 12px;
  padding: 6px 34px 0;
}

.ip-artwork {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  margin: 0;
  min-height: 0;
}

.ip-artwork-frame {
  width: min(46vh, 100%);
  aspect-ratio: 1;
  border-radius: 10px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.06);
  box-shadow: 0 22px 60px rgba(0, 0, 0, 0.55);
}

.ip-artwork-image {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.ip-artwork-placeholder {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 64px;
  font-weight: 600;
  color: rgba(245, 245, 247, 0.35);
  background: linear-gradient(160deg, rgba(255, 255, 255, 0.08), rgba(255, 255, 255, 0.02));
}

.ip-artwork-reflection {
  width: min(46vh, 100%);
  aspect-ratio: 1;
  margin-top: 2px;
  overflow: hidden;
  transform: scaleY(-1);
  opacity: 0.22;
  border-radius: 10px;
  -webkit-mask-image: linear-gradient(to top, rgba(0, 0, 0, 0.55), transparent 68%);
  mask-image: linear-gradient(to top, rgba(0, 0, 0, 0.55), transparent 68%);
  pointer-events: none;
}

.ip-artwork-reflection img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.track-art-enter-active,
.track-art-leave-active {
  transition: opacity 320ms ease, transform 320ms ease;
}

.track-art-enter-from {
  opacity: 0;
  transform: translateY(10px) scale(0.98);
}

.track-art-leave-to {
  opacity: 0;
  transform: translateY(-8px) scale(0.99);
}

.ip-lyric-stage {
  position: relative;
  min-height: 0;
}

.ip-lyrics {
  position: absolute;
  inset: 0;
}

.ip-empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  margin: 0;
  color: rgba(245, 245, 247, 0.55);
  font-size: 15px;
}

.ip-footer {
  position: relative;
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 14px 34px 22px;
  transition: transform 380ms ease, opacity 380ms ease;
}

/* Old behavior: the footer slides away after 5s idle unless pinned/hovered. */
.is-footer-collapsed .ip-footer {
  transform: translateY(112%);
  opacity: 0;
}

.ip-button {
  width: 44px;
  height: 44px;
  border: 0;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.12);
  color: inherit;
  font-size: 17px;
  cursor: pointer;
  transition: background 140ms ease;
}

.ip-button:hover {
  background: rgba(255, 255, 255, 0.22);
}

.ip-button--primary {
  width: 54px;
  height: 54px;
  font-size: 20px;
  background: rgba(255, 255, 255, 0.2);
}

.ip-time {
  min-width: 84px;
  font-size: 13px;
  color: rgba(245, 245, 247, 0.6);
  font-variant-numeric: tabular-nums;
}

.ip-seek {
  position: relative;
  flex: 1;
  height: 5px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.16);
  cursor: pointer;
}

.ip-seek-fill {
  position: absolute;
  inset: 0 auto 0 0;
  border-radius: inherit;
  background: rgba(150, 190, 255, 0.9);
}

.ip-volume-label {
  font-size: 12px;
  color: rgba(245, 245, 247, 0.55);
}

.ip-volume {
  width: 120px;
  accent-color: rgba(150, 190, 255, 0.9);
}

@media (max-width: 900px) {
  .ip-main {
    grid-template-columns: minmax(0, 1fr);
  }

  .ip-artwork {
    display: none;
  }
}

/* ---- old immersive entry/exit motion (values copied from the front-end tokens) ---- */

.is-entering {
  animation: immersive-enter 520ms var(--enter-ease) both;
}

.is-leaving {
  pointer-events: none;
  animation: immersive-leave 260ms var(--exit-ease) both;
  transform-origin: 50% 52%;
}

.is-entering .ip-backdrop {
  animation: canvas-in 520ms var(--enter-ease) both;
}

.is-entering .ip-artwork-frame {
  animation: artwork-settle 460ms var(--enter-ease) 40ms both;
}

.is-entering .ip-artwork-reflection {
  animation: reflection-in 380ms var(--enter-ease) 60ms both;
}

.is-entering :is(.ip-actions, .ip-title, .ip-artist) {
  animation: fade-in-up 320ms var(--enter-ease) 60ms both;
}

.is-entering .ip-lyric-stage {
  animation: fade-in-up 320ms var(--enter-ease) 80ms both;
}

.is-entering .ip-footer {
  animation: slide-up 380ms var(--enter-ease) 80ms both;
}

@keyframes immersive-enter {
  from {
    opacity: 0;
    transform: translateY(12px) scale(1.008);
  }

  to {
    opacity: 1;
    transform: none;
  }
}

@keyframes immersive-leave {
  from {
    opacity: 1;
  }

  to {
    opacity: 0;
    transform: translateY(6px);
  }
}

@keyframes canvas-in {
  from {
    opacity: 0;
  }

  to {
    opacity: 1;
  }
}

@keyframes artwork-settle {
  from {
    opacity: 0;
    transform: translateY(16px) scale(0.965);
  }

  to {
    opacity: 1;
    transform: none;
  }
}

@keyframes reflection-in {
  from {
    opacity: 0;
  }

  to {
    opacity: 0.22;
  }
}

@keyframes fade-in-up {
  from {
    opacity: 0;
    transform: translateY(8px);
  }

  to {
    opacity: 1;
    transform: none;
  }
}

@keyframes slide-up {
  from {
    opacity: 0;
    transform: translateY(26px);
  }

  to {
    opacity: 1;
    transform: none;
  }
}

@media (prefers-reduced-motion: reduce) {
  .is-entering {
    animation: none;
  }

  .is-leaving {
    animation: none !important;
  }

  .is-entering :is(.ip-backdrop, .ip-artwork-frame, .ip-artwork-reflection, .ip-actions, .ip-title, .ip-artist, .ip-lyric-stage, .ip-footer) {
    animation: none !important;
  }
}

/* Keep the existing foreground composition above the cover backdrop. */
.ip-root { isolation: isolate; }
.ip-header { position: absolute; inset: 0 0 auto; z-index: 3; padding: 16px 24px; pointer-events: none; }
.ip-meta { position: fixed; left: 8%; bottom: 132px; max-width: 60%; }
.ip-title { font-size: clamp(20px, 2.2vw, 30px); }
.ip-actions { margin-left: auto; gap: 12px; pointer-events: auto; }
.ip-action { display: grid; place-items: center; width: 48px; height: 48px; padding: 0; border-radius: 50%; border: 1px solid #ffffff14; background: #ffffff0a; backdrop-filter: blur(12px); color: #ffffff99; }
.ip-action svg { width: 22px; height: 22px; stroke-width: 1.7; }
.ip-action--close { padding: 0; }
.ip-main { display: flex; align-items: center; justify-content: center; padding: 70px 6% 115px; z-index: 1; }
.ip-main.has-lyrics { gap: clamp(24px, 4vw, 64px); }
.ip-artwork { display: flex; flex-shrink: 0; --art-size: min(clamp(200px, 28vw, 420px), 42vh); }
.ip-artwork-frame { width: var(--art-size); height: var(--art-size); border-radius: 20px; }
.ip-artwork-reflection { position: absolute; top: 100%; width: var(--art-size); height: var(--art-size); border-radius: 20px; }
.ip-lyric-stage { flex: 1; align-self: stretch; min-width: 0; }
.ip-footer { z-index: 2; display: grid; grid-template-columns: 1fr 44px 54px 44px 1fr; gap: 12px; padding: 12px 8% 24px; }
.ip-seek { grid-row: 1; grid-column: 1 / -1; margin-bottom: 14px; }
.ip-seek-fill { background: #ffffffb3; }
.ip-button { display: grid; place-items: center; grid-row: 2; background: #ffffff0f; border: 1px solid #ffffff14; }
.ip-button:nth-child(1) { grid-column: 2; }
.ip-button:nth-child(2) { grid-column: 3; }
.ip-button:nth-child(3) { grid-column: 4; }
.ip-button svg { width: 22px; height: 22px; stroke-width: 1.7; }
.ip-time { grid-row: 2; grid-column: 5; justify-self: end; }
.ip-volume-label { display: none; }
.ip-volume { grid-row: 2; grid-column: 1; width: min(110px, 100%); accent-color: #ddd; }
@media (max-width: 640px) { .ip-main.has-lyrics .ip-artwork { display: none; } .ip-meta { max-width: 85%; } }
</style>
