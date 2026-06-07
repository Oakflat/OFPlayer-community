import { createApp } from 'vue'
import App from './App.vue'
import { createOFPlayerApp, installOFPlayerApp } from './app/ofplayerApp'
import { logDiagnosticsInfo, logDiagnosticsWarn } from './services/diagnosticsLogger'
import { initializeWindowSurface } from './services/windowSurface'
import './style.css'
import './styles/tokens/of-color-tokens.css'
import './styles/tokens/of-theme-mapping.css'
import './styles/tokens/of-component-tokens.css'
import './styles/tokens/of-player-tokens.css'
import './themes/mist.css'
import './themes/paper.css'
import './themes/material.css'

const STARTUP_DIAGNOSTICS_VERSION = '2026-04-18-startup-v4'
const SPLASH_HIDE_TIMEOUT_MS = 3200

function getStartupSplashElement(): HTMLElement | null {
  if (typeof document === 'undefined') {
    return null
  }

  return document.getElementById('startup-splash')
}

function waitForAnimationFrames(frameCount = 1): Promise<void> {
  if (typeof window === 'undefined' || typeof window.requestAnimationFrame !== 'function') {
    return Promise.resolve()
  }

  return new Promise((resolve) => {
    let remaining = Math.max(1, frameCount)

    const step = () => {
      remaining -= 1

      if (remaining <= 0) {
        resolve()
        return
      }

      window.requestAnimationFrame(step)
    }

    window.requestAnimationFrame(step)
  })
}

async function hideStartupSplash({
  ofplayer,
  startupStartedAt,
}: {
  ofplayer: { waitForVisualReady?: () => Promise<unknown> }
  startupStartedAt: number
}): Promise<void> {
  const splashElement = getStartupSplashElement()

  if (!splashElement) {
    return
  }

  let revealReason = 'visual_ready'

  try {
    await Promise.race([
      ofplayer.waitForVisualReady?.() ?? Promise.resolve(),
      new Promise((resolve) => {
        window.setTimeout(() => {
          revealReason = 'timeout'
          resolve(undefined)
        }, SPLASH_HIDE_TIMEOUT_MS)
      }),
    ])
  } catch {
    revealReason = 'fallback'
  }

  await waitForAnimationFrames(2)
  splashElement.dataset.state = 'hidden'

  void logDiagnosticsInfo('[OFPlayer startup phase]', 'startup', 'startup_splash_hidden', {
    diagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
    revealReason,
    elapsedMs: Math.round(performance.now() - startupStartedAt),
  })

  window.setTimeout(() => {
    splashElement.remove()
  }, 360)
}

async function bootstrap(): Promise<void> {
  const startupStartedAt = performance.now()
  const windowSurfaceStartedAt = performance.now()
  const windowSurfacePromise = initializeWindowSurface().catch((error) => {
    void logDiagnosticsWarn('[OFPlayer window surface]', 'startup', 'window_surface_failed', {
      error,
    })
    return null
  }).then((result) => {
    const windowSurfaceMs = Math.round(performance.now() - windowSurfaceStartedAt)

    if (windowSurfaceMs >= 80) {
      void logDiagnosticsInfo('[OFPlayer window surface]', 'startup', 'window_surface_ready', {
        diagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
        totalMs: windowSurfaceMs,
        completed: Boolean(result !== null),
      })
    }

    return result
  })

  const app = createApp(App)
  const appBootstrapStartedAt = performance.now()
  const ofplayer = await createOFPlayerApp({
    startupDiagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
  })
  const ofplayerReadyMs = Math.round(performance.now() - appBootstrapStartedAt)
  const beforeMountMs = Math.round(performance.now() - startupStartedAt)

  if (beforeMountMs >= 80) {
    void logDiagnosticsInfo('[OFPlayer startup phase]', 'startup', 'frontend_mount_ready', {
      diagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
      beforeMountMs,
      createOFPlayerAppMs: ofplayerReadyMs,
    })
  }

  installOFPlayerApp(app, ofplayer)
  const mountStartedAt = performance.now()
  app.mount('#app')
  const mountMs = Math.round(performance.now() - mountStartedAt)
  const postMountMs = Math.round(performance.now() - startupStartedAt)
  void windowSurfacePromise
  void hideStartupSplash({
    ofplayer,
    startupStartedAt,
  })

  if (postMountMs >= 80) {
    void logDiagnosticsInfo('[OFPlayer startup phase]', 'startup', 'frontend_mount_complete', {
      diagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
      postMountMs,
      createOFPlayerAppMs: ofplayerReadyMs,
      mountMs,
    })
  }

  const preMountMs = postMountMs
  const logStartup = (firstFrameMs = preMountMs) => {
    if (Math.max(preMountMs, firstFrameMs) < 200) {
      return
    }

    void logDiagnosticsInfo('[OFPlayer startup]', 'startup', 'frontend_startup', {
      diagnosticsVersion: STARTUP_DIAGNOSTICS_VERSION,
      preMountMs,
      firstFrameMs,
      createOFPlayerAppMs: ofplayerReadyMs,
      mountMs,
    })
  }

  if (typeof window !== 'undefined') {
    window.setTimeout(() => {
      void ofplayer.startDeferredStartup?.()
    }, 0)

    window.requestAnimationFrame(() => {
      logStartup(Math.round(performance.now() - startupStartedAt))
    })
    return
  }

  void ofplayer.startDeferredStartup?.()
  logStartup()
}

bootstrap()
