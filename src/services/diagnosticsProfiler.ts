interface PerformanceMemory {
  usedJSHeapSize?: number
  totalJSHeapSize?: number
  jsHeapSizeLimit?: number
}

interface RendererResourceSample {
  sampled: boolean
  timestampMs: number
  jsHeapUsedBytes: number
  jsHeapTotalBytes: number
  jsHeapLimitBytes: number
  deviceMemoryGb: number
  hardwareConcurrency: number
}

type MemoryPerformance = Performance & {
  memory?: PerformanceMemory
}

type MemoryNavigator = Navigator & {
  deviceMemory?: number
}

function nowMs() {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function normalizeFiniteNumber(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function deltaNumber(end: unknown, start: unknown): number {
  return normalizeFiniteNumber(end) - normalizeFiniteNumber(start)
}

export function captureRendererResourceSample(): RendererResourceSample {
  const memory = typeof performance !== 'undefined' ? (performance as MemoryPerformance).memory : null
  const navigatorMemory = typeof navigator !== 'undefined' ? (navigator as MemoryNavigator).deviceMemory : null
  const hardwareConcurrency =
    typeof navigator !== 'undefined' && Number.isFinite(navigator.hardwareConcurrency)
      ? navigator.hardwareConcurrency
      : 0

  return {
    sampled: Boolean(memory),
    timestampMs: Math.round(nowMs()),
    jsHeapUsedBytes: normalizeFiniteNumber(memory?.usedJSHeapSize),
    jsHeapTotalBytes: normalizeFiniteNumber(memory?.totalJSHeapSize),
    jsHeapLimitBytes: normalizeFiniteNumber(memory?.jsHeapSizeLimit),
    deviceMemoryGb: normalizeFiniteNumber(navigatorMemory),
    hardwareConcurrency,
  }
}

export function buildRendererResourceProfile(
  start: RendererResourceSample | null = null,
  end: RendererResourceSample | null = null,
) {
  const sampled = start?.sampled === true && end?.sampled === true

  return {
    sampled,
    start,
    end,
    delta: sampled
      ? {
          jsHeapUsedBytes: deltaNumber(end?.jsHeapUsedBytes, start?.jsHeapUsedBytes),
          jsHeapTotalBytes: deltaNumber(end?.jsHeapTotalBytes, start?.jsHeapTotalBytes),
          jsHeapLimitBytes: deltaNumber(end?.jsHeapLimitBytes, start?.jsHeapLimitBytes),
        }
      : null,
  }
}

export function buildRendererStepProfile(
  key: string,
  elapsedMs: unknown,
  start: RendererResourceSample | null = null,
  end: RendererResourceSample | null = null,
) {
  const sampled = start?.sampled === true && end?.sampled === true

  return {
    key,
    elapsedMs: Math.max(0, Math.round(normalizeFiniteNumber(elapsedMs))),
    sampled,
    jsHeapUsedBytes: normalizeFiniteNumber(end?.jsHeapUsedBytes),
    jsHeapUsedDeltaBytes: sampled ? deltaNumber(end?.jsHeapUsedBytes, start?.jsHeapUsedBytes) : 0,
    jsHeapTotalBytes: normalizeFiniteNumber(end?.jsHeapTotalBytes),
    jsHeapTotalDeltaBytes: sampled ? deltaNumber(end?.jsHeapTotalBytes, start?.jsHeapTotalBytes) : 0,
    jsHeapLimitBytes: normalizeFiniteNumber(end?.jsHeapLimitBytes),
    deviceMemoryGb: normalizeFiniteNumber(end?.deviceMemoryGb),
    hardwareConcurrency: normalizeFiniteNumber(end?.hardwareConcurrency),
  }
}
