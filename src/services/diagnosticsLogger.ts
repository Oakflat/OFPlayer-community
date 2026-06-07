import { invoke, isTauri } from '@tauri-apps/api/core'

export type DiagnosticsLogLevel = 'info' | 'warn' | 'error'
export type DiagnosticsPayload = unknown

export interface DiagnosticsLogEventRequest {
  level?: DiagnosticsLogLevel
  label: string
  category: string
  event: string
  payload?: DiagnosticsPayload
}

export interface PersistedDiagnosticsLogEventRequest {
  level: DiagnosticsLogLevel
  label: string
  category: string
  event: string
  payload: unknown
}

export interface DiagnosticsLogStatus {
  path: string
  directory: string
  directoryKind: string
  fallbackReason: string | null
}

let diagnosticsWriteQueue: Promise<boolean | null> = Promise.resolve(true)
let diagnosticsLogStatusPromise: Promise<DiagnosticsLogStatus | null> | null = null

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function normalizeDiagnosticsPayload(value: unknown): unknown {
  if (value instanceof Error) {
    return {
      name: value.name,
      message: value.message,
      stack: value.stack ?? null,
    }
  }

  if (Array.isArray(value)) {
    return value.map((item) => normalizeDiagnosticsPayload(item))
  }

  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, nestedValue]) => [key, normalizeDiagnosticsPayload(nestedValue)]),
    )
  }

  if (typeof value === 'bigint') {
    return value.toString()
  }

  if (typeof value === 'undefined') {
    return null
  }

  return value
}

function consoleMethodForLevel(level: DiagnosticsLogLevel): 'info' | 'warn' | 'error' {
  if (level === 'error') {
    return 'error'
  }

  if (level === 'warn') {
    return 'warn'
  }

  return 'info'
}

function printDiagnosticsToConsole(level: DiagnosticsLogLevel, label: string, payload: unknown): void {
  const method = consoleMethodForLevel(level)
  const logger = typeof console?.[method] === 'function' ? console[method] : console.log

  if (payload === null) {
    logger(label)
    return
  }

  logger(label, payload)
}

async function persistDiagnosticsEvent(request: PersistedDiagnosticsLogEventRequest): Promise<boolean> {
  if (!isTauri()) {
    return false
  }

  diagnosticsWriteQueue = diagnosticsWriteQueue
    .catch(() => null)
    .then(() =>
      invoke('diagnostics_log_event', {
        request,
      }),
    )
    .then(() => true)
    .catch((error) => {
      const normalizedError = normalizeDiagnosticsPayload(error)
      const logger = typeof console?.warn === 'function' ? console.warn : console.log
      logger('Failed to persist OFPlayer diagnostics event.', normalizedError)
      return false
    })

  return diagnosticsWriteQueue.then(Boolean)
}

export function logDiagnosticsEvent({
  level = 'info',
  label,
  category,
  event,
  payload = null,
}: DiagnosticsLogEventRequest): Promise<boolean> {
  const normalizedPayload = normalizeDiagnosticsPayload(payload)
  printDiagnosticsToConsole(level, label, normalizedPayload)

  return persistDiagnosticsEvent({
    level,
    label,
    category,
    event,
    payload: normalizedPayload,
  })
}

export function logDiagnosticsInfo(
  label: string,
  category: string,
  event: string,
  payload: DiagnosticsPayload = null,
): Promise<boolean> {
  return logDiagnosticsEvent({
    level: 'info',
    label,
    category,
    event,
    payload,
  })
}

export function logDiagnosticsWarn(
  label: string,
  category: string,
  event: string,
  payload: DiagnosticsPayload = null,
): Promise<boolean> {
  return logDiagnosticsEvent({
    level: 'warn',
    label,
    category,
    event,
    payload,
  })
}

export function logDiagnosticsError(
  label: string,
  category: string,
  event: string,
  payload: DiagnosticsPayload = null,
): Promise<boolean> {
  return logDiagnosticsEvent({
    level: 'error',
    label,
    category,
    event,
    payload,
  })
}

export async function getDiagnosticsLogStatus(): Promise<DiagnosticsLogStatus | null> {
  if (!isTauri()) {
    return null
  }

  if (!diagnosticsLogStatusPromise) {
    diagnosticsLogStatusPromise = invoke<unknown>('diagnostics_log_status')
      .then((status) => {
        const record = asRecord(status)
        return typeof record.path === 'string'
          ? {
              path: record.path,
              directory: typeof record.directory === 'string' ? record.directory : '',
              directoryKind:
                typeof record.directoryKind === 'string' ? record.directoryKind : 'unknown',
              fallbackReason:
                typeof record.fallbackReason === 'string' && record.fallbackReason
                  ? record.fallbackReason
                  : null,
            }
          : null
      })
      .catch((error) => {
        const normalizedError = normalizeDiagnosticsPayload(error)
        const logger = typeof console?.warn === 'function' ? console.warn : console.log
        logger('Failed to resolve OFPlayer diagnostics log status.', normalizedError)
        return null
      })
  }

  return diagnosticsLogStatusPromise
}
