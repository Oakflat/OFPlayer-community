import { invoke, isTauri } from '@tauri-apps/api/core'
import {
  buildRendererResourceProfile,
  buildRendererStepProfile,
  captureRendererResourceSample,
} from './diagnosticsProfiler'

export type NavigationRequestCacheStatus = 'miss' | 'memory' | 'in-flight'

export interface NavigationSummaryPayload {
  queryRevision?: string | number
  activeLibrary?: string | null
  activeCollection?: string | null
}

export interface NavigationQueryDiagnostics {
  roundTripMs?: number
  originRoundTripMs?: number
  invokeOverheadMs?: number
  originInvokeOverheadMs?: number
  requestCacheStatus?: NavigationRequestCacheStatus
  requestCacheHit?: boolean
  requestCacheServedMs?: number
  cacheWaitMs?: number
  totalMs?: number
  [key: string]: any
}

export interface NavigationSummary {
  diagnostics: NavigationQueryDiagnostics
  [key: string]: any
}

export interface NavigationQueryService {
  resolveNavigationSummary(payload?: NavigationSummaryPayload): Promise<NavigationSummary>
}

const NAVIGATION_CACHE_LIMIT = 12

let cachedNavigationRevision = ''
const navigationSummaryCache = new Map<string, NavigationSummary>()
const inFlightNavigationQueries = new Map<string, Promise<NavigationSummary>>()

function normalizeElapsedMs(value: any): number {
  return Number.isFinite(value) ? Math.max(0, Math.round(value)) : 0
}

function normalizeNavigationRevision(value: any): string {
  return String(value ?? '0')
}

function withNavigationRequestCacheDiagnostics(
  summary: NavigationSummary,
  { cacheStatus = 'miss', servedMs = 0 }: { cacheStatus?: NavigationRequestCacheStatus; servedMs?: number } = {},
): NavigationSummary {
  const diagnostics = summary?.diagnostics ?? {}
  const requestCacheServedMs = normalizeElapsedMs(servedMs)
  const originRoundTripMs = normalizeElapsedMs(
    diagnostics.originRoundTripMs ?? diagnostics.roundTripMs,
  )
  const roundTripMs = cacheStatus === 'miss' ? originRoundTripMs : requestCacheServedMs
  const originInvokeOverheadMs = normalizeElapsedMs(diagnostics.invokeOverheadMs)

  return {
    ...summary,
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

function clearNavigationQueryCache(nextRevision: string): void {
  if (cachedNavigationRevision === nextRevision) {
    return
  }

  cachedNavigationRevision = nextRevision
  navigationSummaryCache.clear()
  inFlightNavigationQueries.clear()
}

function buildNavigationQueryCacheKey({
  queryRevision = '0',
  activeLibrary = null,
  activeCollection = null,
}: NavigationSummaryPayload = {}): string {
  return JSON.stringify({
    queryRevision: normalizeNavigationRevision(queryRevision),
    activeLibrary: activeLibrary ?? null,
    activeCollection: activeCollection ?? null,
  })
}

function rememberNavigationSummary(cacheKey: string, summary: NavigationSummary): void {
  navigationSummaryCache.set(cacheKey, summary)

  if (navigationSummaryCache.size <= NAVIGATION_CACHE_LIMIT) {
    return
  }

  const [oldestKey] = navigationSummaryCache.keys()

  if (oldestKey) {
    navigationSummaryCache.delete(oldestKey)
  }
}

export function createNavigationQueryService(): NavigationQueryService {
  if (!isTauri()) {
    throw new Error('OFPlayer navigation queries require the Tauri runtime.')
  }

  return {
    async resolveNavigationSummary(payload: NavigationSummaryPayload = {}) {
      const queryRevision = normalizeNavigationRevision(payload.queryRevision)
      clearNavigationQueryCache(queryRevision)
      const cacheKey = buildNavigationQueryCacheKey({
        queryRevision,
        activeLibrary: payload.activeLibrary ?? null,
        activeCollection: payload.activeCollection ?? null,
      })

      if (navigationSummaryCache.has(cacheKey)) {
        return withNavigationRequestCacheDiagnostics(navigationSummaryCache.get(cacheKey)!, {
          cacheStatus: 'memory',
          servedMs: 0,
        })
      }

      if (inFlightNavigationQueries.has(cacheKey)) {
        const cacheWaitStartedAt = performance.now()
        return inFlightNavigationQueries.get(cacheKey)!.then((summary) =>
          withNavigationRequestCacheDiagnostics(summary, {
            cacheStatus: 'in-flight',
            servedMs: performance.now() - cacheWaitStartedAt,
          }),
        )
      }

      const requestStartedAt = performance.now()
      const requestResourceStart = captureRendererResourceSample()
      const queryPromise = invoke<NavigationSummary>('desktop_catalog_resolve_navigation', {
        request: {
          activeLibrary: payload.activeLibrary ?? null,
          activeCollectionRef: payload.activeCollection ?? null,
        },
      })
        .then((result) => {
          const roundTripMs = Math.round(performance.now() - requestStartedAt)
          const requestResourceEnd = captureRendererResourceSample()
          const frontendStepProfiles = [
            buildRendererStepProfile(
              'invokeRoundTrip',
              roundTripMs,
              requestResourceStart,
              requestResourceEnd,
            ),
          ]
          const summary = {
            ...result,
            diagnostics: {
              ...(result?.diagnostics ?? {}),
              roundTripMs,
              invokeOverheadMs: Math.max(0, roundTripMs - (result?.diagnostics?.totalMs ?? 0)),
              frontendResources: buildRendererResourceProfile(
                requestResourceStart,
                requestResourceEnd,
              ),
              frontendStepProfiles,
            },
          }
          rememberNavigationSummary(cacheKey, summary)
          return withNavigationRequestCacheDiagnostics(summary, {
            cacheStatus: 'miss',
            servedMs: roundTripMs,
          })
        })
        .finally(() => {
          inFlightNavigationQueries.delete(cacheKey)
        })

      inFlightNavigationQueries.set(cacheKey, queryPromise)
      return queryPromise
    },
  }
}
