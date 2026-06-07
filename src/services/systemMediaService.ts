import { isTauri } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'
import type { UnlistenFn } from '@tauri-apps/api/event'

const SYSTEM_MEDIA_CONTROL_EVENT = 'playback://system-media-control'

interface SystemMediaPayload {
  action?: unknown
  seconds?: unknown
}

interface NormalizedSystemMediaPayload {
  action: string
  seconds: number | null
}

type SystemMediaListener = (payload: NormalizedSystemMediaPayload) => void

function clampSeconds(value: unknown): number | null {
  const numeric = Number(value)
  return Number.isFinite(numeric) ? numeric : null
}

function normalizeAction(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function normalizePayload(payload: SystemMediaPayload = {}): NormalizedSystemMediaPayload {
  return {
    action: normalizeAction(payload?.action),
    seconds: clampSeconds(payload?.seconds),
  }
}

export function createSystemMediaService() {
  if (!isTauri()) {
    throw new Error('OFPlayer system media integration requires the Tauri runtime.')
  }

  const available = true

  return {
    available,
    async listen(listener: SystemMediaListener): Promise<UnlistenFn> {
      if (typeof listener !== 'function') {
        return () => {}
      }

      return listen<unknown>(SYSTEM_MEDIA_CONTROL_EVENT, (event) => {
        listener(normalizePayload(event.payload as SystemMediaPayload))
      })
    },
  }
}
