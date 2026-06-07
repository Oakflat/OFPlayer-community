/**
 * CN: albumViewService.ts
 *
 * 纯派生层：从 Track[] 聚合出 AlbumGroup / ArtistGroup。
 * 不持久化，不依赖 store，只做计算。
 *
 * 封面覆盖（用户自定义封面）持久化到 localStorage。
 * key 格式：ofp:album-cover:<albumKey>
 * 其中 albumKey = normalizeKey(albumName + '|' + albumArtist)
 *
 * EN: albumViewService.ts
 *
 * Pure derivation layer: aggregates Track[] into AlbumGroup / ArtistGroup.
 * No persistence, no store dependency, computation only.
 *
 * Cover overrides (user-customized covers) are persisted to localStorage.
 * Key format: ofp:album-cover:<albumKey>
 * where albumKey = normalizeKey(albumName + '|' + albumArtist)
 */

const COVER_OVERRIDE_PREFIX = 'ofp:album-cover:'

export interface AlbumViewTrack {
  id?: string
  displayTitle?: string | null
  title?: string | null
  fileName?: string | null
  album?: string | null
  albumArtist?: string | null
  artist?: string | null
  genre?: string | null
  composer?: string | null
  lyricist?: string | null
  comment?: string | null
  artwork?: string | null
  year?: number | null
  discNumber?: number | null
  trackNumber?: number | null
  [key: string]: any
}

export interface AlbumGroup {
  key: string
  albumName: string
  albumArtist: string
  year: number | null
  embeddedArtworkUrl: string | null
  coverOverrideUrl: string | null
  coverUrl: any
  trackCount: number
  tracks: any[]
}

export interface ArtistGroup {
  key: string
  artistName: string
  albumCount: number
  trackCount: number
  coverUrl: any
  tracks: any[]
  albums: AlbumGroup[]
}

export interface AlbumBrowserSearchGroup {
  key: string
  coverUrl?: any
  embeddedArtworkUrl?: string | null
  coverOverrideUrl?: string | null
  albumCount?: number
  albums?: AlbumBrowserSearchGroup[]
  [key: string]: any
}

// ─── CN: 工具 ─── EN: Utilities ──────────────────────────────────────────────

function normalizeKey(str: unknown): string {
  return String(str ?? '')
    .trim()
    .toLowerCase()
    .replace(/\s+/g, ' ')
}

function makeAlbumKey(albumName: string, albumArtist: string): string {
  return normalizeKey(`${albumName}|${albumArtist}`)
}

function makeArtistKey(artistName: string): string {
  return normalizeKey(artistName)
}

function normalizeSearchQuery(query: unknown): string {
  return String(query ?? '').trim().toLowerCase()
}

function resolveSearchTitle(track: AlbumViewTrack): string {
  return track.displayTitle || track.title || track.fileName || ''
}

export function matchesAlbumBrowserTrackSearch(track: AlbumViewTrack, query: unknown): boolean {
  const normalizedQuery = normalizeSearchQuery(query)

  if (!normalizedQuery) {
    return true
  }

  const searchableText = [
    resolveSearchTitle(track),
    track.title,
    track.artist,
    track.albumArtist,
    track.album,
    track.genre,
    track.composer,
    track.lyricist,
    track.comment,
    track.fileName,
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()

  return searchableText.includes(normalizedQuery)
}

export function filterTracksForAlbumBrowserSearch<T extends AlbumViewTrack>(
  tracks: T[] = [],
  query: unknown = '',
): T[] {
  const normalizedQuery = normalizeSearchQuery(query)

  if (!normalizedQuery) {
    return tracks
  }

  return tracks.filter((track) => matchesAlbumBrowserTrackSearch(track, normalizedQuery))
}

export function mergeAlbumBrowserSearchGroupMetadata<TGroup extends AlbumBrowserSearchGroup>(
  filteredGroups: TGroup[] = [],
  sourceGroups: TGroup[] = [],
): TGroup[] {
  const sourceByKey = new Map(sourceGroups.map((group) => [group.key, group]))

  return filteredGroups.map((group) => {
    const sourceGroup = sourceByKey.get(group.key)

    if (!sourceGroup) {
      return group
    }

    const mergedAlbums = Array.isArray(group.albums)
      ? mergeAlbumBrowserSearchGroupMetadata(group.albums, sourceGroup.albums ?? [])
      : group.albums

    const mergedGroup = {
      ...group,
      embeddedArtworkUrl: sourceGroup.embeddedArtworkUrl ?? group.embeddedArtworkUrl,
      coverOverrideUrl: sourceGroup.coverOverrideUrl ?? group.coverOverrideUrl,
      coverUrl: sourceGroup.coverUrl ?? group.coverUrl,
    }

    if (Array.isArray(mergedAlbums)) {
      mergedGroup.albums = mergedAlbums
      mergedGroup.albumCount = mergedAlbums.length
    }

    return mergedGroup as TGroup
  })
}

// ─── CN: 封面覆盖持久化 ─── EN: Cover override persistence ───────────────────

export function getAlbumCoverOverride(albumKey: string): string | null {
  try {
    return localStorage.getItem(COVER_OVERRIDE_PREFIX + albumKey) ?? null
  } catch {
    return null
  }
}

// CN: ~1 MB 原始图片数据的 base64 表示（base64 开销 ≈ 4/3×）。
// CN: 超出此限制可能填满 localStorage 并静默破坏其他写入。
// EN: ~1 MB raw image data expressed as base64 (base64 overhead ≈ 4/3×).
// EN: Exceeding this risks filling localStorage and silently breaking other writes.
const MAX_COVER_BASE64_LENGTH = Math.ceil(1024 * 1024 * (4 / 3))

export function setAlbumCoverOverride(albumKey: string, dataUrl: string | null): void {
  try {
    if (dataUrl) {
      if (dataUrl.length > MAX_COVER_BASE64_LENGTH) return // silently reject oversized covers
      localStorage.setItem(COVER_OVERRIDE_PREFIX + albumKey, dataUrl)
    } else {
      localStorage.removeItem(COVER_OVERRIDE_PREFIX + albumKey)
    }
  } catch {
    // CN: localStorage 写入失败（隐私模式/配额）时静默忽略
    // EN: Silently ignore localStorage write failures (private mode/quota exceeded)
  }
}

export function removeAlbumCoverOverride(albumKey: string): void {
  setAlbumCoverOverride(albumKey, null)
}

// ─── CN: 专辑分组 ─── EN: Album grouping ─────────────────────────────────────

/**
 * CN: @param {Track[]} tracks
 * CN: @returns {AlbumGroup[]}
 * CN:
 * CN: AlbumGroup {
 * CN:   key: string           — 唯一键（标准化的 albumName|albumArtist）
 * CN:   albumName: string
 * CN:   albumArtist: string
 * CN:   year: number | null
 * CN:   embeddedArtworkUrl: string | null   — 来自 track.artwork
 * CN:   coverOverrideUrl: string | null     — 用户自定义封面（localStorage）
 * CN:   coverUrl: string | null             — 最终展示封面（override > embedded > null）
 * CN:   trackCount: number
 * CN:   tracks: Track[]                     — 按 discNumber / trackNumber 排序
 * CN: }
 *
 * EN: @param {Track[]} tracks
 * EN: @returns {AlbumGroup[]}
 * EN:
 * EN: AlbumGroup {
 * EN:   key: string           — Unique key (normalized albumName|albumArtist)
 * EN:   albumName: string
 * EN:   albumArtist: string
 * EN:   year: number | null
 * EN:   embeddedArtworkUrl: string | null   — From track.artwork
 * EN:   coverOverrideUrl: string | null     — User-customized cover (localStorage)
 * EN:   coverUrl: string | null             — Final display cover (override > embedded > null)
 * EN:   trackCount: number
 * EN:   tracks: Track[]                     — Sorted by discNumber / trackNumber
 * EN: }
 */
export function groupTracksByAlbum(tracks: AlbumViewTrack[]): AlbumGroup[] {
  const map = new Map<string, AlbumGroup>()

  for (const track of tracks) {
    const albumName = track.album?.trim() || '未知专辑'
    const albumArtist = track.albumArtist?.trim() || track.artist?.trim() || '未知艺术家'
    const key = makeAlbumKey(albumName, albumArtist)

    if (!map.has(key)) {
      map.set(key, {
        key,
        albumName,
        albumArtist,
        year: null,
        embeddedArtworkUrl: null,
        coverOverrideUrl: null,
        coverUrl: null,
        trackCount: 0,
        tracks: [],
      })
    }

    const group = map.get(key)!
    group.tracks.push(track)
    group.trackCount += 1

    // CN: 取第一个有封面的 track 的 artwork
    // EN: Take artwork from the first track with a cover
    if (!group.embeddedArtworkUrl && track.artwork) {
      group.embeddedArtworkUrl = track.artwork
    }

    // CN: 取最小年份
    // EN: Take the minimum year
    const resolvedYear =
      typeof track.year === 'number' && Number.isInteger(track.year) && track.year > 0
        ? track.year
        : null
    if (resolvedYear !== null) {
      if (group.year === null || resolvedYear < group.year) {
        group.year = resolvedYear
      }
    }
  }

  // CN: 每组内按 disc / track number 排序
  // EN: Sort within each group by disc / track number
  for (const group of map.values()) {
    group.tracks.sort((a, b) => {
      const discDiff = (a.discNumber ?? 0) - (b.discNumber ?? 0)
      if (discDiff !== 0) return discDiff
      return (a.trackNumber ?? 0) - (b.trackNumber ?? 0)
    })

    // CN: 合并封面 URL（override 优先）
    // EN: Merge cover URLs (override takes priority)
    group.coverOverrideUrl = getAlbumCoverOverride(group.key)
    group.coverUrl = group.coverOverrideUrl ?? group.embeddedArtworkUrl ?? null
  }

  // CN: 按年份降序，再按专辑名字母升序
  // EN: Sort by year descending, then album name ascending alphabetically
  return [...map.values()].sort((a, b) => {
    if (a.year !== b.year) {
      if (a.year === null) return 1
      if (b.year === null) return -1
      return b.year - a.year
    }
    return a.albumName.localeCompare(b.albumName)
  })
}

// ─── CN: 歌手分组 ─── EN: Artist grouping ────────────────────────────────────

/**
 * CN: @param {Track[]} tracks
 * CN: @returns {ArtistGroup[]}
 * CN:
 * CN: ArtistGroup {
 * CN:   key: string
 * CN:   artistName: string
 * CN:   albumCount: number
 * CN:   trackCount: number
 * CN:   coverUrl: string | null    — 取该歌手第一首有封面的 track
 * CN:   tracks: Track[]
 * CN:   albums: AlbumGroup[]       — 该歌手下的专辑（子聚合）
 * CN: }
 *
 * EN: @param {Track[]} tracks
 * EN: @returns {ArtistGroup[]}
 * EN:
 * EN: ArtistGroup {
 * EN:   key: string
 * EN:   artistName: string
 * EN:   albumCount: number
 * EN:   trackCount: number
 * EN:   coverUrl: string | null    — Take the first track with artwork for this artist
 * EN:   tracks: Track[]
 * EN:   albums: AlbumGroup[]       — Albums under this artist (sub-aggregation)
 * EN: }
 */
export function groupTracksByArtist(tracks: AlbumViewTrack[]): ArtistGroup[] {
  const map = new Map<string, Omit<ArtistGroup, 'albums'> & { albums?: AlbumGroup[] }>()

  for (const track of tracks) {
    const artistName = track.artist?.trim() || track.albumArtist?.trim() || '未知艺术家'
    const key = makeArtistKey(artistName)

    if (!map.has(key)) {
      map.set(key, {
        key,
        artistName,
        albumCount: 0,
        trackCount: 0,
        coverUrl: null,
        tracks: [],
      })
    }

    const group = map.get(key)!
    group.tracks.push(track)
    group.trackCount += 1

    if (!group.coverUrl && track.artwork) {
      group.coverUrl = track.artwork
    }
  }

  // CN: 为每个歌手构建子专辑列表
  // EN: Build sub-album list for each artist
  const result: ArtistGroup[] = []
  for (const group of map.values()) {
    const albums = groupTracksByAlbum(group.tracks)
    group.albums = albums
    group.albumCount = albums.length
    result.push(group as ArtistGroup)
  }

  // CN: 按歌手名字母升序
  // EN: Sort by artist name alphabetically
  result.sort((a, b) => a.artistName.localeCompare(b.artistName))
  return result
}
