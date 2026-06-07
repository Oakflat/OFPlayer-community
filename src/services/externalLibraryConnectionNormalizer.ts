import { EXTERNAL_LIBRARY_PROVIDERS } from '../models/externalLibrary'

export type ExternalLibraryProvider = 'webdav' | 'ftp' | 'subsonic' | (string & {})

export interface ExternalLibraryConnectionInput {
  provider?: ExternalLibraryProvider
  name?: string
  endpoint?: string
  rootPath?: string
  [key: string]: unknown
}

export interface NormalizedExternalLibraryConnectionResult {
  connection: ExternalLibraryConnectionInput & {
    provider: ExternalLibraryProvider
    name: string
    endpoint: string
    rootPath: string
  }
  changes: Array<'provider' | 'endpoint' | 'rootPath' | 'name'>
  changed: boolean
  detectedProvider: ExternalLibraryProvider | ''
  reason: string
}

const NAVIDROME_PORT = '4533'
const ALIST_PORT = '5244'
const WEBDAV_MARKERS = new Set(['dav', 'webdav'])
const NAVIDROME_UI_SEGMENTS = new Set(['app', 'ui', 'login'])
const NAVIDROME_API_SEGMENTS = new Set(['rest', 'api'])

type NormalizationChange = NormalizedExternalLibraryConnectionResult['changes'][number]

interface NormalizationDraft {
  connection: NormalizedExternalLibraryConnectionResult['connection']
  changes: NormalizationChange[]
  detectedProvider: ExternalLibraryProvider | ''
  reason: string
}

function text(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function isLikelyHost(value: string): boolean {
  return /^[a-z0-9.-]+(?::\d+)?(?:[/?#]|$)/i.test(value)
}

function isPrivateHost(hostname = ''): boolean {
  const host = hostname.toLowerCase()

  return (
    host === 'localhost' ||
    host.endsWith('.local') ||
    /^10\./.test(host) ||
    /^127\./.test(host) ||
    /^192\.168\./.test(host) ||
    /^172\.(1[6-9]|2\d|3[0-1])\./.test(host)
  )
}

function inferScheme(value: string): 'http' | 'https' {
  const hostPart = value.split(/[/?#]/, 1)[0] ?? ''
  const hasPort = /:\d+$/.test(hostPart)

  return hasPort || isPrivateHost(hostPart.split(':')[0]) ? 'http' : 'https'
}

function parseUrlLoose(value: unknown): URL | null {
  const raw = text(value)

  if (!raw) {
    return null
  }

  const candidate = /^[a-z][a-z0-9+.-]*:\/\//i.test(raw)
    ? raw
    : isLikelyHost(raw)
      ? `${inferScheme(raw)}://${raw}`
      : ''

  if (!candidate) {
    return null
  }

  try {
    return new URL(candidate)
  } catch {
    return null
  }
}

function getPathSegments(url: URL): string[] {
  return url.pathname
    .split('/')
    .map((segment) => decodeURIComponent(segment).trim())
    .filter(Boolean)
}

function clearSearchAndHash(url: URL): void {
  url.search = ''
  url.hash = ''
}

function normalizeOriginUrl(url: URL): string {
  const next = new URL(url)
  clearSearchAndHash(next)
  next.pathname = '/'
  return next.toString().replace(/\/$/, '')
}

function normalizeUrlWithPath(url: URL, segments: string[]): string {
  const next = new URL(url)
  clearSearchAndHash(next)
  next.pathname = segments.length ? `/${segments.map(encodeURIComponent).join('/')}` : '/'
  return next.toString().replace(/\/$/, '')
}

function normalizePath(value: unknown, { leadingSlash = true }: { leadingSlash?: boolean } = {}): string {
  const raw = text(value)
    .replace(/\\/g, '/')
    .replace(/[?#].*$/, '')
    .replace(/\/+/g, '/')
    .trim()

  if (!raw || raw === '/') {
    return ''
  }

  const stripped = raw.replace(/^\/+|\/+$/g, '')

  return leadingSlash ? `/${stripped}` : stripped
}

function appendRootPath(left: unknown, right: unknown): string {
  const parts = [left, right]
    .map((part) => normalizePath(part, { leadingSlash: false }))
    .filter(Boolean)

  return parts.length ? `/${parts.join('/')}` : ''
}

function findMarkerIndex(segments: string[], markers: Set<string>): number {
  return segments.findIndex((segment) => markers.has(segment.toLowerCase()))
}

function isNavidromeRoutePath(value: unknown): boolean {
  const normalized = normalizePath(value, { leadingSlash: false }).toLowerCase()

  if (!normalized) {
    return false
  }

  const firstSegment = normalized.split('/')[0]
  return NAVIDROME_UI_SEGMENTS.has(firstSegment) || NAVIDROME_API_SEGMENTS.has(firstSegment)
}

function providerFromInput(value: unknown): ExternalLibraryProvider {
  return Object.values(EXTERNAL_LIBRARY_PROVIDERS).includes(value as never)
    ? value as ExternalLibraryProvider
    : EXTERNAL_LIBRARY_PROVIDERS.WEBDAV
}

function providerName(provider: ExternalLibraryProvider): string {
  return provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC
    ? 'Navidrome'
    : provider === EXTERNAL_LIBRARY_PROVIDERS.WEBDAV
      ? 'WebDAV'
      : provider.toUpperCase()
}

function setProvider(result: NormalizationDraft, provider: ExternalLibraryProvider, reason: string): void {
  if (result.connection.provider !== provider) {
    result.connection.provider = provider
    result.changes.push('provider')
  }

  result.detectedProvider = provider
  result.reason = result.reason || reason
}

function setEndpoint(result: NormalizationDraft, endpoint: string): void {
  if (endpoint && result.connection.endpoint !== endpoint) {
    result.connection.endpoint = endpoint
    result.changes.push('endpoint')
  }
}

function setRootPath(result: NormalizationDraft, rootPath: string): void {
  if (result.connection.rootPath !== rootPath) {
    result.connection.rootPath = rootPath
    result.changes.push('rootPath')
  }
}

function setDefaultName(result: NormalizationDraft, previousProvider: ExternalLibraryProvider): void {
  const name = text(result.connection.name)
  const oldProviderName = providerName(previousProvider)
  const providerChanged = result.connection.provider !== previousProvider

  if (!name || (providerChanged && (name === 'Remote Library' || name === oldProviderName))) {
    const nextName = providerName(result.connection.provider)
    if (result.connection.name !== nextName) {
      result.connection.name = nextName
      result.changes.push('name')
    }
  }
}

export function normalizeExternalLibraryConnectionInput(
  input: ExternalLibraryConnectionInput = {},
): NormalizedExternalLibraryConnectionResult {
  const previousProvider = providerFromInput(input.provider)
  const result: NormalizationDraft = {
    connection: {
      ...input,
      provider: previousProvider,
      name: text(input.name),
      endpoint: text(input.endpoint),
      rootPath: text(input.rootPath),
    },
    changes: [],
    detectedProvider: '',
    reason: '',
  }

  if (!result.connection.endpoint && parseUrlLoose(result.connection.rootPath)) {
    setEndpoint(result, result.connection.rootPath)
    setRootPath(result, '')
  }

  const endpointUrl = parseUrlLoose(result.connection.endpoint)
  const rootUrl = parseUrlLoose(result.connection.rootPath)

  if (endpointUrl) {
    const segments = getPathSegments(endpointUrl)
    const firstSegment = segments[0]?.toLowerCase() ?? ''
    const webdavMarkerIndex = findMarkerIndex(segments, WEBDAV_MARKERS)
    const navidromeApiIndex = findMarkerIndex(segments, NAVIDROME_API_SEGMENTS)
    const navidromeUiIndex = findMarkerIndex(segments, NAVIDROME_UI_SEGMENTS)
    const isNavidromePort = endpointUrl.port === NAVIDROME_PORT
    const isAListPort = endpointUrl.port === ALIST_PORT

    if (isNavidromePort || navidromeApiIndex >= 0 || navidromeUiIndex >= 0 || isNavidromeRoutePath(result.connection.rootPath)) {
      setProvider(result, EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC, 'navidrome')
      setEndpoint(result, normalizeOriginUrl(endpointUrl))

      if (isNavidromeRoutePath(result.connection.rootPath) || rootUrl) {
        setRootPath(result, '')
      } else {
        setRootPath(result, normalizePath(result.connection.rootPath, { leadingSlash: false }))
      }
    } else if (webdavMarkerIndex >= 0) {
      setProvider(result, EXTERNAL_LIBRARY_PROVIDERS.WEBDAV, 'webdav')
      const endpointSegments = segments.slice(0, webdavMarkerIndex + 1)
      const trailingSegments = segments.slice(webdavMarkerIndex + 1)
      setEndpoint(result, normalizeUrlWithPath(endpointUrl, endpointSegments))
      setRootPath(result, appendRootPath(`/${trailingSegments.join('/')}`, result.connection.rootPath))
    } else if (isAListPort && firstSegment !== 'dav') {
      setProvider(result, EXTERNAL_LIBRARY_PROVIDERS.WEBDAV, 'alist')
      setEndpoint(result, normalizeUrlWithPath(endpointUrl, ['dav']))
      setRootPath(result, appendRootPath(endpointUrl.pathname, result.connection.rootPath))
    } else {
      setEndpoint(result, normalizeUrlWithPath(endpointUrl, segments))

      if (result.connection.provider === EXTERNAL_LIBRARY_PROVIDERS.WEBDAV) {
        setRootPath(result, normalizePath(result.connection.rootPath))
      } else if (result.connection.provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC) {
        setRootPath(result, normalizePath(result.connection.rootPath, { leadingSlash: false }))
      }
    }
  } else if (result.connection.provider === EXTERNAL_LIBRARY_PROVIDERS.WEBDAV) {
    setRootPath(result, normalizePath(result.connection.rootPath))
  } else if (result.connection.provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC) {
    setRootPath(result, normalizePath(result.connection.rootPath, { leadingSlash: false }))
  }

  setDefaultName(result, previousProvider)

  return {
    ...result,
    changed: result.changes.length > 0,
  }
}
