import { computed, reactive, ref } from 'vue'
import type { ComputedRef, Ref } from 'vue'
import { getResolvedLocalePreference, setLocalePreference } from '../composables/useI18n'
import { createPreferencesModel } from '../models/preferences'
import type {
  ColorSchemeOption,
  ImmersiveTaskbarMode,
  LanguageOption,
  LibrarySortOption,
  MotionOption,
  PreferencesModel,
  PreferencesModelInput,
  RepeatMode,
  SidebarSectionOption,
  TelemetryEnabled,
  ThemeOption,
  WindowEffectsOption,
} from '../models/preferences'
import { setTelemetryEnabled } from '../services/telemetryService'
import { syncWindowSurface } from '../services/windowSurface'

type SettingsCategory = 'general' | 'appearance' | 'playback' | 'privacy' | 'library'
type SettingsNoticeKind = 'info' | 'success' | 'warning' | 'error' | string

interface PreferencesDataService {
  preferences: {
    load(): Promise<PreferencesModelInput | null | undefined>
    save(preferences: PreferencesModel): Promise<unknown> | unknown
  }
}

interface CreatePreferencesStoreOptions {
  dataService: PreferencesDataService
  initialPreferences?: PreferencesModelInput
}

interface SettingsNoticeInput {
  kind?: SettingsNoticeKind
  category?: unknown
}

export interface SettingsNotice {
  id: string
  kind: SettingsNoticeKind
  category: SettingsCategory
}

type OpenSettingsOptions = SettingsCategory | {
  category?: unknown
  notice?: SettingsNoticeInput | null
}

type PreferencesPatch = Record<string, unknown>

export interface PreferencesStore {
  state: PreferencesModel
  isSettingsOpen: Ref<boolean>
  volume: ComputedRef<number>
  rememberVolume: ComputedRef<boolean>
  language: ComputedRef<LanguageOption>
  theme: ComputedRef<ThemeOption>
  colorScheme: ComputedRef<ColorSchemeOption>
  motion: ComputedRef<MotionOption>
  windowEffects: ComputedRef<WindowEffectsOption>
  showTechnicalMetadata: ComputedRef<boolean>
  playbackOutputDeviceId: ComputedRef<string>
  immersiveTaskbarMode: ComputedRef<ImmersiveTaskbarMode>
  repeatMode: ComputedRef<RepeatMode>
  shuffleEnabled: ComputedRef<boolean>
  searchQuery: ComputedRef<string>
  sortOption: ComputedRef<LibrarySortOption>
  collectionSortOptions: ComputedRef<Record<string, LibrarySortOption>>
  typeFilter: ComputedRef<string>
  activeLibrary: ComputedRef<string>
  activeCollection: ComputedRef<string>
  sidebarSection: ComputedRef<SidebarSectionOption>
  storageRoot: ComputedRef<string>
  scanDirectories: ComputedRef<string[]>
  lyricsScanDirectories: ComputedRef<string[]>
  autoScanOnLaunch: ComputedRef<boolean>
  lastScanAt: ComputedRef<string>
  telemetryEnabled: ComputedRef<TelemetryEnabled>
  settingsInitialCategory: ComputedRef<SettingsCategory>
  settingsNotice: ComputedRef<SettingsNotice | null>
  hydrate: (preloadedPreferences?: PreferencesModelInput | null) => Promise<PreferencesModel>
  openSettings: (options?: OpenSettingsOptions) => true
  closeSettings: () => false
  toggleSettings: () => boolean
  setSearchQuery: (query: string) => string
  setSortOption: (option: unknown) => LibrarySortOption
  setCollectionSortOption: (collectionRef: string, sortOption: unknown) => Record<string, LibrarySortOption>
  getCollectionSortOption: (collectionRef: string, fallback?: LibrarySortOption) => LibrarySortOption
  setTypeFilter: (option: unknown) => string
  setVolume: (volume: unknown) => number
  setRememberVolume: (rememberVolume: unknown) => boolean
  setLanguage: (language: unknown) => LanguageOption
  setTheme: (theme: unknown) => ThemeOption
  setColorScheme: (colorScheme: unknown) => ColorSchemeOption
  setMotion: (motion: unknown) => MotionOption
  setWindowEffects: (windowEffects: unknown) => WindowEffectsOption
  setShowTechnicalMetadata: (showTechnicalMetadata: unknown) => boolean
  setPlaybackOutputDeviceId: (playbackOutputDeviceId: unknown) => string
  setImmersiveTaskbarMode: (immersiveTaskbarMode: unknown) => ImmersiveTaskbarMode
  setRepeatMode: (repeatMode: unknown) => RepeatMode
  setShuffleEnabled: (shuffleEnabled: unknown) => boolean
  setActiveLibrary: (activeLibrary: unknown) => string
  setActiveCollection: (activeCollection: unknown) => string
  setSidebarSection: (sidebarSection: unknown) => SidebarSectionOption
  setStorageRoot: (storageRoot: unknown) => string
  setScanDirectories: (scanDirectories: unknown) => string[]
  setLyricsScanDirectories: (lyricsScanDirectories: unknown) => string[]
  setAutoScanOnLaunch: (autoScanOnLaunch: unknown) => boolean
  setLastScanAt: (lastScanAt: unknown) => string
  setTelemetryConsent: (value: unknown) => TelemetryEnabled
}

const STARTUP_LOCALE_KEY = 'ofplayer.startup.locale'
const STARTUP_COLOR_SCHEME_KEY = 'ofplayer.startup.color-scheme'
const STARTUP_THEME_KEY = 'ofplayer.startup.theme'
const STARTUP_WINDOW_EFFECTS_KEY = 'ofplayer.startup.window-effects'
const WINDOW_EFFECTS_AUTO_TIER_KEY = 'ofplayer.window-effects.auto-tier'
const DEFAULT_SETTINGS_CATEGORY: SettingsCategory = 'general'
const SETTINGS_CATEGORIES = new Set<SettingsCategory>(['general', 'appearance', 'playback', 'privacy', 'library'])

function applyPreferencesState(state: PreferencesModel, nextState: PreferencesModel) {
  state.volume = nextState.volume
  state.rememberVolume = nextState.rememberVolume
  state.language = nextState.language
  state.theme = nextState.theme
  state.colorScheme = nextState.colorScheme
  state.motion = nextState.motion
  state.windowEffects = nextState.windowEffects
  state.showTechnicalMetadata = nextState.showTechnicalMetadata
  state.playbackOutputDeviceId = nextState.playbackOutputDeviceId
  state.immersiveTaskbarMode = nextState.immersiveTaskbarMode
  state.repeatMode = nextState.repeatMode
  state.shuffleEnabled = nextState.shuffleEnabled
  state.librarySearchQuery = nextState.librarySearchQuery
  state.librarySortOption = nextState.librarySortOption
  state.collectionSortOptions = nextState.collectionSortOptions
  state.libraryTypeFilter = nextState.libraryTypeFilter
  state.activeLibrary = nextState.activeLibrary
  state.activeCollection = nextState.activeCollection
  state.sidebarSection = nextState.sidebarSection
  state.storageRoot = nextState.storageRoot
  state.scanDirectories = nextState.scanDirectories
  state.lyricsScanDirectories = nextState.lyricsScanDirectories
  state.autoScanOnLaunch = nextState.autoScanOnLaunch
  state.lastScanAt = nextState.lastScanAt
  state.dataDriver = nextState.dataDriver
  state.telemetryEnabled = nextState.telemetryEnabled
}

function getEffectiveColorScheme(colorScheme: ColorSchemeOption): 'dark' | 'light' {
  if (colorScheme === 'system') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }
  return colorScheme
}

function persistStartupPresentationState(state: PreferencesModel, effectiveColorScheme: ColorSchemeOption) {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return
  }

  try {
    window.localStorage.setItem(
      STARTUP_LOCALE_KEY,
      getResolvedLocalePreference(state.language),
    )
    window.localStorage.setItem(
      STARTUP_COLOR_SCHEME_KEY,
      effectiveColorScheme,
    )
    window.localStorage.setItem(STARTUP_THEME_KEY, state.theme)
    window.localStorage.setItem(STARTUP_WINDOW_EFFECTS_KEY, state.windowEffects)
    window.localStorage.removeItem(WINDOW_EFFECTS_AUTO_TIER_KEY)
  } catch {
    // CN: 忽略存储访问问题，保持偏好同步弹性。
    // EN: Ignore storage access issues and keep preference sync resilient.
  }
}

export function clearPersistedStartupState() {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return
  }

  try {
    window.localStorage.removeItem(STARTUP_LOCALE_KEY)
    window.localStorage.removeItem(STARTUP_COLOR_SCHEME_KEY)
    window.localStorage.removeItem(STARTUP_THEME_KEY)
    window.localStorage.removeItem(STARTUP_WINDOW_EFFECTS_KEY)
    window.localStorage.removeItem(WINDOW_EFFECTS_AUTO_TIER_KEY)
  } catch {
    // CN: 忽略存储访问问题，保持破坏性恢复弹性。
    // EN: Ignore storage access issues and keep destructive recovery resilient.
  }
}

function syncDocumentPreferences(state: PreferencesModel, previousState: PreferencesModel | null = null) {
  const effectiveColorScheme =
    typeof window !== 'undefined' ? getEffectiveColorScheme(state.colorScheme) : state.colorScheme
  const shouldSyncWindowSurface =
    !previousState ||
    previousState.theme !== state.theme ||
    previousState.colorScheme !== state.colorScheme ||
    previousState.windowEffects !== state.windowEffects
  const shouldPersistStartupState =
    shouldSyncWindowSurface || !previousState || previousState.language !== state.language
  const shouldSyncMotion = !previousState || previousState.motion !== state.motion

  if (typeof document !== 'undefined') {
    if (!previousState || previousState.theme !== state.theme) {
      document.documentElement.dataset.theme = state.theme
    }

    if (!previousState || previousState.colorScheme !== state.colorScheme) {
      document.documentElement.dataset.colorScheme = state.colorScheme
      document.documentElement.dataset.effectiveColorScheme = effectiveColorScheme
    }

    if (shouldSyncMotion) {
      document.documentElement.dataset.motion = state.motion
    }
  }

  if (!previousState || previousState.language !== state.language) {
    setLocalePreference(state.language)
  }

  if (shouldPersistStartupState) {
    persistStartupPresentationState(state, effectiveColorScheme)
  }

  if (shouldSyncWindowSurface) {
    void syncWindowSurface(state.theme, effectiveColorScheme, state.windowEffects)
  }
}

export function createPreferencesStore({
  dataService,
  initialPreferences,
}: CreatePreferencesStoreOptions): PreferencesStore {
  const state = reactive(createPreferencesModel(initialPreferences))
  const isSettingsOpen = ref(false)
  const settingsInitialCategory = ref<SettingsCategory>(DEFAULT_SETTINGS_CATEGORY)
  const settingsNotice = ref<SettingsNotice | null>(null)

  async function hydrate(preloadedPreferences: PreferencesModelInput | null = null): Promise<PreferencesModel> {
    const persistedPreferences = preloadedPreferences ?? await dataService.preferences.load()
    applyPreferencesState(state, createPreferencesModel(persistedPreferences ?? undefined))
    syncDocumentPreferences(state)
    setTelemetryEnabled(state.telemetryEnabled === true)
    return state
  }

  function persist() {
    void dataService.preferences.save({ ...state })
  }

  function applyNormalizedPatch(patch: PreferencesPatch): PreferencesModel {
    const previousState = { ...state }
    const normalized = createPreferencesModel({
      ...state,
      ...patch,
    })

    applyPreferencesState(state, normalized)
    syncDocumentPreferences(state, previousState)
    persist()
    return normalized
  }

  function normalizeSettingsCategory(category: unknown): SettingsCategory {
    return SETTINGS_CATEGORIES.has(category as SettingsCategory) ? category as SettingsCategory : DEFAULT_SETTINGS_CATEGORY
  }

  function normalizeSettingsNotice(notice: SettingsNoticeInput | null | undefined, category: SettingsCategory): SettingsNotice | null {
    if (!notice || typeof notice !== 'object') {
      return null
    }

    return {
      id: `${Date.now()}:${Math.random().toString(36).slice(2)}`,
      kind: typeof notice.kind === 'string' ? notice.kind : 'info',
      category: normalizeSettingsCategory(notice.category ?? category),
    }
  }

  function openSettings(options: OpenSettingsOptions = {}): true {
    const normalizedOptions = typeof options === 'string' ? { category: options } : options
    const category = normalizeSettingsCategory(normalizedOptions?.category)

    settingsInitialCategory.value = category
    settingsNotice.value = normalizeSettingsNotice(normalizedOptions?.notice, category)
    isSettingsOpen.value = true
    return true
  }

  function closeSettings(): false {
    isSettingsOpen.value = false
    settingsNotice.value = null
    return false
  }

  function toggleSettings() {
    isSettingsOpen.value = !isSettingsOpen.value
    return isSettingsOpen.value
  }

  function setSearchQuery(query: string) {
    return applyNormalizedPatch({ librarySearchQuery: query }).librarySearchQuery
  }

  function setSortOption(option: unknown) {
    return applyNormalizedPatch({ librarySortOption: option }).librarySortOption
  }

  /**
   * CN: 设置指定集合的排序偏好
   * CN: @param {string} collectionRef - 集合引用，如 "playlist:abc123" 或 "view:recent-imports"
   * CN: @param {string} sortOption - 排序选项
   *
   * EN: Set sort preference for a specific collection
   * EN: @param {string} collectionRef - Collection reference, e.g. "playlist:abc123" or "view:recent-imports"
   * EN: @param {string} sortOption - Sort option
   */
  function setCollectionSortOption(collectionRef: string, sortOption: unknown) {
    const nextCollectionSortOptions = {
      ...state.collectionSortOptions,
      [collectionRef]: sortOption,
    }
    return applyNormalizedPatch({ collectionSortOptions: nextCollectionSortOptions }).collectionSortOptions
  }

  /**
   * CN: 获取指定集合的排序偏好，如果没有则返回默认值
   *
   * EN: Get sort preference for a specific collection, returns default if not found
   */
  function getCollectionSortOption(collectionRef: string, fallback?: LibrarySortOption) {
    return state.collectionSortOptions[collectionRef] ?? fallback ?? state.librarySortOption
  }

  function setTypeFilter(option: unknown) {
    return applyNormalizedPatch({ libraryTypeFilter: option }).libraryTypeFilter
  }

  function setVolume(volume: unknown) {
    return applyNormalizedPatch({ volume }).volume
  }

  function setRememberVolume(rememberVolume: unknown) {
    return applyNormalizedPatch({ rememberVolume }).rememberVolume
  }

  function setLanguage(language: unknown) {
    return applyNormalizedPatch({ language }).language
  }

  function setTheme(theme: unknown) {
    return applyNormalizedPatch({ theme }).theme
  }

  function setColorScheme(colorScheme: unknown) {
    return applyNormalizedPatch({ colorScheme }).colorScheme
  }

  function setMotion(motion: unknown) {
    return applyNormalizedPatch({ motion }).motion
  }

  function setWindowEffects(windowEffects: unknown) {
    return applyNormalizedPatch({ windowEffects }).windowEffects
  }

  function setShowTechnicalMetadata(showTechnicalMetadata: unknown) {
    return applyNormalizedPatch({ showTechnicalMetadata }).showTechnicalMetadata
  }

  function setPlaybackOutputDeviceId(playbackOutputDeviceId: unknown) {
    return applyNormalizedPatch({ playbackOutputDeviceId }).playbackOutputDeviceId
  }

  function setImmersiveTaskbarMode(immersiveTaskbarMode: unknown) {
    return applyNormalizedPatch({ immersiveTaskbarMode }).immersiveTaskbarMode
  }

  function setRepeatMode(repeatMode: unknown) {
    return applyNormalizedPatch({ repeatMode }).repeatMode
  }

  function setShuffleEnabled(shuffleEnabled: unknown) {
    return applyNormalizedPatch({ shuffleEnabled }).shuffleEnabled
  }

  function setActiveLibrary(activeLibrary: unknown) {
    return applyNormalizedPatch({ activeLibrary }).activeLibrary
  }

  function setActiveCollection(activeCollection: unknown) {
    return applyNormalizedPatch({ activeCollection }).activeCollection
  }

  function setSidebarSection(sidebarSection: unknown) {
    return applyNormalizedPatch({ sidebarSection }).sidebarSection
  }

  function setStorageRoot(storageRoot: unknown) {
    return applyNormalizedPatch({ storageRoot }).storageRoot
  }

  function setScanDirectories(scanDirectories: unknown) {
    return applyNormalizedPatch({ scanDirectories }).scanDirectories
  }

  function setLyricsScanDirectories(lyricsScanDirectories: unknown) {
    return applyNormalizedPatch({ lyricsScanDirectories }).lyricsScanDirectories
  }

  function setAutoScanOnLaunch(autoScanOnLaunch: unknown) {
    return applyNormalizedPatch({ autoScanOnLaunch }).autoScanOnLaunch
  }

  function setLastScanAt(lastScanAt: unknown) {
    return applyNormalizedPatch({ lastScanAt }).lastScanAt
  }

  function setTelemetryConsent(value: unknown) {
    const normalized = value === true ? true : value === false ? false : null
    state.telemetryEnabled = normalized
    setTelemetryEnabled(normalized === true)
    persist()
    return normalized
  }

  return {
    state,
    isSettingsOpen,
    volume: computed(() => state.volume),
    rememberVolume: computed(() => state.rememberVolume),
    language: computed(() => state.language),
    theme: computed(() => state.theme),
    colorScheme: computed(() => state.colorScheme),
    motion: computed(() => state.motion),
    windowEffects: computed(() => state.windowEffects),
    showTechnicalMetadata: computed(() => state.showTechnicalMetadata),
    playbackOutputDeviceId: computed(() => state.playbackOutputDeviceId),
    immersiveTaskbarMode: computed(() => state.immersiveTaskbarMode),
    repeatMode: computed(() => state.repeatMode),
    shuffleEnabled: computed(() => state.shuffleEnabled),
    searchQuery: computed(() => state.librarySearchQuery),
    sortOption: computed(() => state.librarySortOption),
    collectionSortOptions: computed(() => state.collectionSortOptions),
    typeFilter: computed(() => state.libraryTypeFilter),
    activeLibrary: computed(() => state.activeLibrary),
    activeCollection: computed(() => state.activeCollection),
    sidebarSection: computed(() => state.sidebarSection),
    storageRoot: computed(() => state.storageRoot),
    scanDirectories: computed(() => state.scanDirectories),
    lyricsScanDirectories: computed(() => state.lyricsScanDirectories),
    autoScanOnLaunch: computed(() => state.autoScanOnLaunch),
    lastScanAt: computed(() => state.lastScanAt),
    telemetryEnabled: computed(() => state.telemetryEnabled),
    settingsInitialCategory: computed(() => settingsInitialCategory.value),
    settingsNotice: computed(() => settingsNotice.value),
    hydrate,
    openSettings,
    closeSettings,
    toggleSettings,
    setSearchQuery,
    setSortOption,
    setCollectionSortOption,
    getCollectionSortOption,
    setTypeFilter,
    setVolume,
    setRememberVolume,
    setLanguage,
    setTheme,
    setColorScheme,
    setMotion,
    setWindowEffects,
    setShowTechnicalMetadata,
    setPlaybackOutputDeviceId,
    setImmersiveTaskbarMode,
    setRepeatMode,
    setShuffleEnabled,
    setActiveLibrary,
    setActiveCollection,
    setSidebarSection,
    setStorageRoot,
    setScanDirectories,
    setLyricsScanDirectories,
    setAutoScanOnLaunch,
    setLastScanAt,
    setTelemetryConsent,
  }
}
