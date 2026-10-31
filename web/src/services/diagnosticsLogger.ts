import { hostRequest, isHostBridgeAvailable } from '../immersiveWeb/hostBridge'

function log(level: 'info' | 'warn' | 'error', label: string, category: string,
  event: string, payload: unknown): Promise<boolean> {
  console[level](label, event, payload)
  if (!isHostBridgeAvailable()) return Promise.resolve(false)
  return hostRequest('diagnostics_log_event', {
    request: { level, category, event, payload },
  }).then(() => true).catch(() => false)
}

export const logDiagnosticsInfo = (label: string, category: string, event: string, payload: unknown) =>
  log('info', label, category, event, payload)
export const logDiagnosticsWarn = (label: string, category: string, event: string, payload: unknown) =>
  log('warn', label, category, event, payload)
export const logDiagnosticsError = (label: string, category: string, event: string, payload: unknown) =>
  log('error', label, category, event, payload)
