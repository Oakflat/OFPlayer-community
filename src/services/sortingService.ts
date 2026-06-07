import { invoke, isTauri } from '@tauri-apps/api/core'
import {
  buildRendererResourceProfile,
  buildRendererStepProfile,
  captureRendererResourceSample,
} from './diagnosticsProfiler'

export type TrackSortOption = 'recent' | 'title' | 'duration' | 'size'
export type TrackRequestCacheStatus = 'miss' | 'memory' | 'in-flight'

export interface TrackQueryFilters {
  searchQuery?: string
  typeFilter?: string
  sortOption?: TrackSortOption | string
}

export interface TrackQueryOptions {
  queryRevision?: string | number
  currentLibraryId?: string | null
  activeCollectionRef?: string | null
  offset?: number
  limit?: number | null
  includeTrackIds?: boolean
}

export interface TrackQueryDiagnostics {
  roundTripMs?: number
  originRoundTripMs?: number
  invokeOverheadMs?: number
  originInvokeOverheadMs?: number
  requestCacheStatus?: TrackRequestCacheStatus
  requestCacheHit?: boolean
  requestCacheServedMs?: number
  cacheWaitMs?: number
  totalMs?: number
  [key: string]: any
}

export interface TrackQueryResult {
  totalCount: number
  collectionTotalCount: number
  offset: number
  availableFormats: string[]
  trackIds: string[]
  rows: Record<string, any>[]
  roundTripMs: number
  diagnostics: TrackQueryDiagnostics
}

const SUPPORTED_SORT_OPTIONS = new Set(['recent', 'title', 'duration', 'size'])
const TRACK_QUERY_CACHE_LIMIT = 48

let cachedTrackQueryRevision = ''
const trackQueryCache = new Map<string, TrackQueryResult>()
const inFlightTrackQueries = new Map<string, Promise<TrackQueryResult>>()

function normalizeElapsedMs(value: any): number {
  return Number.isFinite(value) ? Math.max(0, Math.round(value)) : 0
}

function normalizeSortOption(value: any): TrackSortOption {
  return SUPPORTED_SORT_OPTIONS.has(value) ? value : 'recent'
}

function normalizeSearchQuery(value: any): string {
  return String(value ?? '').trim().toLowerCase()
}

function normalizeTypeFilter(value: any): string {
  return String(value ?? 'all').trim().toUpperCase() || 'ALL'
}

function normalizeQueryRevision(value: any): string {
  return String(value ?? '0')
}

function withTrackRequestCacheDiagnostics(
  result: TrackQueryResult,
  { cacheStatus = 'miss', servedMs = 0 }: { cacheStatus?: TrackRequestCacheStatus; servedMs?: number } = {},
): TrackQueryResult {
  const diagnostics = result?.diagnostics ?? {}
  const requestCacheServedMs = normalizeElapsedMs(servedMs)
  const originRoundTripMs = normalizeElapsedMs(
    diagnostics.originRoundTripMs ?? diagnostics.roundTripMs ?? result?.roundTripMs,
  )
  const roundTripMs = cacheStatus === 'miss' ? originRoundTripMs : requestCacheServedMs
  const originInvokeOverheadMs = normalizeElapsedMs(diagnostics.invokeOverheadMs)

  return {
    ...result,
    roundTripMs,
    diagnostics: {
      ...diagnostics,
      roundTripMs,
      originRoundTripMs,
      originInvokeOverheadMs,
      invokeOverheadMs: cacheStatus === 'miss' ? originInvokeOverheadMs : 0,
      requestCacheStatus: cacheStatus,
      requestCacheHit: cacheStatus !== 'miss',
      requestCacheServedMs,
      cacheWaitMs: cacheStatus === 'in-flight' ? requestCacheServedMs : 0,
    },
  }
}

function resetTrackQueryCache(queryRevision: string): void {
  if (cachedTrackQueryRevision === queryRevision) {
    return
  }

  cachedTrackQueryRevision = queryRevision
  trackQueryCache.clear()
  inFlightTrackQueries.clear()
}

function buildTrackQueryCacheKey(
  {
    searchQuery = '',
    typeFilter = 'ALL',
    sortOption = 'recent',
    queryRevision = '0',
  }: TrackQueryFilters & { queryRevision?: string } = {},
  {
    currentLibraryId = null,
    activeCollectionRef = null,
    offset = 0,
    limit = null,
    includeTrackIds = false,
  }: Omit<TrackQueryOptions, 'queryRevision'> = {},
): string {
  return JSON.stringify({
    queryRevision: normalizeQueryRevision(queryRevision),
    currentLibraryId: currentLibraryId ?? null,
    activeCollectionRef: activeCollectionRef ?? null,
    searchQuery,
    typeFilter,
    sortOption,
    offset,
    limit,
    includeTrackIds: includeTrackIds === true,
  })
}

function rememberTrackQueryResult(cacheKey: string, result: TrackQueryResult): void {
  trackQueryCache.set(cacheKey, result)

  if (trackQueryCache.size <= TRACK_QUERY_CACHE_LIMIT) {
    return
  }

  const [oldestKey] = trackQueryCache.keys()

  if (oldestKey) {
    trackQueryCache.delete(oldestKey)
  }
}

async function queryTracksFromDesktopCatalog(
  { searchQuery = '', typeFilter = 'all', sortOption = 'recent' }: TrackQueryFilters = {},
  {
    currentLibraryId = null,
    activeCollectionRef = null,
    offset = 0,
    limit = null,
    includeTrackIds = false,
  }: Omit<TrackQueryOptions, 'queryRevision'> = {},
): Promise<TrackQueryResult> {
  if (!isTauri()) {
    throw new Error('OFPlayer sorting requires the Tauri runtime.')
  }

  const requestStartedAt = performance.now()
  const requestResourceStart = captureRendererResourceSample()
  const result = await invoke<TrackQueryResult>('desktop_catalog_query_collection_tracks', {
    request: {
      activeLibrary: currentLibraryId,
      activeCollectionRef,
      searchQuery,
      typeFilter,
      sortOption,
      offset,
      limit,
      includeTrackIds,
    },
  })
  const roundTripMs = Math.round(performance.now() - requestStartedAt)
  const requestResourceEnd = captureRendererResourceSample()
  const frontendStepProfiles = [
    buildRendererStepProfile('invokeRoundTrip', roundTripMs, requestResourceStart, requestResourceEnd),
  ]

  if (!Number.isInteger(result?.totalCount) || result.totalCount < 0) {
    throw new Error('Rust collection query returned an invalid total count.')
  }

  if (!Number.isInteger(result?.collectionTotalCount) || result.collectionTotalCount < 0) {
    throw new Error('Rust collection query returned an invalid collection total count.')
  }

  if (!Number.isInteger(result?.offset) || result.offset < 0) {
    throw new Error('Rust collection query returned an invalid row offset.')
  }

  if (!Array.isArray(result?.availableFormats)) {
    throw new Error('Rust collection query returned an invalid available format list.')
  }

  if (!Array.isArray(result?.trackIds)) {
    throw new Error('Rust collection query returned an invalid track id list.')
  }

  if (!Array.isArray(result?.rows)) {
    throw new Error('Rust collection query returned an invalid row projection.')
  }

  return {
    totalCount: result.totalCount,
    collectionTotalCount: result.collectionTotalCount,
    offset: result.offset,
    availableFormats: result.availableFormats,
    trackIds: result.trackIds,
    rows: result.rows,
    roundTripMs,
    diagnostics: {
      ...(result?.diagnostics ?? {}),
      roundTripMs,
      invokeOverheadMs: Math.max(0, roundTripMs - (result?.diagnostics?.totalMs ?? 0)),
      frontendResources: buildRendererResourceProfile(requestResourceStart, requestResourceEnd),
      frontendStepProfiles,
    },
  }
}

export async function queryTracksWithBackend(
  { searchQuery = '', typeFilter = 'all', sortOption = 'recent' }: TrackQueryFilters = {},
  {
    queryRevision = '0',
    currentLibraryId = null,
    activeCollectionRef = null,
    offset = 0,
    limit = null,
    includeTrackIds = false,
  }: TrackQueryOptions = {},
): Promise<TrackQueryResult> {
  const normalizedSortOption = normalizeSortOption(sortOption)
  const normalizedSearchQuery = normalizeSearchQuery(searchQuery)
  const normalizedTypeFilter = normalizeTypeFilter(typeFilter)
  const normalizedOffset = Number.isInteger(offset) && offset > 0 ? offset : 0
  const normalizedLimit = typeof limit === 'number' && Number.isInteger(limit) && limit >= 0 ? limit : null
  const shouldIncludeTrackIds = includeTrackIds === true
  const normalizedQueryRevision = normalizeQueryRevision(queryRevision)

  resetTrackQueryCache(normalizedQueryRevision)

  const cacheKey = buildTrackQueryCacheKey(
    {
      searchQuery: normalizedSearchQuery,
      typeFilter: normalizedTypeFilter,
      sortOption: normalizedSortOption,
      queryRevision: normalizedQueryRevision,
    },
    {
      currentLibraryId,
      activeCollectionRef,
      offset: normalizedOffset,
      limit: normalizedLimit,
      includeTrackIds: shouldIncludeTrackIds,
    },
  )

  if (trackQueryCache.has(cacheKey)) {
    return withTrackRequestCacheDiagnostics(trackQueryCache.get(cacheKey)!, {
      cacheStatus: 'memory',
      servedMs: 0,
    })
  }

  if (inFlightTrackQueries.has(cacheKey)) {
    const cacheWaitStartedAt = performance.now()
    return inFlightTrackQueries.get(cacheKey)!.then((result) =>
      withTrackRequestCacheDiagnostics(result, {
        cacheStatus: 'in-flight',
        servedMs: performance.now() - cacheWaitStartedAt,
      }),
    )
  }

  const queryPromise = queryTracksFromDesktopCatalog(
    {
      searchQuery: normalizedSearchQuery,
      typeFilter: normalizedTypeFilter,
      sortOption: normalizedSortOption,
    },
    {
      currentLibraryId,
      activeCollectionRef,
      offset: normalizedOffset,
      limit: normalizedLimit,
      includeTrackIds: shouldIncludeTrackIds,
    },
  )
    .then((result) => {
      rememberTrackQueryResult(cacheKey, result)
      return withTrackRequestCacheDiagnostics(result, {
        cacheStatus: 'miss',
        servedMs: result?.roundTripMs ?? 0,
      })
    })
    .finally(() => {
      inFlightTrackQueries.delete(cacheKey)
    })

  inFlightTrackQueries.set(cacheKey, queryPromise)
  return queryPromise
}
