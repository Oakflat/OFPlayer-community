export const EXTERNAL_LIBRARY_PROVIDERS = Object.freeze({
  WEBDAV: 'webdav',
  FTP: 'ftp',
  SUBSONIC: 'subsonic',
} as const)

export type ExternalLibraryProvider =
  (typeof EXTERNAL_LIBRARY_PROVIDERS)[keyof typeof EXTERNAL_LIBRARY_PROVIDERS]

export const EXTERNAL_LIBRARY_PROVIDER_SET: ReadonlySet<string> =
  new Set(Object.values(EXTERNAL_LIBRARY_PROVIDERS))

export interface ExternalLibraryAuthModel {
  username: string
  password: string
  token: string
  salt: string
  strategy: string
}

export interface ExternalLibrarySyncOptionsModel {
  lastCursor: string
}

export interface ExternalLibraryConnectionOverrides {
  id?: unknown
  provider?: unknown
  name?: unknown
  endpoint?: unknown
  rootPath?: unknown
  auth?: unknown
  sync?: unknown
  enabled?: unknown
  createdAt?: unknown
  updatedAt?: unknown
  lastSyncAt?: unknown
}

export interface ExternalLibraryConnectionModel {
  id: string
  provider: ExternalLibraryProvider
  name: string
  endpoint: string
  rootPath: string
  auth: ExternalLibraryAuthModel
  sync: ExternalLibrarySyncOptionsModel
  enabled: boolean
  createdAt: string
  updatedAt: string
  lastSyncAt: string
}

export interface LibraryExternalSourceOverrides {
  remoteId?: unknown
  rootPath?: unknown
}

export interface LibraryExternalSourceModel {
  kind: 'external'
  provider: ExternalLibraryProvider
  connectionId: string
  remoteId: string
  rootPath: string
}

function createConnectionId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `external-${crypto.randomUUID()}`
  }

  return `external-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? value as Record<string, unknown> : {}
}

function normalizeText(value: unknown, fallback = ''): string {
  if (typeof value !== 'string') {
    return fallback
  }

  const trimmed = value.trim()
  return trimmed || fallback
}

function normalizeBoolean(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function normalizeDate(value: unknown, fallback: string): string {
  return typeof value === 'string' && value ? value : fallback
}

function normalizeProvider(value: unknown): ExternalLibraryProvider {
  return typeof value === 'string' && EXTERNAL_LIBRARY_PROVIDER_SET.has(value)
    ? value as ExternalLibraryProvider
    : EXTERNAL_LIBRARY_PROVIDERS.WEBDAV
}

function normalizeAuth(auth: unknown = {}): ExternalLibraryAuthModel {
  const value = asRecord(auth)

  return {
    username: normalizeText(value.username),
    password: normalizeText(value.password),
    token: normalizeText(value.token),
    salt: normalizeText(value.salt),
    strategy: normalizeText(value.strategy),
  }
}

function normalizeSyncOptions(sync: unknown = {}): ExternalLibrarySyncOptionsModel {
  const value = asRecord(sync)

  return {
    lastCursor: normalizeText(value.lastCursor),
  }
}

export function createExternalLibraryConnectionModel(
  overrides: ExternalLibraryConnectionOverrides = {},
): ExternalLibraryConnectionModel {
  const now = new Date().toISOString()
  const provider = normalizeProvider(overrides.provider)

  return {
    id: normalizeText(overrides.id, createConnectionId()),
    provider,
    name: normalizeText(overrides.name, provider.toUpperCase()),
    endpoint: normalizeText(overrides.endpoint),
    rootPath: normalizeText(overrides.rootPath),
    auth: normalizeAuth(overrides.auth),
    sync: normalizeSyncOptions(overrides.sync),
    enabled: normalizeBoolean(overrides.enabled, true),
    createdAt: normalizeDate(overrides.createdAt, now),
    updatedAt: normalizeDate(overrides.updatedAt, now),
    lastSyncAt: normalizeDate(overrides.lastSyncAt, ''),
  }
}

export function createLibraryExternalSource(
  connection: ExternalLibraryConnectionOverrides,
  overrides: LibraryExternalSourceOverrides = {},
): LibraryExternalSourceModel {
  const normalizedConnection = createExternalLibraryConnectionModel(connection)

  return {
    kind: 'external',
    provider: normalizedConnection.provider,
    connectionId: normalizedConnection.id,
    remoteId: normalizeText(overrides.remoteId),
    rootPath: normalizeText(overrides.rootPath, normalizedConnection.rootPath),
  }
}
