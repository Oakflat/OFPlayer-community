export const LYRIC_SCROLL_RESUME_DELAY_MS = 2000
export const LYRIC_PLAYING_SCROLL_TIME_CONSTANT_MS = 92
export const LYRIC_IDLE_SCROLL_TIME_CONSTANT_MS = 150
export const LYRIC_MAX_SCROLL_FRAME_DELTA_MS = 50
export const LYRIC_SCROLL_SETTLE_THRESHOLD_PX = 0.8
export const LYRIC_NO_ACTIVE_DISTANCE = 99
export const LYRIC_WHEEL_LINE_DELTA_PX = 40

interface LyricScrollTargetInput {
  alignPosition: number
  containerHeight: number
  lineHeight: number
  lineOffsetTop: number
}

interface LyricScrollClampInput {
  clientHeight: number
  scrollHeight: number
}

interface LyricScrollStepInput {
  currentScrollTop: number
  goal: number
  lastFrameAt: number
  playing: boolean
  timestamp: number
}

function clampUnit(value: number, fallback = 0.4) {
  if (!Number.isFinite(value)) {
    return fallback
  }

  return Math.min(Math.max(value, 0), 1)
}

function lerp(left: number, right: number, t: number) {
  return left + (right - left) * t
}

export function resolveLyricStageStyle(offset: number) {
  const safeOffset = Number.isFinite(offset) ? offset : 0

  return {
    transform: `translate3d(0, ${-safeOffset}px, 0)`,
  }
}

export function resolveLyricLineDistance(lineIndex: number, activeIndex: number) {
  if (!Number.isInteger(lineIndex) || activeIndex < 0) {
    return LYRIC_NO_ACTIVE_DISTANCE
  }

  return lineIndex - activeIndex
}

export function resolveLyricFocusIndex(activeIndex: number, pendingIndex: number) {
  if (Number.isInteger(activeIndex) && activeIndex >= 0) {
    return activeIndex
  }

  if (Number.isInteger(pendingIndex) && pendingIndex >= 0) {
    return pendingIndex
  }

  return -1
}

export function resolveLyricScrollTarget({
  alignPosition,
  containerHeight,
  lineHeight,
  lineOffsetTop,
}: LyricScrollTargetInput) {
  if (
    !Number.isFinite(containerHeight) ||
    !Number.isFinite(lineHeight) ||
    !Number.isFinite(lineOffsetTop)
  ) {
    return 0
  }

  return lineOffsetTop -
    containerHeight * clampUnit(alignPosition) +
    lineHeight / 2
}

export function clampLyricScrollTarget(
  { clientHeight, scrollHeight }: LyricScrollClampInput,
  target: number,
) {
  if (!Number.isFinite(target)) {
    return 0
  }

  const maxScrollTop = Math.max(0, scrollHeight - clientHeight)
  return Math.min(Math.max(0, target), maxScrollTop)
}

export function normalizeLyricWheelDelta(
  deltaY: number,
  deltaMode: number,
  viewportHeight: number,
) {
  if (!Number.isFinite(deltaY)) {
    return 0
  }

  if (deltaMode === 1) {
    return deltaY * LYRIC_WHEEL_LINE_DELTA_PX
  }

  if (deltaMode === 2) {
    return deltaY * (Number.isFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 1)
  }

  return deltaY
}

export function resolveLyricScrollFrameDelta(timestamp: number, lastFrameAt: number) {
  if (!Number.isFinite(timestamp) || !Number.isFinite(lastFrameAt) || lastFrameAt <= 0) {
    return 1000 / 60
  }

  return Math.min(
    LYRIC_MAX_SCROLL_FRAME_DELTA_MS,
    Math.max(0, timestamp - lastFrameAt),
  )
}

export function resolveLyricScrollDamping(timestamp: number, lastFrameAt: number, playing: boolean) {
  const elapsedMs = resolveLyricScrollFrameDelta(timestamp, lastFrameAt)
  const timeConstant = playing
    ? LYRIC_PLAYING_SCROLL_TIME_CONSTANT_MS
    : LYRIC_IDLE_SCROLL_TIME_CONSTANT_MS

  return 1 - Math.exp(-elapsedMs / timeConstant)
}

export function resolveLyricScrollStep({
  currentScrollTop,
  goal,
  lastFrameAt,
  playing,
  timestamp,
}: LyricScrollStepInput) {
  const diff = goal - currentScrollTop

  if (Math.abs(diff) < LYRIC_SCROLL_SETTLE_THRESHOLD_PX) {
    return {
      done: true,
      lastFrameAt: timestamp,
      scrollTop: goal,
    }
  }

  return {
    done: false,
    lastFrameAt: timestamp,
    scrollTop: lerp(
      currentScrollTop,
      goal,
      resolveLyricScrollDamping(timestamp, lastFrameAt, playing),
    ),
  }
}
