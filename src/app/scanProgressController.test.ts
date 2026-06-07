import test from 'node:test'
import assert from 'node:assert/strict'
import { createIdleScanProgress } from './appStateHelpers.ts'
import {
  createScanProgressController,
  normalizeLibraryScanProgressEvent,
} from './scanProgressController.ts'

interface MockTimer {
  callback: () => void
  delayMs: number
  cleared: boolean
}

function createController({ lifecycleCurrent = true } = {}) {
  const scheduled: MockTimer[] = []
  const scanProgress = { value: createIdleScanProgress() }
  const controller = createScanProgressController({
    scanProgress,
    getLifecycleToken: () => 1,
    isLifecycleCurrent: () => lifecycleCurrent,
    resetDelayMs: 25,
    setTimeoutFn: (callback: () => void, delayMs: number) => {
      const timer: MockTimer = { callback, delayMs, cleared: false }
      scheduled.push(timer)
      return timer as unknown as ReturnType<typeof setTimeout>
    },
    clearTimeoutFn: (timer: ReturnType<typeof setTimeout>) => {
      ;(timer as unknown as MockTimer).cleared = true
    },
  })

  return { controller, scanProgress, scheduled }
}

test('normalizeLibraryScanProgressEvent converts backend payload into UI state patch', () => {
  assert.equal(normalizeLibraryScanProgressEvent(null), null)
  assert.deepEqual(
    normalizeLibraryScanProgressEvent({
      phase: 'processing',
      percent: 12.6,
      processed: 2,
      total: 8,
      imported: 1,
      discoveredTotal: 10,
      candidateTotal: 8,
      directoriesScanned: 3,
      entriesScanned: 40,
      elapsedMs: 99.4,
      currentFile: 'song.mp3',
      job: {
        id: 'job-1',
        mode: 'scan-import',
        status: 'running',
        currentStage: 'prepare',
        createdAt: 'created',
        updatedAt: 'updated',
        completedAt: '',
        stages: [{ key: 'prepare' }],
      },
    }),
    {
      visible: true,
      active: true,
      phase: 'processing',
      percent: 13,
      processed: 2,
      total: 8,
      imported: 1,
      discoveredTotal: 10,
      candidateTotal: 8,
      directoriesScanned: 3,
      entriesScanned: 40,
      elapsedMs: 99,
      jobId: 'job-1',
      jobMode: 'scan-import',
      jobStatus: 'running',
      jobStage: 'prepare',
      jobCreatedAt: 'created',
      jobUpdatedAt: 'updated',
      jobCompletedAt: '',
      jobStages: [{ key: 'prepare' }],
      currentFile: 'song.mp3',
      error: '',
    },
  )
})

test('scan progress controller starts and keeps active progress monotonic', () => {
  const { controller, scanProgress } = createController()

  controller.start({ source: 'watch' })
  assert.equal(scanProgress.value.active, true)
  assert.equal(scanProgress.value.mode, 'watch')
  assert.equal(scanProgress.value.percent, 4)

  controller.update({ active: true, percent: 30 })
  controller.update({ active: true, percent: 20 })
  assert.equal(scanProgress.value.percent, 30)

  controller.update({ active: false, percent: 20 })
  assert.equal(scanProgress.value.percent, 20)
})

test('normalizeLibraryScanProgressEvent formats structured backend errors', () => {
  const patch = normalizeLibraryScanProgressEvent({
    phase: 'error',
    percent: 100,
    job: {
      error: {
        code: 'metadata_read_failed',
        message: 'Failed to read audio metadata.',
        source: 'failed to fill whole buffer',
      },
    },
  })

  assert.equal(
    patch?.error,
    'Failed to read audio metadata. failed to fill whole buffer',
  )
})

test('scan progress controller schedules reset after successful terminal state', () => {
  const { controller, scanProgress, scheduled } = createController()

  controller.start()
  controller.finish({ phase: 'complete', percent: 100 })

  assert.equal(scanProgress.value.active, false)
  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].delayMs, 25)

  scheduled[0].callback()
  assert.equal(scanProgress.value.phase, 'idle')
  assert.equal(scanProgress.value.visible, false)
})

test('scan progress controller keeps error progress visible until explicit reset', () => {
  const { controller, scanProgress, scheduled } = createController()

  controller.start()
  controller.finish({ phase: 'error', error: 'failed' })

  assert.equal(scanProgress.value.phase, 'error')
  assert.equal(scanProgress.value.error, 'failed')
  assert.equal(scheduled.length, 0)
})
