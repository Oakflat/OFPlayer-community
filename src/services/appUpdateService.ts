import { getVersion } from '@tauri-apps/api/app'
import { isTauri } from '@tauri-apps/api/core'
import { computed, reactive } from 'vue'

export const UPDATE_CHECK_DAILY_MS = 24 * 60 * 60 * 1000

type UpdateStatus =
  | 'idle'
  | 'checking'
  | 'current'
  | 'available'
  | 'downloading'
  | 'restarting'
  | 'unavailable'
  | 'error'

interface AppUpdateProgress {
  downloadedBytes: number
  contentLength: number
  percent: number
  finished: boolean
}

interface AppUpdateState {
  status: UpdateStatus
  channel: string
  currentVersion: string
  lastCheckedAt: string
  lastError: string
  checkReason: string
  availableUpdate: null
  latestAnnouncement: null
  unseenAnnouncement: boolean
  progress: AppUpdateProgress
}

interface CheckForUpdatesOptions {
  reason?: string
  silent?: boolean
  force?: boolean
  minIntervalMs?: number
}

function createCommunityUpdateState(): AppUpdateState {
  return {
    status: 'unavailable',
    channel: 'community',
    currentVersion: '',
    lastCheckedAt: '',
    lastError: '',
    checkReason: '',
    availableUpdate: null,
    latestAnnouncement: null,
    unseenAnnouncement: false,
    progress: {
      downloadedBytes: 0,
      contentLength: 0,
      percent: 0,
      finished: false,
    },
  }
}

function normalizeText(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

export function createAppUpdateService() {
  const state = reactive<AppUpdateState>(createCommunityUpdateState())
  let appVersionPromise: Promise<string> | null = null

  async function resolveCurrentVersion(): Promise<string> {
    if (state.currentVersion) {
      return state.currentVersion
    }

    if (!isTauri()) {
      state.currentVersion = normalizeText(import.meta.env.VITE_APP_VERSION)
      return state.currentVersion
    }

    if (!appVersionPromise) {
      appVersionPromise = getVersion()
        .then((version) => {
          state.currentVersion = normalizeText(version)
          return state.currentVersion
        })
        .catch(() => {
          state.currentVersion = normalizeText(import.meta.env.VITE_APP_VERSION)
          return state.currentVersion
        })
    }

    return appVersionPromise
  }

  async function checkForUpdates({
    reason = 'manual',
  }: CheckForUpdatesOptions = {}): Promise<null> {
    state.checkReason = reason
    state.status = 'unavailable'
    state.lastError = ''
    state.lastCheckedAt = new Date().toISOString()
    state.availableUpdate = null
    state.latestAnnouncement = null
    state.unseenAnnouncement = false
    await resolveCurrentVersion()
    return null
  }

  async function downloadAndInstallUpdate(): Promise<boolean> {
    state.status = 'unavailable'
    state.lastError = ''
    return false
  }

  function dismissAvailableUpdate(): boolean {
    state.status = 'unavailable'
    return true
  }

  return {
    state,
    isChecking: computed(() => state.status === 'checking'),
    isDownloading: computed(() => state.status === 'downloading'),
    isInstalling: computed(() => ['downloading', 'restarting'].includes(state.status)),
    hasAvailableUpdate: computed(() => false),
    resolveCurrentVersion,
    checkForUpdates,
    downloadAndInstallUpdate,
    dismissAvailableUpdate,
  }
}
