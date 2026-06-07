<script setup lang="ts">
import { ref, computed, watch, nextTick, onBeforeUnmount, onMounted, type ComponentPublicInstance } from 'vue'
import {
  findActiveLyricPlayerLineIndex,
  findUpcomingLyricPlayerLineIndex,
  type LyricPlayerLineModel,
} from '../models/lyrics'
import {
  LYRIC_SCROLL_RESUME_DELAY_MS,
  clampLyricScrollTarget,
  normalizeLyricWheelDelta,
  resolveLyricFocusIndex,
  resolveLyricLineDistance,
  resolveLyricScrollStep,
  resolveLyricStageStyle,
  resolveLyricScrollTarget,
} from './lyricViewportEngine'

interface LyricsPlayerViewProps {
  lyricLines?: LyricPlayerLineModel[]
  currentTime?: number
  playing?: boolean
  alignPosition?: number
  enableBlur?: boolean
}

const props = withDefaults(defineProps<LyricsPlayerViewProps>(), {
  lyricLines: () => [],
  currentTime: 0,
  playing: false,
  alignPosition: 0.4,
  enableBlur: true,
})

const emit = defineEmits<{
  'line-click': [payload: { startTime: number }]
}>()

const containerRef = ref<HTMLElement | null>(null)
const stageRef = ref<HTMLElement | null>(null)
const lineRefs = ref<Array<HTMLElement | undefined>>([])
const hoveredIdx = ref(-1)

function setLineRef(el: Element | ComponentPublicInstance | null, idx: number) {
  if (el instanceof HTMLElement) {
    lineRefs.value[idx] = el
  }
}

const activeIndex = computed(() => {
  return findActiveLyricPlayerLineIndex(props.lyricLines, props.currentTime)
})
const pendingIndex = computed(() => {
  if (activeIndex.value >= 0) {
    return -1
  }

  return findUpcomingLyricPlayerLineIndex(props.lyricLines, props.currentTime)
})
const focusIndex = computed(() => resolveLyricFocusIndex(activeIndex.value, pendingIndex.value))
const viewportOffset = ref(0)
const stageStyle = computed(() => resolveLyricStageStyle(viewportOffset.value))
const rootStyle = computed(() => {
  const alignPercent = Math.min(Math.max(props.alignPosition, 0), 1) * 100

  return {
    '--lp-align-position': `${alignPercent}%`,
    '--lp-counter-align-position': `${100 - alignPercent}%`,
  }
})

let rafId: number | null = null
let viewportGoal = 0
let lastScrollFrameAt = 0
let hasSyncedInitialLine = false

// ── CN: 用户手动滚动检测 ─── EN: User manual scroll detection ─────────────────
let userScrolling = false
let resumeTimer: number | null = null

function clearResumeTimer() {
  if (resumeTimer !== null) {
    window.clearTimeout(resumeTimer)
    resumeTimer = null
  }
}

function markUserScrolling() {
  userScrolling = true
  if (rafId) {
    window.cancelAnimationFrame(rafId)
    rafId = null
  }
  viewportGoal = viewportOffset.value
  clearResumeTimer()
  resumeTimer = window.setTimeout(() => {
    resumeTimer = null
    userScrolling = false
    if (props.playing) {
      void syncFocusLine({ force: true })
    }
  }, LYRIC_SCROLL_RESUME_DELAY_MS)
}

function clampViewportTarget(target: number) {
  const container = containerRef.value
  const stage = stageRef.value

  if (!container || !stage) {
    return Math.max(0, target)
  }

  return clampLyricScrollTarget({
    clientHeight: container.clientHeight,
    scrollHeight: stage.scrollHeight,
  }, target)
}

function moveViewportTo(target: number) {
  viewportGoal = clampViewportTarget(target)
  viewportOffset.value = viewportGoal
  lastScrollFrameAt = 0
}

function onContainerWheel(event: WheelEvent) {
  event.preventDefault()
  markUserScrolling()
  moveViewportTo(viewportOffset.value + normalizeLyricWheelDelta(
    event.deltaY,
    event.deltaMode,
    containerRef.value?.clientHeight ?? 0,
  ))
}
function onContainerTouchMove() { markUserScrolling() }

function runScroll(timestamp: number) {
  if (!containerRef.value || !stageRef.value) {
    rafId = null
    return
  }

  const nextFrame = resolveLyricScrollStep({
    currentScrollTop: viewportOffset.value,
    goal: viewportGoal,
    lastFrameAt: lastScrollFrameAt,
    playing: props.playing,
    timestamp,
  })
  lastScrollFrameAt = nextFrame.lastFrameAt
  viewportOffset.value = nextFrame.scrollTop

  if (nextFrame.done) {
    rafId = null
    return
  }

  rafId = window.requestAnimationFrame(runScroll)
}

function scrollToGoal(target: number, { immediate = false }: { immediate?: boolean } = {}) {
  viewportGoal = clampViewportTarget(target)
  lastScrollFrameAt = 0

  if (immediate) {
    if (rafId) {
      window.cancelAnimationFrame(rafId)
      rafId = null
    }
    viewportOffset.value = viewportGoal
    return
  }

  if (!rafId) {
    rafId = window.requestAnimationFrame(runScroll)
  }
}

async function syncFocusLine({ force = false, immediate = false }: { force?: boolean; immediate?: boolean } = {}) {
  const idx = focusIndex.value

  if (idx < 0 || !containerRef.value) {
    return
  }

  if (userScrolling && !force) {
    return
  }

  await nextTick()
  const container = containerRef.value
  const line = lineRefs.value[idx]

  if (!container || !line) {
    return
  }

  const target = resolveLyricScrollTarget({
    alignPosition: props.alignPosition,
    containerHeight: container.clientHeight,
    lineHeight: line.offsetHeight,
    lineOffsetTop: line.offsetTop,
  })

  scrollToGoal(target, { immediate })
  hasSyncedInitialLine = true
}

watch(focusIndex, (idx, previousIdx) => {
  if (idx < 0) {
    return
  }

  void syncFocusLine({ immediate: !hasSyncedInitialLine || previousIdx < 0 })
}, { flush: 'post' })

watch(
  () => props.playing,
  (playing) => {
    if (playing) {
      void syncFocusLine({ force: true })
    }
  },
)

onMounted(() => {
  const el = containerRef.value
  if (!el) return
  el.addEventListener('wheel', onContainerWheel, { passive: false })
  el.addEventListener('touchmove', onContainerTouchMove, { passive: true })
  void syncFocusLine({ force: true, immediate: true })
})

onBeforeUnmount(() => {
  if (rafId) {
    window.cancelAnimationFrame(rafId)
  }
  clearResumeTimer()
  const el = containerRef.value
  if (!el) return
  el.removeEventListener('wheel', onContainerWheel)
  el.removeEventListener('touchmove', onContainerTouchMove)
})

function onLineEnter(idx: number) {
  hoveredIdx.value = idx
}

function onLineLeave() {
  hoveredIdx.value = -1
}

function dist(idx: number) {
  return resolveLyricLineDistance(idx, focusIndex.value)
}

function handleLineClick(line: LyricPlayerLineModel) {
  emit('line-click', { startTime: line.startTime })
}
</script>

<template>
  <div
    ref="containerRef"
    class="lp-root"
    :class="{ 'lp--blur': enableBlur, 'lp--has-hover': hoveredIdx >= 0, 'lp--playing': playing }"
    :style="rootStyle"
  >
    <div
      ref="stageRef"
      class="lp-stage"
      :style="stageStyle"
    >
      <div class="lp-pad lp-pad--top" aria-hidden="true" />

      <div
        v-for="(line, idx) in lyricLines"
        :key="idx"
        :ref="(el) => setLineRef(el, idx)"
        class="lp-line"
        :class="{
          'is-active': activeIndex >= 0 && dist(idx) === 0,
          'is-pending': activeIndex < 0 && dist(idx) === 0,
          'is-near-1': Math.abs(dist(idx)) === 1,
          'is-near-2': Math.abs(dist(idx)) === 2,
          'is-past': dist(idx) < 0,
          'is-future': dist(idx) > 0,
          'is-bg': line.isBG,
          'is-duet': line.isDuet,
          'is-bilingual': line.isBilingual,
          'has-translation': Boolean(line.translatedLyric),
          'has-roman': Boolean(line.romanLyric),
          'is-hovered': hoveredIdx === idx,
        }"
        @click="handleLineClick(line)"
        @mouseenter="onLineEnter(idx)"
        @mouseleave="onLineLeave"
      >
        <p class="lp-text" :class="{ 'lp-text--bg': line.isBG }">
          <span v-for="(word, wordIndex) in line.words" :key="wordIndex" class="lp-word">{{ word.word }}</span>
        </p>
        <p v-if="line.translatedLyric" class="lp-sub lp-sub--translation">
          <span v-for="(item, itemIndex) in line.translatedLyric.split('\n')" :key="`translation-${itemIndex}`">{{ item }}</span>
        </p>
        <p v-if="line.romanLyric" class="lp-sub lp-sub--roman">
          <span v-for="(item, itemIndex) in line.romanLyric.split('\n')" :key="`roman-${itemIndex}`">{{ item }}</span>
        </p>
      </div>

      <div class="lp-pad lp-pad--bottom" aria-hidden="true" />
    </div>
  </div>
</template>

<style scoped>
.lp-root {
  --lp-align-position: 40%;
  --lp-counter-align-position: 60%;
  position: absolute;
  inset: 0;
  overflow: hidden;
  overflow-x: hidden;
  scrollbar-width: none;
  -ms-overflow-style: none;
  user-select: none;
  -webkit-user-select: none;
  mask-image: linear-gradient(
    180deg,
    transparent 0%,
    rgba(0, 0, 0, 0.05) 4%,
    rgba(0, 0, 0, 0.34) 11%,
    rgba(0, 0, 0, 0.78) 20%,
    #000 30%,
    #000 70%,
    rgba(0, 0, 0, 0.78) 80%,
    rgba(0, 0, 0, 0.34) 89%,
    rgba(0, 0, 0, 0.05) 96%,
    transparent 100%
  );
  -webkit-mask-image: linear-gradient(
    180deg,
    transparent 0%,
    rgba(0, 0, 0, 0.05) 4%,
    rgba(0, 0, 0, 0.34) 11%,
    rgba(0, 0, 0, 0.78) 20%,
    #000 30%,
    #000 70%,
    rgba(0, 0, 0, 0.78) 80%,
    rgba(0, 0, 0, 0.34) 89%,
    rgba(0, 0, 0, 0.05) 96%,
    transparent 100%
  );
}

.lp-root::-webkit-scrollbar {
  display: none;
}

.lp-stage {
  height: 100%;
  min-height: 100%;
  will-change: transform;
}

.lp-pad--top {
  height: var(--lp-align-position);
}

.lp-pad--bottom {
  height: var(--lp-counter-align-position);
}

.lp-line {
  padding: 0.72rem 2rem;
  cursor: pointer;
  border-radius: 0.5rem;
  transform: translate3d(0, 0, 0) scaleX(0.985);
  transform-origin: left center;
  will-change: opacity, filter, transform;
  transition:
    opacity 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    filter 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    transform 0.45s cubic-bezier(0.22, 1, 0.36, 1);
  opacity: 0.14;
}

.lp-line.is-past {
  opacity: 0.24;
  transform: translate3d(-0.2rem, 0, 0) scaleX(0.97);
}

.lp-line.is-near-2 {
  opacity: 0.38;
  transform: translate3d(-0.06rem, 0, 0) scaleX(0.98);
}

.lp-line.is-near-1 {
  opacity: 0.6;
  transform: translate3d(0.1rem, 0, 0) scaleX(0.992);
}

.lp-line.is-active {
  opacity: 1;
  transform: translate3d(0.3rem, 0, 0) scaleX(1);
}

.lp-line.is-pending {
  opacity: 0.78;
  transform: translate3d(0.22rem, 0, 0) scaleX(0.998);
}

.lp-line.is-bg.is-active {
  opacity: 0.72;
}

.lp-line.is-duet {
  text-align: right;
  transform-origin: right center;
}

.lp--blur .lp-line {
  filter: blur(4.5px);
}

.lp--blur .lp-line.is-past {
  filter: blur(3.6px);
}

.lp--blur .lp-line.is-near-2 {
  filter: blur(2.2px);
}

.lp--blur .lp-line.is-near-1 {
  filter: blur(0.45px);
}

.lp--blur .lp-line.is-active,
.lp--blur .lp-line.is-pending {
  filter: none;
}

.lp--has-hover .lp-line {
  opacity: 0.12 !important;
  filter: blur(4px) !important;
}

.lp--has-hover .lp-line.is-hovered {
  opacity: 0.92 !important;
  filter: none !important;
}

.lp-text {
  margin: 0;
  font-size: clamp(1.7rem, 2.8vw, 2.25rem);
  font-weight: 700;
  line-height: 1.3;
  letter-spacing: 0.008em;
  color: rgba(255, 255, 255, 0.9);
  text-wrap: balance;
  transition:
    color 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    opacity 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    text-shadow 0.45s cubic-bezier(0.22, 1, 0.36, 1);
}

.lp-line.is-bilingual .lp-text {
  line-height: 1.18;
}

.lp-text--bg {
  font-size: clamp(1.15rem, 1.9vw, 1.5rem);
  font-weight: 600;
  letter-spacing: 0.015em;
}

.lp-word {
  display: inline;
}

.lp-line.is-past .lp-text {
  color: rgba(255, 255, 255, 0.78);
}

.lp-sub {
  display: grid;
  gap: 0.1rem;
  margin: 0.42rem 0 0;
  line-height: 1.45;
  color: rgba(255, 255, 255, 0.4);
  opacity: 0.58;
  transform: translateY(-0.08rem);
  transition:
    color 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    opacity 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    transform 0.45s cubic-bezier(0.22, 1, 0.36, 1),
    filter 0.45s cubic-bezier(0.22, 1, 0.36, 1);
}

.lp-sub--translation {
  font-size: clamp(0.875rem, 1.4vw, 1.05rem);
  font-weight: 400;
}

.lp-sub--roman {
  font-size: clamp(0.8rem, 1.2vw, 0.95rem);
  font-weight: 300;
  font-style: italic;
}

.lp-line.is-active .lp-text {
  color: rgba(255, 255, 255, 0.98);
  text-shadow: 0 0 36px rgba(255, 255, 255, 0.14);
}

.lp-line.is-pending .lp-text {
  color: rgba(255, 255, 255, 0.88);
  text-shadow: 0 0 28px rgba(255, 255, 255, 0.1);
}

.lp-line.is-active .lp-sub {
  opacity: 0.86;
  transform: translateY(0);
  filter: none;
  color: rgba(255, 255, 255, 0.74);
}

.lp-line.is-pending .lp-sub {
  opacity: 0.68;
  transform: translateY(0);
  filter: none;
  color: rgba(255, 255, 255, 0.62);
}

.lp-line.is-active .lp-sub--translation {
  font-weight: 520;
}

.lp-line.is-near-1 .lp-sub {
  opacity: 0.42;
  filter: blur(0.2px);
}

.lp-line.is-near-2 .lp-sub,
.lp-line.is-past .lp-sub,
.lp-line.is-future .lp-sub {
  opacity: 0.24;
  filter: blur(0.8px);
}

.lp--playing .lp-line.is-active .lp-text,
.lp--playing .lp-line.is-active .lp-sub {
  animation: lp-active-breathe 1.8s ease-in-out infinite alternate;
}

@keyframes lp-active-breathe {
  from {
    text-shadow: 0 0 28px rgba(255, 255, 255, 0.1);
  }
  to {
    text-shadow: 0 0 44px rgba(255, 255, 255, 0.18);
  }
}
</style>
