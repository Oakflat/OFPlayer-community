export const DEFAULT_LIBRARY_ID = 'library-default'

export interface LibrarySourceModel {
  kind: 'external' | 'local'
  provider: string
  connectionId: string
  remoteId: string
  rootPath: string
}

export interface LibraryModelOverrides {
  id?: string
  name?: unknown
  order?: unknown
  isDefault?: unknown
  source?: unknown
  createdAt?: unknown
  updatedAt?: unknown
}

export interface LibraryModel {
  id: string
  name: string
  order: number
  isDefault: boolean
  source: LibrarySourceModel
  createdAt: string
  updatedAt: string
}

function createLibraryId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `library-${crypto.randomUUID()}`
  }

  return `library-${Date.now()}-${Math.random().toString(16).slice(2)}`
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

function normalizeOrder(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

function normalizeBoolean(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function normalizeDate(value: unknown, fallback: string): string {
  return typeof value === 'string' && value ? value : fallback
}

function normalizeLibrarySource(value: unknown = {}): LibrarySourceModel {
  const source = asRecord(value)
  const kind = source.kind === 'external' ? 'external' : 'local'

  return {
    kind,
    provider: normalizeText(source.provider, kind === 'external' ? 'external' : 'local'),
    connectionId: normalizeText(source.connectionId),
    remoteId: normalizeText(source.remoteId),
    rootPath: normalizeText(source.rootPath),
  }
}

export function createLibraryModel(overrides: LibraryModelOverrides = {}): LibraryModel {
  const now = new Date().toISOString()

  return {
    id: overrides.id ?? createLibraryId(),
    name: normalizeText(overrides.name),
    order: normalizeOrder(overrides.order),
    isDefault: normalizeBoolean(overrides.isDefault),
    source: normalizeLibrarySource(overrides.source),
    createdAt: normalizeDate(overrides.createdAt, now),
    updatedAt: normalizeDate(overrides.updatedAt, now),
  }
}

export function createDefaultLibraryModel(overrides: LibraryModelOverrides = {}): LibraryModel {
  return createLibraryModel({
    id: DEFAULT_LIBRARY_ID,
    isDefault: true,
    order: 0,
    name: '',
    ...overrides,
  })
}
