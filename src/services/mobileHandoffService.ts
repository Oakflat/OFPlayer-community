import { invoke, isTauri } from '@tauri-apps/api/core'
import { track, type TelemetryData, type TelemetryValue } from './telemetryService'

export type MobileHandoffEventName =
  | 'handoff.discovery_started'
  | 'handoff.discovery_succeeded'
  | 'handoff.discovery_failed'
  | 'handoff.pairing_started'
  | 'handoff.pairing_succeeded'
  | 'handoff.pairing_failed'
  | 'handoff.offer_created'
  | 'handoff.offer_accepted'
  | 'handoff.offer_rejected'
  | 'handoff.resume_started'
  | 'handoff.resume_succeeded'
  | 'handoff.resume_failed'
  | 'stream.transfer_started'
  | 'stream.transfer_first_byte'
  | 'stream.transfer_ready'
  | 'stream.transfer_failed'
  | 'stream.playback_first_audio'
  | 'stream.playback_stall'
  | 'stream.playback_seek_failed'

export interface MobileHandoffCapabilities {
  protocolVersion: string
  eventSchemaVersion: string
  implementationStage: string
  backendOwned: boolean
  supportedDevicePlatforms: string[]
  discoveryMethods: string[]
  controlTransports: string[]
  mediaTransports: string[]
  canRecordEvents: boolean
  canPairDevices: boolean
  canResumePlayback: boolean
  canTransferMedia: boolean
  requiresTelemetryConsent: boolean
}

export interface MobileHandoffEventRequest {
  event: MobileHandoffEventName
  platform?: 'android' | 'harmonyos' | string
  direction?: 'phone-to-desktop' | 'desktop-to-phone' | string
  transport?: 'mdns' | 'qr' | 'websocket-json' | 'https-json' | 'http-file' | 'http-range' | string
  phase?: 'discovery' | 'pairing' | 'offer' | 'resume' | 'transfer' | 'playback' | string
  outcome?: 'started' | 'succeeded' | 'failed' | 'cancelled' | 'rejected' | 'timed-out' | string
  durationMs?: number
  errorCode?: string
  sessionId?: string
  deviceId?: string
  trackMatched?: boolean
  queueSize?: number
  positionDriftMs?: number
}

export interface MobileHandoffEventAttributes {
  platform?: string | null
  direction?: string | null
  transport?: string | null
  phase?: string | null
  outcome?: string | null
  durationMs?: number | null
  errorCode?: string | null
  sessionKey?: string | null
  deviceKey?: string | null
  trackMatched?: boolean | null
  queueSize?: number | null
  positionDriftMs?: number | null
}

export interface MobileHandoffEventRecord {
  id: string
  createdAt: string
  event: MobileHandoffEventName
  schemaVersion: string
  attributes: MobileHandoffEventAttributes
}

export interface MobileHandoffStateSnapshot {
  protocolVersion: string
  eventSchemaVersion: string
  implementationStage: string
  startedAt: string
  lastEventAt: string | null
  recentEventCount: number
  recentEvents: MobileHandoffEventRecord[]
}

export interface RecordMobileHandoffEventOptions {
  emitTelemetry?: boolean
}

function toTelemetryData(record: MobileHandoffEventRecord): TelemetryData {
  const data: TelemetryData = {
    schema: record.schemaVersion,
  }

  for (const [key, value] of Object.entries(record.attributes ?? {})) {
    if (
      typeof value === 'string' ||
      typeof value === 'number' ||
      typeof value === 'boolean'
    ) {
      data[key] = value as TelemetryValue
    }
  }

  return data
}

export async function getMobileHandoffCapabilities(): Promise<MobileHandoffCapabilities | null> {
  if (!isTauri()) {
    return null
  }

  return invoke<MobileHandoffCapabilities>('mobile_handoff_capabilities')
}

export async function getMobileHandoffStateSnapshot(): Promise<MobileHandoffStateSnapshot | null> {
  if (!isTauri()) {
    return null
  }

  return invoke<MobileHandoffStateSnapshot>('mobile_handoff_state_snapshot')
}

export async function recordMobileHandoffEvent(
  request: MobileHandoffEventRequest,
  { emitTelemetry = true }: RecordMobileHandoffEventOptions = {},
): Promise<MobileHandoffEventRecord | null> {
  if (!isTauri()) {
    return null
  }

  const record = await invoke<MobileHandoffEventRecord>('mobile_handoff_record_event', {
    request,
  })

  if (emitTelemetry) {
    track(record.event, toTelemetryData(record))
  }

  return record
}
