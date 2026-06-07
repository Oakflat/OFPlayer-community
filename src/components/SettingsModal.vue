<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, unref, watch } from 'vue'
import {
  Check,
  ChevronDown,
  Database,
  Download,
  Globe,
  HardDrive,
  Monitor,
  MonitorCog,
  Moon,
  Palette,
  RefreshCcw,
  SlidersHorizontal,
  Sun,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-vue-next'
import DialogModal from './DialogModal.vue'
import { useOFPlayerApp } from '../app/ofplayerApp'
import { useI18n } from '../composables/useI18n'

type UnknownRecord = Record<string, unknown>
type SettingsCategory = 'general' | 'appearance' | 'playback' | 'privacy' | 'library'
type SettingsLanguage = 'system' | 'zh-CN' | 'en'
type SettingsTheme = 'mist' | 'paper' | 'material'
type SettingsColorScheme = 'light' | 'dark' | 'system'
type SettingsMotion = 'full' | 'reduced'
type SettingsWindowEffect = 'auto' | 'full' | 'balanced' | 'off' | 'web'
type SettingsImmersiveTaskbarMode = 'show' | 'hide'
type SettingsSortOption = 'recent' | 'title' | 'duration' | 'size'
type ScanMode = 'manual' | 'auto' | 'watch'
type ScanProgressPhase =
  | 'idle'
  | 'discovering'
  | 'preparing'
  | 'processing'
  | 'importing'
  | 'complete'
  | 'empty'
  | 'error'

type UpdateSeverity = 'normal' | 'recommended' | 'force' | string

interface SettingsCategoryOption {
  key: SettingsCategory
  label: string
  description: string
}

interface SettingsValueOption<T extends string = string> {
  value: T
  label: string
  description: string
}

interface PlaybackOutputDevice {
  id?: string
  name?: string
  backend?: string
  backendLabel?: string
  isDefault?: boolean
}

interface PlaybackOutputChoice {
  id: string
  name: string
  description: string
  backendLabel: string
  isDefault: boolean
}

interface SettingsNotice {
  id?: string
  kind?: string
  category?: SettingsCategory | string
}

interface RawScanProgress {
  visible?: unknown
  active?: unknown
  phase?: unknown
  percent?: unknown
  processed?: unknown
  total?: unknown
  imported?: unknown
  mode?: unknown
  discoveredTotal?: unknown
  candidateTotal?: unknown
  directoriesScanned?: unknown
  entriesScanned?: unknown
  elapsedMs?: unknown
  currentFile?: unknown
  error?: unknown
  [key: string]: unknown
}

interface NormalizedScanProgress {
  visible: boolean
  active: boolean
  phase: ScanProgressPhase | string
  percent: number
  processed: number
  total: number
  imported: number
  mode: ScanMode | string
  discoveredTotal: number
  candidateTotal: number
  directoriesScanned: number
  entriesScanned: number
  elapsedMs: number
  currentFile: string
  error: string
}

interface RawStorageUsageItem {
  key?: unknown
  path?: unknown
  bytes?: unknown
  fileCount?: unknown
  directoryCount?: unknown
  reclaimableBytes?: unknown
  reclaimableFileCount?: unknown
  details?: unknown
}

interface NormalizedStorageUsageItem {
  key: string
  path: string
  bytes: number
  fileCount: number
  directoryCount: number
  reclaimableBytes: number
  reclaimableFileCount: number
  details: UnknownRecord | null
}

type StorageUsageItemDefaults = NormalizedStorageUsageItem

interface NormalizedStorageUsage {
  totalBytes: number
  reclaimableBytes: number
  items: NormalizedStorageUsageItem[]
  warnings: string[]
  generatedAt: string
}

interface RawDiagnosticsReportStatus {
  state?: unknown
  message?: unknown
  uploadedAt?: unknown
  eventCount?: unknown
}

interface NormalizedDiagnosticsReportStatus {
  state: string
  message: string
  uploadedAt: string
  eventCount: number
}

interface RawUpdateAnnouncement {
  version?: unknown
  title?: unknown
  message?: unknown
  releaseNotesUrl?: unknown
  severity?: unknown
  [key: string]: unknown
}

interface NormalizedAppUpdateAnnouncement {
  version: string
  title: string
  message: string
  releaseNotesUrl: string
  severity: UpdateSeverity
}

interface RawAvailableUpdate {
  version?: unknown
  notes?: unknown
  releaseNotesUrl?: unknown
  severity?: unknown
  announcement?: unknown
  [key: string]: unknown
}

interface NormalizedAvailableUpdate {
  version: string
  notes: string
  releaseNotesUrl: string
  severity: UpdateSeverity
  announcement: NormalizedAppUpdateAnnouncement | null
}

interface RawAppUpdateState {
  status?: unknown
  channel?: unknown
  currentVersion?: unknown
  lastCheckedAt?: unknown
  lastError?: unknown
  availableUpdate?: unknown
  latestAnnouncement?: unknown
  unseenAnnouncement?: unknown
  progress?: unknown
}

interface RawAppUpdateProgress {
  downloadedBytes?: unknown
  contentLength?: unknown
  percent?: unknown
  finished?: unknown
}

interface NormalizedAppUpdateProgress {
  downloadedBytes: number
  contentLength: number
  percent: number
  finished: boolean
}

interface NormalizedAppUpdateState {
  status: string
  channel: string
  currentVersion: string
  lastCheckedAt: string
  lastError: string
  availableUpdate: NormalizedAvailableUpdate | null
  latestAnnouncement: NormalizedAppUpdateAnnouncement | null
  unseenAnnouncement: boolean
  progress: NormalizedAppUpdateProgress
}

interface RawStorageUsage {
  totalBytes?: unknown
  reclaimableBytes?: unknown
  items?: unknown
  warnings?: unknown
  generatedAt?: unknown
  [key: string]: unknown
}

interface SettingsModalProps {
  isOpen?: boolean
  language?: SettingsLanguage
  theme?: SettingsTheme
  colorScheme?: SettingsColorScheme
  motion?: SettingsMotion
  windowEffects?: SettingsWindowEffect
  immersiveTaskbarMode?: SettingsImmersiveTaskbarMode
  sortOption?: SettingsSortOption
  rememberVolume?: boolean
  isPlaying?: boolean
  playbackOutputDevices?: PlaybackOutputDevice[]
  playbackOutputDeviceId?: string
  activePlaybackOutputDeviceName?: string
  prefersSystemPlaybackOutput?: boolean
  playbackOutputDeviceAvailable?: boolean
  showTechnicalMetadata?: boolean
  storageRoot?: string
  scanDirectories?: string[]
  lyricsScanDirectories?: string[]
  autoScanOnLaunch?: boolean
  lastScanAt?: string
  scanProgress?: RawScanProgress
  isResettingData?: boolean
  storageUsage?: RawStorageUsage | null | undefined
  isLoadingStorageUsage?: boolean
  isCollectingGarbage?: boolean
  storageMaintenanceError?: string
  canManageStorage?: boolean
  telemetryEnabled?: boolean | null
  isUploadingDiagnosticsReport?: boolean
  diagnosticsReportStatus?: RawDiagnosticsReportStatus
  appUpdateState?: RawAppUpdateState
  initialCategory?: SettingsCategory
  notice?: SettingsNotice | null | undefined
}

type EmptyScanProgressDefaults = {
  visible: false
  active: false
  phase: 'idle'
  percent: 0
  processed: 0
  total: 0
  imported: 0
  mode: 'manual'
  discoveredTotal: 0
  candidateTotal: 0
  directoriesScanned: 0
  entriesScanned: 0
  elapsedMs: 0
  currentFile: ''
  error: ''
}

const EMPTY_SCAN_PROGRESS: EmptyScanProgressDefaults = {
  visible: false,
  active: false,
  phase: 'idle',
  percent: 0,
  processed: 0,
  total: 0,
  imported: 0,
  mode: 'manual',
  discoveredTotal: 0,
  candidateTotal: 0,
  directoriesScanned: 0,
  entriesScanned: 0,
  elapsedMs: 0,
  currentFile: '',
  error: '',
}

const EMPTY_DIAGNOSTICS_STATUS: RawDiagnosticsReportStatus = {
  state: 'idle',
  message: '',
  uploadedAt: '',
  eventCount: 0,
}

const SETTINGS_CATEGORIES = new Set<SettingsCategory>([
  'general',
  'appearance',
  'playback',
  'privacy',
  'library',
])

function asRecord(value: unknown): UnknownRecord {
  return typeof value === 'object' && value !== null ? (value as UnknownRecord) : {}
}

function asDiagnosticsStatus(value: RawDiagnosticsReportStatus | undefined): RawDiagnosticsReportStatus {
  return value && typeof value === 'object' ? (value as RawDiagnosticsReportStatus) : EMPTY_DIAGNOSTICS_STATUS
}

function asStorageUsage(value: RawStorageUsage | null | undefined): RawStorageUsage {
  return value && typeof value === 'object' ? (value as RawStorageUsage) : {}
}

function asScanProgressState(value: unknown): RawScanProgress {
  return value && typeof value === 'object' ? (value as RawScanProgress) : {}
}

function asAppUpdateProgressState(value: unknown): RawAppUpdateProgress {
  return value && typeof value === 'object' ? (value as RawAppUpdateProgress) : {}
}

function normalizeBytes(value: unknown): number {
  if (typeof value !== 'number') {
    const numberValue = Number(value)
    return Number.isFinite(numberValue) ? Math.round(numberValue) : 0
  }

  return Number.isFinite(value) ? Math.round(value) : 0
}

function asAppUpdateAnnouncement(value: unknown): NormalizedAppUpdateAnnouncement | null {
  if (!value || typeof value !== 'object') {
    return null
  }

  const record = asRecord(value)

  return {
    version: typeof record.version === 'string' ? record.version : '',
    title: typeof record.title === 'string' ? record.title : '',
    message: typeof record.message === 'string' ? record.message : '',
    releaseNotesUrl: typeof record.releaseNotesUrl === 'string' ? record.releaseNotesUrl : '',
    severity: typeof record.severity === 'string' ? (record.severity as UpdateSeverity) : 'normal',
  }
}

function asAvailableUpdate(value: unknown): NormalizedAvailableUpdate | null {
  if (!value || typeof value !== 'object') {
    return null
  }

  const record = asRecord(value)

  return {
    version: typeof record.version === 'string' ? record.version : '',
    notes: typeof record.notes === 'string' ? record.notes : '',
    releaseNotesUrl: typeof record.releaseNotesUrl === 'string' ? record.releaseNotesUrl : '',
    severity: typeof record.severity === 'string' ? (record.severity as UpdateSeverity) : 'normal',
    announcement: asAppUpdateAnnouncement(record.announcement),
  }
}

function asNormalizedDiagnosticsCount(value: unknown): number {
  if (typeof value === 'number') {
    return Number.isInteger(value) ? value : 0
  }

  const numberValue = Number(value)
  return Number.isInteger(numberValue) ? numberValue : 0
}

const props = withDefaults(defineProps<SettingsModalProps>(), {
  isOpen: false,
  language: 'system',
  theme: 'mist',
  colorScheme: 'system',
  motion: 'full',
  windowEffects: 'auto',
  immersiveTaskbarMode: 'show',
  sortOption: 'recent',
  rememberVolume: true,
  isPlaying: false,
  playbackOutputDevices: () => [],
  playbackOutputDeviceId: '',
  activePlaybackOutputDeviceName: '',
  prefersSystemPlaybackOutput: true,
  playbackOutputDeviceAvailable: true,
  showTechnicalMetadata: true,
  storageRoot: '',
  scanDirectories: () => [],
  lyricsScanDirectories: () => [],
  autoScanOnLaunch: false,
  lastScanAt: '',
  scanProgress: () => ({} as RawScanProgress),
  isResettingData: false,
  storageUsage: undefined,
  isLoadingStorageUsage: false,
  isCollectingGarbage: false,
  storageMaintenanceError: '',
  canManageStorage: false,
  telemetryEnabled: null,
  isUploadingDiagnosticsReport: false,
  diagnosticsReportStatus: () => ({} as RawDiagnosticsReportStatus),
  appUpdateState: () => ({} as RawAppUpdateState),
  initialCategory: 'general',
  notice: undefined,
})

const emit = defineEmits<{
  (event: 'close'): void
  (event: 'set-language', value: SettingsLanguage): void
  (event: 'set-theme', value: SettingsTheme): void
  (event: 'set-color-scheme', value: SettingsColorScheme): void
  (event: 'set-motion', value: SettingsMotion): void
  (event: 'set-window-effects', value: SettingsWindowEffect): void
  (event: 'set-immersive-taskbar-mode', value: SettingsImmersiveTaskbarMode): void
  (event: 'set-sort-option', value: SettingsSortOption): void
  (event: 'set-remember-volume', value: boolean): void
  (event: 'set-playback-output-device', value: string): void
  (event: 'refresh-playback-output-devices'): void
  (event: 'set-show-technical-metadata', value: boolean): void
  (event: 'select-storage-root'): void
  (event: 'add-scan-directory'): void
  (event: 'remove-scan-directory', value: string): void
  (event: 'add-lyrics-scan-directory'): void
  (event: 'remove-lyrics-scan-directory', value: string): void
  (event: 'set-auto-scan-on-launch', value: boolean): void
  (event: 'run-library-scan-import'): void
  (event: 'refresh-storage-usage'): void
  (event: 'collect-storage-garbage'): void
  (event: 'reset-all-data'): void
  (event: 'reopen-onboarding'): void
  (event: 'set-telemetry-consent', value: boolean): void
  (event: 'upload-diagnostics-report'): void
  (event: 'check-app-update'): void
  (event: 'download-and-install-update'): void
}>()

const { t } = useI18n()
const { scanProgress: injectedScanProgress } = useOFPlayerApp()
const activeCategory = ref<SettingsCategory>('general')
const isSortMenuOpen = ref(false)
const isResetDataDialogOpen = ref(false)
const isStorageWindowOpen = ref(false)
const sortMenuRef = ref<HTMLElement | null>(null)
const sortMenuButtonRef = ref<HTMLElement | null>(null)

const categories = computed<SettingsCategoryOption[]>(() => [
  {
    key: 'general',
    label: t('settings.categories.general'),
    description: t('settings.categories.generalCopy'),
  },
  {
    key: 'appearance',
    label: t('settings.categories.appearance'),
    description: t('settings.categories.appearanceCopy'),
  },
  {
    key: 'playback',
    label: t('settings.categories.playback'),
    description: t('settings.categories.playbackCopy'),
  },
  {
    key: 'privacy',
    label: t('settings.categories.privacy'),
    description: t('settings.categories.privacyCopy'),
  },
  {
    key: 'library',
    label: t('settings.categories.library'),
    description: t('settings.categories.libraryCopy'),
  },
])

const languageOptions = computed<SettingsValueOption<SettingsLanguage>[]>(() => [
  {
    value: 'system',
    label: t('settings.language.system'),
    description: t('settings.language.systemCopy'),
  },
  {
    value: 'zh-CN',
    label: t('settings.language.zhCN'),
    description: t('settings.language.zhCNCopy'),
  },
  {
    value: 'en',
    label: t('settings.language.en'),
    description: t('settings.language.enCopy'),
  },
])

const themeOptions = computed<SettingsValueOption<SettingsTheme>[]>(() => [
  {
    value: 'mist',
    label: t('settings.theme.mist'),
    description: t('settings.theme.mistCopy'),
  },
  {
    value: 'paper',
    label: t('settings.theme.paper'),
    description: t('settings.theme.paperCopy'),
  },
  {
    value: 'material',
    label: t('settings.theme.material'),
    description: t('settings.theme.materialCopy'),
  },
])

const colorSchemeOptions = computed<SettingsValueOption<SettingsColorScheme>[]>(() => [
  {
    value: 'light',
    label: t('settings.colorScheme.light'),
    description: t('settings.colorScheme.lightCopy'),
  },
  {
    value: 'dark',
    label: t('settings.colorScheme.dark'),
    description: t('settings.colorScheme.darkCopy'),
  },
  {
    value: 'system',
    label: t('settings.colorScheme.system'),
    description: t('settings.colorScheme.systemCopy'),
  },
])

const motionOptions = computed<SettingsValueOption<SettingsMotion>[]>(() => [
  {
    value: 'full',
    label: t('settings.motion.full'),
    description: t('settings.motion.fullCopy'),
  },
  {
    value: 'reduced',
    label: t('settings.motion.reduced'),
    description: t('settings.motion.reducedCopy'),
  },
])

const sortOptions = computed<SettingsValueOption<SettingsSortOption>[]>(() => [
  {
    value: 'recent',
    label: t('player.sortRecent'),
    description: t('player.sortRecentCopy'),
  },
  {
    value: 'title',
    label: t('player.sortTitle'),
    description: t('player.sortTitleCopy'),
  },
  {
    value: 'duration',
    label: t('player.sortDuration'),
    description: t('player.sortDurationCopy'),
  },
  {
    value: 'size',
    label: t('player.sortSize'),
    description: t('player.sortSizeCopy'),
  },
])

const windowEffectsOptions = computed<SettingsValueOption<SettingsWindowEffect>[]>(() => [
  {
    value: 'auto',
    label: t('settings.windowEffects.auto'),
    description: t('settings.windowEffects.autoCopy'),
  },
  {
    value: 'full',
    label: t('settings.windowEffects.full'),
    description: t('settings.windowEffects.fullCopy'),
  },
  {
    value: 'balanced',
    label: t('settings.windowEffects.balanced'),
    description: t('settings.windowEffects.balancedCopy'),
  },
  {
    value: 'off',
    label: t('settings.windowEffects.off'),
    description: t('settings.windowEffects.offCopy'),
  },
  {
    value: 'web',
    label: t('settings.windowEffects.web'),
    description: t('settings.windowEffects.webCopy'),
  },
])

const immersiveTaskbarOptions = computed<SettingsValueOption<SettingsImmersiveTaskbarMode>[]>(() => [
  {
    value: 'show',
    label: t('settings.immersiveTaskbar.show'),
    description: t('settings.immersiveTaskbar.showCopy'),
  },
  {
    value: 'hide',
    label: t('settings.immersiveTaskbar.hide'),
    description: t('settings.immersiveTaskbar.hideCopy'),
  },
])

const currentSortOption = computed(() => {
  return sortOptions.value.find((option) => option.value === props.sortOption) ?? sortOptions.value[0]
})

const defaultPlaybackOutputName = computed(() => {
  return props.playbackOutputDevices.find((device) => device?.isDefault)?.name ?? ''
})

const activePlaybackOutputLabel = computed(() => {
  return props.activePlaybackOutputDeviceName || defaultPlaybackOutputName.value || t('settings.audioOutput.systemDefault')
})

const playbackOutputOptions = computed<PlaybackOutputChoice[]>(() => [
  {
    id: '',
    name: t('settings.audioOutput.systemDefault'),
    description: defaultPlaybackOutputName.value
      ? t('settings.audioOutput.systemDefaultCopy', {
          name: defaultPlaybackOutputName.value,
        })
      : t('settings.audioOutput.systemDefaultCopyEmpty'),
    backendLabel: '',
    isDefault: true,
  },
  ...props.playbackOutputDevices.map((device) => ({
    id: device?.id ?? '',
    name: formatPlaybackOutputName(device),
    description: device?.isDefault
      ? t('settings.audioOutput.deviceDefault')
      : t('settings.audioOutput.deviceManual'),
    backendLabel: device?.backendLabel ?? '',
    isDefault: device?.isDefault === true,
  })),
])

function formatPlaybackOutputName(device: PlaybackOutputDevice | null | undefined): string {
  const name = device?.name ?? ''
  const backend = String(device?.backend ?? '').toLowerCase()
  const backendLabel = device?.backendLabel ?? ''

  if (name && backendLabel && backend !== 'wasapi') {
    return `${name} (${backendLabel})`
  }

  return name
}

const activeCategoryTitle = computed(() => {
  return categories.value.find((item) => item.key === activeCategory.value) ?? categories.value[0]
})

const normalizedScanProgress = computed<NormalizedScanProgress>(() => {
  const injected = asRecord(unref(injectedScanProgress))
  const source = asRecord(props.scanProgress)

  return {
    ...EMPTY_SCAN_PROGRESS,
    ...injected,
    ...source,
  }
})

const normalizedStorageUsage = computed<NormalizedStorageUsage>(() => {
  const usage = asStorageUsage(props.storageUsage)

  return {
    totalBytes: normalizeByteCount(usage.totalBytes),
    reclaimableBytes: normalizeByteCount(usage.reclaimableBytes),
    items: Array.isArray(usage.items)
      ? usage.items.map((item) => normalizeStorageUsageItem(item as RawStorageUsageItem))
      : [],
    warnings: Array.isArray(usage.warnings) ? usage.warnings.filter((item) => typeof item === 'string') : [],
    generatedAt: typeof usage.generatedAt === 'string' ? usage.generatedAt : '',
  }
})

const normalizedAppUpdate = computed<NormalizedAppUpdateState>(() => {
  const updateState = asRecord(props.appUpdateState) as RawAppUpdateState
  const progress = asAppUpdateProgressState(updateState.progress)

  return {
    status: typeof updateState.status === 'string' ? updateState.status : 'idle',
    channel: typeof updateState.channel === 'string' && updateState.channel ? updateState.channel : 'stable',
    currentVersion:
      typeof updateState.currentVersion === 'string' && updateState.currentVersion
        ? updateState.currentVersion
        : '26.0.1',
    lastCheckedAt: typeof updateState.lastCheckedAt === 'string' ? updateState.lastCheckedAt : '',
    lastError: typeof updateState.lastError === 'string' ? updateState.lastError : '',
    availableUpdate: asAvailableUpdate(updateState.availableUpdate),
    latestAnnouncement: asAppUpdateAnnouncement(updateState.latestAnnouncement),
    unseenAnnouncement: updateState.unseenAnnouncement === true,
    progress: {
      downloadedBytes: normalizeByteCount(progress.downloadedBytes),
      contentLength: normalizeByteCount(progress.contentLength),
      percent: normalizePercent(progress.percent),
      finished: progress.finished === true,
    },
  }
})

const appUpdateAvailable = computed(() => Boolean(normalizedAppUpdate.value.availableUpdate))
const isCheckingUpdate = computed(() => normalizedAppUpdate.value.status === 'checking')
const isInstallingUpdate = computed(() => ['downloading', 'restarting'].includes(normalizedAppUpdate.value.status))
const canInstallUpdate = computed(() => appUpdateAvailable.value && !isCheckingUpdate.value && !isInstallingUpdate.value)
const appUpdateAnnouncement = computed(() =>
  normalizedAppUpdate.value.latestAnnouncement ??
  normalizedAppUpdate.value.availableUpdate?.announcement ??
  (normalizedAppUpdate.value.availableUpdate
    ? {
        version: normalizedAppUpdate.value.availableUpdate.version,
        title: t('settings.updates.availableVersion', {
          version: normalizedAppUpdate.value.availableUpdate.version,
        }),
        message: normalizedAppUpdate.value.availableUpdate.notes,
        releaseNotesUrl: normalizedAppUpdate.value.availableUpdate.releaseNotesUrl,
        severity: normalizedAppUpdate.value.availableUpdate.severity,
      }
    : null),
)
const appUpdateSeverity = computed(() => normalizedAppUpdate.value.availableUpdate?.severity ?? 'normal')
const appUpdateAnnouncementSeverity = computed(() =>
  appUpdateAnnouncement.value?.severity ||
  appUpdateSeverity.value ||
  'normal',
)
const appUpdateSeverityLabel = computed(() => t(`settings.updates.severity.${appUpdateAnnouncementSeverity.value}`))
const updateProgressPercent = computed(() => normalizePercent(normalizedAppUpdate.value.progress.percent))
const updateProgressLabel = computed(() => `${updateProgressPercent.value}%`)
const appVersionLabel = computed(() =>
  t('settings.updates.currentVersionValue', {
    version: normalizedAppUpdate.value.currentVersion || '26.0.1',
  }),
)
const updateChannelLabel = computed(() =>
  t('settings.updates.channelValue', {
    channel: normalizedAppUpdate.value.channel,
  }),
)
const appUpdateAnnouncementTitle = computed(() =>
  appUpdateAnnouncement.value?.title ||
  (appUpdateAnnouncement.value?.version
    ? t('settings.updates.availableVersion', { version: appUpdateAnnouncement.value.version })
    : ''),
)
const appUpdateAnnouncementCopy = computed(() =>
  appUpdateAnnouncement.value?.message ||
  normalizedAppUpdate.value.availableUpdate?.notes ||
  t('settings.updates.noNotes'),
)

const formattedLastUpdateCheckAt = computed(() =>
  formatDateTime(normalizedAppUpdate.value.lastCheckedAt, t('settings.updates.neverChecked')),
)

const appUpdateStatusTitle = computed(() => {
  if (normalizedAppUpdate.value.lastError) {
    return t('settings.updates.statusError')
  }

  switch (normalizedAppUpdate.value.status) {
    case 'checking':
      return t('settings.updates.statusChecking')
    case 'available':
      return t('settings.updates.statusAvailable')
    case 'downloading':
      return t('settings.updates.statusDownloading')
    case 'restarting':
      return t('settings.updates.statusRestarting')
    case 'current':
      return t('settings.updates.statusCurrent')
    case 'unavailable':
      return t('settings.updates.statusUnavailable')
    default:
      return t('settings.updates.statusIdle')
  }
})

const appUpdateStatusCopy = computed(() => {
  if (normalizedAppUpdate.value.lastError) {
    return normalizedAppUpdate.value.lastError
  }

  switch (normalizedAppUpdate.value.status) {
    case 'checking':
      return t('settings.updates.statusCheckingCopy')
    case 'available':
      return t('settings.updates.statusAvailableCopy')
    case 'downloading':
      return t('settings.updates.statusDownloadingCopy')
    case 'restarting':
      return t('settings.updates.statusRestartingCopy')
    case 'current':
      return t('settings.updates.statusCurrentCopy')
    case 'unavailable':
      return t('settings.updates.statusUnavailableCopy')
    default:
      return t('settings.updates.statusIdleCopy')
  }
})

const normalizedDiagnosticsReportStatus = computed<NormalizedDiagnosticsReportStatus>(() => {
  const status = props.diagnosticsReportStatus
    ? asDiagnosticsStatus(props.diagnosticsReportStatus)
    : EMPTY_DIAGNOSTICS_STATUS

  return {
    state: typeof status.state === 'string' ? status.state : 'idle',
    message: typeof status.message === 'string' ? status.message : '',
    uploadedAt: typeof status.uploadedAt === 'string' ? status.uploadedAt : '',
    eventCount: asNormalizedDiagnosticsCount(status.eventCount),
  }
})

const diagnosticsReportStatusTitle = computed(() => {
  switch (normalizedDiagnosticsReportStatus.value.state) {
    case 'uploading':
      return t('settings.telemetry.uploading')
    case 'uploaded':
      return t('settings.telemetry.uploaded')
    case 'blocked':
      return t('settings.telemetry.uploadBlocked')
    case 'error':
      return t('settings.telemetry.uploadFailed')
    default:
      return t('settings.telemetry.uploadReady')
  }
})

const diagnosticsReportStatusCopy = computed(() => {
  if (normalizedDiagnosticsReportStatus.value.message === 'consent_required') {
    return t('settings.telemetry.uploadConsentRequired')
  }

  if (normalizedDiagnosticsReportStatus.value.message === 'endpoint_missing') {
    return t('settings.telemetry.uploadEndpointMissing')
  }

  if (normalizedDiagnosticsReportStatus.value.state === 'uploaded') {
    return t('settings.telemetry.uploadedCopy', {
      count: normalizedDiagnosticsReportStatus.value.eventCount,
    })
  }

  if (normalizedDiagnosticsReportStatus.value.state === 'error') {
    return normalizedDiagnosticsReportStatus.value.message || t('settings.telemetry.uploadFailedCopy')
  }

  return t('settings.telemetry.uploadCopy')
})

const storageUsageItems = computed(() =>
  normalizedStorageUsage.value.items.filter((item) => item.bytes > 0 || item.reclaimableBytes > 0 || item.path),
)

const storageUsagePeakBytes = computed(() =>
  Math.max(1, ...storageUsageItems.value.map((item) => item.bytes)),
)

const storageUsageTotalLabel = computed(() => formatBytes(normalizedStorageUsage.value.totalBytes))
const storageUsageReclaimableLabel = computed(() => formatBytes(normalizedStorageUsage.value.reclaimableBytes))
const canCollectStorageGarbage = computed(
  () =>
    props.canManageStorage &&
    !props.isCollectingGarbage &&
    !props.isLoadingStorageUsage &&
    !normalizedScanProgress.value.active &&
    normalizedStorageUsage.value.reclaimableBytes > 0,
)

const isScanProgressVisible = computed(() => normalizedScanProgress.value.visible)

const scanProgressPercent = computed(() => {
  const nextPercent = Number(normalizedScanProgress.value.percent ?? 0)

  if (!Number.isFinite(nextPercent)) {
    return 0
  }

  return Math.max(0, Math.min(100, Math.round(nextPercent)))
})

const formattedLastScanAt = computed(() => {
  if (!props.lastScanAt) {
    return t('settings.scan.never')
  }

  const date = new Date(props.lastScanAt)

  if (Number.isNaN(date.getTime())) {
    return t('settings.scan.never')
  }

  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
})

const scanProgressTitle = computed(() => {
  switch (normalizedScanProgress.value.phase) {
    case 'discovering':
      return t('settings.scan.discovering')
    case 'preparing':
      return t('settings.scan.preparing')
    case 'processing':
      return t('settings.scan.processing')
    case 'importing':
      return t('settings.scan.importing')
    case 'complete':
      return t('settings.scan.complete')
    case 'empty':
      return t('settings.scan.empty')
    case 'error':
      return t('settings.scan.error')
    default:
      return t('settings.scan.progressLabel')
  }
})

const scanProgressCopy = computed(() => {
  switch (normalizedScanProgress.value.phase) {
    case 'discovering':
      return t('settings.scan.discoveringCopy')
    case 'preparing':
      return t('settings.scan.preparingCopy')
    case 'processing':
      return t('settings.scan.processingCopy')
    case 'importing':
      return t('settings.scan.importingCopy')
    case 'complete':
      return t('settings.scan.completeCopy', {
        count: normalizedScanProgress.value.imported ?? 0,
      })
    case 'empty':
      return t('settings.scan.emptyCopy')
    case 'error':
      return normalizedScanProgress.value.error || t('settings.scan.errorCopy')
    default:
      return ''
  }
})

const scanProgressMeta = computed(() => {
  if (['discovering', 'preparing', 'processing', 'importing'].includes(normalizedScanProgress.value.phase)) {
    return t('settings.scan.progressCount', {
      current: normalizedScanProgress.value.processed ?? 0,
      total: normalizedScanProgress.value.total ?? 0,
    })
  }

  switch (normalizedScanProgress.value.mode) {
    case 'watch':
      return t('settings.scan.progressModeWatch')
    case 'auto':
      return t('settings.scan.progressModeAuto')
    case 'manual':
    default:
      return t('settings.scan.progressModeManual')
  }
})

function normalizeByteCount(value: unknown): number {
  if (typeof value !== 'number') {
    return normalizeBytes(value)
  }

  return Number.isFinite(value) && value > 0 ? Math.round(value) : 0
}

function normalizeCount(value: unknown): number {
  if (typeof value !== 'number') {
    const numberValue = Number(value)
    return Number.isInteger(numberValue) && numberValue > 0 ? numberValue : 0
  }

  return Number.isInteger(value) && value > 0 ? value : 0
}

function normalizePercent(value: unknown): number {
  if (typeof value !== 'number') {
    const numberValue = Number(value)
    if (!Number.isFinite(numberValue)) {
      return 0
    }

    return Math.max(0, Math.min(100, Math.round(numberValue)))
  }

  if (!Number.isFinite(value)) {
    return 0
  }

  return Math.max(0, Math.min(100, Math.round(value)))
}

function formatDateTime(value: string | number | Date | null | undefined, fallback: string): string {
  if (!value) {
    return fallback
  }

  const date = new Date(value)

  if (Number.isNaN(date.getTime())) {
    return fallback
  }

  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function normalizeStorageUsageItem(item: RawStorageUsageItem = {} as RawStorageUsageItem): StorageUsageItemDefaults {
  const key = typeof item.key === 'string' && item.key ? item.key : 'otherCache'

  return {
    key,
    path: typeof item.path === 'string' ? item.path : '',
    bytes: normalizeByteCount(item.bytes),
    fileCount: normalizeCount(item.fileCount),
    directoryCount: normalizeCount(item.directoryCount),
    reclaimableBytes: normalizeByteCount(item.reclaimableBytes),
    reclaimableFileCount: normalizeCount(item.reclaimableFileCount),
    details:
      item.details && typeof item.details === 'object' && !Array.isArray(item.details)
        ? (item.details as UnknownRecord)
        : null,
  }
}

function formatBytes(bytes: unknown): string {
  const safeBytes = normalizeByteCount(bytes)

  if (safeBytes <= 0) {
    return '0 B'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = safeBytes
  let unitIndex = 0

  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  const formatter = new Intl.NumberFormat(undefined, {
    maximumFractionDigits: value >= 10 || unitIndex === 0 ? 0 : 1,
  })

  return `${formatter.format(value)} ${units[unitIndex]}`
}

function storageUsageItemLabel(item: StorageUsageItemDefaults): string {
  const key = `settings.storageUsage.items.${item.key}.label`
  const label = t(key)
  return label === key ? item.key : label
}

function storageUsageItemDescription(item: StorageUsageItemDefaults): string {
  const key = `settings.storageUsage.items.${item.key}.description`
  const description = t(key)
  return description === key ? item.path : description
}

function storageUsageBarWidth(item: StorageUsageItemDefaults): string {
  return `${Math.max(2, Math.round((item.bytes / storageUsagePeakBytes.value) * 100))}%`
}

function storageUsageIconKind(item: StorageUsageItemDefaults): 'database' | 'drive' {
  return item.key === 'database' || item.key === 'diagnostics' ? 'database' : 'drive'
}

function normalizeSettingsCategory(category: string | undefined): SettingsCategory {
  return SETTINGS_CATEGORIES.has(category as SettingsCategory) ? (category as SettingsCategory) : 'general'
}

watch(
  () => props.isOpen,
  (isOpen) => {
    if (isOpen) {
      activeCategory.value = normalizeSettingsCategory(props.initialCategory)

      if (typeof document !== 'undefined') {
        document.body.classList.add('has-modal-open')
      }
    } else {
      isSortMenuOpen.value = false
      isResetDataDialogOpen.value = false
      isStorageWindowOpen.value = false

      if (typeof document !== 'undefined') {
        document.body.classList.remove('has-modal-open')
      }
    }
  },
  { immediate: true },
)

watch(
  () => props.initialCategory,
  (category) => {
    if (props.isOpen) {
      activeCategory.value = normalizeSettingsCategory(category)
    }
  },
)

watch(
  () => props.notice?.id,
  () => {
    if (props.isOpen && props.notice?.category) {
      activeCategory.value = normalizeSettingsCategory(props.notice.category)
    }
  },
)

function closeSortMenu() {
  isSortMenuOpen.value = false
}

function toggleSortMenu() {
  isSortMenuOpen.value = !isSortMenuOpen.value
}

function selectSortOption(value: SettingsSortOption) {
  emit('set-sort-option', value)
  closeSortMenu()
}

function openResetDataDialog() {
  if (props.isResettingData || normalizedScanProgress.value.active) {
    return
  }

  isResetDataDialogOpen.value = true
}

function closeResetDataDialog() {
  isResetDataDialogOpen.value = false
}

function confirmResetData() {
  emit('reset-all-data')
}

function openStorageWindow() {
  if (!props.canManageStorage) {
    return
  }

  isStorageWindowOpen.value = true
  emit('refresh-storage-usage')
}

function closeStorageWindow() {
  isStorageWindowOpen.value = false
}

function handleDocumentPointerDown(event: PointerEvent) {
  if (!props.isOpen || !isSortMenuOpen.value) {
    return
  }

  const target = event.target instanceof Node ? event.target : null

  if (sortMenuRef.value?.contains(target) || sortMenuButtonRef.value?.contains(target)) {
    return
  }

  closeSortMenu()
}

function handleKeydown(event: KeyboardEvent) {
  if (!props.isOpen) {
    return
  }

  if (event.key === 'Escape') {
    if (isStorageWindowOpen.value) {
      closeStorageWindow()
      return
    }

    if (isSortMenuOpen.value) {
      closeSortMenu()
      return
    }

    emit('close')
  }
}

onMounted(() => {
  window.addEventListener('keydown', handleKeydown)
  document.addEventListener('pointerdown', handleDocumentPointerDown)
})

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') {
    document.body.classList.remove('has-modal-open')
  }

  window.removeEventListener('keydown', handleKeydown)
  document.removeEventListener('pointerdown', handleDocumentPointerDown)
})
</script>

<template>
  <Teleport to="body">
    <transition name="settings-modal">
      <div v-if="isOpen" class="settings-modal-root">
        <div class="settings-modal-backdrop" @click="emit('close')"></div>

        <section
          class="settings-modal"
          role="dialog"
          aria-modal="true"
          :aria-label="t('settings.title')"
        >
          <header class="settings-modal-header">
            <div>
              <p class="eyebrow">{{ t('settings.eyebrow') }}</p>
              <h2>{{ t('settings.title') }}</h2>
            </div>

            <button
              class="settings-modal-close"
              type="button"
              :aria-label="t('settings.close')"
              @click="emit('close')"
            >
              <X aria-hidden="true" />
            </button>
          </header>

          <div class="settings-modal-body">
            <aside class="settings-modal-nav" :aria-label="t('settings.navAria')">
              <button
                v-if="canManageStorage"
                class="settings-nav-item settings-nav-storage"
                type="button"
                @click="openStorageWindow"
              >
                <span class="settings-nav-storage-head">
                  <HardDrive aria-hidden="true" />
                  {{ t('settings.storageUsage.navTitle') }}
                </span>
                <small>{{ t('settings.storageUsage.navCopy') }}</small>
              </button>

              <button
                v-for="item in categories"
                :key="item.key"
                class="settings-nav-item"
                :class="{ 'is-active': item.key === activeCategory }"
                type="button"
                @click="activeCategory = item.key"
              >
                <span>{{ item.label }}</span>
                <small>{{ item.description }}</small>
              </button>
            </aside>

            <section class="settings-modal-content">
              <header class="settings-category-head">
                <strong>{{ activeCategoryTitle.label }}</strong>
                <span>{{ activeCategoryTitle.description }}</span>
              </header>

              <div v-if="activeCategory === 'general'" class="settings-panel-list">
                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.language') }}</span>
                    <p>{{ t('settings.fields.languageCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in languageOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === language }"
                      type="button"
                      @click="emit('set-language', option.value)"
                    >
                      <Globe class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === language" class="settings-choice-check" />
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.updates.title') }}</span>
                    <p>{{ t('settings.updates.copy') }}</p>
                  </div>

                  <div class="settings-update-grid">
                    <div class="settings-storage-summary is-muted">
                      <strong>{{ t('settings.updates.currentVersion') }}</strong>
                      <span>{{ appVersionLabel }}</span>
                    </div>
                    <div class="settings-storage-summary is-muted">
                      <strong>{{ t('settings.updates.channel') }}</strong>
                      <span>{{ updateChannelLabel }}</span>
                    </div>
                    <div class="settings-storage-summary is-muted">
                      <strong>{{ t('settings.updates.lastChecked') }}</strong>
                      <span>{{ formattedLastUpdateCheckAt }}</span>
                    </div>
                  </div>

                  <div
                    class="settings-update-status"
                    :class="{
                      'is-error': Boolean(normalizedAppUpdate.lastError),
                      'is-available': appUpdateAvailable,
                    }"
                  >
                    <strong>{{ appUpdateStatusTitle }}</strong>
                    <span>{{ appUpdateStatusCopy }}</span>
                  </div>

                  <div
                    v-if="appUpdateAnnouncement"
                    class="settings-update-card"
                    :class="`is-${appUpdateAnnouncementSeverity}`"
                  >
                    <div class="settings-update-card-head">
                      <div>
                        <span>
                          {{ normalizedAppUpdate.unseenAnnouncement
                            ? t('settings.updates.newAnnouncement')
                            : appUpdateSeverityLabel }}
                        </span>
                        <strong>
                          {{ appUpdateAnnouncementTitle }}
                        </strong>
                      </div>
                      <Download aria-hidden="true" />
                    </div>

                    <p>{{ appUpdateAnnouncementCopy }}</p>

                    <a
                      v-if="appUpdateAnnouncement.releaseNotesUrl"
                      class="settings-update-link"
                      :href="appUpdateAnnouncement.releaseNotesUrl"
                      target="_blank"
                      rel="noreferrer"
                    >
                      {{ t('settings.updates.releaseNotes') }}
                    </a>
                  </div>

                  <div v-if="isInstallingUpdate" class="settings-scan-progress">
                    <div class="settings-scan-progress-head">
                      <div class="settings-scan-progress-copy">
                        <strong>{{ t('settings.updates.downloadProgress') }}</strong>
                        <span>{{ t('settings.updates.downloadProgressCopy') }}</span>
                      </div>
                      <em>{{ updateProgressLabel }}</em>
                    </div>

                    <div
                      class="settings-scan-progress-track"
                      role="progressbar"
                      :aria-label="t('settings.updates.downloadProgress')"
                      :aria-valuemin="0"
                      :aria-valuemax="100"
                      :aria-valuenow="updateProgressPercent"
                    >
                      <span
                        class="settings-scan-progress-fill"
                        :style="{ width: `${updateProgressPercent}%` }"
                      ></span>
                    </div>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button is-secondary"
                      type="button"
                      :disabled="isCheckingUpdate || isInstallingUpdate"
                      @click="emit('check-app-update')"
                    >
                      <RefreshCcw aria-hidden="true" />
                      {{ isCheckingUpdate ? t('settings.updates.checking') : t('settings.updates.checkNow') }}
                    </button>
                    <button
                      class="settings-action-button"
                      type="button"
                      :disabled="!canInstallUpdate"
                      @click="emit('download-and-install-update')"
                    >
                      <Download aria-hidden="true" />
                      {{ isInstallingUpdate ? t('settings.updates.installing') : t('settings.updates.install') }}
                    </button>
                  </div>
                </article>
              </div>

              <div v-else-if="activeCategory === 'appearance'" class="settings-panel-list">
                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.theme') }}</span>
                    <p>{{ t('settings.fields.themeCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in themeOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === theme }"
                      type="button"
                      @click="emit('set-theme', option.value)"
                    >
                      <Palette class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === theme" class="settings-choice-check" />
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.colorScheme') }}</span>
                    <p>{{ t('settings.fields.colorSchemeCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in colorSchemeOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === colorScheme }"
                      type="button"
                      @click="emit('set-color-scheme', option.value)"
                    >
                      <Sun v-if="option.value === 'light'" class="settings-choice-icon" aria-hidden="true" />
                      <Moon v-else-if="option.value === 'dark'" class="settings-choice-icon" aria-hidden="true" />
                      <Monitor v-else class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === colorScheme" class="settings-choice-check" />
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.motion') }}</span>
                    <p>{{ t('settings.fields.motionCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in motionOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === motion }"
                      type="button"
                      @click="emit('set-motion', option.value)"
                    >
                      <MonitorCog class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === motion" class="settings-choice-check" />
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.windowEffects') }}</span>
                    <p>{{ t('settings.fields.windowEffectsCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in windowEffectsOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === windowEffects }"
                      type="button"
                      @click="emit('set-window-effects', option.value)"
                    >
                      <SlidersHorizontal class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === windowEffects" class="settings-choice-check" />
                    </button>
                  </div>
                </article>
              </div>

              <div v-else-if="activeCategory === 'playback'" class="settings-panel-list">
                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.rememberVolume') }}</span>
                    <p>{{ t('settings.fields.rememberVolumeCopy') }}</p>
                  </div>

                  <button
                    class="settings-toggle"
                    :class="{ 'is-active': rememberVolume }"
                    type="button"
                    :aria-pressed="rememberVolume"
                    @click="emit('set-remember-volume', !rememberVolume)"
                  >
                    <span>{{ rememberVolume ? t('settings.on') : t('settings.off') }}</span>
                    <span class="settings-toggle-thumb"></span>
                  </button>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.immersiveTaskbar') }}</span>
                    <p>{{ t('settings.fields.immersiveTaskbarCopy') }}</p>
                  </div>

                  <div class="settings-choice-grid">
                    <button
                      v-for="option in immersiveTaskbarOptions"
                      :key="option.value"
                      class="settings-choice"
                      :class="{ 'is-active': option.value === immersiveTaskbarMode }"
                      type="button"
                      @click="emit('set-immersive-taskbar-mode', option.value)"
                    >
                      <Monitor class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.label }}</strong>
                      <span>{{ option.description }}</span>
                      <Check v-if="option.value === immersiveTaskbarMode" class="settings-choice-check" />
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.audioOutput') }}</span>
                    <p>{{ t('settings.fields.audioOutputCopy') }}</p>
                  </div>

                  <div v-if="playbackOutputDevices.length > 0" class="settings-choice-grid">
                    <button
                      v-for="option in playbackOutputOptions"
                      :key="option.id || '__system__'"
                      class="settings-choice"
                      :class="{ 'is-active': option.id === playbackOutputDeviceId }"
                      type="button"
                      :disabled="isPlaying"
                      @click="emit('set-playback-output-device', option.id)"
                    >
                      <Monitor class="settings-choice-icon" aria-hidden="true" />
                      <strong>{{ option.name }}</strong>
                      <span>{{ option.description }}</span>
                      <Check
                        v-if="option.id === playbackOutputDeviceId"
                        class="settings-choice-check"
                        aria-hidden="true"
                      />
                    </button>
                  </div>
                  <div v-else class="settings-storage-summary is-empty">
                    <strong>{{ t('settings.audioOutput.noDevices') }}</strong>
                    <span>{{ t('settings.audioOutput.noDevicesCopy') }}</span>
                  </div>

                  <div v-if="isPlaying" class="settings-storage-summary is-warning">
                    <strong>{{ t('settings.audioOutput.switchLocked') }}</strong>
                    <span>{{ t('settings.audioOutput.switchLockedCopy') }}</span>
                  </div>

                  <div class="settings-storage-summary is-muted">
                    <strong>{{ t('settings.audioOutput.currentRoute') }}</strong>
                    <span>{{ activePlaybackOutputLabel }}</span>
                  </div>

                  <div
                    v-if="!playbackOutputDeviceAvailable && playbackOutputDeviceId"
                    class="settings-storage-summary is-muted"
                  >
                    <strong>{{ t('settings.audioOutput.preferenceUnavailable') }}</strong>
                    <span>{{ t('settings.audioOutput.preferenceUnavailableCopy') }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button is-secondary"
                      type="button"
                      @click="emit('refresh-playback-output-devices')"
                    >
                      {{ t('settings.audioOutput.refresh') }}
                    </button>
                  </div>
                </article>

              </div>

              <div v-else-if="activeCategory === 'privacy'" class="settings-panel-list">
                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.guide.title') }}</span>
                    <p>{{ t('settings.guide.copy') }}</p>
                  </div>

                  <div class="settings-action-row">
                    <button class="settings-action-button is-secondary" type="button" @click="emit('reopen-onboarding')">
                      {{ t('settings.guide.action') }}
                    </button>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.telemetry.title') }}</span>
                    <p>{{ t('settings.telemetry.copy') }}</p>
                  </div>

                  <div class="settings-storage-summary is-muted">
                    <strong>{{ t('settings.telemetry.status') }}</strong>
                    <span>
                      {{ telemetryEnabled === true ? t('settings.telemetry.on') : t('settings.telemetry.off') }}
                    </span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button"
                      type="button"
                      :disabled="telemetryEnabled === true"
                      @click="emit('set-telemetry-consent', true)"
                    >
                      {{ t('settings.telemetry.allow') }}
                    </button>
                    <button
                      class="settings-action-button is-secondary"
                      type="button"
                      :disabled="telemetryEnabled === false"
                      @click="emit('set-telemetry-consent', false)"
                    >
                      {{ t('settings.telemetry.decline') }}
                    </button>
                  </div>

                  <div class="settings-storage-summary is-muted">
                    <strong>{{ diagnosticsReportStatusTitle }}</strong>
                    <span>{{ diagnosticsReportStatusCopy }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button is-secondary"
                      type="button"
                      :disabled="telemetryEnabled !== true || isUploadingDiagnosticsReport"
                      @click="emit('upload-diagnostics-report')"
                    >
                      <UploadCloud aria-hidden="true" />
                      {{
                        isUploadingDiagnosticsReport
                          ? t('settings.telemetry.uploadingAction')
                          : t('settings.telemetry.uploadAction')
                      }}
                    </button>
                  </div>
                </article>
              </div>

              <div v-else-if="activeCategory === 'library'" class="settings-panel-list">
                <article v-if="canManageStorage" class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.storageRoot') }}</span>
                    <p>{{ t('settings.fields.storageRootCopy') }}</p>
                  </div>

                  <div class="settings-storage-summary" :class="{ 'is-empty': !storageRoot }">
                    <strong>{{ storageRoot || t('settings.storage.notConfigured') }}</strong>
                    <span>{{ t('settings.storage.managedCopy') }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button"
                      type="button"
                      :disabled="normalizedScanProgress.active"
                      @click="emit('select-storage-root')"
                    >
                      {{ storageRoot ? t('settings.storage.changeRoot') : t('settings.storage.pickRoot') }}
                    </button>
                  </div>
                </article>

                <article v-if="canManageStorage" class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.scanDirectories') }}</span>
                    <p>{{ t('settings.fields.scanDirectoriesCopy') }}</p>
                  </div>

                  <div v-if="scanDirectories.length > 0" class="settings-directory-list">
                    <div v-for="directory in scanDirectories" :key="directory" class="settings-directory-item">
                      <span>{{ directory }}</span>
                      <button
                        class="settings-directory-remove"
                        type="button"
                        :disabled="normalizedScanProgress.active"
                        :aria-label="t('settings.scan.removeDirectory')"
                        @click="emit('remove-scan-directory', directory)"
                      >
                        <X aria-hidden="true" />
                      </button>
                    </div>
                  </div>
                  <div v-else class="settings-storage-summary is-empty">
                    <strong>{{ t('settings.scan.noDirectories') }}</strong>
                    <span>{{ t('settings.scan.noDirectoriesCopy') }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button"
                      type="button"
                      :disabled="normalizedScanProgress.active"
                      @click="emit('add-scan-directory')"
                    >
                      {{ t('settings.scan.addDirectory') }}
                    </button>
                    <button
                      class="settings-action-button is-secondary"
                      type="button"
                      :disabled="scanDirectories.length === 0 || normalizedScanProgress.active"
                      @click="emit('run-library-scan-import')"
                    >
                      {{ normalizedScanProgress.active ? t('settings.scan.runNowWorking') : t('settings.scan.runNow') }}
                    </button>
                  </div>

                  <div v-if="isScanProgressVisible" class="settings-scan-progress">
                    <div class="settings-scan-progress-head">
                      <div class="settings-scan-progress-copy">
                        <strong>{{ scanProgressTitle }}</strong>
                        <span>{{ scanProgressCopy }}</span>
                      </div>
                      <em>{{ scanProgressPercent }}%</em>
                    </div>

                    <div
                      class="settings-scan-progress-track"
                      role="progressbar"
                      :aria-label="t('settings.scan.progressLabel')"
                      :aria-valuemin="0"
                      :aria-valuemax="100"
                      :aria-valuenow="scanProgressPercent"
                    >
                      <span
                        class="settings-scan-progress-fill"
                        :class="{ 'is-error': normalizedScanProgress.phase === 'error' }"
                        :style="{ width: `${scanProgressPercent}%` }"
                      ></span>
                    </div>

                    <div class="settings-scan-progress-meta">
                      <span>{{ scanProgressMeta }}</span>
                    </div>

                    <div v-if="normalizedScanProgress.currentFile" class="settings-scan-progress-file">
                      <span>{{ t('settings.scan.currentFile') }}</span>
                      <strong>{{ normalizedScanProgress.currentFile }}</strong>
                    </div>
                  </div>

                  <div class="settings-storage-summary is-muted">
                    <strong>{{ t('settings.scan.lastScan') }}</strong>
                    <span>{{ formattedLastScanAt }}</span>
                  </div>
                </article>

                <article v-if="canManageStorage" class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.lyricsScanDirectories') }}</span>
                    <p>{{ t('settings.fields.lyricsScanDirectoriesCopy') }}</p>
                  </div>

                  <div v-if="lyricsScanDirectories.length > 0" class="settings-directory-list">
                    <div v-for="directory in lyricsScanDirectories" :key="directory" class="settings-directory-item">
                      <span>{{ directory }}</span>
                      <button
                        class="settings-directory-remove"
                        type="button"
                        :disabled="normalizedScanProgress.active"
                        :aria-label="t('settings.scan.removeLyricsDirectory')"
                        @click="emit('remove-lyrics-scan-directory', directory)"
                      >
                        <X aria-hidden="true" />
                      </button>
                    </div>
                  </div>
                  <div v-else class="settings-storage-summary is-empty">
                    <strong>{{ t('settings.scan.noLyricsDirectories') }}</strong>
                    <span>{{ t('settings.scan.noLyricsDirectoriesCopy') }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button"
                      type="button"
                      :disabled="normalizedScanProgress.active"
                      @click="emit('add-lyrics-scan-directory')"
                    >
                      {{ t('settings.scan.addLyricsDirectory') }}
                    </button>
                  </div>
                </article>

                <article v-if="canManageStorage" class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.autoScanOnLaunch') }}</span>
                    <p>{{ t('settings.fields.autoScanOnLaunchCopy') }}</p>
                  </div>

                  <button
                    class="settings-toggle"
                    :class="{ 'is-active': autoScanOnLaunch }"
                    type="button"
                    :aria-pressed="autoScanOnLaunch"
                    @click="emit('set-auto-scan-on-launch', !autoScanOnLaunch)"
                  >
                    <span>{{ autoScanOnLaunch ? t('settings.on') : t('settings.off') }}</span>
                    <span class="settings-toggle-thumb"></span>
                  </button>
                </article>

                <article class="settings-panel-card" :class="{ 'has-open-menu': isSortMenuOpen }">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.defaultSort') }}</span>
                    <p>{{ t('settings.fields.defaultSortCopy') }}</p>
                  </div>

                  <div ref="sortMenuRef" class="settings-select-menu">
                    <button
                      ref="sortMenuButtonRef"
                      class="settings-select settings-select-trigger"
                      :class="{ 'is-open': isSortMenuOpen }"
                      type="button"
                      :aria-label="t('settings.fields.defaultSort')"
                      :aria-expanded="isSortMenuOpen"
                      aria-controls="settings-default-sort-menu"
                      @click="toggleSortMenu"
                    >
                      <SlidersHorizontal aria-hidden="true" />
                      <strong>{{ currentSortOption?.label }}</strong>
                      <ChevronDown aria-hidden="true" />
                    </button>

                    <Transition name="toolbar-menu">
                      <div
                        v-if="isSortMenuOpen"
                        id="settings-default-sort-menu"
                        class="settings-select-dropdown"
                        role="listbox"
                        :aria-label="t('settings.fields.defaultSort')"
                      >
                        <button
                          v-for="option in sortOptions"
                          :key="option.value"
                          class="settings-select-option"
                          :class="{ 'is-active': option.value === sortOption }"
                          type="button"
                          role="option"
                          :aria-selected="option.value === sortOption"
                          @click="selectSortOption(option.value)"
                        >
                          {{ option.label }}
                        </button>
                      </div>
                    </Transition>
                  </div>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.metaVisibility') }}</span>
                    <p>{{ t('settings.fields.metaVisibilityCopy') }}</p>
                  </div>

                  <button
                    class="settings-toggle"
                    :class="{ 'is-active': showTechnicalMetadata }"
                    type="button"
                    :aria-pressed="showTechnicalMetadata"
                    @click="emit('set-show-technical-metadata', !showTechnicalMetadata)"
                  >
                    <span>{{ showTechnicalMetadata ? t('settings.on') : t('settings.off') }}</span>
                    <span class="settings-toggle-thumb"></span>
                  </button>
                </article>

                <article class="settings-panel-card">
                  <div class="settings-panel-copy">
                    <span class="settings-panel-label">{{ t('settings.fields.resetData') }}</span>
                    <p>{{ t('settings.fields.resetDataCopy') }}</p>
                  </div>

                  <div class="settings-storage-summary is-danger">
                    <strong>{{ t('settings.resetData.impactTitle') }}</strong>
                    <span>{{ t('settings.resetData.impactCopy') }}</span>
                  </div>

                  <div class="settings-action-row">
                    <button
                      class="settings-action-button is-danger"
                      type="button"
                      :disabled="normalizedScanProgress.active || isResettingData"
                      @click="openResetDataDialog"
                    >
                      {{ isResettingData ? t('settings.resetData.actionWorking') : t('settings.resetData.action') }}
                    </button>
                  </div>
                </article>
              </div>
            </section>
          </div>
        </section>

        <Transition name="settings-storage-window">
          <div v-if="isStorageWindowOpen" class="settings-storage-window-root">
            <div class="settings-storage-window-backdrop" @click="closeStorageWindow"></div>

            <section
              class="settings-storage-window"
              role="dialog"
              aria-modal="true"
              :aria-label="t('settings.storageUsage.windowTitle')"
            >
              <header class="settings-storage-window-header">
                <div>
                  <p class="eyebrow">{{ t('settings.storageUsage.navTitle') }}</p>
                  <h3>{{ t('settings.storageUsage.windowTitle') }}</h3>
                  <span>{{ t('settings.storageUsage.windowCopy') }}</span>
                </div>

                <button
                  class="settings-modal-close"
                  type="button"
                  :aria-label="t('settings.storageUsage.closeWindow')"
                  @click="closeStorageWindow"
                >
                  <X aria-hidden="true" />
                </button>
              </header>

              <div class="settings-storage-window-body">
                <div class="settings-storage-window-dashboard">
                  <div class="settings-storage-usage-overview">
                    <div>
                      <span>{{ t('settings.storageUsage.total') }}</span>
                      <strong>{{ storageUsageTotalLabel }}</strong>
                    </div>
                    <div>
                      <span>{{ t('settings.storageUsage.reclaimable') }}</span>
                      <strong>{{ storageUsageReclaimableLabel }}</strong>
                    </div>
                  </div>

                  <div class="settings-storage-summary" :class="{ 'is-empty': !storageRoot }">
                    <strong>{{ t('settings.storageUsage.rootTitle') }}</strong>
                    <span>{{ storageRoot || t('settings.storageUsage.rootUnset') }}</span>
                  </div>
                </div>

                <div v-if="normalizedScanProgress.active" class="settings-storage-summary is-muted">
                  <strong>{{ t('settings.storageUsage.scanBusyTitle') }}</strong>
                  <span>{{ t('settings.storageUsage.scanBusyCopy') }}</span>
                </div>

                <div v-if="storageMaintenanceError" class="settings-storage-summary is-danger">
                  <strong>{{ t('settings.storageUsage.errorTitle') }}</strong>
                  <span>{{ storageMaintenanceError }}</span>
                </div>

                <div v-if="storageUsageItems.length > 0" class="settings-storage-usage-list">
                  <div v-for="item in storageUsageItems" :key="item.key" class="settings-storage-usage-item">
                    <div class="settings-storage-usage-icon" aria-hidden="true">
                      <Database v-if="storageUsageIconKind(item) === 'database'" />
                      <HardDrive v-else />
                    </div>

                    <div class="settings-storage-usage-main">
                      <div class="settings-storage-usage-row">
                        <strong>{{ storageUsageItemLabel(item) }}</strong>
                        <em>{{ formatBytes(item.bytes) }}</em>
                      </div>
                      <span>{{ storageUsageItemDescription(item) }}</span>
                      <code v-if="item.path">{{ item.path }}</code>
                      <div class="settings-storage-usage-bar" aria-hidden="true">
                        <i :style="{ width: storageUsageBarWidth(item) }"></i>
                      </div>
                      <small v-if="item.reclaimableBytes > 0">
                        {{ t('settings.storageUsage.cleanable', { size: formatBytes(item.reclaimableBytes) }) }}
                      </small>
                    </div>
                  </div>
                </div>

                <div v-else class="settings-storage-summary is-empty">
                  <strong>{{ t('settings.storageUsage.empty') }}</strong>
                  <span>{{ t('settings.storageUsage.emptyCopy') }}</span>
                </div>
              </div>

              <footer class="settings-storage-window-actions">
                <p>{{ t('settings.storageUsage.actionNote') }}</p>
                <div class="settings-storage-window-action-buttons">
                  <button
                    class="settings-action-button is-secondary"
                    type="button"
                    :disabled="isLoadingStorageUsage || isCollectingGarbage"
                    @click="emit('refresh-storage-usage')"
                  >
                    <RefreshCcw aria-hidden="true" />
                    {{ isLoadingStorageUsage ? t('settings.storageUsage.refreshWorking') : t('settings.storageUsage.refresh') }}
                  </button>
                  <button
                    class="settings-action-button"
                    type="button"
                    :disabled="!canCollectStorageGarbage"
                    @click="emit('collect-storage-garbage')"
                  >
                    <Trash2 aria-hidden="true" />
                    {{ isCollectingGarbage ? t('settings.storageUsage.collectWorking') : t('settings.storageUsage.collect') }}
                  </button>
                </div>
              </footer>
            </section>
          </div>
        </Transition>
      </div>
    </transition>

    <DialogModal
      :is-open="isResetDataDialogOpen"
      :title="t('settings.resetData.confirmTitle')"
      :message="t('settings.resetData.confirmCopy')"
      :confirm-label="t('settings.resetData.confirmButton')"
      :cancel-label="t('common.cancel')"
      :is-danger="true"
      @close="closeResetDataDialog"
      @confirm="confirmResetData"
    />
  </Teleport>
</template>
