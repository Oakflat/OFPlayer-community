<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  ref,
  watch,
  watchEffect,
  type Ref,
} from 'vue'
import {
  Activity,
  Captions,
  Check,
  ChevronDown,
  Cloud,
  Heart,
  Monitor,
  MoreHorizontal,
  Pause,
  Pencil,
  Play,
  RefreshCw,
  Repeat,
  Repeat1,
  Search,
  Shuffle,
  SkipBack,
  SkipForward,
  Trash2,
  Volume2,
  Wifi,
  WifiOff,
  X,
} from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'
import { logDiagnosticsError, logDiagnosticsInfo } from '../services/diagnosticsLogger'
import { buildRendererStepProfile, captureRendererResourceSample } from '../services/diagnosticsProfiler'
import { createTrackMetaItems } from '../services/metadataService'
import { queryTracksWithBackend } from '../services/sortingService'
import {
  filterTracksForAlbumBrowserSearch,
  groupTracksByAlbum,
  groupTracksByArtist,
  mergeAlbumBrowserSearchGroupMetadata,
} from '../services/albumViewService'
import { parseCollectionRef } from '../models/collection'
import MenuDropdown, { type MenuDropdownItem } from './MenuDropdown.vue'
import DialogModal from './DialogModal.vue'
import TrackPlaylistDialog from './TrackPlaylistDialog.vue'
import AlbumBrowserPanel from './AlbumBrowserPanel.vue'

const TRACK_QUERY_DEBOUNCE_MS = 70
const TRACK_QUERY_LOG_THRESHOLD_MS = 20
const TRACK_ROW_HEIGHT_PX = 76
const VIRTUAL_LIST_OVERSCAN = 8
const VIRTUALIZATION_THRESHOLD = 90
const PLAYBACK_SWITCH_ANIMATION_MS = 260
const RECENTLY_PLAYED_COLLECTION_REF = 'view:all-plays'

type QueryRevision = string | number
type PlayerRepeatMode = 'none' | 'one' | 'all' | 'single' | 'playlist'
type SortOption = 'recent' | 'title' | 'duration' | 'size'
type TrackTypeFilter = string
type BrowserKind = 'albums' | 'artists'
type DragPosition = 'before' | 'after' | null
type ToolbarMenu = 'sort' | 'filter' | null
type PlaybackToolsMenuAction = 'toggle-inspector' | 'bind-lyrics' | 'clear-lyrics' | 'refresh-lyrics'
type SortValue = SortOption | string

interface SourceLike {
  path?: string
  url?: string
  kind?: string
  provider?: string
}

interface PlayerTrack {
  id?: string
  libraryId?: string
  displayTitle?: string | null
  title?: string | null
  artist?: string | null
  albumArtist?: string | null
  album?: string | null
  genre?: string | null
  year?: number | null
  trackNumber?: number | null
  trackTotal?: number | null
  discNumber?: number | null
  discTotal?: number | null
  composer?: string | null
  lyricist?: string | null
  comment?: string | null
  duration?: number | null
  bitrate?: number | null
  sampleRate?: number | null
  bitDepth?: number | null
  fileSize?: number | null
  size?: number | null
  format?: string | null
  fileName?: string | null
  isFavorite?: boolean
  artwork?: string | null
  lyricsPath?: string | null
  importedAt?: string | null
  source?: SourceLike | null
  [key: string]: unknown
}

type BrowserTrack = PlayerTrack
interface AlbumBrowserEmittedTrack {
  id?: string
  displayTitle?: string | null
  title?: string | null
  fileName?: string | null
  artist?: string | null
  duration?: number | null
}

interface BrowserGroup {
  key: string
  albumName?: string
  artistName?: string
  albumArtist?: string
  albumCount?: number
  trackCount?: number
  year?: number | null | string
  coverUrl?: string
  tracks?: BrowserTrack[]
  albums?: BrowserGroup[]
}

interface CollectionSummary {
  id?: string | null
  key?: string
  label?: string
  kind?: 'playlist' | 'view' | string
  playlistKind?: 'system' | 'user' | string
  isBrowserView?: boolean
  browserKind?: BrowserKind
  empty?: string
  systemKey?: string | null
  isDefault?: boolean
}

interface LibrarySummary {
  id: string
  key?: string
  label?: string
  isExternal?: boolean
  source?: {
    provider?: string
    kind?: string
    [key: string]: unknown
  }
}

interface OrderedEntityLike {
  id?: string
  order?: number
  createdAt?: string
}

interface PlaylistInfo extends OrderedEntityLike {
  key?: string
  label?: string
  count?: number
  isDefault?: boolean
  isBrowserView?: boolean
  isExternal?: boolean
  playlistKind?: string
  systemKey?: string
  libraryId?: string | null
  name?: string
  kind?: string
  tracks?: unknown[]
}

interface PlaybackOutputDevice {
  id: string
  name: string
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

interface PlaybackSignalFormat {
  sampleRate?: number
  channels?: number
  bitDepth?: number
  sampleFormat?: string
}

interface PlaybackSignalPath {
  bitPerfect?: boolean
  integrityStatus?: string
  resampled?: boolean
  channelConverted?: boolean
  sampleFormatConverted?: boolean
  softwareMixer?: boolean
  softwareVolume?: boolean
  source?: PlaybackSignalFormat | null
  output?: PlaybackSignalFormat | null
  [key: string]: unknown
}

interface RemoteProbeState {
  active?: boolean
  phase?: 'probing' | 'syncing' | 'ready' | 'error' | 'idle' | string
  libraryId?: string
  provider?: string
  ok?: boolean | null
  synced?: boolean
  remoteTotal?: number
  checkedAt?: string
  error?: string
}

interface RemoteTrackState {
  isRemote?: boolean
  isPreparing?: boolean
  metadataReady?: boolean
  artworkReady?: boolean
  playbackReady?: boolean
}

interface LyricsSnapshotState {
  status?: 'missing' | 'resolved' | 'error' | string
  source?: string | null
  sourcePath?: string | null
}

interface PlayerErrorState {
  message?: string
}

interface QueryWindowState {
  offset: number
  limit: number | null
}

interface RenderedTrackEntry {
  track: PlayerTrack
  index: number
}

interface PlaylistDialogState {
  isOpen: boolean
  type: string | null
  title: string
  message: string
  isDanger: boolean
  onConfirm: () => void
}

interface PlaylistDialogTrack {
  id?: string
  libraryId?: string
  displayTitle?: string | null
  title?: string | null
  artist?: string | null
  albumArtist?: string | null
}

interface AddToPlaylistState {
  isOpen: boolean
  track: PlaylistDialogTrackInput | null
  libraryId: string | null
  playlistId: string | null
}

interface TrackQueryContextState {
  queryReady: boolean
  queryRevision: string
  searchQuery: string
  typeFilter: TrackTypeFilter
  sortOption: SortValue
  activeCollection: string
  currentLibraryId: string | null
  queryOffset: number
  queryLimit: number | null
}

interface ParsedCollectionRef {
  type: 'playlist' | 'view' | null
  value: string | null
}

interface PlaylistDialogLibrary {
  id: string
  order?: number
  createdAt?: string
  name?: string
  isDefault?: boolean
}

interface PlaylistDialogPlaylist extends PlaylistDialogLibrary {
  kind?: string
  libraryId?: string
}

interface PlaylistDialogTrackInput {
  id?: string
  libraryId?: string
  displayTitle?: string | null
  title?: string | null
  artist?: string | null
  albumArtist?: string | null
}

interface PlaylistDialogTrackPayload {
  id?: string
  libraryId?: string
  displayTitle?: string
  title?: string
  artist?: string
  albumArtist?: string
}

interface PlaybackToolsMenuItem extends MenuDropdownItem {
  action?: PlaybackToolsMenuAction
}

interface PlaybackToolsPayload {
  trackId: string
  queueTrackIds?: string[] | null
}

interface SelectPlaylistPayload {
  trackId: string
  playlistId: string
  libraryId?: string | null
}

interface ReorderTracksPayload {
  playlistId: string | null | undefined
  orderedTrackIds: string[]
}

type PlayerPanelTrackSelect = string | PlaybackToolsPayload
interface TrackQueryDiagnostics {
  roundTripMs?: number
  requestCacheStatus?: string
  requestCacheServedMs?: number
  totalMs?: number
  requestCacheHit?: boolean
  invokeOverheadMs?: number
  [key: string]: unknown
}
type TrackQueryBackendResult = {
  totalCount: number
  collectionTotalCount: number
  offset: number
  availableFormats: string[]
  trackIds: string[]
  rows: PlayerTrack[]
  roundTripMs?: number
  diagnostics?: TrackQueryDiagnostics
}
type TrackQueryBackend = (
  filters: {
    searchQuery: string
    typeFilter: TrackTypeFilter
    sortOption: SortValue
  },
  options: {
    queryRevision: string
    currentLibraryId: string
    activeCollectionRef: string
    offset: number
    limit: number | null
    includeTrackIds: boolean
  },
) => Promise<TrackQueryBackendResult>
type RendererStepProfileBuilder = (
  label: string,
  elapsedMs: number,
  start?: unknown,
  end?: unknown,
) => unknown
type DiagnosticsLogger = (
  label: string,
  category: string,
  event: string,
  payload?: unknown,
) => unknown

interface PlayerPanelProps {
  queryReady?: boolean
  collectionDataReady?: boolean
  collectionDataStatus?: string
  collectionDataError?: string | RemoteProbeState | null
  queryRevision?: QueryRevision
  tracks?: PlayerTrack[]
  currentLibrary?: LibrarySummary | null
  libraries?: PlaylistInfo[]
  playlists?: PlaylistInfo[]
  currentCollection?: CollectionSummary | null
  remoteProbeStatus?: RemoteProbeState
  currentTrackId?: string | null
  currentTrack?: PlayerTrack | null
  remoteTrackStatus?: RemoteTrackState
  hasAnyTracks?: boolean
  isPlaying?: boolean
  playerError?: PlayerErrorState | Error | null
  currentTime?: number
  duration?: number
  volume?: number
  repeatMode?: PlayerRepeatMode
  shuffleEnabled?: boolean
  playbackSignalPath?: PlaybackSignalPath | null
  playbackOutputDevices?: PlaybackOutputDevice[]
  playbackOutputDeviceId?: string
  activePlaybackOutputDeviceName?: string
  prefersSystemPlaybackOutput?: boolean
  playbackOutputDeviceAvailable?: boolean
  searchQuery?: string
  sortOption?: SortValue
  activeCollection?: string
  typeFilter?: TrackTypeFilter
  showTechnicalMetadata?: boolean
  lyricsSnapshot?: LyricsSnapshotState
  lyricsLoading?: boolean
  canOpenLyricCapsuleWindow?: boolean
  lyricCapsuleWindowActive?: boolean
}

const props = withDefaults(defineProps<PlayerPanelProps>(), {
  queryReady: true,
  collectionDataReady: true,
  collectionDataStatus: 'ready',
  collectionDataError: null,
  queryRevision: '0',
  tracks: () => [],
  currentLibrary: null,
  libraries: () => [],
  playlists: () => [],
  currentCollection: null,
  remoteProbeStatus: () => ({
    active: false,
    phase: 'idle',
    libraryId: '',
    provider: '',
    ok: null,
    synced: false,
    remoteTotal: 0,
    checkedAt: '',
    error: '',
  }),
  currentTrackId: null,
  currentTrack: null,
  remoteTrackStatus: () => ({
    isRemote: false,
    isPreparing: false,
    metadataReady: true,
    artworkReady: true,
    playbackReady: true,
  }),
  hasAnyTracks: false,
  isPlaying: false,
  playerError: null,
  currentTime: 0,
  duration: 0,
  volume: 0.8,
  repeatMode: 'all',
  shuffleEnabled: false,
  playbackSignalPath: null,
  playbackOutputDevices: () => [],
  playbackOutputDeviceId: '',
  activePlaybackOutputDeviceName: '',
  prefersSystemPlaybackOutput: true,
  playbackOutputDeviceAvailable: true,
  searchQuery: '',
  sortOption: 'recent',
  activeCollection: '',
  typeFilter: 'all',
  showTechnicalMetadata: true,
  lyricsSnapshot: () => ({
    status: 'missing',
    source: null,
    sourcePath: '',
  }),
  lyricsLoading: false,
  canOpenLyricCapsuleWindow: false,
  lyricCapsuleWindowActive: false,
})

const emit = defineEmits<{
  (event: 'select-track', value: PlayerPanelTrackSelect): void
  (event: 'toggle-playback'): void
  (event: 'play-previous'): void
  (event: 'play-next'): void
  (event: 'seek', time: number): void
  (event: 'set-volume', volume: number): void
  (event: 'cycle-repeat-mode'): void
  (event: 'cycle-playback-mode'): void
  (event: 'toggle-shuffle'): void
  (event: 'set-search-query', query: string): void
  (event: 'set-sort-option', payload: { sortOption: SortValue; collectionRef: string }): void
  (event: 'set-type-filter', value: string): void
  (event: 'probe-remote-library', collectionId: string): void
  (event: 'set-playback-output-device', deviceId: string): void
  (event: 'refresh-playback-output-devices'): void
  (event: 'add-track-to-playlist', payload: SelectPlaylistPayload): void
  (
    event: 'remove-track-from-playlist',
    payload: { playlistId: string | null | undefined; trackId: string },
  ): void
  (event: 'delete-track', trackId: string): void
  (event: 'delete-tracks', trackIds: string[]): void
  (event: 'toggle-favorite', trackId: string): void
  (event: 'bind-lyrics-file', trackId: string): void
  (event: 'clear-lyrics-binding', trackId: string): void
  (event: 'refresh-lyrics', trackId: string): void
  (event: 'open-immersive-player'): void
  (event: 'toggle-lyric-capsule-window'): void
  (event: 'reorder-tracks', payload: ReorderTracksPayload): void
  (event: 'hydrate-track-artwork', trackIds: string[]): void
}>()

const buildRendererStepProfileTyped = buildRendererStepProfile as unknown as RendererStepProfileBuilder
const logDiagnosticsInfoTyped = logDiagnosticsInfo as unknown as DiagnosticsLogger
const logDiagnosticsErrorTyped = logDiagnosticsError as unknown as DiagnosticsLogger
const safeDiagnosticsProfile = (
  label: string,
  elapsedMs: number,
  start: unknown = null,
  end: unknown = null,
) => {
  return buildRendererStepProfileTyped(label, elapsedMs, start, end)
}
const safeDiagnosticsInfo = (label: string, category: string, event: string, payload: unknown = undefined) => {
  return logDiagnosticsInfoTyped(label, category, event, payload)
}
const safeDiagnosticsError = (label: string, category: string, event: string, payload: unknown = undefined) => {
  return logDiagnosticsErrorTyped(label, category, event, payload)
}
const queryTracks = queryTracksWithBackend as unknown as TrackQueryBackend

const { locale, t } = useI18n()
const searchInput = ref<HTMLInputElement | null>(null)
const isSearchOpen = ref(false)
const isInspectorOpen = ref(false)
const activeToolbarMenu = ref<ToolbarMenu>(null)
const sortMenuRef = ref<HTMLElement | null>(null)
const filterMenuRef = ref<HTMLElement | null>(null)
const songListRef = ref<HTMLElement | null>(null)
const selectAllCheckboxRef = ref<HTMLInputElement | null>(null)
const openTrackMenuId = ref<string | null>(null)
const trackMenuAnchorEl = ref<HTMLElement | null>(null)
const isPlaybackToolsMenuOpen = ref(false)
const playbackToolsMenuAnchorEl = ref<HTMLElement | null>(null)
const isPlaybackOutputDrawerOpen = ref(false)
const playbackOutputAnchorRef = ref<HTMLElement | null>(null)
const playbackOutputDrawerRef = ref<HTMLElement | null>(null)
const playbackOutputDrawerStyle = ref<Record<string, string>>({})
const addToPlaylistState = ref({
  isOpen: false,
  track: null,
  libraryId: null,
  playlistId: null,
}) as Ref<AddToPlaylistState>
const dialogState = ref({
  isOpen: false,
  type: null,
  title: '',
  message: '',
  isDanger: false,
  onConfirm: () => {},
}) as Ref<PlaylistDialogState>
const visibleTracks = ref<PlayerTrack[]>([])
const visibleTrackIds = ref<string[]>([])
const visibleTrackTotalCount = ref(0)
const collectionTrackTotalCount = ref(0)
const availableTrackFormats = ref<string[]>([])
const selectedTrackIds = ref<Set<string>>(new Set())
const lastSelectedTrackId = ref<string | null>(null)
const isTrackEditMode = ref(false)
const visibleTrackWindowOffset = ref(0)
const songListScrollTop = ref(0)
const songListViewportHeight = ref(0)
const progressSliderRef = ref<HTMLElement | null>(null)
const isProgressScrubbing = ref(false)
const progressScrubValue = ref(0)
const isPlaybackSwitching = ref(false)
let visibleTrackQueryRequestId = 0
let visibleTrackQueryTimerId: ReturnType<typeof setTimeout> | null = null
let songListMeasureFrameId: ReturnType<typeof requestAnimationFrame> | null = null
let songListResizeObserver: ResizeObserver | null = null
let playbackSwitchTimerId: ReturnType<typeof setTimeout> | null = null
let playbackSwitchFrameId: ReturnType<typeof requestAnimationFrame> | null = null
let playbackOutputDrawerFrameId: ReturnType<typeof requestAnimationFrame> | null = null

// CN: 拖拽排序状态
// EN: Drag-and-drop sorting state
const draggedTrackId = ref<string | null>(null)
const dragOverTrackId = ref<string | null>(null)
const dragPosition = ref<DragPosition>(null) // 'before' | 'after'
const playlistDialogLibraries = computed<PlaylistDialogLibrary[]>(() =>
  props.libraries.map((library) => ({
    id: library.id ?? '',
    order: library.order,
    createdAt: library.createdAt,
    name: library.name,
    isDefault: library.isDefault,
  })),
)
const playlistDialogPlaylists = computed<PlaylistDialogPlaylist[]>(() =>
  props.playlists.map((playlist) => ({
    id: playlist.id ?? '',
    order: playlist.order,
    createdAt: playlist.createdAt,
    name: playlist.name,
    isDefault: playlist.isDefault,
    kind: playlist.kind,
    libraryId: playlist.libraryId ?? undefined,
  })),
)
const playlistDialogTrack = computed<PlaylistDialogTrackPayload | null>(() => {
  const track = addToPlaylistState.value.track

  if (!track) {
    return null
  }

  return {
    id: track.id,
    libraryId: track.libraryId,
    displayTitle: track.displayTitle ?? undefined,
    title: track.title ?? undefined,
    artist: track.artist ?? undefined,
    albumArtist: track.albumArtist ?? undefined,
  }
})

const playbackMode = computed(() => {
  if (props.shuffleEnabled) {
    return 'shuffle'
  }

  if (props.repeatMode === 'one' || props.repeatMode === 'single') {
    return 'repeat-one'
  }

  if (props.repeatMode === 'all' || props.repeatMode === 'playlist') {
    return 'repeat-all'
  }

  return 'sequential'
})
const playbackModeIcon = computed(() => {
  if (props.shuffleEnabled) {
    return Shuffle
  }

  return props.repeatMode === 'one' || props.repeatMode === 'single' ? Repeat1 : Repeat
})
const playbackModeLabel = computed(() => {
  if (props.shuffleEnabled) {
    return t('player.shuffleOn')
  }

  if (props.repeatMode === 'one' || props.repeatMode === 'single') {
    return t('player.repeatOne')
  }

  if (props.repeatMode === 'all' || props.repeatMode === 'playlist') {
    return t('player.repeatAll')
  }

  return t('player.playbackModeSequential')
})
const playbackModeAriaLabel = computed(() =>
  t('player.playbackMode', {
    mode: playbackModeLabel.value,
  }),
)
const isPlaybackModeActive = computed(() => props.shuffleEnabled || props.repeatMode !== 'none')

// CN: 判断当前集合是否支持拖拽排序（仅用户歌单支持）
// EN: Check if current collection supports drag-and-drop sorting (only user playlists supported)
const canReorderTracks = computed(() => {
  const collection = props.currentCollection
  return collection?.kind === 'playlist' && collection?.playlistKind === 'user'
})

const isBrowserCollection = computed(() => props.currentCollection?.isBrowserView === true)

const browserLibraryTracks = computed(() => {
  const libraryId = props.currentLibrary?.id

  if (!libraryId) {
    return []
  }

  return props.tracks.filter((track) => track?.libraryId === libraryId)
})

const browserTypeFilteredTracks = computed(() => {
  if (props.typeFilter === 'all') {
    return browserLibraryTracks.value
  }

  return browserLibraryTracks.value.filter((track) => resolveTrackFormat(track) === props.typeFilter)
})

const browserVisibleTracks = computed(() => {
  return filterTracksForAlbumBrowserSearch(browserTypeFilteredTracks.value, props.searchQuery)
})

const browserAvailableFormats = computed(() => {
  return [...new Set(browserLibraryTracks.value.map(resolveTrackFormat).filter(Boolean))].sort((left, right) =>
    left.localeCompare(right),
  )
})

function createBrowserGroups(tracks: PlayerTrack[]): BrowserGroup[] {
  if (props.currentCollection?.browserKind === 'artists') {
    return groupTracksByArtist(tracks)
  }

  return groupTracksByAlbum(tracks)
}

const browserAllGroups = computed(() => {
  if (!isBrowserCollection.value) {
    return []
  }

  return createBrowserGroups(browserLibraryTracks.value)
})

const browserTypeGroups = computed(() => {
  if (!isBrowserCollection.value) {
    return []
  }

  return createBrowserGroups(browserTypeFilteredTracks.value)
})

const browserVisibleGroups = computed(() => {
  const query = props.searchQuery.trim()

  if (!query) {
    if (props.typeFilter !== 'all') {
      return mergeAlbumBrowserSearchGroupMetadata(
        browserTypeGroups.value,
        browserAllGroups.value,
      )
    }

    return browserTypeGroups.value
  }

  return mergeAlbumBrowserSearchGroupMetadata(
    createBrowserGroups(browserVisibleTracks.value),
    browserAllGroups.value,
  )
})

const progressValue = computed(() => {
  if (!Number.isFinite(props.duration) || props.duration <= 0) {
    return 0
  }

  return Math.min(props.currentTime, props.duration)
})

const displayProgressValue = computed(() => {
  return isProgressScrubbing.value ? progressScrubValue.value : progressValue.value
})

const displayCurrentTime = computed(() => {
  return displayProgressValue.value
})

const progressPercent = computed(() => {
  const duration = durationValue.value

  if (!Number.isFinite(duration) || duration <= 0) {
    return 0
  }

  return Math.min(Math.max((displayProgressValue.value / duration) * 100, 0), 100)
})

const progressSliderStyle = computed(() => ({
  '--progress': `${progressPercent.value / 100}`,
}))

const durationValue = computed(() => {
  if (!Number.isFinite(props.duration) || props.duration <= 0) {
    return 1
  }

  return props.duration
})

function clampProgressTime(value: number): number {
  const numericValue = Number(value)

  if (!Number.isFinite(numericValue) || numericValue <= 0) {
    return 0
  }

  if (!Number.isFinite(props.duration) || props.duration <= 0) {
    return numericValue
  }

  return Math.min(numericValue, props.duration)
}

function captureProgressPointer(event: PointerEvent) {
  const target = event.currentTarget as HTMLElement | null

  if (
    !target ||
    typeof target?.setPointerCapture !== 'function' ||
    !Number.isInteger(event.pointerId)
  ) {
    return
  }

  try {
    target.setPointerCapture(event.pointerId)
  } catch {
    // CN: 指针可能已被 webview 捕获或释放。
    // EN: The pointer may already be captured or released by the webview.
  }
}

function releaseProgressPointer(event: PointerEvent) {
  const target = event.currentTarget as HTMLElement | null

  if (
    !target ||
    typeof target?.releasePointerCapture !== 'function' ||
    !Number.isInteger(event.pointerId)
  ) {
    return
  }

  try {
    target.releasePointerCapture(event.pointerId)
  } catch {
    // CN: 指针可能已被 blur/change 事件释放。
    // EN: The pointer may have been released by blur/change first.
  }
}

function resolveProgressTimeFromPointer(event: PointerEvent) {
  const sliderEl = progressSliderRef.value
  const rect = sliderEl?.getBoundingClientRect?.()
  const clientX = event?.clientX

  if (!rect || !Number.isFinite(clientX) || rect.width <= 0) {
    return progressScrubValue.value
  }

  const ratio = Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1)
  return clampProgressTime(ratio * durationValue.value)
}

function updateProgressScrubFromPointer(event: PointerEvent) {
  if (!props.currentTrack) {
    return
  }

  event?.preventDefault?.()
  progressScrubValue.value = resolveProgressTimeFromPointer(event)
  isProgressScrubbing.value = true
}

function beginProgressScrub(event: PointerEvent) {
  if (!props.currentTrack) {
    return
  }

  captureProgressPointer(event)
  updateProgressScrubFromPointer(event)
}

function handleProgressPointerMove(event: PointerEvent) {
  if (!isProgressScrubbing.value) {
    return
  }

  updateProgressScrubFromPointer(event)
}

function cancelProgressScrub(event: PointerEvent) {
  releaseProgressPointer(event)
  progressScrubValue.value = progressValue.value
  isProgressScrubbing.value = false
}

function commitProgressSeek(event: PointerEvent) {
  if (!props.currentTrack) {
    isProgressScrubbing.value = false
    return
  }

  releaseProgressPointer(event)
  if (isProgressScrubbing.value && Number.isFinite(event.clientX)) {
    updateProgressScrubFromPointer(event)
  }

  const nextTime = clampProgressTime(progressScrubValue.value)

  if (!isProgressScrubbing.value && Math.abs(nextTime - progressScrubValue.value) < 0.05) {
    return
  }

  progressScrubValue.value = nextTime
  isProgressScrubbing.value = false
  emit('seek', nextTime)
}

function commitProgressSeekByBlur(event: FocusEvent) {
  if (!isProgressScrubbing.value) {
    return
  }

  const relatedTarget = event.relatedTarget instanceof Node ? event.relatedTarget : null
  if (progressSliderRef.value && relatedTarget && progressSliderRef.value.contains(relatedTarget)) {
    return
  }

  if (!props.currentTrack) {
    isProgressScrubbing.value = false
    return
  }

  const nextTime = clampProgressTime(progressScrubValue.value)
  progressScrubValue.value = nextTime
  isProgressScrubbing.value = false
  emit('seek', nextTime)
}

function handleProgressKeydown(event: KeyboardEvent) {
  if (!props.currentTrack) {
    return
  }

  const smallStep = Math.max(1, Math.min(durationValue.value / 100, 5))
  const largeStep = Math.max(smallStep, Math.min(durationValue.value / 10, 30))
  let nextTime = null

  switch (event.key) {
    case 'ArrowLeft':
    case 'ArrowDown':
      nextTime = displayProgressValue.value - smallStep
      break
    case 'ArrowRight':
    case 'ArrowUp':
      nextTime = displayProgressValue.value + smallStep
      break
    case 'PageDown':
      nextTime = displayProgressValue.value - largeStep
      break
    case 'PageUp':
      nextTime = displayProgressValue.value + largeStep
      break
    case 'Home':
      nextTime = 0
      break
    case 'End':
      nextTime = durationValue.value
      break
    default:
      return
  }

  event.preventDefault()
  progressScrubValue.value = clampProgressTime(nextTime)
  isProgressScrubbing.value = false
  emit('seek', progressScrubValue.value)
}

const availableTypeFilters = computed(() => {
  const options = [{ value: 'all', label: t('player.filterAll') }]
  const formats = isBrowserCollection.value ? browserAvailableFormats.value : availableTrackFormats.value

  formats.forEach((fileType) => {
    options.push({
      value: fileType,
      label: fileType,
    })
  })

  return options
})

const resolvedActiveCollectionRef = computed<string>(() => props.currentCollection?.key ?? props.activeCollection ?? '')
const isRecentlyPlayedCollection = computed(() => resolvedActiveCollectionRef.value === RECENTLY_PLAYED_COLLECTION_REF)
const canApplyLocalRecentPlaybackUpdate = computed(() =>
  props.queryReady &&
  isRecentlyPlayedCollection.value &&
  !isBrowserCollection.value &&
  props.searchQuery.trim().length === 0 &&
  props.typeFilter === 'all',
)

const sortOptions = computed(() => [
  {
    value: 'recent',
    label: isRecentlyPlayedCollection.value ? t('player.sortRecentlyPlayed') : t('player.sortRecent'),
  },
  { value: 'title', label: t('player.sortTitle') },
  { value: 'duration', label: t('player.sortDuration') },
  { value: 'size', label: t('player.sortSize') },
])

const visibleCountLabel = computed(() => {
  const shown = isBrowserCollection.value ? browserVisibleGroups.value.length : visibleTrackTotalCount.value
  const total = isBrowserCollection.value ? browserAllGroups.value.length : collectionTrackTotalCount.value

  return t('player.visibleCount', {
    shown,
    total,
  })
})

const selectedTrackIdsArray = computed(() => Array.from(selectedTrackIds.value))
const selectedTrackCount = computed(() => selectedTrackIds.value.size)
const hasSelectedTracks = computed(() => selectedTrackCount.value > 0)
const selectedCountLabel = computed(() =>
  t('player.selectedCount', {
    count: selectedTrackCount.value,
  }),
)
const allVisibleTracksSelected = computed(() => {
  const trackIds = visibleTrackIds.value

  return trackIds.length > 0 && trackIds.every((trackId) => selectedTrackIds.value.has(trackId))
})
const someVisibleTracksSelected = computed(() => {
  const trackIds = visibleTrackIds.value

  return trackIds.some((trackId) => selectedTrackIds.value.has(trackId))
})
const isSelectAllIndeterminate = computed(
  () => someVisibleTracksSelected.value && !allVisibleTracksSelected.value,
)
const showsTrackSelectionControls = computed(() => isTrackEditMode.value || hasSelectedTracks.value)
const editSelectionLabel = computed(() =>
  isTrackEditMode.value ? t('player.finishSelection') : t('player.editSelection'),
)

const showsRemoteProbe = computed(() =>
  props.currentLibrary?.isExternal === true || props.currentLibrary?.source?.kind === 'external',
)
const remoteProbeLibraryId = computed(() => props.currentLibrary?.id ?? '')
const remoteProbeStatus = computed(() => {
  const status = props.remoteProbeStatus ?? {}

  if (status.libraryId && status.libraryId !== remoteProbeLibraryId.value) {
    return {
      phase: 'idle',
    }
  }

  return status
})
const remoteProbePhase = computed(() => remoteProbeStatus.value.phase ?? 'idle')
const isRemoteProbeBusy = computed(() => ['probing', 'syncing'].includes(remoteProbePhase.value))
const remoteProbeProviderLabel = computed(() => {
  const provider = String(
    remoteProbeStatus.value.provider || props.currentLibrary?.source?.provider || '',
  ).toLowerCase()

  if (provider === 'subsonic') {
    return t('player.remoteProbe.navidrome')
  }

  if (provider === 'webdav') {
    return t('player.remoteProbe.webdav')
  }

  return t('player.remoteProbe.remote')
})
const remoteProbeLabel = computed(() => {
  if (remoteProbePhase.value === 'probing') {
    return t('player.remoteProbe.probing')
  }

  if (remoteProbePhase.value === 'syncing') {
    return t('player.remoteProbe.syncing')
  }

  if (remoteProbePhase.value === 'ready' && remoteProbeStatus.value.synced) {
    return t('player.remoteProbe.synced')
  }

  if (remoteProbePhase.value === 'ready') {
    return t('player.remoteProbe.online')
  }

  if (remoteProbePhase.value === 'error') {
    return t('player.remoteProbe.offline')
  }

  return t('player.remoteProbe.idle')
})
const remoteProbeMeta = computed(() => {
  if (remoteProbePhase.value === 'ready' && remoteProbeStatus.value.synced) {
    return t('player.remoteProbe.syncedMeta', {
      count: remoteProbeStatus.value.remoteTotal ?? 0,
    })
  }

  return remoteProbeProviderLabel.value
})
const remoteProbeIcon = computed(() => {
  if (remoteProbePhase.value === 'probing' || remoteProbePhase.value === 'syncing') {
    return RefreshCw
  }

  if (remoteProbePhase.value === 'ready') {
    return Wifi
  }

  if (remoteProbePhase.value === 'error') {
    return WifiOff
  }

  return Activity
})
const remoteProbeTitle = computed(() => {
  if (remoteProbeStatus.value.error) {
    return remoteProbeStatus.value.error
  }

  return t('player.remoteProbe.aria')
})

const shouldWindowQueries = computed(() => {
  if (props.searchQuery.trim().length > 0 || props.typeFilter !== 'all') {
    return visibleTrackTotalCount.value >= VIRTUALIZATION_THRESHOLD
  }

  return collectionTrackTotalCount.value >= VIRTUALIZATION_THRESHOLD
})

const shouldVirtualizeTracks = computed(() => visibleTrackTotalCount.value >= VIRTUALIZATION_THRESHOLD)

const queryWindow = computed(() => {
  if (!shouldWindowQueries.value) {
    return {
      offset: 0,
      limit: null,
    }
  }

  const offset = Math.max(0, Math.floor(songListScrollTop.value / TRACK_ROW_HEIGHT_PX) - VIRTUAL_LIST_OVERSCAN)
  const visibleRowCount = Math.ceil(songListViewportHeight.value / TRACK_ROW_HEIGHT_PX) + VIRTUAL_LIST_OVERSCAN * 2

  return {
    offset,
    limit: Math.max(visibleRowCount, 1),
  }
})

const trackSnapshotById = computed(() => {
  const tracksById = new Map<string, PlayerTrack>()

  for (const track of props.tracks) {
    if (track?.id) {
      tracksById.set(track.id, track)
    }
  }

  if (props.currentTrack?.id) {
    tracksById.set(props.currentTrack.id, props.currentTrack)
  }

  return tracksById
})

const artworkByAlbumKey = computed(() => {
  const artworkByKey = new Map<string, string>()
  const candidates = props.currentTrack ? [props.currentTrack, ...props.tracks] : props.tracks

  for (const track of candidates) {
    const key = createTrackArtworkAlbumKey(track)
    const artwork = normalizeArtworkSource(track?.artwork)

    if (key && artwork && !artworkByKey.has(key)) {
      artworkByKey.set(key, artwork)
    }
  }

  return artworkByKey
})

const renderedTrackEntries = computed(() => {
  return visibleTracks.value.map((track, offset) => ({
    track: resolveRowTrackArtwork(track),
    index: visibleTrackWindowOffset.value + offset,
  }))
})

const visibleArtworkHydrationIds = computed(() => {
  return visibleTracks.value
    .filter((track) => track?.id && !normalizeArtworkSource(resolveRowTrackArtwork(track).artwork))
    .map((track) => track.id as string)
})

const totalVirtualTrackHeight = computed(() => visibleTrackTotalCount.value * TRACK_ROW_HEIGHT_PX)
const virtualTrackOffset = computed(() => visibleTrackWindowOffset.value * TRACK_ROW_HEIGHT_PX)

const isSearchVisible = computed(() => {
  return isSearchOpen.value || props.searchQuery.trim().length > 0
})

const currentTrackMeta = computed(() => {
  const items = createTrackMetaItems(props.currentTrack, {
    locale: locale.value,
    showTechnicalMetadata: props.showTechnicalMetadata,
    includeDuration: true,
    maxItems: 3,
  })

  return items.length > 0 ? items.join(' • ') : t('track.localFile')
})

const remoteTrackReadinessMessage = computed(() => {
  const status = props.remoteTrackStatus ?? {}

  if (!status.isRemote) {
    return ''
  }

  if (status.isPreparing) {
    return t('player.remoteStatus.preparing')
  }

  if (!status.metadataReady && !status.artworkReady) {
    return t('player.remoteStatus.metadataAndArtworkPending')
  }

  if (!status.metadataReady) {
    return t('player.remoteStatus.metadataPending')
  }

  if (!status.artworkReady) {
    return t('player.remoteStatus.artworkPending')
  }

  if (!status.playbackReady) {
    return t('player.remoteStatus.indexed')
  }

  return ''
})

const playbackErrorMessage = computed(() => {
  if (props.currentTrack && !hasManagedPlaybackSource(props.currentTrack)) {
    return t('player.rustPlaybackRequired')
  }

  return typeof props.playerError?.message === 'string' ? props.playerError.message : ''
})

const defaultPlaybackOutputName = computed(() => {
  return props.playbackOutputDevices.find((device) => device?.isDefault)?.name ?? ''
})

const currentPlaybackOutputLabel = computed(() => {
  return (
    props.activePlaybackOutputDeviceName ||
    defaultPlaybackOutputName.value ||
    t('settings.audioOutput.systemDefault')
  )
})

const playbackOutputOptions = computed<PlaybackOutputChoice[]>(() => [
  {
    id: '',
    name: t('settings.audioOutput.systemDefault'),
    description: defaultPlaybackOutputName.value || t('settings.audioOutput.systemDefaultCopyEmpty'),
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

const hasPlaybackOutputChoices = computed(() => playbackOutputOptions.value.length > 1)

const playbackToolsMenuItems = computed<PlaybackToolsMenuItem[]>(() => {
  if (!props.currentTrack) {
    return []
  }

  const items: PlaybackToolsMenuItem[] = [
    {
      key: 'inspector',
      action: 'toggle-inspector' as PlaybackToolsMenuAction,
      label: isInspectorOpen.value ? t('player.details.closePanel') : t('player.details.openPanel'),
    },
    {
      key: 'bind-lyrics',
      action: 'bind-lyrics' as PlaybackToolsMenuAction,
      label: hasExplicitLyricsBinding.value ? t('player.details.rebindLyrics') : t('player.details.bindLyrics'),
    },
    {
      key: 'refresh-lyrics',
      action: 'refresh-lyrics' as PlaybackToolsMenuAction,
      label: t('player.details.refreshLyrics'),
    },
  ]

  if (hasExplicitLyricsBinding.value) {
    items.push({
      key: 'clear-lyrics',
      action: 'clear-lyrics' as PlaybackToolsMenuAction,
      label: t('player.details.clearLyricsBinding'),
    })
  }

  return items
})

const collectionEyebrow = computed(() => props.currentLibrary?.label || t('player.library'))
const collectionTitle = computed(() => props.currentCollection?.label || t('player.importedTracks'))
const emptyCollectionCopy = computed(() => props.currentCollection?.empty || t('player.collectionEmpty'))
const headerCopyKey = computed(() => `${collectionEyebrow.value}::${collectionTitle.value}`)
const visibleCountKey = computed(() => visibleCountLabel.value)
const songListStyle = computed(() => ({
  '--song-row-height': `${TRACK_ROW_HEIGHT_PX}px`,
}))

const currentSortLabel = computed(() => {
  if (isRecentlyPlayedCollection.value) {
    return t('player.sortRecentlyPlayed')
  }

  return sortOptions.value.find((option) => option.value === props.sortOption)?.label ?? t('player.sortRecent')
})

const currentTypeFilterLabel = computed(() => {
  return (
    availableTypeFilters.value.find((option) => option.value === props.typeFilter)?.label ?? t('player.filterAll')
  )
})

const inspectorItems = computed(() => {
  if (!props.currentTrack) {
    return []
  }

  const track = props.currentTrack
  const metadataItems = [
    {
      key: 'artist',
      label: t('player.details.artist'),
      value: track?.artist || '',
    },
    {
      key: 'albumArtist',
      label: t('player.details.albumArtist'),
      value: track?.albumArtist || '',
    },
    {
      key: 'album',
      label: t('player.details.album'),
      value: track?.album || '',
    },
    {
      key: 'genre',
      label: t('player.details.genre'),
      value: track?.genre || '',
    },
    {
      key: 'year',
      label: t('player.details.year'),
      value: formatYear(track?.year),
    },
    {
      key: 'track',
      label: t('player.details.track'),
      value: formatSequence(track?.trackNumber, track?.trackTotal),
    },
    {
      key: 'disc',
      label: t('player.details.disc'),
      value: formatSequence(track?.discNumber, track?.discTotal),
    },
    {
      key: 'composer',
      label: t('player.details.composer'),
      value: track?.composer || '',
    },
    {
      key: 'lyricist',
      label: t('player.details.lyricist'),
      value: track?.lyricist || '',
    },
    {
      key: 'comment',
      label: t('player.details.comment'),
      value: track?.comment || '',
    },
  ].filter((item) => item.value)

  const technicalItems = [
    {
      key: 'format',
      label: t('player.details.format'),
      value: resolveTrackFormat(track),
    },
    {
      key: 'bitrate',
      label: t('player.details.bitrate'),
      value: formatBitrate(track?.bitrate),
    },
    {
      key: 'sampleRate',
      label: t('player.details.sampleRate'),
      value: formatSampleRate(track?.sampleRate),
    },
    {
      key: 'bitDepth',
      label: t('player.details.bitDepth'),
      value: formatBitDepth(track?.bitDepth),
    },
    {
      key: 'duration',
      label: t('player.details.duration'),
      value: formatTime(track?.duration),
    },
    {
      key: 'size',
      label: t('player.details.size'),
      value: formatFileSize(track?.fileSize ?? track?.size),
    },
    {
      key: 'fileName',
      label: t('player.details.fileName'),
      value: track?.fileName || t('player.details.unavailable'),
    },
    {
      key: 'importedAt',
      label: t('player.details.importedAt'),
      value: formatImportedAt(track?.importedAt),
    },
  ]

  const playbackPathItems = props.playbackSignalPath
    ? [
        {
          key: 'playbackIntegrity',
          label: t('player.details.signalStatus'),
          value: formatPlaybackIntegrityStatus(props.playbackSignalPath),
        },
        {
          key: 'playbackSource',
          label: t('player.details.signalSource'),
          value: formatSignalFormat(props.playbackSignalPath.source),
        },
        {
          key: 'playbackOutput',
          label: t('player.details.signalOutput'),
          value: formatSignalFormat(props.playbackSignalPath.output),
        },
        {
          key: 'playbackConversions',
          label: t('player.details.signalConversions'),
          value: formatSignalConversions(props.playbackSignalPath),
        },
      ]
    : []

  return [...metadataItems, ...technicalItems, ...playbackPathItems]
})

function formatInspectorPath(path?: string | null) {
  const normalized = String(path ?? '').trim()

  if (!normalized) {
    return t('player.details.unavailable')
  }

  return normalized.replace(/\\/g, '/').split('/').pop()?.trim() || normalized
}

const hasExplicitLyricsBinding = computed(() => {
  return typeof props.currentTrack?.lyricsPath === 'string' && props.currentTrack.lyricsPath.trim().length > 0
})

const lyricsBindingLabel = computed(() => {
  return hasExplicitLyricsBinding.value
    ? formatInspectorPath(props.currentTrack?.lyricsPath)
    : t('player.details.lyricsAuto')
})

const lyricsResolvedLabel = computed(() => {
  if (props.lyricsLoading) {
    return t('player.details.lyricsStateLoading')
  }

  if (props.lyricsSnapshot?.status === 'resolved') {
    if (props.lyricsSnapshot?.sourcePath) {
      return formatInspectorPath(props.lyricsSnapshot.sourcePath)
    }

    if (String(props.lyricsSnapshot?.source ?? '').startsWith('embedded')) {
      return t('player.details.lyricsEmbedded')
    }

    return t('player.details.lyricsStateResolved')
  }

  return t('player.details.noLyricsSource')
})

const lyricsStatusLabel = computed(() => {
  if (props.lyricsLoading) {
    return t('player.details.lyricsStateLoading')
  }

  return props.lyricsSnapshot?.status === 'resolved'
    ? t('player.details.lyricsStateResolved')
    : t('player.details.lyricsStateMissing')
})

function clearPlaybackSwitchAnimation() {
  if (playbackSwitchFrameId !== null) {
    cancelAnimationFrame(playbackSwitchFrameId)
    playbackSwitchFrameId = null
  }

  if (playbackSwitchTimerId !== null) {
    clearTimeout(playbackSwitchTimerId)
    playbackSwitchTimerId = null
  }
}

function triggerPlaybackSwitchAnimation() {
  clearPlaybackSwitchAnimation()
  isPlaybackSwitching.value = false

  playbackSwitchFrameId = requestAnimationFrame(() => {
    playbackSwitchFrameId = null
    isPlaybackSwitching.value = true
    playbackSwitchTimerId = setTimeout(() => {
      playbackSwitchTimerId = null
      isPlaybackSwitching.value = false
    }, PLAYBACK_SWITCH_ANIMATION_MS)
  })
}

watch(
  () => props.currentTrack?.id ?? null,
  (trackId) => {
    isProgressScrubbing.value = false
    progressScrubValue.value = progressValue.value

    if (!trackId) {
      clearPlaybackSwitchAnimation()
      isPlaybackSwitching.value = false
      isInspectorOpen.value = false
      return
    }

    triggerPlaybackSwitchAnimation()
  },
)

watch(isSearchVisible, (visible) => {
  if (visible) {
    activeToolbarMenu.value = null
  }
})

watch(
  () => ({
    enabled: canApplyLocalRecentPlaybackUpdate.value,
    trackId: props.currentTrackId,
    track: props.currentTrack,
  }),
  ({ enabled, trackId, track }) => {
    if (!enabled || !trackId || track?.id !== trackId) {
      return
    }

    applyLocalRecentPlaybackTrack(track)
  },
  { flush: 'post' },
)

watch(
  (): TrackQueryContextState => ({
    queryReady: props.queryReady,
    queryRevision: String(props.queryRevision ?? '0'),
    searchQuery: props.searchQuery,
    typeFilter: props.typeFilter,
    sortOption: props.sortOption ?? 'recent',
    activeCollection: resolvedActiveCollectionRef.value ?? '',
    currentLibraryId: props.currentLibrary?.id ?? null,
    queryOffset: queryWindow.value.offset,
    queryLimit: queryWindow.value.limit,
  }),
  (
    {
      queryReady,
      queryRevision,
      searchQuery,
      typeFilter,
      sortOption,
      activeCollection,
      currentLibraryId,
      queryOffset,
      queryLimit,
    }: TrackQueryContextState,
    previousState?: TrackQueryContextState,
  ) => {
    const requestId = ++visibleTrackQueryRequestId
    const previousSearchQuery = previousState?.searchQuery ?? ''
    const queryIdentityChanged =
      previousState?.queryReady !== queryReady ||
      previousState?.queryRevision !== queryRevision ||
      previousState?.searchQuery !== searchQuery ||
      previousState?.typeFilter !== typeFilter ||
      previousState?.sortOption !== sortOption ||
      previousState?.activeCollection !== activeCollection ||
      previousState?.currentLibraryId !== currentLibraryId
    const shouldIncludeTrackIds =
      queryIdentityChanged || !queryLimit || visibleTrackIds.value.length === 0

    if (visibleTrackQueryTimerId !== null) {
      clearTimeout(visibleTrackQueryTimerId)
      visibleTrackQueryTimerId = null
    }

    if (!queryReady) {
      visibleTracks.value = []
      visibleTrackIds.value = []
      visibleTrackTotalCount.value = 0
      collectionTrackTotalCount.value = 0
      availableTrackFormats.value = []
      visibleTrackWindowOffset.value = 0
      return
    }

    if (isBrowserCollection.value) {
      const browserVisibleTrackIds = browserVisibleTracks.value
        .map((track) => track.id)
        .filter((trackId): trackId is string => Boolean(trackId))

      visibleTracks.value = []
      visibleTrackIds.value = browserVisibleTrackIds
      visibleTrackTotalCount.value = browserVisibleGroups.value.length
      collectionTrackTotalCount.value = browserAllGroups.value.length
      availableTrackFormats.value = browserAvailableFormats.value
      visibleTrackWindowOffset.value = 0
      return
    }

    const runTrackQuery = async () => {
      if (!currentLibraryId || !activeCollection) {
        visibleTracks.value = []
        visibleTrackIds.value = []
        visibleTrackTotalCount.value = 0
        collectionTrackTotalCount.value = 0
        availableTrackFormats.value = []
        visibleTrackWindowOffset.value = 0
        visibleTrackQueryTimerId = null
        return
      }

      try {
        const requestQueryRevision = queryRevision ?? '0'
        const requestActiveCollection = activeCollection
        const result = await queryTracks({
          searchQuery,
          typeFilter,
          sortOption,
        }, {
          queryRevision: requestQueryRevision,
          currentLibraryId,
          activeCollectionRef: requestActiveCollection,
          offset: queryOffset,
          limit: queryLimit,
          includeTrackIds: shouldIncludeTrackIds,
        })

        if (requestId !== visibleTrackQueryRequestId) {
          return
        }

        const applyResultStartedAt = performance.now()
        const applyResultResourceStart = captureRendererResourceSample()
        if (shouldIncludeTrackIds) {
          visibleTrackIds.value = result.trackIds
        }
        visibleTrackTotalCount.value = result.totalCount
        collectionTrackTotalCount.value = result.collectionTotalCount
        availableTrackFormats.value = result.availableFormats
        visibleTrackWindowOffset.value = queryLimit ? result.offset : 0
        visibleTracks.value = result.rows

        if (queryLimit && result.offset !== queryOffset && songListRef.value) {
          const nextScrollTop = result.offset * TRACK_ROW_HEIGHT_PX
          songListRef.value.scrollTop = nextScrollTop
          songListScrollTop.value = nextScrollTop
        }
        const applyResultResourceEnd = captureRendererResourceSample()
        const applyResultProfile = safeDiagnosticsProfile(
          'uiApply',
          performance.now() - applyResultStartedAt,
          applyResultResourceStart,
          applyResultResourceEnd,
        )

        const resultRoundTripMs = Number(result?.roundTripMs)
        const diagnosticsRoundTripMs = Number(result?.diagnostics?.roundTripMs)
        const roundTripMs = Number.isFinite(resultRoundTripMs)
          ? resultRoundTripMs
          : (Number.isFinite(diagnosticsRoundTripMs) ? diagnosticsRoundTripMs : 0)
        const requestCacheStatus = result?.diagnostics?.requestCacheStatus ?? 'miss'
        const requestCacheServedMs = result?.diagnostics?.requestCacheServedMs ?? 0

        if (
          (result?.diagnostics?.totalMs ?? 0) >= TRACK_QUERY_LOG_THRESHOLD_MS ||
          roundTripMs >= TRACK_QUERY_LOG_THRESHOLD_MS ||
          requestCacheServedMs >= TRACK_QUERY_LOG_THRESHOLD_MS
        ) {
          void safeDiagnosticsInfo('[OFPlayer track query]', 'query', 'track_collection', {
            roundTripMs,
            queryRevision,
            queryIdentityChanged,
            includeTrackIds: shouldIncludeTrackIds,
            requestCacheStatus,
            requestCacheHit: result?.diagnostics?.requestCacheHit === true,
            requestCacheServedMs,
            uiApplyProfile: applyResultProfile,
            diagnostics: result?.diagnostics ?? null,
            invokeOverheadMs: result?.diagnostics?.invokeOverheadMs ?? Math.max(0, roundTripMs - (result?.diagnostics?.totalMs ?? 0)),
            totalCount: result.totalCount,
            collectionTotalCount: result.collectionTotalCount,
            offset: result.offset,
            limit: queryLimit,
            activeCollection,
            currentLibraryId,
          })
        }
      } catch (error) {
        if (requestId !== visibleTrackQueryRequestId) {
          return
        }

        visibleTracks.value = []
        visibleTrackIds.value = []
        visibleTrackTotalCount.value = 0
        collectionTrackTotalCount.value = 0
        availableTrackFormats.value = []
        visibleTrackWindowOffset.value = 0
        void safeDiagnosticsError('[OFPlayer track query]', 'query', 'track_collection_failed', {
          error,
          activeCollection,
          currentLibraryId,
          offset: queryOffset,
          limit: queryLimit,
        })
      } finally {
        visibleTrackQueryTimerId = null
      }
    }

    if (previousSearchQuery !== searchQuery) {
      visibleTrackQueryTimerId = setTimeout(runTrackQuery, TRACK_QUERY_DEBOUNCE_MS)
      return
    }

    void runTrackQuery()
  },
  { immediate: true },
)

watch(
  () => resolvedActiveCollectionRef.value,
  () => {
    resetSongListScroll()
  },
)

watch(
  () => [visibleTracks.value.length, visibleTrackTotalCount.value],
  () => {
    scheduleSongListMeasurement()
  },
)

watch(
  visibleArtworkHydrationIds,
  (trackIds) => {
    if (trackIds.length > 0) {
      emit('hydrate-track-artwork', trackIds)
    }
  },
  { immediate: true, flush: 'post' },
)

watch(songListRef, (element, previousElement) => {
  if (songListResizeObserver && previousElement) {
    songListResizeObserver.unobserve(previousElement)
  }

  if (songListResizeObserver && element) {
    songListResizeObserver.observe(element)
  }

  scheduleSongListMeasurement()
})

function resolveTrackTitle(track: PlayerTrack | null | undefined): string {
  return track?.displayTitle || track?.title || t('player.untitled')
}

function normalizeArtworkSource(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function normalizeArtworkKeyField(value: unknown, fallback = ''): string {
  const normalized = typeof value === 'string' ? value.trim().toLowerCase() : ''
  return normalized || fallback
}

function createTrackArtworkAlbumKey(track: PlayerTrack | null | undefined): string {
  if (!track) {
    return ''
  }

  const libraryId = normalizeArtworkKeyField(track.libraryId, normalizeArtworkKeyField(props.currentLibrary?.id, '<unknown-library>'))
  const album = normalizeArtworkKeyField(track.album, '<unknown-album>')
  const artist = normalizeArtworkKeyField(track.albumArtist || track.artist, '<unknown-artist>')

  return `${libraryId}\u001f${album}\u001f${artist}`
}

function resolveRowTrackArtwork(track: PlayerTrack): PlayerTrack {
  const ownArtwork = normalizeArtworkSource(track?.artwork)

  if (ownArtwork) {
    return ownArtwork === track.artwork ? track : { ...track, artwork: ownArtwork }
  }

  const snapshotTrack = track.id ? trackSnapshotById.value.get(track.id) : null
  const snapshotArtwork = normalizeArtworkSource(snapshotTrack?.artwork)

  if (snapshotArtwork) {
    return { ...track, artwork: snapshotArtwork }
  }

  const albumArtwork = artworkByAlbumKey.value.get(createTrackArtworkAlbumKey(snapshotTrack ?? track)) ?? ''

  return albumArtwork ? { ...track, artwork: albumArtwork } : track
}

function hasManagedPlaybackSource(track: PlayerTrack | null | undefined): boolean {
  return typeof track?.source?.path === 'string' && track.source.path.length > 0
}

function resolveTrackArtist(track: PlayerTrack | null | undefined): string {
  return track?.artist || track?.albumArtist || t('track.unknownArtist')
}

function sortByOrder<T extends OrderedEntityLike>(items: T[] = []): T[] {
  return [...items].sort((left, right) => {
    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.createdAt ?? left?.id ?? '').localeCompare(String(right?.createdAt ?? right?.id ?? ''))
  })
}

function resolveTrackFormat(track: BrowserTrack | PlayerTrack | null | undefined): string {
  const format = String(track?.format ?? '')
    .trim()
    .toUpperCase()

  if (format) {
    return format
  }

  const extension = track?.fileName?.split('.').pop()?.toUpperCase()
  return extension || t('track.fileTypeFallback')
}

function normalizeRemoteRowMetadata(track: PlayerTrack): PlayerTrack {
  const bitrate = Number(track?.bitrate)
  const provider = props.currentLibrary?.source?.provider

  if (provider === 'subsonic' && Number.isFinite(bitrate) && bitrate > 0 && bitrate < 10_000) {
    return {
      ...track,
      bitrate: bitrate * 1_000,
    }
  }

  return track
}

function createRowMeta(track: PlayerTrack): string {
  const items = createTrackMetaItems(normalizeRemoteRowMetadata(track), {
    locale: locale.value,
    showTechnicalMetadata: props.showTechnicalMetadata,
    includeFormat: false,
    includeDuration: false,
    maxItems: 3,
  })

  return items.length > 0 ? items.join(' • ') : ''
}

function createRecentPlaybackRow(track: PlayerTrack): PlayerTrack {
  const fileSize = Number.isFinite(track?.fileSize)
    ? track.fileSize
    : Number.isFinite(track?.size)
      ? track.size
      : 0

  return {
    id: track?.id ?? '',
    libraryId: track?.libraryId ?? props.currentLibrary?.id ?? '',
    displayTitle: track?.displayTitle ?? null,
    title: track?.title ?? null,
    artist: track?.artist ?? null,
    albumArtist: track?.albumArtist ?? null,
    album: track?.album ?? null,
    fileName: track?.fileName ?? null,
    artwork: normalizeArtworkSource(track?.artwork),
    format: track?.format ?? null,
    duration: Number.isFinite(track?.duration) ? track.duration : 0,
    fileSize,
    size: fileSize,
    bitrate: Number.isFinite(track?.bitrate) ? track.bitrate : 0,
    sampleRate: Number.isFinite(track?.sampleRate) ? track.sampleRate : 0,
    bitDepth: Number.isFinite(track?.bitDepth) ? track.bitDepth : 0,
    isFavorite: track?.isFavorite === true,
  }
}

function applyLocalRecentPlaybackTrack(track: PlayerTrack | null | undefined): void {
  if (!canApplyLocalRecentPlaybackUpdate.value || !track?.id) {
    return
  }

  const row = createRecentPlaybackRow(track)

  if (!row.id || (props.currentLibrary?.id && row.libraryId !== props.currentLibrary.id)) {
    return
  }

  const wasWindowed = shouldWindowQueries.value
  const previousIds = visibleTrackIds.value
  const hadTrackId = previousIds.includes(row.id)
  const nextIds = [row.id, ...previousIds.filter((trackId) => trackId !== row.id)]
  visibleTrackIds.value = nextIds

  if (!hadTrackId) {
    visibleTrackTotalCount.value += 1
    collectionTrackTotalCount.value += 1
  }

  const existingIndex = visibleTracks.value.findIndex((item) => item?.id === row.id)
  const existingRow = existingIndex >= 0 ? visibleTracks.value[existingIndex] : null
  const nextRow = existingRow ? { ...existingRow, ...row } : row

  if (visibleTrackWindowOffset.value === 0) {
    const remainingRows = visibleTracks.value.filter((item) => item?.id !== row.id)
    const maxRows = wasWindowed
      ? Math.max(visibleTracks.value.length, 1)
      : remainingRows.length + 1
    visibleTracks.value = [nextRow, ...remainingRows].slice(0, maxRows)
    scheduleSongListMeasurement()
    return
  }

  if (existingIndex >= 0) {
    visibleTracks.value = visibleTracks.value.map((item) => (item?.id === row.id ? nextRow : item))
  }
}

function focusSearchInput() {
  nextTick(() => {
    searchInput.value?.focus()
  })
}

function toggleSearch() {
  if (!isSearchVisible.value) {
    isSearchOpen.value = true
  }

  focusSearchInput()
}

function closeSearch() {
  if (props.searchQuery.trim().length > 0) {
    emit('set-search-query', '')
  }

  isSearchOpen.value = false
}

function handleSearchInput(event: Event) {
  const target = event.currentTarget as HTMLInputElement | null
  emit('set-search-query', target?.value ?? '')
}

function handleVolumeInput(event: Event) {
  const target = event.currentTarget as HTMLInputElement | null
  emit('set-volume', Number(target?.value ?? props.volume))
}

function handleToggleFavorite(trackId?: string | null) {
  if (trackId) {
    emit('toggle-favorite', trackId)
  }
}

function handleRefreshLyrics(trackId?: string | null) {
  if (trackId) {
    emit('refresh-lyrics', trackId)
  }
}

function handleBindLyricsFile(trackId?: string | null) {
  if (trackId) {
    emit('bind-lyrics-file', trackId)
  }
}

function handleClearLyricsBinding(trackId?: string | null) {
  if (trackId) {
    emit('clear-lyrics-binding', trackId)
  }
}

function handleSearchEscape() {
  if (props.searchQuery.trim().length > 0) {
    closeSearch()
    return
  }

  isSearchOpen.value = false
}

function isToolbarMenuOpen(menu: ToolbarMenu) {
  return activeToolbarMenu.value === menu
}

function toggleToolbarMenu(menu: ToolbarMenu) {
  if (menu === 'sort' && isRecentlyPlayedCollection.value) {
    activeToolbarMenu.value = null
    return
  }

  activeToolbarMenu.value = activeToolbarMenu.value === menu ? null : menu
}

function closeToolbarMenu() {
  activeToolbarMenu.value = null
}

function selectSortOption(value: SortValue) {
  emit('set-sort-option', { sortOption: value, collectionRef: resolvedActiveCollectionRef.value })
  closeToolbarMenu()
}

function selectTypeFilter(value: string) {
  emit('set-type-filter', value)
  closeToolbarMenu()
}

function handleDocumentPointerDown(event: MouseEvent) {
  const target = event.target
  if (!(target instanceof Node)) {
    return
  }

  if (
    sortMenuRef.value?.contains(target) ||
    filterMenuRef.value?.contains(target) ||
    playbackOutputAnchorRef.value?.contains(target) ||
    playbackOutputDrawerRef.value?.contains(target)
  ) {
    return
  }

  closeToolbarMenu()
  closePlaybackOutputDrawer()
}

function handleDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    if (showsTrackSelectionControls.value) {
      closeTrackEditMode()
    }

    closeToolbarMenu()
    closePlaybackOutputDrawer()
  }
}

function formatTime(seconds?: number | null) {
  const safeSeconds = Number(seconds)
  if (!Number.isFinite(safeSeconds) || safeSeconds < 0) {
    return '0:00'
  }

  const totalSeconds = Math.floor(safeSeconds)
  const minutes = Math.floor(totalSeconds / 60)
  const remainder = totalSeconds % 60

  return `${minutes}:${String(remainder).padStart(2, '0')}`
}

function formatBitrate(bitrate: number | null | undefined) {
  const safeBitrate = Number(bitrate)
  if (!Number.isFinite(safeBitrate) || safeBitrate <= 0) {
    return t('player.details.unavailable')
  }

  return `${Math.round(safeBitrate / 1000)} kbps`
}

function formatSampleRate(sampleRate: number | null | undefined) {
  const safeSampleRate = Number(sampleRate)
  if (!Number.isFinite(safeSampleRate) || safeSampleRate <= 0) {
    return t('player.details.unavailable')
  }

  const rate = safeSampleRate / 1000
  return `${Number.isInteger(rate) ? rate : rate.toFixed(1)} kHz`
}

function formatPlaybackOutputName(device: PlaybackOutputDevice | null | undefined) {
  const name = device?.name ?? ''
  const backend = String(device?.backend ?? '').toLowerCase()
  const backendLabel = device?.backendLabel ?? ''

  if (name && backendLabel && backend !== 'wasapi') {
    return `${name} (${backendLabel})`
  }

  return name || t('settings.audioOutput.systemDefault')
}

function formatPlaybackIntegrityStatus(signalPath: PlaybackSignalPath | null = null) {
  if (!signalPath) {
    return t('player.details.signalUnknown')
  }

  if (signalPath.bitPerfect) {
    return t('player.details.signalBitPerfect')
  }

  if (signalPath.integrityStatus === 'converted') {
    return t('player.details.signalConverted')
  }

  if (signalPath.integrityStatus === 'not-bit-perfect') {
    return t('player.details.signalNotBitPerfect')
  }

  return t('player.details.signalUnknown')
}

function formatSignalFormat(format: PlaybackSignalFormat | null = null) {
  const safeFormat = format ?? {}
  const parts: string[] = []
  const sampleRate = Number(safeFormat.sampleRate)
  const channels = Number(safeFormat.channels)
  const bitDepth = Number(safeFormat.bitDepth)

  if (Number.isFinite(sampleRate) && sampleRate > 0) {
    parts.push(formatSampleRate(sampleRate))
  }

  if (Number.isFinite(channels) && channels > 0) {
    parts.push(t('player.details.signalChannels', { count: channels }))
  }

  if (Number.isFinite(bitDepth) && bitDepth > 0) {
    parts.push(formatBitDepth(bitDepth))
  }

  if (safeFormat.sampleFormat) {
    parts.push(safeFormat.sampleFormat)
  }

  return parts.length > 0 ? parts.join(' / ') : t('player.details.unavailable')
}

function formatSignalConversions(signalPath: PlaybackSignalPath | null = null) {
  const path = signalPath ?? {}
  const conversions: string[] = []

  if (path.resampled) {
    conversions.push(t('player.details.signalConversionRate'))
  }

  if (path.channelConverted) {
    conversions.push(t('player.details.signalConversionChannels'))
  }

  if (path.sampleFormatConverted) {
    conversions.push(t('player.details.signalConversionFormat'))
  }

  if (path.softwareMixer) {
    conversions.push(t('player.details.signalSoftwareMixer'))
  }

  if (path.softwareVolume) {
    conversions.push(t('player.details.signalSoftwareVolume'))
  }

  return conversions.length > 0
    ? conversions.join(', ')
    : t('player.details.signalNoConversion')
}

function formatBitDepth(bitDepth: number | null | undefined) {
  const safeBitDepth = Number(bitDepth)
  if (!Number.isFinite(safeBitDepth) || safeBitDepth <= 0) {
    return t('player.details.unavailable')
  }

  return `${safeBitDepth}-bit`
}

function formatYear(year?: number | null) {
  const safeYear = Number(year)
  if (!Number.isInteger(safeYear) || safeYear <= 0) {
    return ''
  }

  return String(safeYear)
}

function formatSequence(number?: number | null, total?: number | null) {
  const safeNumber = Number(number)
  const safeTotal = Number(total)
  if (!Number.isInteger(safeNumber) || safeNumber <= 0) {
    return ''
  }

  if (Number.isInteger(safeTotal) && safeTotal > 0) {
    return `${safeNumber} / ${safeTotal}`
  }

  return String(safeNumber)
}

function formatFileSize(bytes?: number | null) {
  const safeBytes = Number(bytes)
  if (!Number.isFinite(safeBytes) || safeBytes <= 0) {
    return t('player.details.unavailable')
  }

  const mb = safeBytes / (1024 * 1024)
  const formatter = new Intl.NumberFormat(locale.value, {
    maximumFractionDigits: mb >= 10 ? 0 : 1,
  })

  return `${formatter.format(mb)} MB`
}

function formatImportedAt(value?: string | null) {
  if (!value) {
    return t('player.details.unavailable')
  }

  const date = new Date(value)

  if (Number.isNaN(date.getTime())) {
    return t('player.details.unavailable')
  }

  return new Intl.DateTimeFormat(locale.value, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date)
}

function formatTrackIndex(index: number) {
  return String(index + 1).padStart(2, '0')
}

function artworkMonogram(track: PlayerTrack | null | undefined) {
  const label = resolveTrackTitle(track)

  if (!label) {
    return 'OF'
  }

  return label.slice(0, 2).toUpperCase()
}

function toggleInspector() {
  if (!props.currentTrack) {
    return
  }

  isInspectorOpen.value = !isInspectorOpen.value
}

function togglePlaybackToolsMenu(event: MouseEvent) {
  if (!props.currentTrack) {
    return
  }

  if (isPlaybackToolsMenuOpen.value) {
    closePlaybackToolsMenu()
    return
  }

  playbackToolsMenuAnchorEl.value = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  isPlaybackToolsMenuOpen.value = true
}

function closePlaybackToolsMenu() {
  isPlaybackToolsMenuOpen.value = false
  playbackToolsMenuAnchorEl.value = null
}

function updatePlaybackOutputDrawerPosition() {
  const anchor = playbackOutputAnchorRef.value

  if (!anchor || typeof window === 'undefined') {
    return
  }

  const rect = anchor.getBoundingClientRect()
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0
  const margin = 16
  const gap = 10
  const drawerWidth = Math.min(320, Math.max(260, viewportWidth - margin * 2))
  const maxLeft = Math.max(margin, viewportWidth - drawerWidth - margin)
  const left = Math.min(Math.max(rect.right - drawerWidth, margin), maxLeft)
  const bottom = Math.max(viewportHeight - rect.top + gap, margin)
  const maxHeight = Math.max(180, Math.min(380, rect.top - margin - gap))

  playbackOutputDrawerStyle.value = {
    '--playback-output-drawer-left': `${Math.round(left)}px`,
    '--playback-output-drawer-bottom': `${Math.round(bottom)}px`,
    '--playback-output-drawer-width': `${Math.round(drawerWidth)}px`,
    '--playback-output-drawer-max-height': `${Math.round(maxHeight)}px`,
  }
}

function schedulePlaybackOutputDrawerPositionUpdate() {
  if (playbackOutputDrawerFrameId !== null || typeof requestAnimationFrame === 'undefined') {
    if (typeof requestAnimationFrame === 'undefined') {
      updatePlaybackOutputDrawerPosition()
    }

    return
  }

  playbackOutputDrawerFrameId = requestAnimationFrame(() => {
    playbackOutputDrawerFrameId = null
    updatePlaybackOutputDrawerPosition()
  })
}

function togglePlaybackOutputDrawer() {
  if (isPlaybackOutputDrawerOpen.value) {
    closePlaybackOutputDrawer()
    return
  }

  closePlaybackToolsMenu()
  closeToolbarMenu()
  emit('refresh-playback-output-devices')
  isPlaybackOutputDrawerOpen.value = true
  void nextTick(() => {
    schedulePlaybackOutputDrawerPositionUpdate()
  })
}

function closePlaybackOutputDrawer() {
  isPlaybackOutputDrawerOpen.value = false
}

function selectPlaybackOutputDevice(deviceId: string) {
  if (props.isPlaying) {
    return
  }

  emit('set-playback-output-device', deviceId)
  closePlaybackOutputDrawer()
}

function handlePlaybackToolsMenuSelect(item: PlaybackToolsMenuItem) {
  closePlaybackToolsMenu()

  const trackId = props.currentTrack?.id
  if (!trackId) {
    return
  }

  if (item?.action === 'toggle-inspector') {
    toggleInspector()
    return
  }

  if (item?.action === 'bind-lyrics') {
    emit('bind-lyrics-file', trackId)
    return
  }

  if (item?.action === 'clear-lyrics') {
    emit('clear-lyrics-binding', trackId)
    return
  }

  if (item?.action === 'refresh-lyrics') {
    emit('refresh-lyrics', trackId)
  }
}

// CN: 曲目操作菜单
// EN: Track action menu
function toggleTrackMenu(trackId: string | null | undefined, event: Event) {
  if (openTrackMenuId.value === trackId) {
    closeTrackMenu()
    return
  }

  openTrackMenuId.value = trackId ?? null
  trackMenuAnchorEl.value = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
}

function closeTrackMenu() {
  openTrackMenuId.value = null
  trackMenuAnchorEl.value = null
}

function getTrackMenuItems(track: PlayerTrack): MenuDropdownItem[] {
  const items: MenuDropdownItem[] = [
    { key: 'add-to-playlist', label: t('sidebar.actions.addToPlaylist') },
  ]

  // CN: 如果当前是用户歌单，显示"从歌单移出"选项
  // EN: If current is user playlist, show "Remove from playlist" option
  if (props.currentCollection && !isSystemCollection(props.currentCollection)) {
    items.push({ key: 'remove-from-playlist', label: t('sidebar.actions.removeFromPlaylist') })
  }

  items.push({ key: 'delete-from-library', label: t('sidebar.actions.deleteFromLibrary') })

  return items
}

function isSystemCollection(collection: CollectionSummary | null | undefined) {
  if (!collection) return false
  // CN: 系统歌单或智能视图不可修改
  // EN: System playlists or smart views cannot be modified
  return collection.kind === 'view' || collection.systemKey != null
}

function handleTrackMenuSelect(track: PlayerTrack, action: MenuDropdownItem) {
  closeTrackMenu()

  if (action.key === 'add-to-playlist') {
    // CN: 显示歌单选择器
    // EN: Show playlist selector
    openAddToPlaylistDialog(track)
  } else if (action.key === 'remove-from-playlist') {
    openRemoveFromPlaylistDialog(track)
  } else if (action.key === 'delete-from-library') {
    openDeleteTrackDialog(track)
  }
}

function updateSongListMetrics() {
  if (!songListRef.value) {
    songListViewportHeight.value = 0
    return
  }

  songListViewportHeight.value = songListRef.value.clientHeight
  songListScrollTop.value = songListRef.value.scrollTop
}

function scheduleSongListMeasurement() {
  if (songListMeasureFrameId !== null) {
    cancelAnimationFrame(songListMeasureFrameId)
  }

  songListMeasureFrameId = requestAnimationFrame(() => {
    songListMeasureFrameId = null
    updateSongListMetrics()
  })
}

function handleSongListScroll(event: Event) {
  const target = event.currentTarget as HTMLElement | null
  songListScrollTop.value = target?.scrollTop ?? 0
}

function resetSongListScroll() {
  songListScrollTop.value = 0

  if (songListRef.value) {
    songListRef.value.scrollTop = 0
  }
}

function openAddToPlaylistDialog(track: PlayerTrack) {
  const preferredPlaylistId = resolvePreferredPlaylistId(track)

  addToPlaylistState.value = {
    isOpen: true,
    track,
    libraryId: track?.libraryId ?? props.currentLibrary?.id ?? null,
    playlistId: preferredPlaylistId,
  }
}

function resolvePreferredPlaylistId(track: PlayerTrack): string | null {
  if (!track?.libraryId) {
    return null
  }

  const collection = props.currentCollection
  if (
    collection?.kind === 'playlist' &&
    collection?.playlistKind === 'user' &&
    collection?.id
  ) {
    const currentPlaylist = props.playlists.find(
      (playlist) =>
        playlist.id === collection.id &&
        playlist.kind === 'user' &&
        playlist.libraryId === track.libraryId,
    )

    if (currentPlaylist) {
      return currentPlaylist.id ?? null
    }
  }

  return (
    sortByOrder(
      props.playlists.filter(
        (playlist) => playlist.kind === 'user' && playlist.libraryId === track.libraryId,
      ),
    )[0]?.id ?? null
  )
}

function closeAddToPlaylistDialog() {
  addToPlaylistState.value = {
    isOpen: false,
    track: null,
    libraryId: null,
    playlistId: null,
  }
}

function handleAddToPlaylistConfirm(payload: { trackId: string; playlistId: string }) {
  const { trackId, playlistId } = payload
  emit('add-track-to-playlist', {
    trackId,
    playlistId,
  })
  closeAddToPlaylistDialog()
}

// ========== CN: 拖拽排序 ========== EN: Drag-and-drop sorting ==========

function resolveSongRowWrap(event: Event): HTMLElement | null {
  const target = event.target
  return target instanceof Element ? target.closest<HTMLElement>('.song-row-wrap') : null
}

function handleDragStart(event: DragEvent, trackId?: string | null) {
  if (!canReorderTracks.value) {
    event.preventDefault()
    return
  }

  if (!trackId || !event.dataTransfer) {
    event.preventDefault()
    return
  }

  draggedTrackId.value = trackId
  event.dataTransfer.effectAllowed = 'move'
  event.dataTransfer.setData('text/plain', trackId)

  const target = resolveSongRowWrap(event)
  if (target) {
    target.classList.add('is-dragging')
  }
}

function handleDragEnd(event: DragEvent) {
  const target = resolveSongRowWrap(event)
  if (target) {
    target.classList.remove('is-dragging')
  }

  draggedTrackId.value = null
  dragOverTrackId.value = null
  dragPosition.value = null
}

function handleDragOver(event: DragEvent, trackId?: string | null) {
  if (!canReorderTracks.value || !draggedTrackId.value || draggedTrackId.value === trackId) {
    return
  }

  event.preventDefault()
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = 'move'
  }

  dragOverTrackId.value = trackId ?? null

  const target = resolveSongRowWrap(event)
  if (target) {
    const rect = target.getBoundingClientRect()
    const midY = rect.top + rect.height / 2
    dragPosition.value = event.clientY < midY ? 'before' : 'after'
  }
}

function handleDragLeave(event: DragEvent) {
  const target = resolveSongRowWrap(event)
  const relatedTarget = event.relatedTarget instanceof Node ? event.relatedTarget : null
  if (!target?.contains(relatedTarget)) {
    dragOverTrackId.value = null
    dragPosition.value = null
  }
}

function handleDrop(event: DragEvent, targetTrackId?: string | null) {
  event.preventDefault()

  if (
    !canReorderTracks.value ||
    !draggedTrackId.value ||
    draggedTrackId.value === targetTrackId
  ) {
    handleDragEnd(event)
    return
  }

  const sourceTrackId = draggedTrackId.value
  const destinationTrackId = targetTrackId
  const position = dragPosition.value || 'after'

  const currentTrackIds = [...visibleTrackIds.value]
  const sourceIndex = currentTrackIds.indexOf(sourceTrackId)
  const targetIndex = destinationTrackId ? currentTrackIds.indexOf(destinationTrackId) : -1

  if (sourceIndex === -1 || targetIndex === -1) {
    handleDragEnd(event)
    return
  }

  currentTrackIds.splice(sourceIndex, 1)

  const insertIndex = position === 'before' ? targetIndex : targetIndex + 1
  if (sourceIndex < targetIndex) {
    currentTrackIds.splice(position === 'before' ? targetIndex - 1 : targetIndex, 0, sourceTrackId)
  } else {
    currentTrackIds.splice(insertIndex > sourceIndex ? insertIndex - 1 : insertIndex, 0, sourceTrackId)
  }

  const playlistId = props.currentCollection?.id ?? null
  if (!playlistId) {
    handleDragEnd(event)
    return
  }

  emit('reorder-tracks', {
    playlistId,
    orderedTrackIds: currentTrackIds,
  })

  handleDragEnd(event)
}

function openRemoveFromPlaylistDialog(track: PlayerTrack) {
  const collection = props.currentCollection
  const trackId = track?.id
  const parsed = parseCollectionRef(collection?.key ?? '') as ParsedCollectionRef
  const playlistId = parsed.value ?? collection?.id ?? null

  if (!trackId || !playlistId) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'remove-from-playlist',
    title: t('sidebar.actions.removeFromPlaylist'),
    message: t('sidebar.dialogs.removeFromPlaylistConfirm'),
    isDanger: false,
    onConfirm: () => {
      emit('remove-track-from-playlist', {
        playlistId,
        trackId,
      })
    },
  }
}

function openDeleteTrackDialog(track: PlayerTrack) {
  const trackId = track?.id
  if (!trackId) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'delete-track',
    title: t('sidebar.actions.deleteFromLibrary'),
    message: t('sidebar.dialogs.deleteTrackConfirm'),
    isDanger: true,
    onConfirm: () => {
      emit('delete-track', trackId)
    },
  }
}

function handleDialogConfirm() {
  dialogState.value.onConfirm()
  closeDialog()
}

function closeDialog() {
  dialogState.value.isOpen = false
}

function isTrackSelected(trackId?: string | null) {
  return Boolean(trackId && selectedTrackIds.value.has(trackId))
}

function setTrackSelection(nextIds: Iterable<string>) {
  selectedTrackIds.value = new Set(
    Array.from(nextIds)
      .map((trackId) => String(trackId ?? '').trim())
      .filter(Boolean),
  )
}

function pruneSelectedTracks() {
  if (selectedTrackIds.value.size === 0) {
    return
  }

  const visibleIdSet = new Set(visibleTrackIds.value)
  const nextIds = selectedTrackIdsArray.value.filter((trackId) => visibleIdSet.has(trackId))

  if (nextIds.length !== selectedTrackIds.value.size) {
    setTrackSelection(nextIds)
  }
}

function clearTrackSelection() {
  setTrackSelection([])
  lastSelectedTrackId.value = null
}

function closeTrackEditMode() {
  isTrackEditMode.value = false
  clearTrackSelection()
}

function toggleTrackEditMode() {
  if (isTrackEditMode.value) {
    closeTrackEditMode()
    return
  }

  isTrackEditMode.value = true
}

function toggleTrackSelection(trackId?: string | null, event?: MouseEvent) {
  if (!trackId) {
    return
  }

  const nextIds = new Set(selectedTrackIds.value)

  if (event?.shiftKey && lastSelectedTrackId.value) {
    const startIndex = visibleTrackIds.value.indexOf(lastSelectedTrackId.value)
    const endIndex = visibleTrackIds.value.indexOf(trackId)

    if (startIndex !== -1 && endIndex !== -1) {
      const [from, to] = startIndex < endIndex ? [startIndex, endIndex] : [endIndex, startIndex]
      visibleTrackIds.value.slice(from, to + 1).forEach((id) => nextIds.add(id))
      setTrackSelection(nextIds)
      return
    }
  }

  if (nextIds.has(trackId)) {
    nextIds.delete(trackId)
  } else {
    nextIds.add(trackId)
  }

  lastSelectedTrackId.value = trackId
  setTrackSelection(nextIds)
}

function toggleVisibleTrackSelection() {
  if (allVisibleTracksSelected.value) {
    clearTrackSelection()
    return
  }

  setTrackSelection(visibleTrackIds.value)
}

function openDeleteSelectedTracksDialog() {
  const trackIds = selectedTrackIdsArray.value

  if (trackIds.length === 0) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'delete-tracks',
    title: t('player.deleteSelected'),
    message: t('player.deleteSelectedConfirm', { count: trackIds.length }),
    isDanger: true,
    onConfirm: () => {
      emit('delete-tracks', trackIds)
      closeTrackEditMode()
    },
  }
}

async function handleSelectTrack(trackId?: string | null) {
  if (!trackId) {
    return
  }

  if (visibleTrackIds.value.length > 0) {
    emit('select-track', { trackId, queueTrackIds: visibleTrackIds.value })
    return
  }

  emit('select-track', trackId)
}

function handleBrowserSelectTrack(track: AlbumBrowserEmittedTrack) {
  if (!track?.id) {
    return
  }

  const queueTrackIds = browserVisibleTracks.value
    .map((item) => item.id)
    .filter((trackId): trackId is string => Boolean(trackId))
  emit('select-track', {
    trackId: track.id,
    queueTrackIds: queueTrackIds.length > 0 ? queueTrackIds : null,
  })
}

function handleBrowserPlayGroup(tracks: AlbumBrowserEmittedTrack[] = []) {
  const queueTrackIds = tracks
    .map((track) => track?.id)
    .filter((trackId): trackId is string => Boolean(trackId))

  if (queueTrackIds.length === 0) {
    return
  }

  emit('select-track', {
    trackId: queueTrackIds[0],
    queueTrackIds,
  })
}

watch(visibleTrackIds, pruneSelectedTracks)

watch(isPlaybackOutputDrawerOpen, (isOpen) => {
  if (isOpen) {
    void nextTick(() => {
      schedulePlaybackOutputDrawerPositionUpdate()
    })
  }
})

watch(
  () => [props.currentLibrary?.id ?? '', resolvedActiveCollectionRef.value],
  () => {
    closeTrackEditMode()
  },
)

watchEffect(() => {
  if (selectAllCheckboxRef.value) {
    selectAllCheckboxRef.value.indeterminate = isSelectAllIndeterminate.value
  }
})

onMounted(() => {
  document.addEventListener('pointerdown', handleDocumentPointerDown)
  document.addEventListener('keydown', handleDocumentKeydown)
  window.addEventListener('resize', schedulePlaybackOutputDrawerPositionUpdate)
  scheduleSongListMeasurement()

  if (typeof ResizeObserver !== 'undefined') {
    songListResizeObserver = new ResizeObserver(() => {
      updateSongListMetrics()
    })

    if (songListRef.value) {
      songListResizeObserver.observe(songListRef.value)
    }
  }
})

onBeforeUnmount(() => {
  closePlaybackToolsMenu()

  if (playbackOutputDrawerFrameId !== null) {
    cancelAnimationFrame(playbackOutputDrawerFrameId)
    playbackOutputDrawerFrameId = null
  }

  if (visibleTrackQueryTimerId !== null) {
    clearTimeout(visibleTrackQueryTimerId)
    visibleTrackQueryTimerId = null
  }

  clearPlaybackSwitchAnimation()

  if (songListMeasureFrameId !== null) {
    cancelAnimationFrame(songListMeasureFrameId)
    songListMeasureFrameId = null
  }

  if (songListResizeObserver) {
    songListResizeObserver.disconnect()
    songListResizeObserver = null
  }

  document.removeEventListener('pointerdown', handleDocumentPointerDown)
  document.removeEventListener('keydown', handleDocumentKeydown)
  window.removeEventListener('resize', schedulePlaybackOutputDrawerPositionUpdate)
})

</script>

<template>
  <section class="panel panel-player">
    <div class="player-main">
      <section class="player-content">
        <header class="content-header">
          <div
            class="content-header-main content-header-main-compact"
            :class="{ 'has-remote-probe': showsRemoteProbe }"
          >
            <Transition name="content-copy-swap" mode="out-in" appear>
              <div :key="headerCopyKey" class="content-header-copy-block">
                <p class="eyebrow">{{ collectionEyebrow }}</p>
                <h2 class="content-title">{{ collectionTitle }}</h2>
              </div>
            </Transition>

            <button
              v-if="showsRemoteProbe"
              class="remote-probe"
              :class="[`is-${remoteProbePhase}`, { 'is-busy': isRemoteProbeBusy }]"
              type="button"
              :disabled="isRemoteProbeBusy"
              :title="remoteProbeTitle"
              :aria-label="t('player.remoteProbe.aria')"
              @click="emit('probe-remote-library', remoteProbeLibraryId)"
            >
              <component :is="remoteProbeIcon" aria-hidden="true" />
              <span>{{ remoteProbeLabel }}</span>
              <small>{{ remoteProbeMeta }}</small>
            </button>
          </div>

          <section class="library-toolbar" :aria-label="t('player.libraryTools')">
            <div class="library-toolbar-summary" :class="{ 'is-selection-active': hasSelectedTracks }">
              <Transition name="content-copy-swap" mode="out-in" appear>
                <strong :key="hasSelectedTracks ? selectedCountLabel : visibleCountKey" class="library-toolbar-summary-value">
                  {{ hasSelectedTracks ? selectedCountLabel : visibleCountLabel }}
                </strong>
              </Transition>
              <div v-if="hasSelectedTracks" class="library-selection-actions">
                <button
                  class="library-selection-button is-danger"
                  type="button"
                  :aria-label="t('player.deleteSelected')"
                  @click="openDeleteSelectedTracksDialog"
                >
                  <Trash2 aria-hidden="true" />
                  <span>{{ t('player.deleteSelected') }}</span>
                </button>
                <button
                  class="library-selection-icon"
                  type="button"
                  :aria-label="t('player.clearSelection')"
                  @click="clearTrackSelection"
                >
                  <X aria-hidden="true" />
                </button>
              </div>
            </div>

            <div v-if="!isSearchVisible" class="library-toolbar-actions">
              <button
                v-if="!currentCollection?.isBrowserView && visibleTrackIds.length > 0"
                class="library-toolbar-edit"
                :class="{ 'is-active': isTrackEditMode }"
                type="button"
                :aria-label="editSelectionLabel"
                :aria-pressed="isTrackEditMode"
                @click="toggleTrackEditMode"
              >
                <Pencil aria-hidden="true" />
                <span>{{ editSelectionLabel }}</span>
              </button>

              <div ref="sortMenuRef" class="library-toolbar-menu">
                <button
                  class="library-toolbar-select library-toolbar-select-button"
                  :class="{ 'is-open': isToolbarMenuOpen('sort') }"
                  type="button"
                  :aria-label="t('player.sortBy')"
                  :aria-expanded="isToolbarMenuOpen('sort')"
                  aria-controls="library-sort-menu"
                  @click="toggleToolbarMenu('sort')"
                >
                  <span>{{ t('player.sortBy') }}</span>
                  <strong>{{ currentSortLabel }}</strong>
                  <ChevronDown aria-hidden="true" />
                </button>

                <Transition name="toolbar-menu">
                  <div
                    v-if="isToolbarMenuOpen('sort')"
                    id="library-sort-menu"
                    class="library-toolbar-dropdown"
                    role="listbox"
                    :aria-label="t('player.sortBy')"
                  >
                    <button
                      v-for="option in sortOptions"
                      :key="option.value"
                      class="library-toolbar-option"
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

              <div ref="filterMenuRef" class="library-toolbar-menu">
                <button
                  class="library-toolbar-select library-toolbar-select-button"
                  :class="{ 'is-open': isToolbarMenuOpen('filter') }"
                  type="button"
                  :aria-label="t('player.filterBy')"
                  :aria-expanded="isToolbarMenuOpen('filter')"
                  aria-controls="library-filter-menu"
                  @click="toggleToolbarMenu('filter')"
                >
                  <span>{{ t('player.filterBy') }}</span>
                  <strong>{{ currentTypeFilterLabel }}</strong>
                  <ChevronDown aria-hidden="true" />
                </button>

                <Transition name="toolbar-menu">
                  <div
                    v-if="isToolbarMenuOpen('filter')"
                    id="library-filter-menu"
                    class="library-toolbar-dropdown"
                    role="listbox"
                    :aria-label="t('player.filterBy')"
                  >
                    <button
                      v-for="option in availableTypeFilters"
                      :key="option.value"
                      class="library-toolbar-option"
                      :class="{ 'is-active': option.value === typeFilter }"
                      type="button"
                      role="option"
                      :aria-selected="option.value === typeFilter"
                      @click="selectTypeFilter(option.value)"
                    >
                      {{ option.label }}
                    </button>
                  </div>
                </Transition>
              </div>

              <!-- CN: 保持为按钮，使搜索功能固定在右边缘。 -->
              <!-- EN: Kept as a button so the search affordance stays pinned on the right edge. -->
              <button
                class="library-search-toggle"
                :class="{ 'is-active': isSearchVisible }"
                type="button"
                :aria-label="t('player.searchTracks')"
                :aria-expanded="isSearchVisible"
                aria-controls="library-search-popover"
                @click="toggleSearch"
              >
                <Search aria-hidden="true" />
              </button>
            </div>

            <div v-if="isSearchVisible" id="library-search-popover" class="library-search-popover">
              <Search class="library-search-icon" aria-hidden="true" />
              <input
                ref="searchInput"
                :value="searchQuery"
                type="search"
                :aria-label="t('player.searchPlaceholder')"
                :placeholder="t('player.searchPlaceholder')"
                @input="handleSearchInput"
                @keydown.esc.prevent="handleSearchEscape"
              />
              <button
                class="library-search-clear"
                type="button"
                :aria-label="t('player.clearSearch')"
                @click="closeSearch"
              >
                <X aria-hidden="true" />
              </button>
            </div>
          </section>
        </header>

        <section
          class="track-region"
          :class="{ 'is-editing-tracks': showsTrackSelectionControls }"
          :aria-label="t('player.trackList')"
        >
          <div
            v-if="currentCollection?.isBrowserView && !collectionDataReady"
            class="main-empty-state main-empty-state--preparing"
          >
            <p>
              {{
                collectionDataStatus === 'error'
                  ? t('player.browserPrepareFailed')
                  : t('player.browserPreparing')
              }}
            </p>
            <span>
              {{
                collectionDataStatus === 'error'
                  ? t('player.browserPrepareRetry')
                  : t('player.browserPrepareCopy')
              }}
            </span>
          </div>

          <AlbumBrowserPanel
            v-else-if="currentCollection?.isBrowserView"
            :browser-kind="currentCollection.browserKind"
            :groups="browserVisibleGroups"
            :current-track-id="currentTrackId"
            :is-playing="isPlaying"
            :search-query="searchQuery"
            @select-track="handleBrowserSelectTrack"
            @play-group="handleBrowserPlayGroup"
            @cover-changed="() => {}"
          />

          <template v-else>
          <div class="track-region-head">
            <span v-if="showsTrackSelectionControls" class="track-region-head-cell track-region-head-cell--select">
              <input
                ref="selectAllCheckboxRef"
                class="track-select-checkbox"
                type="checkbox"
                :checked="allVisibleTracksSelected"
                :disabled="visibleTrackIds.length === 0"
                :aria-label="t('player.selectAllTracks')"
                @change="toggleVisibleTrackSelection"
              />
            </span>
            <span class="track-region-head-cell track-region-head-cell--index">#</span>
            <span class="track-region-head-cell track-region-head-cell--artwork"></span>
            <span class="track-region-head-cell track-region-head-cell--title">{{ t('player.trackColumn') }}</span>
            <span class="track-region-head-cell track-region-head-cell--duration">{{ t('player.length') }}</span>
            <span class="track-region-head-cell track-region-head-cell--format">{{ t('player.details.format') }}</span>
            <span class="track-region-head-cell track-region-head-cell--favorite" :aria-label="t('sidebar.actions.favorite')">
              <Heart aria-hidden="true" />
            </span>
            <span class="track-region-head-cell track-region-head-cell--more"></span>
          </div>

          <div v-if="!hasAnyTracks" class="main-empty-state">
            <p>{{ t('player.noTracks') }}</p>
            <span>{{ t('player.libraryWillAppear') }}</span>
          </div>

          <div v-else-if="collectionTrackTotalCount === 0 && visibleTracks.length === 0" class="main-empty-state">
            <p>{{ collectionTitle }}</p>
            <span>{{ emptyCollectionCopy }}</span>
          </div>

          <div v-else-if="visibleTracks.length === 0" class="main-empty-state">
            <p>{{ t('player.noMatches') }}</p>
            <span>{{ t('player.adjustTools') }}</span>
          </div>

          <div
            v-else
            ref="songListRef"
            class="song-list"
            :style="songListStyle"
            @scroll="handleSongListScroll"
          >
            <div
              v-if="shouldVirtualizeTracks"
              class="song-list-virtual-spacer"
              :style="{ height: `${totalVirtualTrackHeight}px` }"
            >
              <div
                class="song-list-virtual-window"
                :style="{ transform: `translateY(${virtualTrackOffset}px)` }"
              >
                <div
                  v-for="trackEntry in renderedTrackEntries"
                  :key="trackEntry.track.id"
                  class="song-row-wrap"
                  :class="{
                    'is-dragging': draggedTrackId === trackEntry.track.id,
                    'is-drag-over': dragOverTrackId === trackEntry.track.id,
                    'is-drag-over-before': dragOverTrackId === trackEntry.track.id && dragPosition === 'before',
                    'is-drag-over-after': dragOverTrackId === trackEntry.track.id && dragPosition === 'after',
                    'is-selected': isTrackSelected(trackEntry.track.id),
                    'can-reorder': canReorderTracks,
                  }"
                  :draggable="canReorderTracks"
                  @dragstart="handleDragStart($event, trackEntry.track.id)"
                  @dragend="handleDragEnd($event)"
                  @dragover="handleDragOver($event, trackEntry.track.id)"
                  @dragleave="handleDragLeave($event)"
                  @drop="handleDrop($event, trackEntry.track.id)"
                >
                  <div
                    class="song-row-shell"
                    :class="{
                      'is-active': trackEntry.track.id === currentTrackId,
                      'is-playing': trackEntry.track.id === currentTrackId && isPlaying,
                      'is-selected': isTrackSelected(trackEntry.track.id),
                      'has-selection-column': showsTrackSelectionControls,
                    }"
                  >
                    <input
                      v-if="showsTrackSelectionControls"
                      class="track-select-checkbox song-row-select"
                      type="checkbox"
                      :checked="isTrackSelected(trackEntry.track.id)"
                      :aria-label="t('player.selectTrack')"
                      @click.stop="toggleTrackSelection(trackEntry.track.id, $event)"
                    />
                    <button
                      class="song-row"
                      type="button"
                      @click="handleSelectTrack(trackEntry.track.id)"
                    >
                      <span class="song-row-index" :data-index="formatTrackIndex(trackEntry.index)" aria-hidden="true"></span>
                      <span class="song-row-artwork" aria-hidden="true">
                        <img
                          v-if="trackEntry.track.artwork"
                          :src="trackEntry.track.artwork"
                          alt=""
                          loading="lazy"
                          decoding="async"
                        />
                        <span v-else>{{ artworkMonogram(trackEntry.track) }}</span>
                      </span>
                      <div class="song-title">
                        <strong>{{ resolveTrackTitle(trackEntry.track) }}</strong>
                        <span v-if="createRowMeta(trackEntry.track)">{{ createRowMeta(trackEntry.track) }}</span>
                      </div>
                      <span class="song-duration">{{ formatTime(trackEntry.track.duration) }}</span>
                      <span class="song-state">{{ resolveTrackFormat(trackEntry.track) }}</span>
                    </button>
                    <button
                      type="button"
                      class="song-row-favorite"
                      :class="{ 'is-active': trackEntry.track.isFavorite }"
                      :aria-label="trackEntry.track.isFavorite ? t('sidebar.actions.unfavorite') : t('sidebar.actions.favorite')"
                      :aria-pressed="trackEntry.track.isFavorite"
                      @click.stop="handleToggleFavorite(trackEntry.track.id)"
                    >
                      <Heart aria-hidden="true" />
                    </button>
                    <button
                      type="button"
                      class="song-row-more"
                      :aria-label="t('sidebar.actions.more')"
                      @click.stop="toggleTrackMenu(trackEntry.track.id, $event)"
                    >
                      <MoreHorizontal aria-hidden="true" />
                    </button>
                  </div>
                  <MenuDropdown
                    :is-open="openTrackMenuId === trackEntry.track.id"
                    :anchor-el="openTrackMenuId === trackEntry.track.id ? trackMenuAnchorEl : null"
                    :items="getTrackMenuItems(trackEntry.track)"
                    @close="closeTrackMenu"
                    @select="(action) => handleTrackMenuSelect(trackEntry.track, action)"
                  />
                </div>
              </div>
            </div>

            <TransitionGroup
              v-else
              name="list"
              tag="div"
              class="song-list-static"
            >
              <div
                v-for="trackEntry in renderedTrackEntries"
                :key="trackEntry.track.id"
                class="song-row-wrap"
                :class="{
                  'is-dragging': draggedTrackId === trackEntry.track.id,
                  'is-drag-over': dragOverTrackId === trackEntry.track.id,
                  'is-drag-over-before': dragOverTrackId === trackEntry.track.id && dragPosition === 'before',
                  'is-drag-over-after': dragOverTrackId === trackEntry.track.id && dragPosition === 'after',
                  'is-selected': isTrackSelected(trackEntry.track.id),
                  'can-reorder': canReorderTracks,
                }"
                :draggable="canReorderTracks"
                @dragstart="handleDragStart($event, trackEntry.track.id)"
                @dragend="handleDragEnd($event)"
                @dragover="handleDragOver($event, trackEntry.track.id)"
                @dragleave="handleDragLeave($event)"
                @drop="handleDrop($event, trackEntry.track.id)"
              >
                <div
                  class="song-row-shell"
                  :class="{
                    'is-active': trackEntry.track.id === currentTrackId,
                    'is-playing': trackEntry.track.id === currentTrackId && isPlaying,
                    'is-selected': isTrackSelected(trackEntry.track.id),
                    'has-selection-column': showsTrackSelectionControls,
                  }"
                >
                  <input
                    v-if="showsTrackSelectionControls"
                    class="track-select-checkbox song-row-select"
                    type="checkbox"
                    :checked="isTrackSelected(trackEntry.track.id)"
                    :aria-label="t('player.selectTrack')"
                    @click.stop="toggleTrackSelection(trackEntry.track.id, $event)"
                  />
                  <button
                    class="song-row"
                    type="button"
                    @click="handleSelectTrack(trackEntry.track.id)"
                  >
                    <span class="song-row-index" :data-index="formatTrackIndex(trackEntry.index)" aria-hidden="true"></span>
                    <span class="song-row-artwork" aria-hidden="true">
                      <img
                        v-if="trackEntry.track.artwork"
                        :src="trackEntry.track.artwork"
                        alt=""
                        loading="lazy"
                        decoding="async"
                      />
                      <span v-else>{{ artworkMonogram(trackEntry.track) }}</span>
                    </span>
                    <div class="song-title">
                      <strong>{{ resolveTrackTitle(trackEntry.track) }}</strong>
                      <span v-if="createRowMeta(trackEntry.track)">{{ createRowMeta(trackEntry.track) }}</span>
                    </div>
                    <span class="song-duration">{{ formatTime(trackEntry.track.duration) }}</span>
                    <span class="song-state">{{ resolveTrackFormat(trackEntry.track) }}</span>
                  </button>
                  <button
                    type="button"
                    class="song-row-favorite"
                    :class="{ 'is-active': trackEntry.track.isFavorite }"
                    :aria-label="trackEntry.track.isFavorite ? t('sidebar.actions.unfavorite') : t('sidebar.actions.favorite')"
                    :aria-pressed="trackEntry.track.isFavorite"
                    @click.stop="handleToggleFavorite(trackEntry.track.id)"
                  >
                    <Heart aria-hidden="true" />
                  </button>
                  <button
                    type="button"
                    class="song-row-more"
                    :aria-label="t('sidebar.actions.more')"
                    @click.stop="toggleTrackMenu(trackEntry.track.id, $event)"
                  >
                    <MoreHorizontal aria-hidden="true" />
                  </button>
                </div>
                <MenuDropdown
                  :is-open="openTrackMenuId === trackEntry.track.id"
                  :anchor-el="openTrackMenuId === trackEntry.track.id ? trackMenuAnchorEl : null"
                  :items="getTrackMenuItems(trackEntry.track)"
                  @close="closeTrackMenu"
                  @select="(action) => handleTrackMenuSelect(trackEntry.track, action)"
                />
              </div>
            </TransitionGroup>
          </div>
          </template>
        </section>
      </section>

    </div>

    <div class="playback-dock-wrap">
      <footer
        class="playback-dock"
        :class="{
          'has-track': !!currentTrack,
          'is-playing': isPlaying,
          'is-switching': isPlaybackSwitching,
          'has-output-drawer': isPlaybackOutputDrawerOpen,
        }"
      >
        <div class="playback-topline">
          <div class="playback-summary">
            <button
              class="playback-artwork"
              type="button"
              :disabled="!currentTrack"
              :aria-label="t('player.immersive.open')"
              @click="emit('open-immersive-player')"
            >
              <img
                v-if="currentTrack?.artwork"
                :src="currentTrack.artwork"
                :alt="resolveTrackTitle(currentTrack)"
              />
              <span v-else>{{ artworkMonogram(currentTrack) }}</span>
            </button>

            <div v-if="currentTrack" class="playback-copy">
              <strong>{{ resolveTrackTitle(currentTrack) }}</strong>
              <span class="playback-meta">{{ currentTrackMeta }}</span>
              <span v-if="remoteTrackReadinessMessage" class="playback-remote-status">
                <Cloud aria-hidden="true" />
                <span>{{ remoteTrackReadinessMessage }}</span>
              </span>
              <span v-if="playbackErrorMessage" class="playback-error">{{ playbackErrorMessage }}</span>
            </div>
            <div v-else class="playback-copy is-empty">
              <strong>{{ t('player.nothingSelected') }}</strong>
              <span>{{ t('player.emptyPlaybackCopy') }}</span>
            </div>
          </div>

          <div class="playback-transport-slot">
            <div class="transport-stack">
              <div class="transport-row">
                <button
                  class="transport-button transport-button-secondary transport-button-icon"
                  type="button"
                  :disabled="!hasAnyTracks"
                  :aria-label="t('player.prev')"
                  @click="emit('play-previous')"
                >
                  <SkipBack aria-hidden="true" />
                </button>
                <button
                  class="transport-button transport-button-primary transport-button-icon transport-button-play"
                  type="button"
                  :disabled="!hasAnyTracks"
                  :aria-label="isPlaying ? t('player.pause') : t('player.play')"
                  @click="emit('toggle-playback')"
                >
                  <Pause v-if="isPlaying" aria-hidden="true" />
                  <Play v-else aria-hidden="true" />
                </button>
                <button
                  class="transport-button transport-button-secondary transport-button-icon"
                  type="button"
                  :disabled="!hasAnyTracks"
                  :aria-label="t('player.next')"
                  @click="emit('play-next')"
                >
                  <SkipForward aria-hidden="true" />
                </button>
                <button
                  class="transport-button transport-button-secondary transport-button-icon transport-button-mode"
                  :class="{ 'is-active': isPlaybackModeActive }"
                  :data-mode="playbackMode"
                  type="button"
                  :disabled="!hasAnyTracks"
                  :aria-label="playbackModeAriaLabel"
                  :aria-pressed="isPlaybackModeActive"
                  :title="playbackModeAriaLabel"
                  @click="emit('cycle-playback-mode')"
                >
                  <component :is="playbackModeIcon" aria-hidden="true" />
                </button>
              </div>
            </div>
          </div>

          <div class="playback-side">
            <button
              v-if="canOpenLyricCapsuleWindow"
              class="playback-tools-trigger playback-capsule-trigger"
              :class="{ 'is-active': lyricCapsuleWindowActive }"
              type="button"
              :aria-label="
                lyricCapsuleWindowActive
                  ? t('player.lyricCapsule.close')
                  : t('player.lyricCapsule.open')
              "
              :title="
                lyricCapsuleWindowActive
                  ? t('player.lyricCapsule.close')
                  : t('player.lyricCapsule.open')
              "
              @click="emit('toggle-lyric-capsule-window')"
            >
              <Captions aria-hidden="true" />
            </button>

            <button
              v-if="currentTrack"
              class="playback-tools-trigger"
              type="button"
              :aria-label="t('sidebar.actions.more')"
              :aria-expanded="isPlaybackToolsMenuOpen"
              @click="togglePlaybackToolsMenu"
            >
              <MoreHorizontal aria-hidden="true" />
            </button>

            <div
              ref="playbackOutputAnchorRef"
              class="playback-output-cell"
              :class="{ 'is-open': isPlaybackOutputDrawerOpen }"
            >
              <button
                class="playback-output-trigger"
                :class="{ 'is-warning': !playbackOutputDeviceAvailable && playbackOutputDeviceId }"
                type="button"
                :aria-label="t('player.playbackOutputDrawerToggle')"
                :aria-expanded="isPlaybackOutputDrawerOpen"
                aria-controls="playback-output-drawer"
                :title="t('player.playbackOutputDrawerToggle')"
                @click="togglePlaybackOutputDrawer"
              >
                <Monitor aria-hidden="true" />
                <span>{{ currentPlaybackOutputLabel }}</span>
              </button>

              <Teleport to="body">
                <Transition name="playback-output-drawer">
                  <section
                    v-if="isPlaybackOutputDrawerOpen"
                    id="playback-output-drawer"
                    ref="playbackOutputDrawerRef"
                    class="playback-output-drawer"
                    :style="playbackOutputDrawerStyle"
                    :aria-label="t('settings.fields.audioOutput')"
                    role="menu"
                  >
                    <header class="playback-output-drawer-head">
                      <strong>{{ t('settings.fields.audioOutput') }}</strong>
                      <button
                        class="playback-output-drawer-close"
                        type="button"
                        :aria-label="t('settings.close')"
                        @click="closePlaybackOutputDrawer"
                      >
                        <X aria-hidden="true" />
                      </button>
                    </header>

                    <div v-if="isPlaying" class="playback-output-drawer-note">
                      {{ t('settings.audioOutput.switchLocked') }}
                    </div>

                    <div class="playback-output-options">
                      <button
                        v-for="option in playbackOutputOptions"
                        :key="option.id || '__system__'"
                        class="playback-output-option"
                        :class="{ 'is-active': option.id === playbackOutputDeviceId }"
                        type="button"
                        role="menuitemradio"
                        :aria-checked="option.id === playbackOutputDeviceId"
                        :disabled="isPlaying || option.id === playbackOutputDeviceId"
                        @click="selectPlaybackOutputDevice(option.id)"
                      >
                        <Monitor aria-hidden="true" />
                        <span>
                          <strong>{{ option.name }}</strong>
                          <small>{{ option.description }}</small>
                        </span>
                        <Check v-if="option.id === playbackOutputDeviceId" aria-hidden="true" />
                      </button>
                    </div>

                    <div v-if="!hasPlaybackOutputChoices" class="playback-output-drawer-note is-muted">
                      {{ t('settings.audioOutput.noDevices') }}
                    </div>
                  </section>
                </Transition>
              </Teleport>
            </div>

            <label class="playback-volume">
              <Volume2 aria-hidden="true" />
              <span class="sr-only">{{ t('player.volume') }}</span>
              <input
                class="slider"
                type="range"
                min="0"
                max="1"
                step="0.01"
                :value="volume"
                @input="handleVolumeInput"
              />
            </label>
          </div>
        </div>

        <div class="playback-progress">
          <div class="timeline-head">
            <span>{{ formatTime(displayCurrentTime) }}</span>
            <span>{{ formatTime(duration) }}</span>
          </div>

          <div
            ref="progressSliderRef"
            class="progress-slider"
            :class="{
              'is-disabled': !currentTrack,
              'is-scrubbing': isProgressScrubbing,
            }"
            role="slider"
            :tabindex="currentTrack ? 0 : -1"
            :aria-label="t('player.progress')"
            aria-valuemin="0"
            :aria-valuemax="Math.round(durationValue)"
            :aria-valuenow="Math.round(displayProgressValue)"
            :aria-valuetext="formatTime(displayProgressValue)"
            :aria-disabled="!currentTrack"
            :style="progressSliderStyle"
            @pointerdown="beginProgressScrub"
            @pointermove="handleProgressPointerMove"
            @pointerup="commitProgressSeek"
            @pointercancel="cancelProgressScrub"
            @keydown="handleProgressKeydown"
            @blur="commitProgressSeekByBlur"
          >
            <span class="progress-slider-track" aria-hidden="true"></span>
            <span class="progress-slider-fill" aria-hidden="true"></span>
            <span class="progress-slider-thumb" aria-hidden="true"></span>
          </div>
        </div>
      </footer>

      <MenuDropdown
        :is-open="isPlaybackToolsMenuOpen"
        :anchor-el="playbackToolsMenuAnchorEl"
        :items="playbackToolsMenuItems"
        @close="closePlaybackToolsMenu"
        @select="handlePlaybackToolsMenuSelect"
      />

      <Transition name="inspector-popover">
        <aside
          v-if="currentTrack && isInspectorOpen"
          id="track-inspector-popover"
          class="track-inspector track-inspector-popover"
          :aria-label="t('player.details.panelTitle')"
        >
          <div class="track-inspector-scroll">
            <div class="track-inspector-head">
              <p class="eyebrow">{{ t('player.details.eyebrow') }}</p>
              <h3>{{ t('player.details.panelTitle') }}</h3>
            </div>

            <div class="track-inspector-body">
              <div class="track-inspector-artwork" aria-hidden="true">
                <img
                  v-if="currentTrack.artwork"
                  :src="currentTrack.artwork"
                  :alt="t('player.details.artworkAlt', { title: resolveTrackTitle(currentTrack) })"
                />
                <span v-else>{{ artworkMonogram(currentTrack) }}</span>
              </div>

              <div class="track-inspector-copy">
                <strong>{{ resolveTrackTitle(currentTrack) }}</strong>
                <span>{{ resolveTrackArtist(currentTrack) }}</span>
                <small v-if="currentTrack.album">{{ currentTrack.album }}</small>
              </div>

              <section class="track-inspector-lyrics">
                <div class="track-inspector-lyrics-head">
                  <div class="track-inspector-lyrics-head-copy">
                    <p class="track-inspector-section-label">{{ t('player.details.lyricsSection') }}</p>
                    <span class="track-inspector-section-status">{{ lyricsStatusLabel }}</span>
                  </div>

                  <div class="track-inspector-lyrics-actions">
                    <button
                      class="track-inspector-action track-inspector-action--secondary"
                      type="button"
                      @click="handleRefreshLyrics(currentTrack.id)"
                    >
                      {{ t('player.details.refreshLyrics') }}
                    </button>

                    <button
                      class="track-inspector-action"
                      type="button"
                      @click="handleBindLyricsFile(currentTrack.id)"
                    >
                      {{ hasExplicitLyricsBinding ? t('player.details.rebindLyrics') : t('player.details.bindLyrics') }}
                    </button>

                    <button
                      v-if="hasExplicitLyricsBinding"
                      class="track-inspector-action track-inspector-action--secondary"
                      type="button"
                      @click="handleClearLyricsBinding(currentTrack.id)"
                    >
                      {{ t('player.details.clearLyricsBinding') }}
                    </button>
                  </div>
                </div>

                <dl class="track-inspector-lyrics-meta">
                  <div class="track-inspector-lyrics-item">
                    <dt>{{ t('player.details.lyricsBinding') }}</dt>
                    <dd>
                      <strong :title="currentTrack.lyricsPath || ''">{{ lyricsBindingLabel }}</strong>
                      <small v-if="hasExplicitLyricsBinding">{{ currentTrack.lyricsPath }}</small>
                    </dd>
                  </div>

                  <div class="track-inspector-lyrics-item">
                    <dt>{{ t('player.details.lyricsResolved') }}</dt>
                    <dd>
                      <strong :title="props.lyricsSnapshot?.sourcePath || ''">{{ lyricsResolvedLabel }}</strong>
                      <small v-if="props.lyricsSnapshot?.sourcePath">{{ props.lyricsSnapshot.sourcePath }}</small>
                    </dd>
                  </div>

                  <div class="track-inspector-lyrics-item">
                    <dt>{{ t('player.details.lyricsStatus') }}</dt>
                    <dd>
                      <strong>{{ lyricsStatusLabel }}</strong>
                    </dd>
                  </div>
                </dl>
              </section>

              <dl class="track-inspector-meta">
                <div v-for="item in inspectorItems" :key="item.key" class="track-inspector-meta-item">
                  <dt>{{ item.label }}</dt>
                  <dd>{{ item.value }}</dd>
                </div>
              </dl>
            </div>
          </div>
        </aside>
      </Transition>
    </div>

    <DialogModal
      :is-open="dialogState.isOpen"
      :title="dialogState.title"
      :message="dialogState.message"
      :is-danger="dialogState.isDanger"
      @close="closeDialog"
      @confirm="handleDialogConfirm"
    />

    <TrackPlaylistDialog
      :is-open="addToPlaylistState.isOpen"
      :track="playlistDialogTrack"
      :libraries="playlistDialogLibraries"
      :playlists="playlistDialogPlaylists"
      :preferred-library-id="addToPlaylistState.libraryId"
      :preferred-playlist-id="addToPlaylistState.playlistId"
      @close="closeAddToPlaylistDialog"
      @confirm="handleAddToPlaylistConfirm"
    />
  </section>
</template>

<style scoped>
.track-region-head-cell--select,
.track-region-head-cell--favorite,
.track-region-head-cell--more {
  display: grid;
  place-items: center;
}

.track-select-checkbox {
  appearance: none;
  width: 16px;
  height: 16px;
  display: grid;
  place-items: center;
  margin: 0;
  border: 1px solid color-mix(in srgb, var(--ink-muted) 42%, var(--line-soft));
  border-radius: 5px;
  background: color-mix(in srgb, var(--surface-elevated) 84%, transparent);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08);
  accent-color: var(--ink);
  cursor: pointer;
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast),
    box-shadow var(--transition-fast),
    transform var(--transition-fast),
    opacity var(--transition-fast);
}

.track-select-checkbox::after {
  content: '';
  width: 8px;
  height: 4px;
  border-left: 2px solid var(--surface-base);
  border-bottom: 2px solid var(--surface-base);
  opacity: 0;
  transform: rotate(-45deg) scale(0.72) translateY(-1px);
  transition: opacity var(--transition-fast), transform var(--transition-fast);
}

.track-select-checkbox:hover {
  border-color: color-mix(in srgb, var(--ink) 38%, var(--line-soft));
  background: var(--surface-overlay);
  transform: scale(1.04);
}

.track-select-checkbox:checked {
  border-color: color-mix(in srgb, var(--primary) 52%, var(--line-soft));
  background: color-mix(in srgb, var(--primary) 78%, var(--ink) 22%);
  box-shadow:
    0 0 0 3px color-mix(in srgb, var(--primary) 14%, transparent),
    inset 0 1px 0 rgba(255, 255, 255, 0.22);
}

.track-select-checkbox:checked::after {
  opacity: 1;
  transform: rotate(-45deg) scale(1) translateY(-1px);
}

.track-select-checkbox:disabled {
  cursor: default;
  opacity: 0.42;
}

.library-toolbar-summary.is-selection-active {
  grid-template-columns: minmax(0, auto) auto;
  gap: var(--space-3);
}

.library-selection-actions {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}

.library-selection-button,
.library-selection-icon {
  display: inline-grid;
  place-items: center;
  min-width: 32px;
  height: 32px;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-full);
  background: var(--surface-base);
  color: var(--ink-muted);
  cursor: pointer;
  transition: background-color var(--transition-fast), border-color var(--transition-fast), color var(--transition-fast);
}

.library-selection-button {
  grid-auto-flow: column;
  gap: 0.4rem;
  padding: 0 0.7rem;
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
}

.library-selection-button.is-danger {
  color: var(--of-danger);
  border-color: color-mix(in srgb, var(--of-danger) 24%, var(--line-soft));
  background: color-mix(in srgb, var(--of-danger) 7%, var(--surface-base));
}

.library-selection-button:hover,
.library-selection-icon:hover {
  background: var(--state-layer-hover);
  border-color: var(--line);
  color: var(--ink);
}

.library-selection-button.is-danger:hover {
  color: var(--of-danger);
}

.library-selection-button svg,
.library-selection-icon svg {
  width: 15px;
  height: 15px;
}

.library-toolbar-edit {
  display: inline-grid;
  grid-auto-flow: column;
  align-items: center;
  gap: 0.4rem;
  height: 40px;
  padding: 0 var(--space-3);
  border: 1px solid var(--line);
  border-radius: var(--radius-full);
  background: var(--surface-elevated);
  box-shadow: var(--shadow-sm);
  color: var(--ink-muted);
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
  cursor: pointer;
  transition: background-color var(--transition-fast), border-color var(--transition-fast), color var(--transition-fast);
}

.library-toolbar-edit:hover,
.library-toolbar-edit.is-active {
  border-color: color-mix(in srgb, var(--primary) 24%, var(--line));
  background: color-mix(in srgb, var(--primary-container) 56%, var(--surface-elevated) 44%);
  color: var(--ink);
}

.library-toolbar-edit span {
  white-space: nowrap;
}

.library-toolbar-edit svg {
  width: 15px;
  height: 15px;
}

.track-region-head-cell--favorite svg {
  width: 14px;
  height: 14px;
  color: var(--ink-subtle);
}

.content-header-copy-block {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--space-2);
  min-width: 0;
}

.content-header-copy-block .eyebrow,
.content-header-copy-block .content-title {
  animation: none;
}

.library-toolbar-summary-value {
  display: block;
}

.content-copy-swap-enter-active,
.content-copy-swap-leave-active {
  transition:
    opacity var(--duration-md) var(--ease-standard),
    transform var(--duration-lg) var(--ease-emphasized-decelerate),
    filter var(--duration-md) var(--ease-standard);
  will-change: opacity, transform, filter;
}

.content-copy-swap-enter-from,
.content-copy-swap-leave-to {
  opacity: 0;
  transform: translateY(8px);
  filter: blur(8px);
}

.content-copy-swap-enter-to,
.content-copy-swap-leave-from {
  opacity: 1;
  transform: translateY(0);
  filter: blur(0);
}

.song-row-wrap {
  position: relative;
}

/* 拖拽排序样式 */
.song-row-wrap.can-reorder {
  cursor: grab;
}

.song-row-wrap.can-reorder:active {
  cursor: grabbing;
}

.song-row-wrap.is-dragging {
  opacity: 0.4;
}

.song-row-wrap.is-drag-over::before {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  height: 2px;
  background: var(--ink);
  z-index: 10;
}

.song-row-wrap.is-drag-over-before::before {
  top: 0;
}

.song-row-wrap.is-drag-over-after::before {
  bottom: 0;
}

.song-row-shell {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 32px 32px;
  align-items: center;
  gap: var(--space-1);
  transition:
    grid-template-columns var(--duration-md) var(--ease-emphasized-decelerate),
    background-color var(--transition-fast),
    box-shadow var(--transition-fast);
}

.song-row-shell.has-selection-column {
  grid-template-columns: 32px minmax(0, 1fr) 32px 32px;
}

.song-row-select {
  justify-self: center;
  opacity: 0.76;
  transform: scale(0.94);
}

.song-row-shell.has-selection-column .song-row-select,
.song-row-shell:hover .song-row-select {
  opacity: 1;
  transform: scale(1);
}

.song-list-virtual-spacer {
  position: relative;
  width: 100%;
}

.song-list-virtual-window {
  position: absolute;
  inset: 0 0 auto 0;
  width: 100%;
}

.song-list-static {
  width: 100%;
}

.song-row-favorite,
.song-row-more {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--ink-muted);
  cursor: pointer;
  transition: opacity var(--transition-fast), background-color var(--transition-fast), color var(--transition-fast);
}

.song-row-favorite {
  opacity: 0.72;
}

.song-row-more {
  opacity: 0.46;
}

.song-row-favorite.is-active {
  color: var(--of-like);
  opacity: 1;
}

.song-row-favorite.is-active svg {
  fill: currentColor;
}

.song-row-shell:hover .song-row-favorite,
.song-row-shell:hover .song-row-more,
.song-row-favorite:focus,
.song-row-more:focus {
  opacity: 1;
}

.song-row-favorite:hover,
.song-row-more:hover {
  background: var(--state-layer-hover);
  color: var(--ink);
}

.song-row-favorite svg,
.song-row-more svg {
  width: 16px;
  height: 16px;
}

.playback-error {
  display: block;
  margin-top: 0.125rem;
  font-size: var(--font-size-xs);
  line-height: 1.5;
  color: var(--of-danger);
}

.playback-remote-status {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  max-width: 100%;
  font-size: var(--font-size-xs);
  line-height: 1.35;
  color: var(--of-playing);
}

.playback-remote-status svg {
  flex: 0 0 auto;
  width: 13px;
  height: 13px;
}

.playback-remote-status span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.playback-topline {
  position: relative;
  z-index: 2;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
  align-items: center;
  min-height: 54px;
  gap: clamp(0.75rem, 1.1vw, var(--space-5));
}

.playback-summary {
  width: min(100%, 360px);
  justify-self: start;
}

.playback-transport-slot {
  min-width: 0;
  width: max-content;
  display: flex;
  align-items: center;
  justify-content: center;
  justify-self: center;
}

.playback-side {
  min-width: 0;
  display: grid;
  grid-template-columns: 34px 34px minmax(120px, 220px) minmax(88px, 128px);
  align-items: center;
  justify-self: end;
  justify-content: end;
  gap: var(--space-2);
  width: min(100%, 430px);
}

.playback-capsule-trigger {
  grid-column: 1;
}

.playback-side > .playback-tools-trigger:not(.playback-capsule-trigger) {
  grid-column: 2;
}

.playback-output-cell {
  grid-column: 3;
  position: relative;
  min-width: 0;
}

.playback-volume {
  grid-column: 4;
  width: min(100%, 128px);
  justify-self: end;
}

.playback-tools-trigger {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  min-width: 34px;
  padding: 0;
  border: 1px solid var(--line-soft);
  border-radius: 50%;
  background: var(--surface-overlay);
  color: var(--ink-muted);
  cursor: pointer;
  transition: background-color var(--transition-fast), border-color var(--transition-fast), color var(--transition-fast);
}

.playback-tools-trigger:hover {
  background: var(--state-layer-hover);
  border-color: color-mix(in srgb, var(--line-soft) 72%, var(--ink) 28%);
  color: var(--ink);
}

.playback-capsule-trigger.is-active {
  border-color: rgba(255, 105, 105, 0.44);
  background: rgba(255, 105, 105, 0.12);
  color: #ff6969;
}

.playback-capsule-trigger.is-active:hover {
  border-color: rgba(255, 105, 105, 0.58);
  background: rgba(255, 105, 105, 0.18);
  color: #ff7d7d;
}

.playback-tools-trigger svg {
  width: 16px;
  height: 16px;
}

.playback-output-trigger {
  min-width: 0;
  width: 100%;
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.45rem 0.7rem;
  border: 1px solid var(--line-soft);
  border-radius: 999px;
  background: var(--surface-overlay);
  color: var(--ink-muted);
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
  text-align: left;
  cursor: pointer;
  transition:
    transform var(--transition-normal),
    background-color var(--transition-fast),
    border-color var(--transition-fast),
    color var(--transition-fast),
    box-shadow var(--transition-fast);
}

.playback-output-trigger:hover,
.playback-output-trigger:focus-visible {
  border-color: color-mix(in srgb, var(--line-soft) 70%, var(--ink) 30%);
  background: var(--state-layer-hover);
  color: var(--ink);
  box-shadow: var(--shadow-sm);
}

.playback-output-trigger:hover {
  transform: translateY(-1px);
}

.playback-output-trigger.is-warning {
  color: var(--of-warning);
  border-color: var(--of-warning-border);
}

.playback-output-trigger svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.playback-output-trigger span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.playback-output-drawer {
  position: fixed;
  left: var(--playback-output-drawer-left, 1rem);
  bottom: var(--playback-output-drawer-bottom, 5rem);
  z-index: 4000;
  width: var(--playback-output-drawer-width, min(320px, calc(100vw - 2rem)));
  max-height: var(--playback-output-drawer-max-height, min(380px, calc(100vh - 180px)));
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-2);
  border: 1px solid var(--line);
  border-radius: var(--radius-lg);
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.14) 0%, rgba(255, 255, 255, 0.04) 100%),
    color-mix(in srgb, var(--surface-elevated) 54%, transparent);
  box-shadow:
    0 24px 54px -28px rgba(0, 0, 0, 0.68),
    0 10px 24px -20px rgba(0, 0, 0, 0.5),
    inset 0 1px 0 rgba(255, 255, 255, 0.1);
  backdrop-filter: blur(32px) saturate(1.42) brightness(1.06);
  -webkit-backdrop-filter: blur(32px) saturate(1.42) brightness(1.06);
  isolation: isolate;
  overflow: visible;
}

.playback-output-drawer-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  padding: 0.2rem 0.2rem 0.1rem 0.55rem;
}

.playback-output-drawer-head strong {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-bold);
  color: var(--ink);
}

.playback-output-drawer-close {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  min-width: 26px;
  padding: 0;
  border: none;
  border-radius: var(--radius-full);
  background: transparent;
  color: var(--ink-muted);
  cursor: pointer;
  transition: background-color var(--transition-fast), color var(--transition-fast);
}

.playback-output-drawer-close:hover {
  background: var(--state-layer-hover);
  color: var(--ink);
}

.playback-output-drawer-close svg {
  width: 14px;
  height: 14px;
}

.playback-output-options {
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  overflow-y: auto;
  scrollbar-width: none;
  -ms-overflow-style: none;
}

.playback-output-options::-webkit-scrollbar {
  width: 0;
  height: 0;
  display: none;
}

.playback-output-option {
  width: 100%;
  min-width: 0;
  display: grid;
  grid-template-columns: 24px minmax(0, 1fr) 18px;
  align-items: center;
  gap: 0.55rem;
  padding: 0.55rem 0.6rem;
  border: 1px solid transparent;
  border-radius: var(--radius-md);
  background: transparent;
  color: var(--ink-muted);
  text-align: left;
  cursor: pointer;
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast),
    color var(--transition-fast);
}

.playback-output-option:hover:not(:disabled) {
  border-color: var(--line-soft);
  background: var(--state-layer-hover);
  color: var(--ink);
}

.playback-output-option.is-active {
  border-color: color-mix(in srgb, var(--primary) 24%, var(--line-soft));
  background: color-mix(in srgb, var(--primary-container) 42%, var(--surface-overlay) 58%);
  color: var(--ink);
}

.playback-output-option:disabled {
  cursor: default;
  opacity: 0.72;
}

.playback-output-option > svg {
  width: 16px;
  height: 16px;
  justify-self: center;
}

.playback-output-option > svg:last-child {
  color: var(--primary);
}

.playback-output-option span {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 0.16rem;
}

.playback-output-option strong,
.playback-output-option small {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.playback-output-option strong {
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
  color: inherit;
}

.playback-output-option small {
  font-size: 0.6875rem;
  color: var(--ink-subtle);
}

.playback-output-drawer-note {
  margin: 0 0.2rem;
  padding: 0.45rem 0.55rem;
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--of-warning) 9%, transparent);
  color: var(--of-warning);
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-medium);
}

.playback-output-drawer-note.is-muted {
  background: var(--surface-overlay);
  color: var(--ink-muted);
}

.playback-output-drawer-enter-active,
.playback-output-drawer-leave-active {
  transition:
    opacity var(--duration-md) var(--ease-standard),
    transform var(--duration-md) var(--ease-emphasized-decelerate),
    filter var(--duration-md) var(--ease-standard);
  transform-origin: bottom right;
}

.playback-output-drawer-enter-from,
.playback-output-drawer-leave-to {
  opacity: 0;
  filter: blur(4px);
  transform: translateY(0.4rem) scale(0.96);
}

@media (max-width: 920px) {
  .playback-topline {
    grid-template-columns: 1fr;
    align-items: stretch;
    gap: var(--space-3);
  }

  .playback-summary,
  .playback-transport-slot {
    width: 100%;
    justify-self: stretch;
  }

  .playback-side {
    width: 100%;
    justify-self: stretch;
    justify-content: stretch;
    grid-template-columns: 34px 34px minmax(0, 1fr) minmax(96px, 128px);
  }

  .playback-output-trigger {
    width: min(100%, 240px);
  }
}

.track-inspector-lyrics {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  padding: 0.875rem 0;
  border-top: 1px solid var(--line-soft);
  border-bottom: 1px solid var(--line-soft);
}

.track-inspector-lyrics-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-3);
}

.track-inspector-lyrics-head-copy {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.track-inspector-section-label {
  margin: 0;
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-bold);
  letter-spacing: var(--letter-spacing-wide);
  text-transform: uppercase;
  color: var(--ink-muted);
}

.track-inspector-section-status {
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
}

.track-inspector-lyrics-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: var(--space-2);
}

.track-inspector-action {
  padding: 0.45rem 0.8rem;
  border: 1px solid var(--line-soft);
  border-radius: 999px;
  background: var(--surface-overlay);
  color: var(--ink);
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
  cursor: pointer;
  transition: background-color var(--transition-fast), border-color var(--transition-fast), color var(--transition-fast);
}

.track-inspector-action:hover {
  background: var(--state-layer-hover);
  border-color: color-mix(in srgb, var(--line-soft) 70%, var(--ink) 30%);
}

.track-inspector-action--secondary {
  color: var(--ink-muted);
}

.track-inspector-lyrics-meta {
  display: grid;
  gap: 0.7rem;
  margin: 0;
}

.track-inspector-lyrics-item {
  display: grid;
  gap: 0.3rem;
}

.track-inspector-lyrics-item dt {
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-bold);
  letter-spacing: var(--letter-spacing-wide);
  text-transform: uppercase;
  color: var(--ink-muted);
}

.track-inspector-lyrics-item dd {
  display: flex;
  flex-direction: column;
  gap: 0.24rem;
  margin: 0;
  min-width: 0;
}

.track-inspector-lyrics-item strong {
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
  word-break: break-word;
}

.track-inspector-lyrics-item small {
  font-size: var(--font-size-xs);
  color: var(--ink-subtle);
  word-break: break-all;
}
</style>
