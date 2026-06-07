export interface NormalizedCommandError {
  code: string
  message: string
  source: string
  path: string
  fileName: string
  recoverable: boolean | null
  raw: unknown
}

const DEFAULT_ERROR_MESSAGE = 'Operation failed.'

function normalizeText(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function parseJsonRecord(value: string): Record<string, unknown> | null {
  try {
    return asRecord(JSON.parse(value))
  } catch {
    return null
  }
}

function resolveErrorRecord(error: unknown): Record<string, unknown> | null {
  if (error instanceof Error) {
    return {
      message: error.message,
      name: error.name,
    }
  }

  if (typeof error === 'string') {
    return parseJsonRecord(error)
  }

  return asRecord(error)
}

export function normalizeCommandError(
  error: unknown,
  fallbackMessage = DEFAULT_ERROR_MESSAGE,
): NormalizedCommandError {
  const record = resolveErrorRecord(error)
  const fallback = normalizeText(fallbackMessage) || DEFAULT_ERROR_MESSAGE
  const rawMessage = typeof error === 'string' ? error.trim() : ''
  const code = normalizeText(record?.code)
  const message =
    normalizeText(record?.message) ||
    normalizeText(record?.error) ||
    rawMessage ||
    fallback
  const source = normalizeText(record?.source) || normalizeText(record?.details)
  const path = normalizeText(record?.path)
  const fileName = normalizeText(record?.fileName) || normalizeText(record?.file_name)
  const recoverable = typeof record?.recoverable === 'boolean' ? record.recoverable : null

  return {
    code,
    message,
    source,
    path,
    fileName,
    recoverable,
    raw: error,
  }
}

export function formatCommandError(error: unknown, fallbackMessage = DEFAULT_ERROR_MESSAGE): string {
  const normalized = normalizeCommandError(error, fallbackMessage)
  return normalized.source && normalized.source !== normalized.message
    ? `${normalized.message} ${normalized.source}`
    : normalized.message
}
