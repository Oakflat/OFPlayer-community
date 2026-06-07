import { invoke, isTauri } from '@tauri-apps/api/core'
import { LogicalPosition, LogicalSize } from '@tauri-apps/api/dpi'
import { primaryMonitor } from '@tauri-apps/api/window'
import { WebviewWindow } from '@tauri-apps/api/webviewWindow'
import type { WebviewWindow as LyricCapsuleWebviewWindow } from '@tauri-apps/api/webviewWindow'
import type { LyricCapsuleAttemptId } from './lyricCapsuleDiagnostics'
import { LYRIC_CAPSULE_LABEL, LYRIC_CAPSULE_ROUTE } from './lyricCapsuleBridge'
import {
  LYRIC_CAPSULE_HEIGHT,
  LYRIC_CAPSULE_TOP_MARGIN,
  LYRIC_CAPSULE_WIDTH,
} from './lyricCapsuleWindowBounds'
import {
  createLyricCapsuleAttemptId,
  elapsedMs,
  logLyricCapsuleInfo,
  logLyricCapsuleWarn,
  nowMs,
} from './lyricCapsuleDiagnostics'

export {
  LYRIC_CAPSULE_HEIGHT,
  LYRIC_CAPSULE_TOP_MARGIN,
  LYRIC_CAPSULE_WIDTH,
} from './lyricCapsuleWindowBounds'
export const LYRIC_CAPSULE_ENABLE_STORAGE_KEY = 'ofplayer:lyric-capsule-window'

export interface LyricCapsuleWindowDebugContext {
  attemptId?: LyricCapsuleAttemptId | string | null
  activeAttemptId?: LyricCapsuleAttemptId | string | null
  reason?: string
  [key: string]: unknown
}

interface ConfigureLyricCapsuleWindowOptions {
  show?: boolean
  debugContext?: LyricCapsuleWindowDebugContext
}

type TimedWindowCall = () => Promise<unknown> | unknown

const ENABLED_FLAG_VALUES = new Set(['1', 'true', 'enabled', 'on'])

function isEnabledFlagValue(value: unknown): boolean {
  return typeof value === 'string' && ENABLED_FLAG_VALUES.has(value.trim().toLowerCase())
}

export function isLyricCapsuleWindowEnabled(): boolean {
  if (!isTauri()) {
    return false
  }

  if (isEnabledFlagValue(import.meta.env.VITE_OFPLAYER_LYRIC_CAPSULE_WINDOW)) {
    return true
  }

  try {
    return isEnabledFlagValue(window.localStorage.getItem(LYRIC_CAPSULE_ENABLE_STORAGE_KEY))
  } catch {
    return false
  }
}

export function setLyricCapsuleWindowEnabled(enabled: boolean): boolean {
  if (!isTauri()) {
    return false
  }

  try {
    window.localStorage.setItem(LYRIC_CAPSULE_ENABLE_STORAGE_KEY, enabled ? '1' : '0')
    return true
  } catch {
    return false
  }
}

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, ms)
  })
}

async function waitForWindowClosed(label: string, timeoutMs = 1200): Promise<boolean> {
  const startedAt = nowMs()

  while (elapsedMs(startedAt) < timeoutMs) {
    const existingWindow = await WebviewWindow.getByLabel(label)
    if (!existingWindow) {
      return true
    }
    await wait(80)
  }

  return false
}

async function waitForWindowAvailable(label: string, timeoutMs = 1600): Promise<LyricCapsuleWebviewWindow | null> {
  const startedAt = nowMs()

  while (elapsedMs(startedAt) < timeoutMs) {
    const existingWindow = await WebviewWindow.getByLabel(label)
    if (existingWindow) {
      return existingWindow
    }
    await wait(80)
  }

  return WebviewWindow.getByLabel(label)
}

async function resolveTopCenterPosition(
  width = LYRIC_CAPSULE_WIDTH,
  context: LyricCapsuleWindowDebugContext = {},
): Promise<LogicalPosition> {
  const startedAt = nowMs()
  const monitor = await primaryMonitor()

  if (!monitor) {
    void logLyricCapsuleWarn('window_position_monitor_missing', {
      ...context,
      elapsedMs: elapsedMs(startedAt),
      width,
      fallbackX: 120,
      fallbackY: LYRIC_CAPSULE_TOP_MARGIN,
    })
    return new LogicalPosition(120, LYRIC_CAPSULE_TOP_MARGIN)
  }

  const scaleFactor = monitor.scaleFactor || 1
  const monitorX = monitor.position.x / scaleFactor
  const monitorY = monitor.position.y / scaleFactor
  const monitorWidth = monitor.size.width / scaleFactor
  const x = Math.round(monitorX + Math.max(0, (monitorWidth - width) / 2))
  const y = Math.round(monitorY + LYRIC_CAPSULE_TOP_MARGIN)

  void logLyricCapsuleInfo('window_position_resolved', {
    ...context,
    elapsedMs: elapsedMs(startedAt),
    scaleFactor,
    monitorX,
    monitorY,
    monitorWidth,
    x,
    y,
  })

  return new LogicalPosition(x, y)
}

async function tryTimedWindowCall(
  step: string,
  callback: TimedWindowCall,
  context: LyricCapsuleWindowDebugContext = {},
): Promise<boolean> {
  const startedAt = nowMs()

  try {
    await callback()
    void logLyricCapsuleInfo('window_configure_step', {
      ...context,
      step,
      ok: true,
      elapsedMs: elapsedMs(startedAt),
    })
    return true
  } catch (error) {
    void logLyricCapsuleWarn('window_configure_step_failed', {
      ...context,
      step,
      ok: false,
      elapsedMs: elapsedMs(startedAt),
      error,
    })
    return false
  }
}

async function configureLyricCapsuleWindow(
  webviewWindow: LyricCapsuleWebviewWindow,
  { show = true, debugContext = {} }: ConfigureLyricCapsuleWindowOptions = {},
): Promise<LyricCapsuleWebviewWindow> {
  const startedAt = nowMs()
  const context = {
    label: LYRIC_CAPSULE_LABEL,
    show,
    ...debugContext,
  }

  void logLyricCapsuleInfo('window_configure_start', context)

  const position = await resolveTopCenterPosition(LYRIC_CAPSULE_WIDTH, context)

  await tryTimedWindowCall('setPosition', () => webviewWindow.setPosition(position), {
    ...context,
    x: position.x,
    y: position.y,
  })
  await tryTimedWindowCall('setSize', () => webviewWindow.setSize(new LogicalSize(LYRIC_CAPSULE_WIDTH, LYRIC_CAPSULE_HEIGHT)), {
    ...context,
    width: LYRIC_CAPSULE_WIDTH,
    height: LYRIC_CAPSULE_HEIGHT,
  })
  await tryTimedWindowCall('setAlwaysOnTop', () => webviewWindow.setAlwaysOnTop(true), context)
  await tryTimedWindowCall('setFocusable', () => webviewWindow.setFocusable(false), context)
  await tryTimedWindowCall('setIgnoreCursorEvents', () => webviewWindow.setIgnoreCursorEvents(false), context)

  if (show) {
    await tryTimedWindowCall('show', () => webviewWindow.show(), context)
  }

  void logLyricCapsuleInfo('window_configure_complete', {
    ...context,
    totalMs: elapsedMs(startedAt),
  })

  return webviewWindow
}

export async function closeLyricCapsuleWindow(
  debugContext: LyricCapsuleWindowDebugContext = {},
): Promise<boolean> {
  if (!isTauri()) {
    return false
  }

  const startedAt = nowMs()
  const context = {
    label: LYRIC_CAPSULE_LABEL,
    ...debugContext,
  }

  void logLyricCapsuleInfo('window_close_start', context)

  const existingWindow = await WebviewWindow.getByLabel(LYRIC_CAPSULE_LABEL)

  if (!existingWindow) {
    void logLyricCapsuleInfo('window_close_missing', {
      ...context,
      totalMs: elapsedMs(startedAt),
    })
    return true
  }

  const closed = await tryTimedWindowCall('close', () => existingWindow.close(), context)

  void logLyricCapsuleInfo('window_close_complete', {
    ...context,
    ok: closed,
    totalMs: elapsedMs(startedAt),
  })

  return closed
}

export async function createLyricCapsuleWindow(
  debugContext: LyricCapsuleWindowDebugContext = {},
): Promise<LyricCapsuleWebviewWindow | null> {
  if (!isTauri()) {
    return null
  }

  const startedAt = nowMs()
  const context = {
    attemptId: debugContext.attemptId ?? createLyricCapsuleAttemptId('service-open'),
    label: LYRIC_CAPSULE_LABEL,
    route: LYRIC_CAPSULE_ROUTE,
    width: LYRIC_CAPSULE_WIDTH,
    height: LYRIC_CAPSULE_HEIGHT,
    ...debugContext,
  }

  void logLyricCapsuleInfo('window_open_start', context)

  const lookupStartedAt = nowMs()
  const existingWindow = await WebviewWindow.getByLabel(LYRIC_CAPSULE_LABEL)
  void logLyricCapsuleInfo('window_lookup_complete', {
    ...context,
    elapsedMs: elapsedMs(lookupStartedAt),
    foundExisting: Boolean(existingWindow),
  })

  if (existingWindow) {
    const closeStartedAt = nowMs()
    const closed = await tryTimedWindowCall('closeExistingForResize', () => existingWindow.close(), {
      ...context,
      reusedExisting: true,
    })

    void logLyricCapsuleInfo('window_recreate_existing_complete', {
      ...context,
      ok: closed,
      elapsedMs: elapsedMs(closeStartedAt),
      reason: 'ensure-interactive-capsule-size',
    })

    if (!closed) {
      const configuredWindow = await configureLyricCapsuleWindow(existingWindow, {
        debugContext: {
          ...context,
          reusedExisting: true,
          resizeFallback: true,
        },
      })
      void logLyricCapsuleInfo('window_open_complete', {
        ...context,
        reusedExisting: true,
        resizeFallback: true,
        totalMs: elapsedMs(startedAt),
      })
      return configuredWindow
    }

    const closedBeforeRecreate = await waitForWindowClosed(LYRIC_CAPSULE_LABEL)
    void logLyricCapsuleInfo('window_recreate_wait_complete', {
      ...context,
      closedBeforeRecreate,
    })
  }

  const constructorStartedAt = nowMs()
  await invoke('capsule_create_window')
  const webviewWindow = await waitForWindowAvailable(LYRIC_CAPSULE_LABEL)

  if (!webviewWindow) {
    throw new Error('Failed to attach to the lyric capsule window after native creation.')
  }

  void logLyricCapsuleInfo('window_native_create_complete', {
    ...context,
    elapsedMs: elapsedMs(constructorStartedAt),
  })

  const configuredWindow = await configureLyricCapsuleWindow(webviewWindow, {
    debugContext: {
      ...context,
      reusedExisting: false,
    },
  })

  void logLyricCapsuleInfo('window_open_complete', {
    ...context,
    reusedExisting: false,
    totalMs: elapsedMs(startedAt),
  })

  return configuredWindow
}
