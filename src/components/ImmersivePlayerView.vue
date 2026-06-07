<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import { getCurrentWindow } from '@tauri-apps/api/window'
import {
  ChevronDown,
  FileText,
  Heart,
  Maximize2,
  Minimize2,
  Pause,
  Pin,
  PinOff,
  Play,
  Repeat,
  Repeat1,
  Shuffle,
  SkipBack,
  SkipForward,
  Trash2,
  Volume2,
  VolumeX,
} from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'
import { createLyricPlayerLines, toLyricPlayerTimeMs } from '../models/lyrics'
import { extractDominantColors, generateBackgroundGradient, defaultColors } from '../utils/colorExtractor'
import { renderImmersiveBackground } from '../utils/immersiveBackgroundRenderer'
import LyricsPlayerView from './LyricsPlayerView.vue'

type RepeatMode = 'none' | 'all' | 'one'
type ImmersiveTaskbarMode = 'show' | 'hide'
type TrackDirection = 'next' | 'prev'

interface RgbColor {
  r: number
  g: number
  b: number
}

interface ImmersiveBackgroundColors {
  base: RgbColor
  topLeft: RgbColor
  topRight: RgbColor
  center: RgbColor
  bottomLeft: RgbColor
  bottomRight: RgbColor
  rareAccent: RgbColor
  rareAccentStrength: number
  rareAccentSalience?: number
  rareAccentHueDistance?: number
  whiteRatio?: number
  lightRatio?: number
  darkRatio?: number
}

interface ImmersiveTrack {
  id?: string
  displayTitle?: string
  title?: string
  artist?: string
  albumArtist?: string
  album?: string
  artwork?: string
  lyricsPath?: string
  isFavorite?: boolean
  format?: string
  bitrate?: number
  sampleRate?: number
  bitDepth?: number
}

interface LyricsSnapshot {
  lines?: unknown[]
  hasLyrics?: boolean
  [key: string]: unknown
}

interface LyricLineClickPayload {
  startTime?: number
}

interface ImmersivePlayerProps {
  currentTrack?: ImmersiveTrack | null
  isPlaying?: boolean
  currentTime?: number
  duration?: number
  volume?: number
  repeatMode?: RepeatMode
  shuffleEnabled?: boolean
  lyrics?: LyricsSnapshot
  lyricsActiveIndex?: number
  lyricsHasTimestamps?: boolean
  lyricsLoading?: boolean
  windowed?: boolean
  taskbarMode?: ImmersiveTaskbarMode
}

const props = withDefaults(defineProps<ImmersivePlayerProps>(), {
  currentTrack: null,
  isPlaying: false,
  currentTime: 0,
  duration: 0,
  volume: 0.8,
  repeatMode: 'all',
  shuffleEnabled: false,
  lyrics: () => ({
    lines: [],
    hasLyrics: false,
  }),
  lyricsActiveIndex: -1,
  lyricsHasTimestamps: false,
  lyricsLoading: false,
  windowed: false,
  taskbarMode: 'show',
})

const emit = defineEmits<{
  close: []
  'toggle-taskbar-mode': []
  'toggle-playback': []
  'play-previous': []
  'play-next': []
  seek: [seconds: number]
  'set-volume': [volume: number]
  'cycle-repeat-mode': []
  'toggle-shuffle': []
  'toggle-favorite': [trackId?: string]
  'bind-lyrics-file': [trackId: string]
  'clear-lyrics-binding': [trackId: string]
}>()

const { t } = useI18n()
const rootRef = ref<HTMLElement | null>(null)
const backgroundCanvas = ref<HTMLCanvasElement | null>(null)
const extractedColors = ref<ImmersiveBackgroundColors | null>(null)
const isExtracting = ref(false)
const trackDirection = ref<TrackDirection>('next')
const isTaskbarHidden = computed(() => props.taskbarMode === 'hide')
const isFooterPinned = ref(false)
const isFooterCollapsed = ref(false)
const taskbarModeIcon = computed(() => (isTaskbarHidden.value ? Minimize2 : Maximize2))
const taskbarModeLabel = computed(() =>
  isTaskbarHidden.value ? t('player.immersive.showTaskbar') : t('player.immersive.hideTaskbar'),
)
const footerPinLabel = computed(() =>
  isFooterPinned.value ? t('player.immersive.unpinFooter') : t('player.immersive.pinFooter'),
)
const isRepeatControlDisabled = computed(() => props.shuffleEnabled)
const isRepeatModeActive = computed(() => !props.shuffleEnabled && props.repeatMode !== 'none')
const repeatModeIcon = computed(() => (props.repeatMode === 'one' ? Repeat1 : Repeat))
const repeatModeLabel = computed(() => {
  if (props.shuffleEnabled) {
    return t('player.repeatDisabledByShuffle')
  }

  if (props.repeatMode === 'one') {
    return t('player.repeatOne')
  }

  if (props.repeatMode === 'all') {
    return t('player.repeatAll')
  }

  return t('player.repeatOff')
})
const shuffleModeLabel = computed(() =>
  props.shuffleEnabled ? t('player.shuffleOn') : t('player.shuffleOff'),
)
let colorExtractionRequestId = 0
let backgroundRenderFrame = 0
let backgroundResizeObserver: ResizeObserver | null = null
let backgroundFlowFrame = 0
let backgroundFlowStartedAt = 0
let backgroundFlowLastRenderAt = 0
const BACKGROUND_FLOW_FRAME_INTERVAL_MS = 1000 / 20
const FOOTER_AUTO_HIDE_DELAY_MS = 5000
const FOOTER_ACTIVITY_THROTTLE_MS = 250
let footerAutoHideTimer: number | null = null
let footerHovering = false
let lastFooterActivityAt = 0
const metaTransitionName = computed(() => `track-meta-${trackDirection.value}`)
const lyricCurrentTime = computed(() => toLyricPlayerTimeMs(props.currentTime))
const lyricLines = computed(() => createLyricPlayerLines(props.lyrics))
const hasLyrics = computed(() => lyricLines.value.length > 0)
const hasExplicitLyricsBinding = computed(() => {
  return typeof props.currentTrack?.lyricsPath === 'string' && props.currentTrack.lyricsPath.trim().length > 0
})

const progressPercent = computed(() => {
  if (!Number.isFinite(props.duration) || props.duration <= 0) return 0
  return Math.min((props.currentTime / props.duration) * 100, 100)
})

function formatTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds < 0) return '0:00'
  const totalSeconds = Math.floor(seconds)
  const minutes = Math.floor(totalSeconds / 60)
  const remainder = totalSeconds % 60
  return `${minutes}:${String(remainder).padStart(2, '0')}`
}

function resolveTrackTitle(track: ImmersiveTrack | null | undefined) {
  return track?.displayTitle || track?.title || t('player.untitled')
}

function resolveTrackArtist(track: ImmersiveTrack | null | undefined) {
  return track?.artist || track?.albumArtist || t('track.unknownArtist')
}

function artworkMonogram(track: ImmersiveTrack | null | undefined) {
  const label = resolveTrackTitle(track)
  if (!label) return 'OF'
  return label.slice(0, 2).toUpperCase()
}

function handleProgressClick(event: MouseEvent) {
  const target = event.currentTarget

  if (!(target instanceof HTMLElement)) {
    return
  }

  const rect = target.getBoundingClientRect()
  const x = event.clientX - rect.left
  const percent = x / rect.width
  const newTime = percent * props.duration
  emit('seek', newTime)
}

function handleClose() {
  emit('close')
}

function clearFooterAutoHideTimer() {
  if (footerAutoHideTimer !== null) {
    window.clearTimeout(footerAutoHideTimer)
    footerAutoHideTimer = null
  }
}

function scheduleFooterAutoHide() {
  clearFooterAutoHideTimer()

  if (isFooterPinned.value || footerHovering) {
    return
  }

  footerAutoHideTimer = window.setTimeout(() => {
    footerAutoHideTimer = null
    if (!isFooterPinned.value && !footerHovering) {
      isFooterCollapsed.value = true
    }
  }, FOOTER_AUTO_HIDE_DELAY_MS)
}

function revealFooter({ restartTimer = true }: { restartTimer?: boolean } = {}) {
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
  revealFooter({ restartTimer: false })
  clearFooterAutoHideTimer()
}

function handleFooterLeave() {
  footerHovering = false
  scheduleFooterAutoHide()
}

function handleFooterFocusOut(event: FocusEvent) {
  const target = event.currentTarget
  const relatedTarget = event.relatedTarget

  if (target instanceof HTMLElement && relatedTarget instanceof Node && target.contains(relatedTarget)) {
    return
  }

  handleFooterLeave()
}

function toggleFooterPinned() {
  isFooterPinned.value = !isFooterPinned.value
  revealFooter({ restartTimer: !isFooterPinned.value })

  if (isFooterPinned.value) {
    clearFooterAutoHideTimer()
  }
}

async function handleDragRegionDoubleClick() {
  if (!props.windowed) {
    return
  }

  try {
    await getCurrentWindow().toggleMaximize()
  } catch {
    // Drag-region double click should stay best-effort in non-Tauri previews.
  }
}

function handlePlayNext() {
  trackDirection.value = 'next'
  emit('play-next')
}

function handlePlayPrev() {
  trackDirection.value = 'prev'
  emit('play-previous')
}

function handleBindLyrics() {
  if (!props.currentTrack?.id) {
    return
  }

  emit('bind-lyrics-file', props.currentTrack.id)
}

function handleClearLyricsBinding() {
  if (!props.currentTrack?.id) {
    return
  }

  emit('clear-lyrics-binding', props.currentTrack.id)
}

function handleKeydown(event: KeyboardEvent) {
  handleImmersiveActivity()

  const target = event.target

  if (event.key === 'Escape') {
    handleClose()
  } else if (event.key === ' ' && (!(target instanceof HTMLElement) || target.tagName !== 'BUTTON')) {
    event.preventDefault()
    emit('toggle-playback')
  }
}

function handleLyricLineClick(event: LyricLineClickPayload | null) {
  const startTime = event?.startTime

  if (typeof startTime !== 'number' || !Number.isFinite(startTime)) {
    return
  }

  emit('seek', startTime / 1000)
}

function handleVolumeInput(event: Event) {
  const target = event.target

  if (!(target instanceof HTMLInputElement)) {
    return
  }

  emit('set-volume', Number(target.value))
}

const backgroundColors = computed<ImmersiveBackgroundColors>(
  () => extractedColors.value ?? (defaultColors as ImmersiveBackgroundColors),
)

const backgroundStyle = computed(() => ({
  background: generateBackgroundGradient(backgroundColors.value),
}))

function resolveBackgroundFlow(timestamp = window.performance.now()) {
  if (!backgroundFlowStartedAt) return 0
  return (timestamp - backgroundFlowStartedAt) / 1000
}

function renderBackground(flow = resolveBackgroundFlow()) {
  if (!backgroundCanvas.value) return
  renderImmersiveBackground(backgroundCanvas.value, backgroundColors.value, { flow })
}

function scheduleBackgroundRender() {
  if (backgroundRenderFrame) return

  backgroundRenderFrame = window.requestAnimationFrame((timestamp) => {
    backgroundRenderFrame = 0
    renderBackground(resolveBackgroundFlow(timestamp))
  })
}

function shouldRunBackgroundFlow() {
  const prefersReducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  return props.isPlaying && !prefersReducedMotion && document.documentElement?.dataset?.motion !== 'reduced'
}

function tickBackgroundFlow(timestamp: number) {
  if (!backgroundFlowStartedAt) {
    backgroundFlowStartedAt = timestamp
  }

  if (timestamp - backgroundFlowLastRenderAt >= BACKGROUND_FLOW_FRAME_INTERVAL_MS) {
    backgroundFlowLastRenderAt = timestamp
    renderBackground(resolveBackgroundFlow(timestamp))
  }

  backgroundFlowFrame = window.requestAnimationFrame(tickBackgroundFlow)
}

function startBackgroundFlow() {
  if (backgroundFlowFrame || !shouldRunBackgroundFlow()) return
  backgroundFlowLastRenderAt = 0
  backgroundFlowFrame = window.requestAnimationFrame(tickBackgroundFlow)
}

function stopBackgroundFlow() {
  if (backgroundFlowFrame) {
    window.cancelAnimationFrame(backgroundFlowFrame)
    backgroundFlowFrame = 0
  }
  backgroundFlowLastRenderAt = 0
  scheduleBackgroundRender()
}

function syncBackgroundFlow() {
  if (shouldRunBackgroundFlow()) {
    startBackgroundFlow()
  } else {
    stopBackgroundFlow()
  }
}

async function extractArtworkColors(artworkUrl: string | null | undefined) {
  if (!artworkUrl) return

  const requestId = ++colorExtractionRequestId
  isExtracting.value = true
  try {
    const colors = await extractDominantColors(artworkUrl) as ImmersiveBackgroundColors
    if (requestId !== colorExtractionRequestId) return
    extractedColors.value = colors
  } catch (error) {
    if (requestId !== colorExtractionRequestId) return
    console.warn('Failed to extract colors:', error)
    extractedColors.value = defaultColors
  } finally {
    if (requestId === colorExtractionRequestId) {
      isExtracting.value = false
    }
  }
}

const audioSpecs = computed(() => {
  const track = props.currentTrack
  if (!track) return []

  const specs: string[] = []
  if (track.format) specs.push(track.format.toUpperCase())
  if (track.bitrate) specs.push(`${Math.round(track.bitrate / 1000)} kbps`)
  if (track.sampleRate) specs.push(`${(track.sampleRate / 1000).toFixed(1)} kHz`)
  if (track.bitDepth) specs.push(`${track.bitDepth}-bit`)

  return specs
})

onMounted(() => {
  document.addEventListener('keydown', handleKeydown)
  scheduleFooterAutoHide()
  void nextTick(() => {
    scheduleBackgroundRender()

    if (typeof ResizeObserver !== 'undefined' && rootRef.value) {
      backgroundResizeObserver = new ResizeObserver(scheduleBackgroundRender)
      backgroundResizeObserver.observe(rootRef.value)
    } else {
      window.addEventListener('resize', scheduleBackgroundRender)
    }
  })

  if (props.currentTrack?.artwork) {
    void extractArtworkColors(props.currentTrack.artwork)
  }

  syncBackgroundFlow()
})

onBeforeUnmount(() => {
  document.removeEventListener('keydown', handleKeydown)
  window.removeEventListener('resize', scheduleBackgroundRender)
  if (backgroundResizeObserver) {
    backgroundResizeObserver.disconnect()
    backgroundResizeObserver = null
  }
  if (backgroundRenderFrame) {
    window.cancelAnimationFrame(backgroundRenderFrame)
    backgroundRenderFrame = 0
  }
  if (backgroundFlowFrame) {
    window.cancelAnimationFrame(backgroundFlowFrame)
    backgroundFlowFrame = 0
  }
  clearFooterAutoHideTimer()
})

watch(
  () => props.currentTrack?.artwork,
  (newArtwork) => {
    if (newArtwork) {
      void extractArtworkColors(newArtwork)
    } else {
      extractedColors.value = null
    }
  },
)

watch(() => props.isPlaying, syncBackgroundFlow)
watch(backgroundColors, scheduleBackgroundRender, { deep: true })
watch(() => props.currentTrack?.id, () => {
  revealFooter()
})
</script>

<template>
  <div
    ref="rootRef"
    class="immersive-root"
    :class="{
      'immersive-root--windowed': windowed,
      'is-playing': isPlaying,
      'is-footer-collapsed': isFooterCollapsed,
      'is-footer-pinned': isFooterPinned,
    }"
    :style="backgroundStyle"
    @pointermove.passive="handleImmersiveActivity"
    @pointerdown="handleImmersiveActivity"
  >
    <canvas ref="backgroundCanvas" class="immersive-background-canvas" aria-hidden="true"></canvas>

    <div
      v-if="windowed"
      class="immersive-window-drag-region"
      data-tauri-drag-region
      aria-hidden="true"
      @dblclick="handleDragRegionDoubleClick"
    ></div>

    <div class="immersive-top-actions">
      <button
        class="immersive-top-cta"
        :class="{ 'is-active': isFooterPinned }"
        type="button"
        :aria-label="footerPinLabel"
        :aria-pressed="isFooterPinned"
        :title="footerPinLabel"
        @click="toggleFooterPinned"
      >
        <Pin v-if="isFooterPinned" aria-hidden="true" />
        <PinOff v-else aria-hidden="true" />
      </button>

      <button
        class="immersive-top-cta"
        type="button"
        :aria-label="taskbarModeLabel"
        :title="taskbarModeLabel"
        @click="emit('toggle-taskbar-mode')"
      >
        <component :is="taskbarModeIcon" aria-hidden="true" />
      </button>

      <button
        class="immersive-top-cta immersive-close"
        type="button"
        :aria-label="t('player.immersive.close')"
        :title="t('player.immersive.close')"
        @click="handleClose"
      >
        <ChevronDown aria-hidden="true" />
      </button>
    </div>

    <main class="immersive-main" :class="{ 'has-lyrics': hasLyrics }">
      <figure class="immersive-artwork">
        <Transition name="track-art" mode="out-in">
          <div :key="currentTrack?.id ?? '_'" class="immersive-artwork-frame">
            <img
              v-if="currentTrack?.artwork"
              :src="currentTrack.artwork"
              :alt="resolveTrackTitle(currentTrack)"
              class="immersive-artwork-image"
            />
            <div v-else class="immersive-artwork-placeholder">
              <span>{{ artworkMonogram(currentTrack) }}</span>
            </div>
          </div>
        </Transition>

        <div class="immersive-artwork-reflection" aria-hidden="true">
          <img
            v-if="currentTrack?.artwork"
            :src="currentTrack.artwork"
            :alt="''"
            class="immersive-artwork-image"
          />
          <div v-else class="immersive-artwork-placeholder">
            <span>{{ artworkMonogram(currentTrack) }}</span>
          </div>
        </div>
      </figure>

      <LyricsPlayerView
        v-if="hasLyrics"
        class="immersive-lyrics"
        :lyric-lines="lyricLines"
        :current-time="lyricCurrentTime"
        :playing="isPlaying"
        :align-position="0.42"
        :enable-blur="true"
        @line-click="handleLyricLineClick"
      />
    </main>

    <footer
      class="immersive-footer"
      @pointerenter="handleFooterEnter"
      @pointerleave="handleFooterLeave"
      @focusin="handleFooterEnter"
      @focusout="handleFooterFocusOut"
    >
      <div class="immersive-footer-inner">
        <div class="immersive-meta-row">
          <Transition :name="metaTransitionName" mode="out-in">
            <div :key="currentTrack?.id ?? '_'" class="immersive-meta-main">
              <h1 class="immersive-title">{{ resolveTrackTitle(currentTrack) }}</h1>
              <p class="immersive-artist">{{ resolveTrackArtist(currentTrack) }}</p>
              <p v-if="currentTrack?.album" class="immersive-album">{{ currentTrack.album }}</p>
            </div>
          </Transition>

          <div class="immersive-meta-side">
            <div v-if="audioSpecs.length > 0" class="immersive-specs">
              <span v-for="(spec, index) in audioSpecs" :key="index" class="immersive-spec-tag">{{ spec }}</span>
            </div>

            <button
              v-if="hasExplicitLyricsBinding"
              class="immersive-action-btn immersive-action-btn--lyrics-clear"
              type="button"
              :aria-label="t('player.details.clearLyricsBinding')"
              @click="handleClearLyricsBinding"
            >
              <Trash2 aria-hidden="true" />
            </button>
            <button
              v-else
              class="immersive-action-btn"
              :class="{ 'is-active': hasLyrics }"
              type="button"
              :aria-label="hasLyrics ? t('player.details.rebindLyrics') : t('player.details.bindLyrics')"
              @click="handleBindLyrics"
            >
              <FileText aria-hidden="true" />
            </button>

            <button
              class="immersive-action-btn"
              :class="{ 'is-active': currentTrack?.isFavorite }"
              type="button"
              :aria-label="currentTrack?.isFavorite ? t('sidebar.actions.unfavorite') : t('sidebar.actions.favorite')"
              @click="emit('toggle-favorite', currentTrack?.id)"
            >
              <Heart aria-hidden="true" />
            </button>
          </div>
        </div>

        <div class="immersive-progress-section">
          <div
            class="immersive-progress"
            role="slider"
            :aria-label="t('player.progress')"
            :aria-valuemin="0"
            :aria-valuemax="duration"
            :aria-valuenow="currentTime"
            tabindex="0"
            @click="handleProgressClick"
            @keydown.left.stop="emit('seek', Math.max(0, currentTime - 5))"
            @keydown.right.stop="emit('seek', Math.min(duration, currentTime + 5))"
          >
            <div class="immersive-progress-track">
              <div
                class="immersive-progress-fill"
                :style="{ width: `${progressPercent}%` }"
              ></div>
            </div>
            <div
              class="immersive-progress-thumb"
              :style="{ left: `${progressPercent}%` }"
            ></div>
          </div>
        </div>

        <div class="immersive-controls-row">
          <div class="immersive-volume">
            <button
              class="immersive-volume-btn"
              type="button"
              :aria-label="volume === 0 ? t('player.unmute') : t('player.mute')"
              @click="emit('set-volume', volume === 0 ? 0.8 : 0)"
            >
              <VolumeX v-if="volume === 0" aria-hidden="true" />
              <Volume2 v-else aria-hidden="true" />
            </button>
            <input
              class="immersive-volume-slider"
              type="range"
              min="0"
              max="1"
              step="0.01"
              :value="volume"
              :aria-label="t('player.volume')"
              @input="handleVolumeInput"
            />
          </div>

          <div class="immersive-controls-stack">
            <div class="immersive-controls">
              <button
                class="immersive-control-btn immersive-control-btn--secondary"
                type="button"
                :aria-label="t('player.prev')"
                @click="handlePlayPrev"
              >
                <SkipBack aria-hidden="true" />
              </button>
              <button
                class="immersive-control-btn immersive-control-btn--primary"
                type="button"
                :aria-label="isPlaying ? t('player.pause') : t('player.play')"
                @click="emit('toggle-playback')"
              >
                <Pause v-if="isPlaying" aria-hidden="true" />
                <Play v-else aria-hidden="true" />
              </button>
              <button
                class="immersive-control-btn immersive-control-btn--secondary"
                type="button"
                :aria-label="t('player.next')"
                @click="handlePlayNext"
              >
                <SkipForward aria-hidden="true" />
              </button>
            </div>
            <div class="immersive-mode-row">
              <button
                class="immersive-mode-btn"
                :class="{ 'is-active': shuffleEnabled }"
                type="button"
                :aria-label="shuffleModeLabel"
                :aria-pressed="shuffleEnabled"
                :title="shuffleModeLabel"
                @click="emit('toggle-shuffle')"
              >
                <Shuffle aria-hidden="true" />
              </button>
              <button
                class="immersive-mode-btn"
                :class="{ 'is-active': isRepeatModeActive }"
                type="button"
                :disabled="isRepeatControlDisabled"
                :aria-label="repeatModeLabel"
                :aria-pressed="isRepeatModeActive"
                :title="repeatModeLabel"
                @click="emit('cycle-repeat-mode')"
              >
                <component :is="repeatModeIcon" aria-hidden="true" />
              </button>
            </div>
          </div>

          <div class="immersive-time">
            <span class="immersive-time-current">{{ formatTime(currentTime) }}</span>
            <span class="immersive-time-divider">/</span>
            <span class="immersive-time-total">{{ formatTime(duration) }}</span>
          </div>
        </div>
      </div>
    </footer>
  </div>
</template>

<style scoped>
.immersive-root {
  --immersive-cta-size: clamp(48px, 2.8vw, 58px);
  --immersive-cta-icon-size: calc(var(--immersive-cta-size) * 0.5);
  position: fixed;
  inset: 0;
  min-height: 100dvh;
  z-index: 100;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  color: rgba(255, 255, 255, 0.92);
  font-family: var(--font-family);
  background-color: #0b1417;
  isolation: isolate;
}

.immersive-root::before,
.immersive-root::after {
  content: '';
  position: absolute;
  pointer-events: none;
  z-index: 0;
}

.immersive-root::before {
  inset: -24%;
  opacity: 0.14;
  mix-blend-mode: soft-light;
  filter: blur(28px);
  background:
    linear-gradient(96deg, transparent 0%, rgba(255, 255, 255, 0.038) 38%, transparent 68%),
    linear-gradient(18deg, transparent 8%, rgba(255, 255, 255, 0.026) 48%, transparent 82%);
  background-size: 180% 150%, 160% 170%;
  background-position: 8% 50%, 92% 46%;
}

.immersive-root::after {
  inset: 0;
  opacity: 0.68;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.07) 0%, transparent 26%, transparent 66%, rgba(0, 0, 0, 0.18) 100%),
    linear-gradient(90deg, rgba(0, 0, 0, 0.16), transparent 30%, transparent 72%, rgba(0, 0, 0, 0.2)),
    url("data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20180%20180'%3E%3Cfilter%20id='n'%3E%3CfeTurbulence%20type='fractalNoise'%20baseFrequency='.78'%20numOctaves='1'%20stitchTiles='stitch'/%3E%3CfeColorMatrix%20type='saturate'%20values='0'/%3E%3C/filter%3E%3Crect%20width='180'%20height='180'%20filter='url(%23n)'%20opacity='.065'/%3E%3C/svg%3E");
  background-size: auto, auto, 180px 180px;
  background-repeat: no-repeat, no-repeat, repeat;
  background-blend-mode: normal, normal, soft-light;
}

.immersive-root.is-playing::before {
  animation: immersive-light-drift 34s ease-in-out infinite alternate;
}

.immersive-background-canvas {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  z-index: 0;
  display: block;
  pointer-events: none;
  opacity: 1;
}

@keyframes immersive-light-drift {
  from {
    opacity: 0.11;
    background-position: 8% 50%, 92% 46%;
  }
  to {
    opacity: 0.18;
    background-position: 92% 48%, 8% 54%;
  }
}

.immersive-root--windowed {
  inset: 0;
  height: 100dvh;
  min-height: 100dvh;
  max-height: 100dvh;
}

.immersive-window-drag-region {
  position: absolute;
  inset: 0 0 auto;
  height: calc(var(--window-titlebar-height, 40px) + env(safe-area-inset-top, 0px));
  z-index: 9;
  user-select: none;
  -webkit-user-select: none;
}

.immersive-top-actions {
  position: absolute;
  top: clamp(1rem, 2vw, 1.5rem);
  right: clamp(1rem, 2vw, 1.5rem);
  z-index: 10;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.immersive-top-cta {
  display: grid;
  place-items: center;
  width: var(--immersive-cta-size);
  height: var(--immersive-cta-size);
  min-width: var(--immersive-cta-size);
  padding: 0;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.04);
  backdrop-filter: blur(12px);
  color: rgba(255, 255, 255, 0.6);
  cursor: pointer;
  transition: all 0.25s cubic-bezier(0.2, 0, 0, 1);
}

.immersive-root--windowed .immersive-top-actions {
  top: 1rem;
}

.immersive-top-cta:hover {
  background: rgba(255, 255, 255, 0.1);
  border-color: rgba(255, 255, 255, 0.15);
  color: rgba(255, 255, 255, 0.9);
  transform: scale(1.05);
}

.immersive-top-cta.is-active {
  background: rgba(255, 255, 255, 0.14);
  border-color: rgba(255, 255, 255, 0.22);
  color: rgba(255, 255, 255, 0.95);
}

.immersive-top-cta svg {
  width: var(--immersive-cta-icon-size);
  height: var(--immersive-cta-icon-size);
  stroke-width: 2;
}

@media (max-width: 1440px), (max-height: 820px) {
  .immersive-root {
    --immersive-cta-size: 56px;
  }
}

@media (max-width: 1600px) and (min-resolution: 1.5dppx) {
  .immersive-root {
    --immersive-cta-size: 62px;
  }
}

@media (max-width: 920px), (pointer: coarse) {
  .immersive-root {
    --immersive-cta-size: 60px;
  }
}

@media (max-width: 920px) and (min-resolution: 1.5dppx) {
  .immersive-root {
    --immersive-cta-size: 64px;
  }
}

.immersive-main {
  position: relative;
  z-index: 1;
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem 3rem 0.5rem;
  min-height: 0;
  overflow: hidden;
}

.immersive-main.has-lyrics {
  flex-direction: row;
  align-items: stretch;
  justify-content: flex-start;
  gap: clamp(2rem, 4vw, 4.5rem);
}

.immersive-main.has-lyrics .immersive-artwork {
  align-self: center;
  flex-shrink: 0;
}

.immersive-lyrics {
  flex: 1 1 0;
  min-width: 0;
  position: relative;
  max-width: min(62vw, 980px);
  margin-inline-end: clamp(1rem, 4vw, 5rem);
}

.immersive-artwork {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0;
  margin: 0;
  --art-size: min(clamp(200px, 24vw, 420px), 46vh);
}

.immersive-artwork-frame {
  position: relative;
  width: var(--art-size);
  height: var(--art-size);
  border-radius: 1.25rem;
  overflow: hidden;
  box-shadow:
    0 40px 80px -20px rgba(0, 0, 0, 0.6),
    0 0 0 1px rgba(255, 255, 255, 0.05);
}

.immersive-artwork-frame::after {
  content: '';
  position: absolute;
  inset: 0;
  pointer-events: none;
  opacity: 0.42;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.18), transparent 28%, transparent 72%, rgba(255, 255, 255, 0.08));
}

.track-art-leave-active {
  transition: opacity 0.18s ease, transform 0.18s ease, filter 0.18s ease;
}

.track-art-leave-to {
  opacity: 0;
  transform: scale(0.88);
  filter: blur(6px);
}

.track-art-enter-active {
  transition: opacity 0.36s cubic-bezier(0.2, 0, 0, 1), transform 0.36s cubic-bezier(0.2, 0, 0, 1);
}

.track-art-enter-from {
  opacity: 0;
  transform: scale(0.92);
}

.track-meta-next-leave-active {
  transition: opacity 0.14s ease, transform 0.14s ease;
}

.track-meta-next-leave-to {
  opacity: 0;
  transform: translateY(-10px);
}

.track-meta-next-enter-active {
  transition: opacity 0.28s cubic-bezier(0.2, 0, 0, 1) 0.03s, transform 0.28s cubic-bezier(0.2, 0, 0, 1) 0.03s;
}

.track-meta-next-enter-from {
  opacity: 0;
  transform: translateY(10px);
}

.track-meta-prev-leave-active {
  transition: opacity 0.14s ease, transform 0.14s ease;
}

.track-meta-prev-leave-to {
  opacity: 0;
  transform: translateY(10px);
}

.track-meta-prev-enter-active {
  transition: opacity 0.28s cubic-bezier(0.2, 0, 0, 1) 0.03s, transform 0.28s cubic-bezier(0.2, 0, 0, 1) 0.03s;
}

.track-meta-prev-enter-from {
  opacity: 0;
  transform: translateY(-10px);
}

.immersive-artwork-image {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.immersive-artwork-placeholder {
  width: 100%;
  height: 100%;
  display: grid;
  place-items: center;
  background: linear-gradient(145deg, rgba(60, 55, 75, 0.5), rgba(40, 35, 55, 0.7));
  font-size: clamp(3rem, 6vw, 5rem);
  font-weight: 700;
  letter-spacing: -0.03em;
  color: rgba(255, 255, 255, 0.25);
}

.immersive-artwork-reflection {
  width: var(--art-size);
  height: calc(var(--art-size) * 0.28);
  border-radius: 0 0 1.25rem 1.25rem;
  overflow: hidden;
  transform: scaleY(-1);
  mask-image: linear-gradient(to bottom, rgba(0, 0, 0, 0.12) 0%, transparent 50%);
  -webkit-mask-image: linear-gradient(to bottom, rgba(0, 0, 0, 0.12) 0%, transparent 50%);
  opacity: 0.5;
  filter: blur(2px);
}

.immersive-artwork-reflection .immersive-artwork-image {
  filter: blur(4px);
}

.immersive-meta-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1.25rem;
  width: 100%;
}

.immersive-meta-main {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  min-width: 0;
  flex: 1;
}

.immersive-meta-side {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-shrink: 0;
}

.immersive-title {
  margin: 0;
  font-size: clamp(1.1rem, 1.6vw, 1.5rem);
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.25;
  color: rgba(255, 255, 255, 0.95);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.immersive-artist {
  margin: 0;
  font-size: clamp(0.85rem, 1.1vw, 1rem);
  font-weight: 500;
  color: rgba(255, 255, 255, 0.65);
}

.immersive-album {
  margin: 0;
  font-size: 0.8125rem;
  color: rgba(255, 255, 255, 0.4);
}

.immersive-specs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.375rem;
}

.immersive-spec-tag {
  padding: 0.3rem 0.625rem;
  border-radius: 2rem;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.08);
  font-size: 0.7rem;
  font-weight: 500;
  letter-spacing: 0.02em;
  color: rgba(255, 255, 255, 0.5);
}

.immersive-action-btn {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  padding: 0;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.04);
  color: rgba(255, 255, 255, 0.5);
  cursor: pointer;
  transition: all 0.2s ease;
}

.immersive-action-btn:hover {
  background: rgba(255, 255, 255, 0.1);
  border-color: rgba(255, 255, 255, 0.2);
  color: rgba(255, 255, 255, 0.8);
}

.immersive-action-btn.is-active {
  background: rgba(255, 80, 100, 0.15);
  border-color: rgba(255, 80, 100, 0.3);
  color: #ff6b7a;
}

.immersive-action-btn svg {
  width: 18px;
  height: 18px;
}

.immersive-action-btn.is-active svg {
  fill: currentColor;
}

.immersive-action-btn--lyrics-clear {
  border-color: rgba(255, 100, 60, 0.2);
  color: rgba(255, 120, 80, 0.7);
}

.immersive-action-btn--lyrics-clear:hover {
  background: rgba(255, 80, 40, 0.15);
  border-color: rgba(255, 80, 40, 0.35);
  color: #ff7050;
}

.immersive-footer {
  position: relative;
  z-index: 1;
  display: flex;
  justify-content: center;
  max-height: 46dvh;
  overflow: hidden;
  background: transparent;
  border-top: 0;
  opacity: 1;
  transform: translateY(0);
  transition:
    max-height 0.42s cubic-bezier(0.22, 1, 0.36, 1),
    opacity 0.26s ease,
    transform 0.42s cubic-bezier(0.22, 1, 0.36, 1);
  will-change: max-height, opacity, transform;
}

.immersive-footer::before {
  content: '';
  position: absolute;
  inset: -2.25rem 0 0;
  z-index: 0;
  pointer-events: none;
  background: linear-gradient(
    180deg,
    rgba(0, 0, 0, 0),
    rgba(0, 0, 0, 0.04) 42%,
    rgba(0, 0, 0, 0.14) 78%,
    rgba(0, 0, 0, 0.24)
  );
  backdrop-filter: blur(118px) saturate(165%);
  -webkit-backdrop-filter: blur(118px) saturate(165%);
  opacity: 1;
  transition: opacity 0.26s ease;
}

.immersive-root.is-footer-collapsed .immersive-footer {
  max-height: 0;
  opacity: 0;
  pointer-events: none;
  transform: translateY(1.25rem);
}

.immersive-root.is-footer-collapsed .immersive-footer::before {
  opacity: 0;
}

.immersive-footer-inner {
  position: relative;
  z-index: 1;
  width: 100%;
  max-width: 900px;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 0.875rem 2rem 1.35rem;
}

.immersive-root--windowed .immersive-footer-inner {
  padding-bottom: max(1.75rem, calc(1.35rem + env(safe-area-inset-bottom, 0px)));
}

.immersive-progress-section {
  width: 100%;
}

.immersive-time {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.25rem;
  font-size: 0.75rem;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  letter-spacing: 0.01em;
  white-space: nowrap;
}

.immersive-time-current {
  color: rgba(255, 255, 255, 0.85);
}

.immersive-time-divider {
  color: rgba(255, 255, 255, 0.2);
}

.immersive-time-total {
  color: rgba(255, 255, 255, 0.4);
}

.immersive-progress {
  position: relative;
  width: 100%;
  height: 20px;
  display: flex;
  align-items: center;
  cursor: pointer;
}

.immersive-progress-track {
  position: relative;
  width: 100%;
  height: 3px;
  border-radius: 1.5px;
  background: rgba(255, 255, 255, 0.08);
  overflow: hidden;
}

.immersive-progress-fill {
  position: absolute;
  left: 0;
  top: 0;
  height: 100%;
  background: rgba(255, 255, 255, 0.5);
  border-radius: 1.5px;
  transition: width 0.18s linear, background 0.24s ease;
}

.immersive-progress:hover .immersive-progress-track {
  height: 4px;
}

.immersive-progress:hover .immersive-progress-fill {
  background: rgba(255, 255, 255, 0.7);
}

.immersive-progress-thumb {
  position: absolute;
  top: 50%;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.95);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.3);
  transform: translate(-50%, -50%) scale(0);
  transition: left 0.18s linear, transform 0.12s ease;
  pointer-events: none;
}

.immersive-progress:hover .immersive-progress-thumb {
  transform: translate(-50%, -50%) scale(1);
}

.immersive-controls-row {
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  width: 100%;
}

.immersive-controls {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.immersive-controls-stack {
  position: relative;
  display: block;
}

.immersive-mode-row {
  position: absolute;
  left: 50%;
  top: calc(100% + 2px);
  z-index: 2;
  transform: translateX(-50%);
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  height: 20px;
}

.immersive-control-btn {
  display: grid;
  place-items: center;
  border: none;
  border-radius: 50%;
  cursor: pointer;
  transition: all 0.2s cubic-bezier(0.2, 0, 0, 1);
}

.immersive-control-btn--secondary {
  width: 40px;
  height: 40px;
  background: rgba(255, 255, 255, 0.06);
  color: rgba(255, 255, 255, 0.55);
}

.immersive-control-btn--secondary:hover {
  background: rgba(255, 255, 255, 0.12);
  color: rgba(255, 255, 255, 0.85);
  transform: scale(1.05);
}

.immersive-control-btn--secondary:active {
  transform: scale(0.95);
}

.immersive-control-btn--secondary svg {
  width: 18px;
  height: 18px;
  stroke-width: 2;
}

.immersive-mode-btn {
  display: grid;
  place-items: center;
  width: 24px;
  height: 18px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: rgba(255, 255, 255, 0.45);
  cursor: pointer;
  transition: all 0.2s cubic-bezier(0.2, 0, 0, 1);
}

.immersive-mode-btn svg {
  width: 12px;
  height: 12px;
  stroke-width: 2.2;
}

.immersive-mode-btn:not(:disabled):hover {
  background: rgba(255, 255, 255, 0.08);
  color: rgba(255, 255, 255, 0.78);
}

.immersive-mode-btn:disabled {
  cursor: not-allowed;
  opacity: 0.36;
}

.immersive-mode-btn.is-active {
  border-color: rgba(255, 255, 255, 0.18);
  background: rgba(255, 255, 255, 0.12);
  color: rgba(255, 255, 255, 0.95);
}

.immersive-control-btn--primary {
  width: 52px;
  height: 52px;
  background: rgba(255, 255, 255, 0.1);
  backdrop-filter: blur(32px);
  -webkit-backdrop-filter: blur(32px);
  color: rgba(255, 255, 255, 0.95);
  border: 1px solid rgba(255, 255, 255, 0.12);
  box-shadow:
    0 4px 20px -4px rgba(0, 0, 0, 0.25),
    inset 0 1px 0 rgba(255, 255, 255, 0.08);
}

.immersive-control-btn--primary:hover {
  background: rgba(255, 255, 255, 0.16);
  border-color: rgba(255, 255, 255, 0.2);
  transform: scale(1.05);
  box-shadow:
    0 6px 24px -4px rgba(0, 0, 0, 0.35),
    inset 0 1px 0 rgba(255, 255, 255, 0.12);
}

.immersive-control-btn--primary:active {
  transform: scale(0.97);
}

.immersive-control-btn--primary svg {
  width: 22px;
  height: 22px;
  stroke-width: 2;
}

.immersive-volume {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: 0.5rem;
}

.immersive-volume-btn {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: rgba(255, 255, 255, 0.45);
  cursor: pointer;
  transition: color 0.2s ease;
}

.immersive-volume-btn:hover {
  color: rgba(255, 255, 255, 0.75);
}

.immersive-volume-btn svg {
  width: 18px;
  height: 18px;
}

.immersive-volume-slider {
  width: 70px;
  height: 3px;
  appearance: none;
  border-radius: 1.5px;
  background: rgba(255, 255, 255, 0.1);
  cursor: pointer;
}

.immersive-volume-slider::-webkit-slider-thumb {
  appearance: none;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.85);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
  cursor: pointer;
  transition: transform 0.12s ease;
}

.immersive-volume-slider::-webkit-slider-thumb:hover {
  transform: scale(1.15);
}

.immersive-volume-slider::-moz-range-thumb {
  width: 10px;
  height: 10px;
  border: none;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.85);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
  cursor: pointer;
}

@media (max-width: 920px) {
  .immersive-main {
    padding: 2.5rem 1.5rem 0.5rem;
    align-items: start;
  }

  .immersive-main.has-lyrics {
    flex-direction: column;
    align-items: center;
    gap: 1rem;
  }

  .immersive-artwork {
    --art-size: min(clamp(200px, 45vw, 320px), 32vh);
  }

  .immersive-artwork-frame {
    width: var(--art-size);
    height: var(--art-size);
  }

  .immersive-artwork-reflection {
    width: var(--art-size);
    height: calc(var(--art-size) * 0.25);
  }

  .immersive-footer-inner {
    padding: 0.875rem 1.5rem 1.25rem;
  }

  .immersive-volume {
    display: none;
  }

  .immersive-controls-row {
    grid-template-columns: 0 auto 1fr;
  }

  .immersive-top-actions {
    top: 1rem;
    right: 1rem;
  }
}

@media (max-width: 640px) {
  .immersive-main {
    padding: 3rem 1rem 0.5rem;
  }

  .immersive-artwork {
    --art-size: min(clamp(160px, 55vw, 240px), 28vh);
  }

  .immersive-artwork-frame {
    width: var(--art-size);
    height: var(--art-size);
  }

  .immersive-artwork-reflection {
    display: none;
  }

  .immersive-title {
    font-size: 1.1rem;
  }

  .immersive-artist {
    font-size: 0.875rem;
  }

  .immersive-footer-inner {
    padding: 0.75rem 1rem 1rem;
  }

  .immersive-meta-row {
    gap: 0.5rem;
  }

  .immersive-specs {
    display: none;
  }

  .immersive-control-btn--primary {
    width: 48px;
    height: 48px;
  }

  .immersive-control-btn--primary svg {
    width: 20px;
    height: 20px;
  }

  .immersive-control-btn--secondary {
    width: 36px;
    height: 36px;
  }

  .immersive-control-btn--secondary svg {
    width: 16px;
    height: 16px;
  }

  .immersive-time {
    font-size: 0.6875rem;
  }
}

@media (prefers-reduced-motion: reduce) {
  .immersive-root {
    animation: none;
  }

  .immersive-root::before,
  .immersive-background-canvas {
    animation: none !important;
  }

  .immersive-progress-fill {
    transition: none;
  }
}
</style>

<style>
.immersive-enter-active {
  animation: immersive-enter 0.72s cubic-bezier(0.16, 1, 0.3, 1) both;
  transform-origin: 50% 48%;
  will-change: opacity, transform;
  backface-visibility: hidden;
}

.immersive-leave-active {
  pointer-events: none;
  animation: immersive-leave 0.32s cubic-bezier(0.32, 0, 0.67, 0) both;
  transform-origin: 50% 52%;
  will-change: opacity, transform;
  backface-visibility: hidden;
}

@keyframes immersive-enter {
  0% {
    opacity: 0;
    transform: translateY(18px) scale(1.018);
  }

  56% {
    opacity: 1;
  }

  100% {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}

@keyframes immersive-leave {
  0% {
    opacity: 1;
    transform: translate3d(0, 0, 0);
  }

  100% {
    opacity: 0;
    transform: translate3d(0, 6px, 0);
  }
}

.immersive-enter-active::before {
  animation: immersive-bg-bloom 0.92s cubic-bezier(0.16, 1, 0.3, 1) both;
}

.immersive-enter-active .immersive-background-canvas {
  animation: immersive-canvas-in 0.86s cubic-bezier(0.16, 1, 0.3, 1) both;
}

.immersive-enter-active .immersive-top-actions {
  animation: fade-in-up 0.46s cubic-bezier(0.16, 1, 0.3, 1) 0.18s both;
}

.immersive-enter-active .immersive-artwork-frame {
  animation: artwork-settle 0.78s cubic-bezier(0.16, 1, 0.3, 1) 0.08s both;
}

.immersive-enter-active .immersive-artwork-reflection {
  animation: reflection-in 0.72s cubic-bezier(0.16, 1, 0.3, 1) 0.28s both;
}

.immersive-enter-active .immersive-title {
  animation: fade-in-up 0.54s cubic-bezier(0.16, 1, 0.3, 1) 0.2s both;
}

.immersive-enter-active .immersive-artist {
  animation: fade-in-up 0.54s cubic-bezier(0.16, 1, 0.3, 1) 0.25s both;
}

.immersive-enter-active .immersive-album {
  animation: fade-in-up 0.54s cubic-bezier(0.16, 1, 0.3, 1) 0.3s both;
}

.immersive-enter-active .immersive-specs {
  animation: fade-in-up 0.54s cubic-bezier(0.16, 1, 0.3, 1) 0.35s both;
}

.immersive-enter-active .immersive-meta-side {
  animation: fade-in-up 0.54s cubic-bezier(0.16, 1, 0.3, 1) 0.38s both;
}

.immersive-enter-active .immersive-progress-section {
  animation: fade-in-up 0.56s cubic-bezier(0.16, 1, 0.3, 1) 0.3s both;
}

.immersive-enter-active .immersive-footer {
  animation: slide-up 0.62s cubic-bezier(0.16, 1, 0.3, 1) 0.2s both;
}

@keyframes fade-in {
  from {
    opacity: 0;
  }

  to {
    opacity: 1;
  }
}

@keyframes fade-in-up {
  from {
    opacity: 0;
    transform: translateY(14px);
  }

  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes scale-in {
  from {
    opacity: 0;
    transform: scale(0.92);
  }

  to {
    opacity: 1;
    transform: scale(1);
  }
}

@keyframes immersive-bg-bloom {
  from {
    opacity: 0;
    transform: scale(1.08);
  }

  to {
    opacity: 0.14;
    transform: scale(1);
  }
}

@keyframes immersive-canvas-in {
  from {
    opacity: 0;
    transform: scale(1.04);
  }

  to {
    opacity: 1;
    transform: scale(1);
  }
}

@keyframes artwork-settle {
  0% {
    opacity: 0;
    transform: translateY(22px) scale(0.9);
    filter: blur(10px);
  }

  58% {
    opacity: 1;
    filter: blur(0);
  }

  100% {
    opacity: 1;
    transform: translateY(0) scale(1);
    filter: blur(0);
  }
}

@keyframes reflection-in {
  from {
    opacity: 0;
    transform: translateY(-8px) scaleY(0.88);
  }

  to {
    opacity: 1;
    transform: translateY(0) scaleY(1);
  }
}

@keyframes slide-up {
  from {
    opacity: 0;
    transform: translateY(20px);
  }

  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@media (prefers-reduced-motion: reduce) {
  .immersive-enter-active,
  .immersive-leave-active {
    animation: none;
  }

  .immersive-enter-active .immersive-top-actions,
  .immersive-enter-active .immersive-background-canvas,
  .immersive-enter-active .immersive-artwork-frame,
  .immersive-enter-active .immersive-artwork-reflection,
  .immersive-enter-active .immersive-title,
  .immersive-enter-active .immersive-artist,
  .immersive-enter-active .immersive-album,
  .immersive-enter-active .immersive-specs,
  .immersive-enter-active .immersive-meta-side,
  .immersive-enter-active .immersive-progress-section,
  .immersive-enter-active .immersive-footer {
    animation: none;
  }
}
</style>
