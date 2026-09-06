import type { MusicIconName } from './icons/musicIconPaths'

export const NAV_RAIL_GROUP_SIZE = 5
export const NAV_RAIL_MAX_MARKERS = 40
export const NAV_RAIL_MIN_TRACK_COUNT = 40
export const NAV_RAIL_DEFAULT_ROW_HEIGHT_PX = 76
export interface NavRailMarkerSymbol {
  icon: MusicIconName
  /** Notation reference for the marker sequence; rendering uses the SVG icon. */
  glyph: string
}

export const NAV_RAIL_MARKER_SYMBOLS: readonly NavRailMarkerSymbol[] = [
  { icon: 'eighth-note', glyph: '♪' },
  { icon: 'treble-clef', glyph: '𝄞' },
  { icon: 'eighth-pair', glyph: '♫' },
  { icon: 'sharp', glyph: '♯' },
  { icon: 'quarter-note', glyph: '♩' },
  { icon: 'bass-clef', glyph: '𝄢' },
  { icon: 'sixteenth-pair', glyph: '♬' },
  { icon: 'flat', glyph: '♭' },
]
export const NAV_RAIL_SCROLL_MIN_DURATION_MS = 240
export const NAV_RAIL_SCROLL_MAX_DURATION_MS = 560
export const NAV_RAIL_SCROLL_DURATION_DISTANCE_PX = 20_000

export interface NavRailMarker {
  key: string
  groupIndex: number
  startTrack: number
  endTrack: number
}

export interface NavRailPreviewTrack {
  id: string
  title: string
  artwork: string | null
  monogram: string
}

function normalizePositiveInt(value: number, fallback: number) {
  if (!Number.isFinite(value) || value < 1) {
    return fallback
  }

  return Math.floor(value)
}

export function resolveNavRailMarkerSymbol(markerIndex: number): NavRailMarkerSymbol {
  if (!Number.isInteger(markerIndex) || markerIndex < 0) {
    return NAV_RAIL_MARKER_SYMBOLS[0]
  }

  return NAV_RAIL_MARKER_SYMBOLS[markerIndex % NAV_RAIL_MARKER_SYMBOLS.length]
}

export function buildNavRailMarkers(
  totalCount: number,
  groupSize = NAV_RAIL_GROUP_SIZE,
  maxMarkers = NAV_RAIL_MAX_MARKERS,
): NavRailMarker[] {
  if (!Number.isFinite(totalCount) || totalCount <= 0) {
    return []
  }

  const safeTotal = Math.floor(totalCount)
  const safeGroupSize = normalizePositiveInt(groupSize, NAV_RAIL_GROUP_SIZE)
  const safeMaxMarkers = normalizePositiveInt(maxMarkers, NAV_RAIL_MAX_MARKERS)
  const groupCount = Math.ceil(safeTotal / safeGroupSize)
  const markerCount = Math.min(groupCount, safeMaxMarkers)
  const markers: NavRailMarker[] = []

  for (let index = 0; index < markerCount; index += 1) {
    const groupIndex =
      markerCount === 1 ? 0 : Math.round((index * (groupCount - 1)) / (markerCount - 1))
    const startTrack = groupIndex * safeGroupSize + 1
    const endTrack = Math.min(startTrack + safeGroupSize - 1, safeTotal)

    markers.push({
      key: String(groupIndex),
      groupIndex,
      startTrack,
      endTrack,
    })
  }

  return markers
}

export function resolveActiveMarkerIndex(
  markers: NavRailMarker[],
  scrollTop: number,
  rowHeightPx = NAV_RAIL_DEFAULT_ROW_HEIGHT_PX,
  groupSize = NAV_RAIL_GROUP_SIZE,
) {
  if (markers.length === 0) {
    return -1
  }

  const safeScrollTop = Number.isFinite(scrollTop) ? Math.max(0, scrollTop) : 0
  const safeRowHeight = rowHeightPx > 0 ? rowHeightPx : NAV_RAIL_DEFAULT_ROW_HEIGHT_PX
  const safeGroupSize = normalizePositiveInt(groupSize, NAV_RAIL_GROUP_SIZE)
  const activeGroup = Math.floor(safeScrollTop / (safeRowHeight * safeGroupSize))
  let activeIndex = 0
  let activeDistance = Number.POSITIVE_INFINITY

  markers.forEach((marker, index) => {
    const distance = Math.abs(marker.groupIndex - activeGroup)

    if (distance < activeDistance) {
      activeDistance = distance
      activeIndex = index
    }
  })

  return activeIndex
}

export function navRailGroupScrollTop(
  marker: NavRailMarker,
  rowHeightPx = NAV_RAIL_DEFAULT_ROW_HEIGHT_PX,
  groupSize = NAV_RAIL_GROUP_SIZE,
) {
  const safeRowHeight = rowHeightPx > 0 ? rowHeightPx : NAV_RAIL_DEFAULT_ROW_HEIGHT_PX
  const safeGroupSize = normalizePositiveInt(groupSize, NAV_RAIL_GROUP_SIZE)

  return marker.groupIndex * safeGroupSize * safeRowHeight
}

export function resolveNavRailScrollDuration(distancePx: number) {
  const safeDistance = Number.isFinite(distancePx) ? Math.abs(distancePx) : 0
  const ratio = Math.min(safeDistance / NAV_RAIL_SCROLL_DURATION_DISTANCE_PX, 1)
  const duration =
    NAV_RAIL_SCROLL_MIN_DURATION_MS +
    ratio * (NAV_RAIL_SCROLL_MAX_DURATION_MS - NAV_RAIL_SCROLL_MIN_DURATION_MS)

  return Math.round(duration)
}

// Match --ease-emphasized-decelerate in style.css. CSS progress is the curve's
// x coordinate, so solve x(t) before sampling y(t); t is not elapsed time.
function easeNavRailScroll(progress: number) {
  if (progress <= 0 || progress >= 1) return progress

  let lower = 0
  let upper = 1
  for (let iteration = 0; iteration < 24; iteration += 1) {
    const t = (lower + upper) / 2
    const inverse = 1 - t
    const x = 3 * inverse * inverse * t * 0.22 + 3 * inverse * t * t * 0.36 + t * t * t
    if (x < progress) lower = t
    else upper = t
  }
  return 1 - Math.pow(1 - (lower + upper) / 2, 3)
}

export function resolveNavRailScrollFrame(
  startScrollTop: number,
  targetScrollTop: number,
  elapsedMs: number,
  durationMs: number,
) {
  const safeDuration = Number.isFinite(durationMs) && durationMs > 0 ? durationMs : 1
  const safeElapsed = Number.isFinite(elapsedMs) ? elapsedMs : 0
  const progress = Math.min(Math.max(safeElapsed / safeDuration, 0), 1)
  const easedProgress = easeNavRailScroll(progress)
  const scrollTop = startScrollTop + (targetScrollTop - startScrollTop) * easedProgress

  return {
    scrollTop,
    done: progress >= 1,
  }
}

export function clampPreviewTop(
  markerTop: number,
  cardHeight: number,
  viewportHeight: number,
  margin = 8,
) {
  const safeMarkerTop = Number.isFinite(markerTop) ? markerTop : margin
  const safeViewportHeight = Number.isFinite(viewportHeight) ? viewportHeight : margin * 2
  const safeCardHeight = Number.isFinite(cardHeight) && cardHeight > 0 ? cardHeight : 0
  const maxTop = Math.max(margin, safeViewportHeight - safeCardHeight - margin)

  return Math.min(Math.max(safeMarkerTop, margin), maxTop)
}
