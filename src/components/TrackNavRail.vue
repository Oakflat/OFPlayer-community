<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, type CSSProperties } from 'vue'
import { useI18n } from '../composables/useI18n'
import { MusicIcon } from './icons/MusicIcon'
import {
  NAV_RAIL_DEFAULT_ROW_HEIGHT_PX,
  buildNavRailMarkers,
  clampPreviewTop,
  navRailGroupScrollTop,
  resolveActiveMarkerIndex,
  resolveNavRailMarkerSymbol,
  resolveNavRailScrollDuration,
  resolveNavRailScrollFrame,
  type NavRailMarker,
  type NavRailPreviewTrack,
} from './trackNavRailModel'

const NAV_RAIL_EDGE_ZONE_PX = 56
const NAV_RAIL_HIDE_DELAY_MS = 400
const NAV_RAIL_PREVIEW_CACHE_LIMIT = 20
const NAV_RAIL_PREVIEW_CARD_HEIGHT_PX = 208

interface TrackNavRailProps {
  totalCount: number
  scrollTop: number
  scrollContainer: HTMLElement | null
  queryIdentity?: string
  rowHeightPx?: number
  loadGroupTracks: (startIndex: number, count: number) => Promise<NavRailPreviewTrack[]>
}

const props = withDefaults(defineProps<TrackNavRailProps>(), {
  queryIdentity: '',
  rowHeightPx: NAV_RAIL_DEFAULT_ROW_HEIGHT_PX,
})

const { t } = useI18n()

const railRef = ref<HTMLElement | null>(null)
const isVisible = ref(false)
const isPinned = ref(false)
const previewMarker = ref<NavRailMarker | null>(null)
const previewTracks = ref<NavRailPreviewTrack[]>([])
const isPreviewLoading = ref(false)
const previewStyle = ref<CSSProperties>({})
const previewCache = new Map<number, NavRailPreviewTrack[]>()
let hideTimerId: ReturnType<typeof setTimeout> | null = null
let previewRequestId = 0
let scrollAnimationFrameId: number | null = null
const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')

const markers = computed(() => buildNavRailMarkers(props.totalCount))
const markerSymbols = computed(() => markers.value.map((_, index) => resolveNavRailMarkerSymbol(index)))
const activeMarkerIndex = computed(() =>
  resolveActiveMarkerIndex(markers.value, props.scrollTop, props.rowHeightPx),
)

watch(
  () => [props.totalCount, props.queryIdentity],
  () => {
    previewCache.clear()
    previewMarker.value = null
  },
)

watch(
  () => props.scrollContainer,
  (container, _previousContainer, onCleanup) => {
    cancelScrollAnimation()

    if (!container) {
      return
    }

    container.addEventListener('wheel', cancelScrollAnimation, { passive: true })
    container.addEventListener('pointerdown', cancelScrollAnimation)
    onCleanup(() => {
      container.removeEventListener('wheel', cancelScrollAnimation)
      container.removeEventListener('pointerdown', cancelScrollAnimation)
    })
  },
  { immediate: true },
)

function cancelHideTimer() {
  if (hideTimerId !== null) {
    clearTimeout(hideTimerId)
    hideTimerId = null
  }
}

function showRail() {
  cancelHideTimer()
  isVisible.value = true
}

function closePreview() {
  previewRequestId += 1
  previewMarker.value = null
  previewTracks.value = []
  isPreviewLoading.value = false
}

function scheduleHide() {
  if (hideTimerId !== null || !isVisible.value || isPinned.value) {
    return
  }

  hideTimerId = setTimeout(() => {
    hideTimerId = null

    if (!isPinned.value) {
      isVisible.value = false
      closePreview()
    }
  }, NAV_RAIL_HIDE_DELAY_MS)
}

function pinRail() {
  isPinned.value = true
  showRail()
}

function unpinRail() {
  isPinned.value = false
  scheduleHide()
}

function isPointerNearRailEdge(clientX: number, clientY: number) {
  const container = props.scrollContainer

  if (!container) {
    return false
  }

  const rect = container.getBoundingClientRect()

  return (
    clientX >= rect.left &&
    clientX <= rect.left + NAV_RAIL_EDGE_ZONE_PX &&
    clientY >= rect.top &&
    clientY <= rect.bottom
  )
}

function handleWindowPointerMove(event: PointerEvent) {
  if (isPinned.value) {
    return
  }

  if (isPointerNearRailEdge(event.clientX, event.clientY)) {
    showRail()
  } else {
    scheduleHide()
  }
}

function prefersReducedMotion() {
  return document.documentElement.dataset.motion === 'reduced' || reducedMotionQuery.matches
}

function cancelScrollAnimation() {
  if (scrollAnimationFrameId !== null) {
    cancelAnimationFrame(scrollAnimationFrameId)
    scrollAnimationFrameId = null
  }
}

function scrollToMarker(marker: NavRailMarker) {
  const container = props.scrollContainer

  if (!container) {
    return
  }

  cancelScrollAnimation()

  const targetTop = Math.min(
    navRailGroupScrollTop(marker, props.rowHeightPx),
    Math.max(0, container.scrollHeight - container.clientHeight),
  )
  const startTop = container.scrollTop
  const distance = Math.abs(targetTop - startTop)

  if (prefersReducedMotion() || distance < 2) {
    container.scrollTo({ top: targetTop, behavior: 'instant' })
    return
  }

  const duration = resolveNavRailScrollDuration(distance)
  const startedAt = performance.now()
  const step = (timestamp: number) => {
    // A preference change during a jump should also take effect immediately.
    if (prefersReducedMotion()) {
      container.scrollTo({ top: targetTop, behavior: 'instant' })
      scrollAnimationFrameId = null
      return
    }

    const frame = resolveNavRailScrollFrame(startTop, targetTop, timestamp - startedAt, duration)

    container.scrollTo({ top: frame.scrollTop, behavior: 'instant' })

    if (frame.done) {
      scrollAnimationFrameId = null
      return
    }

    scrollAnimationFrameId = requestAnimationFrame(step)
  }

  scrollAnimationFrameId = requestAnimationFrame(step)
}

function positionPreview(anchor: HTMLElement) {
  const anchorRect = anchor.getBoundingClientRect()
  const railRect = railRef.value?.getBoundingClientRect()
  const left = (railRect?.right ?? anchorRect.right) + 10
  const top = clampPreviewTop(
    anchorRect.top - 8,
    NAV_RAIL_PREVIEW_CARD_HEIGHT_PX,
    window.innerHeight,
  )

  previewStyle.value = {
    left: `${left}px`,
    top: `${top}px`,
  }
}

function cachePreviewTracks(groupIndex: number, tracks: NavRailPreviewTrack[]) {
  previewCache.set(groupIndex, tracks)

  if (previewCache.size > NAV_RAIL_PREVIEW_CACHE_LIMIT) {
    const oldestKey = previewCache.keys().next().value

    if (oldestKey !== undefined) {
      previewCache.delete(oldestKey)
    }
  }
}

async function openPreview(marker: NavRailMarker, anchor: HTMLElement) {
  previewRequestId += 1
  const requestId = previewRequestId

  previewMarker.value = marker
  positionPreview(anchor)

  const cached = previewCache.get(marker.groupIndex)

  if (cached) {
    previewTracks.value = cached
    isPreviewLoading.value = false
    return
  }

  isPreviewLoading.value = true
  previewTracks.value = []

  try {
    const tracks = await props.loadGroupTracks(
      marker.startTrack - 1,
      marker.endTrack - marker.startTrack + 1,
    )

    if (requestId !== previewRequestId) {
      return
    }

    cachePreviewTracks(marker.groupIndex, tracks)
    previewTracks.value = tracks
  } catch {
    if (requestId === previewRequestId) {
      previewTracks.value = []
    }
  } finally {
    if (requestId === previewRequestId) {
      isPreviewLoading.value = false
    }
  }
}

function handleMarkerEnter(marker: NavRailMarker, event: Event) {
  const anchor = event.currentTarget

  if (!(anchor instanceof HTMLElement)) {
    return
  }

  pinRail()
  void openPreview(marker, anchor)
}

onMounted(() => {
  window.addEventListener('pointermove', handleWindowPointerMove, { passive: true })
})

onBeforeUnmount(() => {
  window.removeEventListener('pointermove', handleWindowPointerMove)
  cancelHideTimer()
  cancelScrollAnimation()
})
</script>

<template>
  <Transition name="track-nav-rail-fade">
    <nav
      v-if="isVisible && markers.length > 0"
      ref="railRef"
      class="track-nav-rail"
      role="navigation"
      :aria-label="t('player.navRail.aria')"
      @pointerenter="pinRail"
      @pointerleave="unpinRail"
      @focusin="pinRail"
      @focusout="unpinRail"
    >
      <button
        v-for="(marker, index) in markers"
        :key="marker.key"
        type="button"
        class="track-nav-rail-marker"
        :class="{
          'is-active': index === activeMarkerIndex,
          'is-previewed': previewMarker?.key === marker.key,
        }"
        :aria-label="t('player.navRail.marker', { start: marker.startTrack, end: marker.endTrack })"
        @click="scrollToMarker(marker)"
        @pointerenter="handleMarkerEnter(marker, $event)"
        @focus="handleMarkerEnter(marker, $event)"
      >
        <MusicIcon
          class="track-nav-rail-note"
          :name="markerSymbols[index]?.icon ?? 'eighth-note'"
          aria-hidden="true"
        />
      </button>
    </nav>
  </Transition>
  <Teleport to="body">
    <Transition name="track-nav-rail-preview-pop">
      <div
        v-if="isVisible && previewMarker"
        class="track-nav-rail-preview"
        :style="previewStyle"
        role="tooltip"
        @pointerenter="pinRail"
        @pointerleave="unpinRail"
      >
        <p class="track-nav-rail-preview-range">
          #{{ previewMarker.startTrack }}–{{ previewMarker.endTrack }}
        </p>
        <ul v-if="previewTracks.length > 0" class="track-nav-rail-preview-list">
          <li v-for="track in previewTracks" :key="track.id" class="track-nav-rail-preview-row">
            <img
              v-if="track.artwork"
              class="track-nav-rail-preview-artwork"
              :src="track.artwork"
              alt=""
              loading="lazy"
            />
            <span v-else class="track-nav-rail-preview-monogram" aria-hidden="true">
              {{ track.monogram }}
            </span>
            <span class="track-nav-rail-preview-title">{{ track.title }}</span>
          </li>
        </ul>
        <p v-else class="track-nav-rail-preview-empty">
          {{ isPreviewLoading ? '…' : t('player.navRail.previewEmpty') }}
        </p>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.track-nav-rail {
  position: absolute;
  left: 6px;
  top: 50%;
  transform: translateY(-50%);
  z-index: 36;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  max-height: calc(100% - 24px);
  padding: 8px 4px;
  overflow: hidden;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-lg);
  background: var(--surface-solid);
  box-shadow: var(--shadow-md);
  backdrop-filter: blur(18px);
}

.track-nav-rail-marker {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: none;
  border-radius: var(--radius-md);
  background: transparent;
  color: var(--ink-subtle);
  cursor: pointer;
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast),
    transform var(--duration-md) var(--ease-emphasized);
}

.track-nav-rail-marker:hover,
.track-nav-rail-marker:focus-visible,
.track-nav-rail-marker.is-previewed {
  color: var(--accent);
  background: var(--accent-soft);
}

.track-nav-rail-note {
  display: block;
  width: 18px;
  height: 18px;
  flex: none;
  user-select: none;
}

.track-nav-rail-marker.is-active {
  color: var(--accent);
  transform: scale(1.08);
}

.track-nav-rail-fade-enter-active {
  transition:
    opacity var(--motion-duration-popover) var(--ease-standard),
    transform var(--motion-duration-popover) var(--enter-ease);
}

.track-nav-rail-fade-leave-active {
  transition:
    opacity var(--motion-duration-dismiss) var(--ease-standard),
    transform var(--motion-duration-dismiss) var(--ease-emphasized-accelerate);
}

.track-nav-rail-fade-enter-from {
  opacity: 0;
  transform: translateY(-50%) translateX(-8px);
}

.track-nav-rail-fade-leave-to {
  opacity: 0;
  transform: translateY(-50%) translateX(-4px);
}

.track-nav-rail-preview {
  position: fixed;
  z-index: 64;
  width: 236px;
  padding: 10px;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-lg);
  background: var(--surface-solid);
  box-shadow: var(--shadow-md);
  backdrop-filter: blur(18px);
  transition:
    top var(--duration-md) var(--ease-emphasized),
    left var(--duration-md) var(--ease-emphasized);
}

.track-nav-rail-preview-pop-enter-active {
  transition:
    opacity var(--motion-duration-popover) var(--ease-standard),
    transform var(--motion-duration-popover) var(--enter-ease);
}

.track-nav-rail-preview-pop-leave-active {
  transition:
    opacity var(--motion-duration-dismiss) var(--ease-standard),
    transform var(--motion-duration-dismiss) var(--ease-emphasized-accelerate);
}

.track-nav-rail-preview-pop-enter-from {
  opacity: 0;
  transform: translateY(6px) scale(0.97);
}

.track-nav-rail-preview-pop-leave-to {
  opacity: 0;
  transform: translateY(3px) scale(0.98);
}

.track-nav-rail-preview-range {
  margin: 0 0 8px;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--ink-subtle);
}

.track-nav-rail-preview-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  animation: track-nav-rail-preview-content-in var(--duration-sm) var(--enter-ease);
}

@keyframes track-nav-rail-preview-content-in {
  from {
    opacity: 0;
    transform: translateY(3px);
  }

  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.track-nav-rail-preview-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.track-nav-rail-preview-artwork,
.track-nav-rail-preview-monogram {
  flex: none;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  object-fit: cover;
}

.track-nav-rail-preview-monogram {
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
}

.track-nav-rail-preview-title {
  min-width: 0;
  overflow: hidden;
  font-size: 12px;
  color: var(--ink);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.track-nav-rail-preview-empty {
  margin: 0;
  font-size: 12px;
  color: var(--ink-subtle);
  animation: track-nav-rail-preview-content-in var(--duration-sm) var(--enter-ease);
}

@media (prefers-reduced-motion: reduce) {
  .track-nav-rail,
  .track-nav-rail-marker,
  .track-nav-rail-preview,
  .track-nav-rail-preview-list,
  .track-nav-rail-preview-empty,
  .track-nav-rail-fade-enter-active,
  .track-nav-rail-fade-leave-active,
  .track-nav-rail-preview-pop-enter-active,
  .track-nav-rail-preview-pop-leave-active {
    transition: none !important;
    animation: none !important;
  }
}
</style>
