<script setup lang="ts">
import { nextTick, ref, watch, type ComponentPublicInstance } from 'vue'
import { useI18n } from '../composables/useI18n'

interface LyricsPanelLine {
  text?: string
  translatedLyric?: string
  startTime?: number
}

interface LyricsPanelProps {
  lines?: LyricsPanelLine[]
  activeIndex?: number
  hasLyrics?: boolean
  hasTimestamps?: boolean
  isLoading?: boolean
}

const props = withDefaults(defineProps<LyricsPanelProps>(), {
  lines: () => [],
  activeIndex: -1,
  hasLyrics: false,
  hasTimestamps: false,
  isLoading: false,
})

const emit = defineEmits<{
  seek: [seconds: number]
}>()
const { t } = useI18n()
const lineRefs = ref<Array<HTMLElement | undefined>>([])

function setLineRef(element: Element | ComponentPublicInstance | null, index: number) {
  if (element instanceof HTMLElement) {
    lineRefs.value[index] = element
  }
}

function resolveDistanceClass(index: number) {
  if (props.activeIndex < 0 || !props.hasTimestamps) {
    return 'is-resting'
  }

  const distance = Math.abs(index - props.activeIndex)

  if (distance === 0) return 'is-active'
  if (distance === 1) return 'is-near'
  if (distance <= 3) return 'is-mid'
  return 'is-far'
}

function handleLineClick(line: LyricsPanelLine) {
  const startTime = line.startTime

  if (!props.hasTimestamps || typeof startTime !== 'number' || !Number.isFinite(startTime)) {
    return
  }

  emit('seek', startTime)
}

watch(
  () => props.activeIndex,
  async (index) => {
    if (!props.hasTimestamps || index < 0) {
      return
    }

    await nextTick()
    const element = lineRefs.value[index]

    if (element) {
      element.scrollIntoView({
        behavior: 'smooth',
        block: 'center',
        inline: 'nearest',
      })
    }
  },
  { flush: 'post' },
)
</script>

<template>
  <section class="lyrics-panel" :class="{ 'is-loading': isLoading }">
    <div v-if="!hasLyrics && !isLoading" class="lyrics-state lyrics-state--empty" aria-live="polite">
      <p class="lyrics-empty-title">{{ t('player.lyricsEmptyTitle') }}</p>
      <p class="lyrics-empty-copy">{{ t('player.lyricsEmptyCopy') }}</p>
    </div>

    <ol v-else-if="hasLyrics" class="lyrics-list" role="list">
      <li
        v-for="(line, index) in lines"
        :key="`${index}-${line.text}`"
        :ref="(element) => setLineRef(element, index)"
        class="lyrics-line"
        :class="[
          resolveDistanceClass(index),
          {
            'is-clickable': hasTimestamps && Number.isFinite(line?.startTime),
            'is-past': hasTimestamps && activeIndex >= 0 && index < activeIndex,
          },
        ]"
        @click="handleLineClick(line)"
      >
        <span class="lyrics-line-text">{{ line.text || ' ' }}</span>
        <span v-if="line.translatedLyric" class="lyrics-line-sub">
          <span v-for="(item, itemIndex) in line.translatedLyric.split('\n')" :key="itemIndex">{{ item }}</span>
        </span>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.lyrics-panel {
  position: relative;
  min-height: clamp(380px, 58vh, 760px);
  height: 100%;
  overflow-y: auto;
  overflow-x: hidden;
  scrollbar-width: none;
  -ms-overflow-style: none;
  scroll-behavior: smooth;
  scroll-padding-block: 42%;
  mask-image: linear-gradient(
    180deg,
    transparent 0%,
    rgba(0, 0, 0, 0.14) 8%,
    rgba(0, 0, 0, 0.85) 18%,
    #000 30%,
    #000 72%,
    rgba(0, 0, 0, 0.86) 84%,
    rgba(0, 0, 0, 0.12) 94%,
    transparent 100%
  );
  -webkit-mask-image: linear-gradient(
    180deg,
    transparent 0%,
    rgba(0, 0, 0, 0.14) 8%,
    rgba(0, 0, 0, 0.85) 18%,
    #000 30%,
    #000 72%,
    rgba(0, 0, 0, 0.86) 84%,
    rgba(0, 0, 0, 0.12) 94%,
    transparent 100%
  );
}

.lyrics-panel::-webkit-scrollbar {
  display: none;
}

.lyrics-list {
  list-style: none;
  width: min(100%, 44rem);
  margin: 0 auto;
  padding: 37% 0 40%;
}

.lyrics-line {
  padding: 0.6rem 0;
  user-select: none;
  transform-origin: left center;
  transition: opacity 220ms ease, filter 220ms ease, transform 220ms ease;
}

.lyrics-line.is-clickable {
  cursor: pointer;
}

.lyrics-line-text {
  display: block;
  font-size: clamp(1.24rem, 1.52vw, 1.72rem);
  font-weight: 600;
  line-height: 1.48;
  letter-spacing: -0.022em;
  color: rgba(255, 255, 255, 0.2);
  transition:
    color 220ms ease,
    transform 220ms ease,
    filter 220ms ease,
    opacity 220ms ease,
    text-shadow 220ms ease;
}

.lyrics-line-sub {
  display: grid;
  gap: 0.08rem;
  margin-top: 0.16rem;
  font-size: clamp(0.92rem, 1.08vw, 1.08rem);
  font-weight: 500;
  line-height: 1.42;
  color: rgba(255, 255, 255, 0.24);
  transition:
    color 220ms ease,
    opacity 220ms ease,
    transform 220ms ease;
}

.lyrics-line.is-active .lyrics-line-text {
  color: rgba(255, 255, 255, 0.985);
  transform: translateX(0.2rem) scale(1.015);
  text-shadow: 0 0 34px rgba(255, 255, 255, 0.12);
}

.lyrics-line.is-active .lyrics-line-sub {
  color: rgba(255, 255, 255, 0.7);
  transform: translateX(0.2rem);
}

.lyrics-line.is-near .lyrics-line-sub {
  color: rgba(255, 255, 255, 0.46);
}

.lyrics-line.is-mid .lyrics-line-sub,
.lyrics-line.is-far .lyrics-line-sub,
.lyrics-line.is-past:not(.is-active) .lyrics-line-sub {
  color: rgba(255, 255, 255, 0.18);
}

.lyrics-line.is-near .lyrics-line-text {
  color: rgba(255, 255, 255, 0.68);
  filter: blur(0.3px);
  transform: scale(0.988);
}

.lyrics-line.is-mid .lyrics-line-text {
  color: rgba(255, 255, 255, 0.36);
  filter: blur(1.1px);
  transform: scale(0.965);
}

.lyrics-line.is-far .lyrics-line-text {
  color: rgba(255, 255, 255, 0.12);
  filter: blur(4px);
  opacity: 0.34;
  transform: scale(0.93);
}

.lyrics-line.is-resting .lyrics-line-text {
  color: rgba(255, 255, 255, 0.34);
}

.lyrics-line.is-past:not(.is-active) .lyrics-line-text {
  color: rgba(255, 255, 255, 0.16);
}

.lyrics-line.is-clickable:hover .lyrics-line-text {
  color: rgba(255, 255, 255, 0.78);
}

.lyrics-state {
  position: absolute;
  inset: 0;
  z-index: 2;
  display: grid;
  place-items: center;
  padding: 2rem;
  text-align: center;
  background: transparent;
}

.lyrics-state--empty {
  gap: 0.65rem;
}

.lyrics-empty-title {
  margin: 0;
  font-size: 1.02rem;
  font-weight: 620;
  color: rgba(255, 255, 255, 0.62);
}

.lyrics-empty-copy {
  margin: 0;
  max-width: 25rem;
  font-size: 0.92rem;
  line-height: 1.72;
  color: rgba(255, 255, 255, 0.28);
}

@media (max-width: 980px) {
  .lyrics-panel {
    min-height: 16rem;
    scroll-padding-block: 34%;
  }

  .lyrics-list {
    width: 100%;
    padding: 28% 0 32%;
  }

  .lyrics-line-text {
    font-size: 1.12rem;
  }
}

@media (prefers-reduced-motion: reduce) {
  .lyrics-panel {
    scroll-behavior: auto;
  }

  .lyrics-line,
  .lyrics-line-text {
    transition: none;
  }
}
</style>
