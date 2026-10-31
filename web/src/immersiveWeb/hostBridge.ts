// Shared WebView2 bridge for the immersive and lyric capsule pages.
// C# owns the command allowlist; both pages use request/response by id and
// host-pushed { event, data } frames.
import { activeWebText } from '../services/webLocale.ts'

export type HostBridgeResponse = {
  id: number
  result?: unknown
  error?: { code: string; message: string }
}

export type HostBridgeEvent = {
  event: string
  data: unknown
}

type ChromeWebView = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', handler: (event: { data: unknown }) => void) => void
}

function resolveWebView(): ChromeWebView | null {
  const chrome = (window as unknown as { chrome?: { webview?: ChromeWebView } }).chrome
  return chrome?.webview ?? null
}

let webview: ChromeWebView | null = null
let subscribed = false
let nextRequestId = 1
const pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
const listeners = new Set<(event: HostBridgeEvent) => void>()

export function initHostBridge(): boolean {
  const resolved = resolveWebView()
  if (webview !== resolved) subscribed = false
  webview = resolved

  if (!webview) {
    return false
  }
  if (subscribed) return true
  subscribed = true

  webview.addEventListener('message', (event) => {
    const payload = event.data as HostBridgeResponse | HostBridgeEvent | null

    if (!payload || typeof payload !== 'object') {
      return
    }
    if ('perfId' in payload && typeof payload.perfId === 'number') {
      const received = performance.now()
      requestAnimationFrame(() => webview?.postMessage({
        kind: 'perf.web.applied', id: payload.perfId,
        jsMs: performance.now() - received,
      }))
    }

    if ('event' in payload) {
      listeners.forEach((listener) => listener(payload as HostBridgeEvent))
      return
    }

    const response = payload as HostBridgeResponse
    const entry = pending.get(response.id)

    if (!entry) {
      return
    }

    pending.delete(response.id)

    if (response.error) {
      entry.reject(new Error(response.error.message))
    } else {
      entry.resolve(response.result)
    }
  })

  return true
}

export function isHostBridgeAvailable(): boolean {
  return webview !== null
}

export function hostRequest<T>(command: string, args: unknown = {}, timeoutMs = 8000): Promise<T> {
  if (!webview) {
    return Promise.reject(new Error(activeWebText('bridgeUnavailable')))
  }

  const id = nextRequestId++
  const activeWebView = webview

  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id)
      reject(new Error(activeWebText('requestTimeout', { command })))
    }, timeoutMs)

    pending.set(id, {
      resolve: (value) => {
        window.clearTimeout(timer)
        resolve(value as T)
      },
      reject: (error) => {
        window.clearTimeout(timer)
        reject(error)
      },
    })

    activeWebView.postMessage({ id, command, args })
  })
}

export function onHostEvent(handler: (event: HostBridgeEvent) => void): () => void {
  listeners.add(handler)
  return () => listeners.delete(handler)
}

export function notifyHostReady(): void {
  webview?.postMessage({ event: 'web.ready', data: {} })
}
