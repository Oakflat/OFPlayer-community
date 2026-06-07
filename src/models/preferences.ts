import { DEFAULT_LIBRARY_ID } from './library'
import { DEFAULT_ACTIVE_COLLECTION_REF } from './collection'
import { clampVolume } from './playback'

export type LibrarySortOption = (typeof LIBRARY_SORT_OPTIONS)[number]
export type LanguageOption = (typeof LANGUAGE_OPTIONS)[number]
export type ThemeOption = (typeof THEME_OPTIONS)[number]
export type ColorSchemeOption = (typeof COLOR_SCHEME_OPTIONS)[number]
export type MotionOption = (typeof MOTION_OPTIONS)[number]
export type WindowEffectsOption = (typeof WINDOW_EFFECTS_OPTIONS)[number]
export type SidebarSectionOption = (typeof SIDEBAR_SECTION_OPTIONS)[number]
export type ImmersiveTaskbarMode = (typeof IMMERSIVE_TASKBAR_MODE_OPTIONS)[number]
export type RepeatMode = (typeof REPEAT_MODE_OPTIONS)[number]
export type TelemetryEnabled = boolean | null

export interface PreferencesModel {
  volume: number
  rememberVolume: boolean
  language: LanguageOption
  theme: ThemeOption
  colorScheme: ColorSchemeOption
  motion: MotionOption
  windowEffects: WindowEffectsOption
  showTechnicalMetadata: boolean
  playbackOutputDeviceId: string
  immersiveTaskbarMode: ImmersiveTaskbarMode
  repeatMode: RepeatMode
  shuffleEnabled: boolean
  librarySearchQuery: string
  librarySortOption: LibrarySortOption
  collectionSortOptions: Record<string, LibrarySortOption>
  libraryTypeFilter: string
  activeLibrary: string
  activeCollection: string
  sidebarSection: SidebarSectionOption
  storageRoot: string
  scanDirectories: string[]
  lyricsScanDirectories: string[]
  autoScanOnLaunch: boolean
  lastScanAt: string
  dataDriver: unknown
  telemetryEnabled: TelemetryEnabled
  [key: string]: any
}

export type PreferencesModelInput = Record<string, any> & Partial<PreferencesModel> & {
  playbackShuffleEnabled?: unknown
  playbackRepeatMode?: unknown
  localePreference?: unknown
  locale?: unknown
  showBitrateFileSize?: unknown
  audioOutputDeviceId?: unknown
  immersiveFullscreenMode?: unknown
  searchQuery?: unknown
  sortOption?: unknown
  activePlaylist?: unknown
  collectionSortOptions?: unknown
}

export const DEFAULT_VOLUME = 0.8
export const DEFAULT_LIBRARY_SORT = 'recent'
export const DEFAULT_LIBRARY_FILTER = 'all'
export const DEFAULT_LANGUAGE = 'system'
export const DEFAULT_THEME = 'mist'
export const DEFAULT_COLOR_SCHEME = 'system'
export const DEFAULT_MOTION = 'full'
export const DEFAULT_WINDOW_EFFECTS = 'auto'
export const DEFAULT_SIDEBAR_SECTION = 'library'
export const DEFAULT_STORAGE_ROOT = ''
export const DEFAULT_PLAYBACK_OUTPUT_DEVICE_ID = ''
export const DEFAULT_TELEMETRY_ENABLED = null
export const IMMERSIVE_TASKBAR_MODES = Object.freeze({
  SHOW: 'show',
  HIDE: 'hide',
} as const)
export const DEFAULT_IMMERSIVE_TASKBAR_MODE = IMMERSIVE_TASKBAR_MODES.SHOW
export const REPEAT_MODES = Object.freeze({
  NONE: 'none',
  ALL: 'all',
  ONE: 'one',
} as const)
export const DEFAULT_REPEAT_MODE = REPEAT_MODES.ALL

export const LIBRARY_SORT_OPTIONS = Object.freeze(['recent', 'title'] as const)
export const LANGUAGE_OPTIONS = Object.freeze(['system', 'en', 'zh-CN'] as const)
export const THEME_OPTIONS = Object.freeze(['mist', 'paper', 'material'] as const)
export const COLOR_SCHEME_OPTIONS = Object.freeze(['light', 'dark', 'system'] as const)
export const MOTION_OPTIONS = Object.freeze(['full', 'reduced'] as const)
export const WINDOW_EFFECTS_OPTIONS = Object.freeze(['auto', 'full', 'balanced', 'off', 'web'] as const)
export const SIDEBAR_SECTION_OPTIONS = Object.freeze(['library', 'playlist', 'view'] as const)
export const IMMERSIVE_TASKBAR_MODE_OPTIONS = Object.freeze(Object.values(IMMERSIVE_TASKBAR_MODES))
export const REPEAT_MODE_OPTIONS = Object.freeze(Object.values(REPEAT_MODES))

function normalizeSearchQuery(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function normalizeBoolean(value: unknown, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function normalizeEnum<const T extends readonly string[]>(value: unknown, options: T, fallback: T[number]): T[number] {
  return typeof value === 'string' && (options as readonly string[]).includes(value) ? value : fallback
}

function normalizeText(value: unknown, fallback: string): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeStringArray(values: unknown): string[] {
  if (!Array.isArray(values)) {
    return []
  }

  return [...new Set(values.map((value) => String(value ?? '').trim()).filter(Boolean))]
}

function normalizeOptionalTimestamp(value: unknown): string {
  const text = String(value ?? '').trim()
  return text || ''
}

export function normalizeLibrarySort(option: unknown): LibrarySortOption {
  return normalizeEnum(option, LIBRARY_SORT_OPTIONS, DEFAULT_LIBRARY_SORT)
}

export function normalizeLibraryTypeFilter(option: unknown): string {
  const value = String(option ?? DEFAULT_LIBRARY_FILTER).trim()
  return value || DEFAULT_LIBRARY_FILTER
}

export function normalizeLanguage(option: unknown): LanguageOption {
  return normalizeEnum(option, LANGUAGE_OPTIONS, DEFAULT_LANGUAGE)
}

export function normalizeTheme(option: unknown): ThemeOption {
  return normalizeEnum(option, THEME_OPTIONS, DEFAULT_THEME)
}

export function normalizeColorScheme(option: unknown): ColorSchemeOption {
  return normalizeEnum(option, COLOR_SCHEME_OPTIONS, DEFAULT_COLOR_SCHEME)
}

export function normalizeMotion(option: unknown): MotionOption {
  return normalizeEnum(option, MOTION_OPTIONS, DEFAULT_MOTION)
}

export function normalizeWindowEffects(option: unknown): WindowEffectsOption {
  return normalizeEnum(option, WINDOW_EFFECTS_OPTIONS, DEFAULT_WINDOW_EFFECTS)
}

export function normalizeSidebarSection(option: unknown): SidebarSectionOption {
  return normalizeEnum(option, SIDEBAR_SECTION_OPTIONS, DEFAULT_SIDEBAR_SECTION)
}

export function normalizeActiveLibrary(option: unknown): string {
  return normalizeText(option, DEFAULT_LIBRARY_ID)
}

export function normalizeActiveCollection(option: unknown): string {
  return normalizeText(option, DEFAULT_ACTIVE_COLLECTION_REF)
}

export function normalizeTelemetryEnabled(value: unknown): TelemetryEnabled {
  if (value === true || value === false) {
    return value
  }

  return DEFAULT_TELEMETRY_ENABLED
}

export function normalizeRepeatMode(value: unknown): RepeatMode {
  return normalizeEnum(value, REPEAT_MODE_OPTIONS, DEFAULT_REPEAT_MODE)
}

export function normalizeImmersiveTaskbarMode(value: unknown): ImmersiveTaskbarMode {
  return normalizeEnum(value, IMMERSIVE_TASKBAR_MODE_OPTIONS, DEFAULT_IMMERSIVE_TASKBAR_MODE)
}

/**
 * CN: 规范化集合排序偏好
 * CN: 格式: { [collectionRef]: sortOption }
 *
 * EN: Normalize collection sort preferences
 * EN: Format: { [collectionRef]: sortOption }
 */
function normalizeCollectionSortOptions(value: unknown): Record<string, LibrarySortOption> {
  if (typeof value !== 'object' || value === null) {
    return {}
  }

  const result: Record<string, LibrarySortOption> = {}
  for (const [collectionRef, sortOption] of Object.entries(value)) {
    if (typeof collectionRef === 'string' && collectionRef.trim()) {
      result[collectionRef.trim()] = normalizeLibrarySort(sortOption)
    }
  }
  return result
}

export function createPreferencesModel(overrides: PreferencesModelInput = {}): PreferencesModel {
  const shuffleEnabled = normalizeBoolean(
    overrides.shuffleEnabled ?? overrides.playbackShuffleEnabled,
    false,
  )
  const repeatMode = shuffleEnabled
    ? REPEAT_MODES.NONE
    : normalizeRepeatMode(overrides.repeatMode ?? overrides.playbackRepeatMode)

  return {
    volume: clampVolume(overrides.volume, DEFAULT_VOLUME),
    rememberVolume: normalizeBoolean(overrides.rememberVolume, true),
    language: normalizeLanguage(overrides.language ?? overrides.localePreference ?? overrides.locale),
    theme: normalizeTheme(overrides.theme),
    colorScheme: normalizeColorScheme(overrides.colorScheme),
    motion: normalizeMotion(overrides.motion),
    windowEffects: normalizeWindowEffects(overrides.windowEffects),
    showTechnicalMetadata: normalizeBoolean(
      overrides.showTechnicalMetadata ?? overrides.showBitrateFileSize,
      true,
    ),
    playbackOutputDeviceId: normalizeText(
      overrides.playbackOutputDeviceId ?? overrides.audioOutputDeviceId,
      DEFAULT_PLAYBACK_OUTPUT_DEVICE_ID,
    ),
    immersiveTaskbarMode: normalizeImmersiveTaskbarMode(
      overrides.immersiveTaskbarMode ?? overrides.immersiveFullscreenMode,
    ),
    repeatMode,
    shuffleEnabled,
    librarySearchQuery: normalizeSearchQuery(
      overrides.librarySearchQuery ?? overrides.searchQuery,
    ),
    librarySortOption: normalizeLibrarySort(
      overrides.librarySortOption ?? overrides.sortOption,
    ),
    // CN: 每个集合的排序偏好
    // EN: Sort preference for each collection
    collectionSortOptions: normalizeCollectionSortOptions(overrides.collectionSortOptions),
    libraryTypeFilter: normalizeLibraryTypeFilter(overrides.libraryTypeFilter),
    activeLibrary: normalizeActiveLibrary(overrides.activeLibrary),
    activeCollection: normalizeActiveCollection(
      overrides.activeCollection ?? overrides.activePlaylist,
    ),
    sidebarSection: normalizeSidebarSection(overrides.sidebarSection),
    storageRoot: normalizeText(overrides.storageRoot, DEFAULT_STORAGE_ROOT),
    scanDirectories: normalizeStringArray(overrides.scanDirectories),
    lyricsScanDirectories: normalizeStringArray(overrides.lyricsScanDirectories),
    autoScanOnLaunch: normalizeBoolean(overrides.autoScanOnLaunch, false),
    lastScanAt: normalizeOptionalTimestamp(overrides.lastScanAt),
    dataDriver: overrides.dataDriver ?? 'indexeddb',
    telemetryEnabled: normalizeTelemetryEnabled(overrides.telemetryEnabled),
  }
}

export function createPersistablePreferencesModel(preferences: PreferencesModelInput): PreferencesModel {
  const normalized = createPreferencesModel(preferences)

  return {
    ...normalized,
    volume: normalized.rememberVolume ? normalized.volume : DEFAULT_VOLUME,
  }
}
