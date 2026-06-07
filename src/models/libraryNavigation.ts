import { createPlaylistCollectionRef, createViewCollectionRef, SMART_VIEW_KEYS } from './collection'
import { SYSTEM_PLAYLIST_KEYS } from './playlist'

type TranslationParams = Record<string, string | number | boolean | null | undefined>
type TFunction = (key: string, params?: TranslationParams) => string
type BrowserKind = 'albums' | 'artists'

interface OrderedEntity {
  id: string
  order?: number
  createdAt?: string
}

interface LibraryNavigationSource {
  kind?: string
  provider?: string
  [key: string]: unknown
}

interface LibraryNavigationLibrary extends OrderedEntity {
  name?: string
  isDefault?: boolean
  source?: LibraryNavigationSource | null
}

interface LibraryNavigationPlaylist extends OrderedEntity {
  name?: string
  kind?: string
  libraryId?: string | null
  systemKey?: string | null
}

interface NavigationSummary {
  activeLibrary?: string | null
  activeCollectionKey?: string | null
  libraryTrackCounts?: Record<string, number>
  playlistTrackCounts?: Record<string, number>
  smartCollectionCounts?: Record<string, number>
}

interface SmartCollectionDefinition {
  key: string
  labelKey: string
  metaKey: string
  emptyKey: string
  isBrowserView?: boolean
  browserKind?: BrowserKind
}

interface CollectionNavigationItem {
  id: string
  key: string
  kind: 'playlist' | 'view'
  playlistKind?: string
  systemKey?: string
  label: string
  meta: string
  empty: string
  count: number
  isBrowserView?: boolean
  browserKind?: BrowserKind
  canRename: boolean
  canDelete: boolean
  canReorder: boolean
}

interface LibraryNavigationItem {
  id: string
  key: string
  label: string
  meta: string
  count: number
  isDefault?: boolean
  source?: LibraryNavigationSource | null
  isExternal: boolean
  canDelete: boolean
  canRename: boolean
}

interface BuildLibraryNavigationViewOptions {
  libraries?: LibraryNavigationLibrary[]
  playlists?: LibraryNavigationPlaylist[]
  summary?: NavigationSummary
  t: TFunction
}

function sortByOrder<T extends OrderedEntity>(items: T[] = []): T[] {
  return [...items].sort((left, right) => {
    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.createdAt ?? left?.id ?? '').localeCompare(String(right?.createdAt ?? right?.id ?? ''))
  })
}

function getPlaylistLabel(playlist: LibraryNavigationPlaylist, t: TFunction) {
  if (playlist?.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS) {
    return t('sidebar.playlists.allTracks')
  }

  return playlist?.name || t('sidebar.playlistSection')
}

function getPlaylistMeta(playlist: LibraryNavigationPlaylist, t: TFunction) {
  if (playlist?.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS) {
    return t('sidebar.playlists.allTracksMeta')
  }

  return t('sidebar.switcher.playlistMeta')
}

function getPlaylistEmpty(playlist: LibraryNavigationPlaylist, t: TFunction) {
  if (playlist?.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS) {
    return t('sidebar.playlists.allTracksEmpty')
  }

  return t('sidebar.playlists.allTracksEmpty')
}

function createPlaylistCollectionItem(
  playlist: LibraryNavigationPlaylist,
  count: number,
  t: TFunction,
): CollectionNavigationItem {
  return {
    id: playlist.id,
    key: createPlaylistCollectionRef(playlist.id),
    kind: 'playlist',
    playlistKind: playlist.kind,
    systemKey: playlist.systemKey ?? undefined,
    label: getPlaylistLabel(playlist, t),
    meta: getPlaylistMeta(playlist, t),
    empty: getPlaylistEmpty(playlist, t),
    count,
    canRename: playlist.kind !== 'system',
    canDelete: playlist.kind !== 'system',
    canReorder: playlist.kind !== 'system',
  }
}

const SMART_COLLECTION_DEFINITIONS: readonly SmartCollectionDefinition[] = Object.freeze([
  {
    key: SMART_VIEW_KEYS.RECENT_IMPORTS,
    labelKey: 'sidebar.smartCollections.recentImports',
    metaKey: 'sidebar.smartCollections.recentImportsMeta',
    emptyKey: 'sidebar.smartCollections.recentImportsEmpty',
  },
  {
    key: SMART_VIEW_KEYS.RECENTLY_PLAYED,
    labelKey: 'sidebar.smartCollections.recentlyPlayed',
    metaKey: 'sidebar.smartCollections.recentlyPlayedMeta',
    emptyKey: 'sidebar.smartCollections.recentlyPlayedEmpty',
  },
  {
    key: SMART_VIEW_KEYS.FAVORITES,
    labelKey: 'sidebar.smartCollections.favorites',
    metaKey: 'sidebar.smartCollections.favoritesMeta',
    emptyKey: 'sidebar.smartCollections.favoritesEmpty',
  },
  {
    key: SMART_VIEW_KEYS.CURRENT_QUEUE,
    labelKey: 'sidebar.smartCollections.currentQueue',
    metaKey: 'sidebar.smartCollections.currentQueueMeta',
    emptyKey: 'sidebar.smartCollections.currentQueueEmpty',
  },
  {
    key: SMART_VIEW_KEYS.ALBUMS,
    labelKey: 'sidebar.smartCollections.albums',
    metaKey: 'sidebar.smartCollections.albumsMeta',
    emptyKey: 'sidebar.smartCollections.albumsEmpty',
    isBrowserView: true,
    browserKind: 'albums',
  },
  {
    key: SMART_VIEW_KEYS.ARTISTS,
    labelKey: 'sidebar.smartCollections.artists',
    metaKey: 'sidebar.smartCollections.artistsMeta',
    emptyKey: 'sidebar.smartCollections.artistsEmpty',
    isBrowserView: true,
    browserKind: 'artists',
  },
])

function createSmartCollectionItem(
  definition: SmartCollectionDefinition,
  trackCount: number,
  t: TFunction,
): CollectionNavigationItem {
  return {
    id: definition.key,
    key: createViewCollectionRef(definition.key),
    kind: 'view',
    label: t(definition.labelKey),
    meta: t(definition.metaKey),
    empty: t(definition.emptyKey),
    count: trackCount,
    isBrowserView: definition.isBrowserView ?? false,
    browserKind: definition.browserKind ?? undefined,
    canRename: false,
    canDelete: false,
    canReorder: false,
  }
}

function buildDefaultCollectionKey(
  playlistItems: CollectionNavigationItem[] = [],
  smartCollections: CollectionNavigationItem[] = [],
) {
  return (
    playlistItems.find((playlist) => playlist.systemKey === SYSTEM_PLAYLIST_KEYS.ALL_TRACKS)?.key ??
    playlistItems[0]?.key ??
    smartCollections[0]?.key ??
    null
  )
}

export function buildLibraryNavigationView({
  libraries = [],
  playlists = [],
  summary = {},
  t,
}: BuildLibraryNavigationViewOptions) {
  const sortedLibraries = sortByOrder(libraries)
  const activeLibraryId =
    sortedLibraries.find((library) => library.id === summary.activeLibrary)?.id ??
    sortedLibraries[0]?.id ??
    null
  const libraryTrackCounts = summary.libraryTrackCounts ?? {}
  const playlistTrackCounts = summary.playlistTrackCounts ?? {}
  const smartCollectionCounts = summary.smartCollectionCounts ?? {}

  const libraryItems = sortedLibraries.map((library) => ({
    id: library.id,
    key: library.id,
    label: library.name || (library.isDefault ? t('sidebar.libraries.local') : t('sidebar.librarySection')),
    meta: library.isDefault ? t('sidebar.libraries.localMeta') : t('sidebar.switcher.libraryMeta'),
    count: libraryTrackCounts[library.id] ?? 0,
    isDefault: library.isDefault,
    source: library.source ?? undefined,
    isExternal: library.source?.kind === 'external',
    canDelete: !library.isDefault,
    canRename: true,
  }))

  const libraryPlaylists = activeLibraryId
    ? sortByOrder(playlists.filter((playlist) => playlist.libraryId === activeLibraryId))
    : []
  const playlistItems = libraryPlaylists.map((playlist) =>
    createPlaylistCollectionItem(playlist, playlistTrackCounts[playlist.id] ?? 0, t),
  )
  const smartCollections = SMART_COLLECTION_DEFINITIONS.map((definition) =>
    createSmartCollectionItem(definition, smartCollectionCounts[definition.key] ?? 0, t),
  )
  const collectionMap = new Map(
    [...playlistItems, ...smartCollections].map((collection) => [collection.key, collection]),
  )
  const defaultCollectionKey = buildDefaultCollectionKey(playlistItems, smartCollections)
  const resolvedCollection =
    collectionMap.get(summary.activeCollectionKey ?? '') ??
    (defaultCollectionKey ? collectionMap.get(defaultCollectionKey) : null) ??
    null

  return {
    activeLibrary: activeLibraryId,
    activeLibraryItem:
      libraryItems.find((library) => library.id === activeLibraryId) ?? libraryItems[0] ?? null,
    activeCollection: resolvedCollection,
    libraries: libraryItems,
    playlists: playlistItems,
    smartCollections,
  }
}
