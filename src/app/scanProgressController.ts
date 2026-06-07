import {
  clampPercent,
  createIdleScanProgress,
  normalizeScanCount,
  resolveScanMode,
  type ScanProgressState,
} from './appStateHelpers.ts'
import { formatCommandError } from '../services/errorNormalizer.ts'

type Ref<T> = { value: T }
type TimerId = ReturnType<typeof setTimeout>

export type LibraryScanProgressEventPatch = Omit<ScanProgressState, 'mode'>

export type LibraryScanProgressPayload = Record<string, any> & {
  job?: Record<string, any> | null
}

export type ScanProgressControllerOptions = {
  scanProgress: Ref<ScanProgressState>
  getLifecycleToken: () => unknown
  isLifecycleCurrent: (token: unknown) => boolean
  resetDelayMs: number
  setTimeoutFn?: (callback: () => void, delayMs: number) => TimerId
  clearTimeoutFn?: (timerId: TimerId) => void
}

export function normalizeLibraryScanProgressEvent(
  payload: LibraryScanProgressPayload | null | undefined,
): LibraryScanProgressEventPatch | null {
  if (!payload || typeof payload !== 'object') {
    return null
  }

  const phase = typeof payload.phase === 'string' ? payload.phase : 'discovering'
  const isTerminalPhase = ['complete', 'empty', 'error'].includes(phase)
  const job = payload?.job && typeof payload.job === 'object' ? payload.job : null

  return {
    visible: true,
    active: !isTerminalPhase,
    phase,
    percent: normalizeScanCount(payload.percent),
    processed: normalizeScanCount(payload.processed),
    total: normalizeScanCount(payload.total),
    imported: normalizeScanCount(payload.imported),
    discoveredTotal: normalizeScanCount(payload.discoveredTotal),
    candidateTotal: normalizeScanCount(payload.candidateTotal),
    directoriesScanned: normalizeScanCount(payload.directoriesScanned),
    entriesScanned: normalizeScanCount(payload.entriesScanned),
    elapsedMs: normalizeScanCount(payload.elapsedMs),
    jobId: typeof job?.id === 'string' ? job.id : '',
    jobMode: typeof job?.mode === 'string' ? job.mode : '',
    jobStatus: typeof job?.status === 'string' ? job.status : 'queued',
    jobStage: typeof job?.currentStage === 'string' ? job.currentStage : '',
    jobCreatedAt: typeof job?.createdAt === 'string' ? job.createdAt : '',
    jobUpdatedAt: typeof job?.updatedAt === 'string' ? job.updatedAt : '',
    jobCompletedAt: typeof job?.completedAt === 'string' ? job.completedAt : '',
    jobStages: Array.isArray(job?.stages) ? job.stages : [],
    currentFile: typeof payload.currentFile === 'string' ? payload.currentFile : '',
    error: phase === 'error' && job?.error ? formatCommandError(job.error, '') : '',
  }
}

export function createScanProgressController({
  scanProgress,
  getLifecycleToken,
  isLifecycleCurrent,
  resetDelayMs,
  setTimeoutFn = setTimeout,
  clearTimeoutFn = clearTimeout,
}: ScanProgressControllerOptions) {
  let resetTimerId: TimerId | null = null

  function clearResetTimer(): void {
    if (resetTimerId !== null) {
      clearTimeoutFn(resetTimerId)
      resetTimerId = null
    }
  }

  function reset(): void {
    clearResetTimer()
    scanProgress.value = createIdleScanProgress()
  }

  function update(patch: Partial<ScanProgressState> = {}): void {
    const nextActive = patch.active ?? scanProgress.value.active
    const nextPercent = clampPercent(patch.percent ?? scanProgress.value.percent)

    scanProgress.value = {
      ...scanProgress.value,
      ...patch,
      percent: nextActive ? Math.max(scanProgress.value.percent, nextPercent) : nextPercent,
    }
  }

  function start(options: Parameters<typeof resolveScanMode>[0] = {}): void {
    clearResetTimer()
    scanProgress.value = {
      ...createIdleScanProgress(),
      visible: true,
      active: true,
      phase: 'discovering',
      percent: 4,
      mode: resolveScanMode(options),
    }
  }

  function finish(patch: Partial<ScanProgressState> = {}): void {
    clearResetTimer()

    scanProgress.value = {
      ...scanProgress.value,
      ...patch,
      visible: true,
      active: false,
      percent: clampPercent(patch.percent ?? 100),
    }

    if (scanProgress.value.phase !== 'error') {
      const resetToken = getLifecycleToken()
      resetTimerId = setTimeoutFn(() => {
        resetTimerId = null
        if (!isLifecycleCurrent(resetToken)) {
          return
        }
        reset()
      }, resetDelayMs)
    }
  }

  function handleBackendProgress(payload: LibraryScanProgressPayload | null | undefined): void {
    const patch = normalizeLibraryScanProgressEvent(payload)

    if (!patch) {
      return
    }

    update(patch)
  }

  return {
    clearResetTimer,
    finish,
    handleBackendProgress,
    reset,
    start,
    update,
  }
}
