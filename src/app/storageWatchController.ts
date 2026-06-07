type Ref<T> = { value: T }
type TimerId = ReturnType<typeof setTimeout>

export type StorageWatchPayload = {
  kind?: string
  paths?: unknown[]
}

export type StorageWatchControllerOptions = {
  desktopStorageService: {
    available: boolean
    configureWatchDirectories: (options: {
      storageRoot: string
      directories: string[]
      enabled: boolean
    }) => Promise<unknown>
  }
  preferencesStore: {
    autoScanOnLaunch: Ref<boolean>
    scanDirectories: Ref<string[]>
    storageRoot: Ref<string>
    setStorageRoot: (storageRoot: unknown) => string
    setScanDirectories: (scanDirectories: unknown) => string[]
    setLyricsScanDirectories: (lyricsScanDirectories: unknown) => string[]
    setAutoScanOnLaunch: (nextValue: unknown) => boolean
  }
  isDisposed: () => boolean
  isResettingData: () => boolean
  runLibraryScanImport: (options: { source: 'watch' }) => Promise<unknown> | unknown
  debounceMs: number
  setTimeoutFn?: (callback: () => void, delayMs: number) => TimerId
  clearTimeoutFn?: (timerId: TimerId) => void
}

function defaultSetTimeout(callback: () => void, delayMs: number): TimerId {
  const timer = typeof window !== 'undefined' ? window.setTimeout : setTimeout
  return timer(callback, delayMs)
}

function defaultClearTimeout(timerId: TimerId): void {
  const clear = typeof window !== 'undefined' ? window.clearTimeout : clearTimeout
  clear(timerId)
}

export function createStorageWatchController({
  desktopStorageService,
  preferencesStore,
  isDisposed,
  isResettingData,
  runLibraryScanImport,
  debounceMs,
  setTimeoutFn = defaultSetTimeout,
  clearTimeoutFn = defaultClearTimeout,
}: StorageWatchControllerOptions) {
  let syncPromise: Promise<unknown> = Promise.resolve(null)
  let watchTimerId: TimerId | null = null

  function clearPendingScan(): void {
    if (watchTimerId !== null) {
      clearTimeoutFn(watchTimerId)
      watchTimerId = null
    }
  }

  function syncWatch(): Promise<unknown> {
    if (!desktopStorageService.available || isDisposed()) {
      return Promise.resolve(null)
    }

    const enabled =
      preferencesStore.autoScanOnLaunch.value &&
      preferencesStore.scanDirectories.value.length > 0

    syncPromise = syncPromise
      .catch(() => null)
      .then(() => {
        if (isDisposed()) {
          return null
        }

        return desktopStorageService.configureWatchDirectories({
          storageRoot: preferencesStore.storageRoot.value,
          directories: preferencesStore.scanDirectories.value,
          enabled,
        })
      })

    return syncPromise
  }

  function scheduleScan(): void {
    if (isDisposed() || isResettingData() || !preferencesStore.autoScanOnLaunch.value) {
      return
    }

    clearPendingScan()
    watchTimerId = setTimeoutFn(() => {
      watchTimerId = null
      void runLibraryScanImport({ source: 'watch' })
    }, debounceMs)
  }

  async function applyStorageRoot(storageRoot: unknown): Promise<string> {
    const nextStorageRoot = preferencesStore.setStorageRoot(storageRoot)
    await syncWatch()
    return nextStorageRoot
  }

  function applyScanDirectories(scanDirectories: unknown): string[] {
    const nextDirectories = preferencesStore.setScanDirectories(scanDirectories)
    void syncWatch()
    return nextDirectories
  }

  function applyLyricsScanDirectories(lyricsScanDirectories: unknown): string[] {
    return preferencesStore.setLyricsScanDirectories(lyricsScanDirectories)
  }

  function applyAutoScanPreference(nextValue: unknown): boolean {
    const normalizedValue = preferencesStore.setAutoScanOnLaunch(nextValue)

    if (!normalizedValue) {
      clearPendingScan()
    }

    void syncWatch()
    return normalizedValue
  }

  function handleWatchEvent(payload: StorageWatchPayload | null | undefined): void {
    if (isDisposed()) {
      return
    }

    if (payload?.kind === 'error') {
      return
    }

    if (!Array.isArray(payload?.paths) || payload.paths.length === 0) {
      return
    }

    scheduleScan()
  }

  return {
    applyAutoScanPreference,
    applyLyricsScanDirectories,
    applyScanDirectories,
    applyStorageRoot,
    clearPendingScan,
    handleWatchEvent,
    scheduleScan,
    syncWatch,
  }
}
