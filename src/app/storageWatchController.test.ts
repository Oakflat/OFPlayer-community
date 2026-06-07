import test from 'node:test'
import assert from 'node:assert/strict'
import { createStorageWatchController } from './storageWatchController.ts'

interface MockTimer {
  callback: () => void
  delayMs: number
  cleared: boolean
}

function createPreferencesStore({
  autoScanOnLaunch = true,
  storageRoot = 'E:/Music',
  scanDirectories = ['E:/Music'],
}: {
  autoScanOnLaunch?: boolean
  storageRoot?: string
  scanDirectories?: string[]
} = {}) {
  return {
    autoScanOnLaunch: { value: autoScanOnLaunch },
    storageRoot: { value: storageRoot },
    scanDirectories: { value: scanDirectories },
    lyricsScanDirectories: { value: [] as string[] },
    setAutoScanOnLaunch(value: boolean) {
      this.autoScanOnLaunch.value = value === true
      return this.autoScanOnLaunch.value
    },
    setStorageRoot(value: string) {
      this.storageRoot.value = value
      return value
    },
    setScanDirectories(value: string[]) {
      this.scanDirectories.value = value
      return value
    },
    setLyricsScanDirectories(value: string[]) {
      this.lyricsScanDirectories.value = value
      return value
    },
  }
}

function createController(options: {
  preferences?: {
    autoScanOnLaunch?: boolean
    storageRoot?: string
    scanDirectories?: string[]
  }
  available?: boolean
} = {}) {
  const configured: unknown[] = []
  const scheduled: MockTimer[] = []
  const scans: unknown[] = []
  let disposed = false
  let resetting = false
  const preferencesStore = createPreferencesStore(options.preferences)
  const controller = createStorageWatchController({
    desktopStorageService: {
      available: options.available !== false,
      configureWatchDirectories: async (request: unknown) => {
        configured.push(request)
        return request
      },
    },
    preferencesStore: preferencesStore as unknown as {
      autoScanOnLaunch: { value: boolean }
      scanDirectories: { value: string[] }
      storageRoot: { value: string }
      setStorageRoot: (storageRoot: unknown) => string
      setScanDirectories: (scanDirectories: unknown) => string[]
      setLyricsScanDirectories: (lyricsScanDirectories: unknown) => string[]
      setAutoScanOnLaunch: (nextValue: unknown) => boolean
    },
    isDisposed: () => disposed,
    isResettingData: () => resetting,
    runLibraryScanImport: (request: unknown) => {
      scans.push(request)
    },
    debounceMs: 50,
    setTimeoutFn: (callback: () => void, delayMs: number) => {
      const timer: MockTimer = { callback, delayMs, cleared: false }
      scheduled.push(timer)
      return timer as unknown as ReturnType<typeof setTimeout>
    },
    clearTimeoutFn: (timer: ReturnType<typeof setTimeout>) => {
      ;(timer as unknown as MockTimer).cleared = true
    },
  })

  return {
    configured,
    controller,
    preferencesStore,
    scans,
    scheduled,
    setDisposed: (value: boolean) => {
      disposed = value
    },
    setResetting: (value: boolean) => {
      resetting = value
    },
  }
}

test('storage watch controller syncs enabled watch configuration', async () => {
  const { configured, controller } = createController()

  await controller.syncWatch()

  assert.deepEqual(configured, [
    {
      storageRoot: 'E:/Music',
      directories: ['E:/Music'],
      enabled: true,
    },
  ])
})

test('storage watch controller does not require a legacy storage root', async () => {
  const { configured, controller } = createController({
    preferences: {
      storageRoot: '',
      scanDirectories: ['E:/Music'],
    },
  })

  await controller.syncWatch()

  assert.deepEqual(configured, [
    {
      storageRoot: '',
      directories: ['E:/Music'],
      enabled: true,
    },
  ])
})

test('storage watch controller disables watch when auto scan or directories are missing', async () => {
  const { configured, controller } = createController({
    preferences: {
      autoScanOnLaunch: false,
      storageRoot: 'E:/Music',
      scanDirectories: [],
    },
  })

  await controller.syncWatch()

  assert.equal((configured[0] as Record<string, unknown>).enabled, false)
})

test('storage watch controller schedules debounced scan for valid watch events', () => {
  const { controller, scans, scheduled } = createController()

  controller.handleWatchEvent({ kind: 'changed', paths: ['E:/Music/a.mp3'] })

  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].delayMs, 50)
  scheduled[0].callback()
  assert.deepEqual(scans, [{ source: 'watch' }])
})

test('storage watch controller ignores invalid events and reset/disabled states', () => {
  const { controller, scheduled, setResetting } = createController()

  controller.handleWatchEvent({ kind: 'error', paths: ['E:/Music/a.mp3'] })
  controller.handleWatchEvent({ kind: 'changed', paths: [] })
  setResetting(true)
  controller.handleWatchEvent({ kind: 'changed', paths: ['E:/Music/a.mp3'] })

  assert.equal(scheduled.length, 0)
})

test('storage watch controller applies preferences and clears pending scan when disabled', async () => {
  const { controller, preferencesStore, scheduled } = createController()

  controller.scheduleScan()
  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].cleared, false)

  assert.equal(controller.applyAutoScanPreference(false), false)
  assert.equal(scheduled[0].cleared, true)
  assert.equal(preferencesStore.autoScanOnLaunch.value, false)

  assert.equal(await controller.applyStorageRoot('D:/Audio'), 'D:/Audio')
  assert.deepEqual(controller.applyScanDirectories(['D:/Audio']), ['D:/Audio'])
  assert.deepEqual(controller.applyLyricsScanDirectories(['D:/Lyrics']), ['D:/Lyrics'])
})
