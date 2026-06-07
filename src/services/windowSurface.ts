import { invoke, isTauri } from '@tauri-apps/api/core'
import { Effect, getCurrentWindow, type Color } from '@tauri-apps/api/window'
import { DEFAULT_THEME, DEFAULT_WINDOW_EFFECTS } from '../models/preferences'
import { logDiagnosticsInfo } from './diagnosticsLogger'

type WindowSurfaceColor = Color
type WindowSurfaceEffect = Effect
type EffectiveColorScheme = 'light' | 'dark'
type WindowEffectsPreference = 'auto' | 'full' | 'balanced' | 'off' | 'web'
type EffectiveWindowEffectsTier = 'full' | 'balanced' | 'off'
type WindowSurfaceMode = 'native-glass' | 'solid' | 'web'
type WindowSurfaceState = 'preparing' | 'ready' | 'fallback'
type WindowSurfaceGuard = 'opaque' | ''
type WindowShellMode = 'native' | 'web'

interface NativeSurfaceProfile {
  platform: string
  majorVersion: number | null
  minorVersion: number | null
  buildNumber: number | null
  isWindows: boolean
  isWindows10: boolean
  isWindows11OrNewer: boolean
}

interface WindowSurfacePreset {
  lightBackgroundColor: WindowSurfaceColor
  lightGlassBackgroundColor: WindowSurfaceColor
  darkBackgroundColor: WindowSurfaceColor
  effect?: WindowSurfaceEffect
  lightEffect?: WindowSurfaceEffect
  darkEffect?: WindowSurfaceEffect
  lightFallbackEffect?: WindowSurfaceEffect | null
  darkFallbackEffect?: WindowSurfaceEffect | null
}

interface ResolvedWindowSurfacePreset {
  backgroundColor: WindowSurfaceColor
  effect: WindowSurfaceEffect | null
  fallbackEffect: WindowSurfaceEffect | null
}

interface ApplyDocumentWindowSurfaceRequest {
  theme: string
  effectiveColorScheme: EffectiveColorScheme
  windowEffectsPreference: WindowEffectsPreference
  effectiveWindowEffectsTier: EffectiveWindowEffectsTier
  windowSurfaceMode?: WindowSurfaceMode | null
  windowSurfaceState?: WindowSurfaceState
  nativeSurfaceProfile?: NativeSurfaceProfile | null
}

interface NativeWindowSurfaceRequest {
  theme: string
  effectiveColorScheme: EffectiveColorScheme
  windowEffectsPreference: WindowEffectsPreference
  effectiveWindowEffectsTier: EffectiveWindowEffectsTier
  nativeSurfaceProfile: NativeSurfaceProfile | null
}

interface WindowSurfaceDiagnostics {
  requestedMode: WindowSurfaceMode
  mode: WindowSurfaceMode
  surfaceState: WindowSurfaceState
  theme: string
  effectiveColorScheme: EffectiveColorScheme
  windowEffectsPreference: WindowEffectsPreference
  effectiveWindowEffects: EffectiveWindowEffectsTier
  nativeSurfaceProfile: NativeSurfaceProfile | null
  nativeSurfaceGuard: WindowSurfaceGuard
  backgroundColor: WindowSurfaceColor
  effect: WindowSurfaceEffect | null
  fallbackEffect: WindowSurfaceEffect | null
  setThemeMs: number
  setThemeApplied: boolean
  setBackgroundColorMs: number
  setBackgroundColorApplied: boolean
  clearEffectsMs: number
  clearEffectsApplied: boolean
  setEffectsMs: number
  setEffectsApplied: boolean
  fallbackEffectsMs: number
  fallbackEffectsApplied: boolean
  appliedEffect: WindowSurfaceEffect | null
  totalMs?: number
  fallbackReason?: 'effect-unavailable' | 'fallback-effect-unavailable'
}

interface WebWindowSurfaceDiagnostics {
  mode: 'web'
  theme: string
  effectiveColorScheme: EffectiveColorScheme
  windowEffectsPreference: WindowEffectsPreference
  effectiveWindowEffects: EffectiveWindowEffectsTier
  totalMs: number
}

type WindowSurfaceResult = WindowSurfaceDiagnostics | WebWindowSurfaceDiagnostics

const logWindowSurfaceDiagnostics = logDiagnosticsInfo as (
  label: string,
  category: string,
  event: string,
  payload: unknown,
) => Promise<boolean>
const WINDOW_SURFACE_MODE = isTauri() ? 'native-glass' : 'web'
const WINDOW_SURFACE_STEP_LOG_THRESHOLD_MS = 16
const WINDOWS_11_BUILD_NUMBER = 22000
const WINDOWS_10_GLASS_BACKGROUND_ALPHA = 218
const STARTUP_THEME_KEY = 'ofplayer.startup.theme'
const STARTUP_WINDOW_EFFECTS_KEY = 'ofplayer.startup.window-effects'
const WINDOW_EFFECTS_PREFERENCE_SET: ReadonlySet<WindowEffectsPreference> = new Set(['auto', 'full', 'balanced', 'off', 'web'])
const WINDOW_EFFECTS_EFFECTIVE_SET: ReadonlySet<EffectiveWindowEffectsTier> = new Set(['full', 'balanced', 'off'])

const WINDOW_SURFACE_PRESETS: Readonly<Record<string, WindowSurfacePreset>> = Object.freeze({
  mist: {
    lightBackgroundColor: '#F4F6F8',
    lightGlassBackgroundColor: [244, 246, 248, 36],
    darkBackgroundColor: '#070B10',
    effect: Effect.Mica,
    lightEffect: Effect.TabbedLight,
    lightFallbackEffect: Effect.TabbedLight,
    darkFallbackEffect: Effect.TabbedDark,
  },
  paper: {
    lightBackgroundColor: '#F5F1EB',
    lightGlassBackgroundColor: [245, 241, 235, 36],
    darkBackgroundColor: '#070B10',
    effect: Effect.Mica,
    lightEffect: Effect.TabbedLight,
    lightFallbackEffect: Effect.TabbedLight,
    darkFallbackEffect: Effect.TabbedDark,
  },
  material: {
    lightBackgroundColor: '#EADDFF',
    lightGlassBackgroundColor: [234, 221, 255, 36],
    darkBackgroundColor: '#070B10',
    lightEffect: Effect.TabbedLight,
    darkEffect: Effect.Mica,
    lightFallbackEffect: null,
    darkFallbackEffect: Effect.TabbedDark,
  },
})

const GLOBAL_DARK_WINDOW_SURFACE = Object.freeze({
  backgroundColor: '#12171D',
  glassBackgroundColor: [18, 23, 29, 34] as [number, number, number, number],
  effect: Effect.Acrylic,
  balancedEffect: Effect.Mica,
  fallbackEffect: Effect.Mica,
  balancedFallbackEffect: Effect.TabbedDark,
}) satisfies Readonly<{
  backgroundColor: WindowSurfaceColor
  glassBackgroundColor: WindowSurfaceColor
  effect: WindowSurfaceEffect
  balancedEffect: WindowSurfaceEffect
  fallbackEffect: WindowSurfaceEffect
  balancedFallbackEffect: WindowSurfaceEffect
}>

let lastAppliedSignature: string | null = null
let lastAppliedDiagnostics: WindowSurfaceDiagnostics | null = null
let nativeSurfaceProfilePromise: Promise<NativeSurfaceProfile | null> | null = null

function nowMs(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function getDocumentElement(): HTMLElement | null {
  if (typeof document === 'undefined') {
    return null
  }

  return document.documentElement
}

function readLocalStorageValue(key: string): string | null {
  if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
    return null
  }

  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function normalizeWindowEffectsPreference(value: unknown): WindowEffectsPreference {
  return typeof value === 'string' && WINDOW_EFFECTS_PREFERENCE_SET.has(value as WindowEffectsPreference)
    ? value as WindowEffectsPreference
    : DEFAULT_WINDOW_EFFECTS as WindowEffectsPreference
}

function normalizeWindowEffectsTier(value: unknown): EffectiveWindowEffectsTier {
  return typeof value === 'string' && WINDOW_EFFECTS_EFFECTIVE_SET.has(value as EffectiveWindowEffectsTier)
    ? value as EffectiveWindowEffectsTier
    : 'full'
}

function normalizeNativeSurfaceProfile(profile: unknown = null): NativeSurfaceProfile | null {
  if (!profile || typeof profile !== 'object') {
    return null
  }

  const candidate = profile as Record<string, unknown>
  const platform = typeof candidate.platform === 'string' ? candidate.platform : ''
  const buildNumber = typeof candidate.buildNumber === 'number' && Number.isFinite(candidate.buildNumber) ? candidate.buildNumber : null
  const majorVersion = typeof candidate.majorVersion === 'number' && Number.isFinite(candidate.majorVersion) ? candidate.majorVersion : null

  return {
    platform,
    majorVersion,
    minorVersion:
      typeof candidate.minorVersion === 'number' && Number.isFinite(candidate.minorVersion)
        ? candidate.minorVersion
        : null,
    buildNumber,
    isWindows: candidate.isWindows === true || platform === 'windows',
    isWindows10:
      candidate.isWindows10 === true ||
      (platform === 'windows' &&
        majorVersion === 10 &&
        buildNumber !== null &&
        buildNumber < WINDOWS_11_BUILD_NUMBER),
    isWindows11OrNewer:
      candidate.isWindows11OrNewer === true ||
      (platform === 'windows' && buildNumber !== null && buildNumber >= WINDOWS_11_BUILD_NUMBER),
  }
}

function resolveNativeSurfaceProfile(): Promise<NativeSurfaceProfile | null> {
  if (WINDOW_SURFACE_MODE !== 'native-glass') {
    return Promise.resolve(null)
  }

  if (!nativeSurfaceProfilePromise) {
    nativeSurfaceProfilePromise = invoke<unknown>('window_surface_platform_profile')
      .then((profile) => normalizeNativeSurfaceProfile(profile))
      .catch(() => null)
  }

  return nativeSurfaceProfilePromise
}

function isWindows10SurfaceProfile(nativeSurfaceProfile: NativeSurfaceProfile | null = null): boolean {
  return nativeSurfaceProfile?.isWindows10 === true
}

function withWindowSurfaceAlpha(
  backgroundColor: WindowSurfaceColor,
  alpha = WINDOWS_10_GLASS_BACKGROUND_ALPHA,
): WindowSurfaceColor {
  if (Array.isArray(backgroundColor)) {
    return [backgroundColor[0] ?? 0, backgroundColor[1] ?? 0, backgroundColor[2] ?? 0, alpha]
  }

  return backgroundColor
}

function resolveWindowSurfaceGuard(nativeSurfaceProfile: NativeSurfaceProfile | null = null): WindowSurfaceGuard {
  return isWindows10SurfaceProfile(nativeSurfaceProfile) ? 'opaque' : ''
}

function resolveWindowSurfaceGuardBackground(
  theme: string = DEFAULT_THEME,
  effectiveColorScheme: EffectiveColorScheme = 'light',
): string {
  const preset =
    WINDOW_SURFACE_PRESETS[theme] ??
    WINDOW_SURFACE_PRESETS[DEFAULT_THEME]

  if (effectiveColorScheme === 'dark') {
    return 'rgba(18, 23, 29, 0.9)'
  }

  const glassColor = Array.isArray(preset.lightGlassBackgroundColor)
    ? preset.lightGlassBackgroundColor
    : [247, 248, 251]
  return `rgba(${glassColor[0] ?? 247}, ${glassColor[1] ?? 248}, ${glassColor[2] ?? 251}, 0.9)`
}

function resolveStartupTheme(explicitTheme: string | null = null): string {
  if (typeof explicitTheme === 'string' && explicitTheme.trim()) {
    return explicitTheme.trim()
  }

  const documentTheme = getDocumentElement()?.dataset.theme

  if (typeof documentTheme === 'string' && documentTheme.trim()) {
    return documentTheme.trim()
  }

  return readLocalStorageValue(STARTUP_THEME_KEY) ?? DEFAULT_THEME
}

function resolveEffectiveColorScheme(explicitColorScheme: string | null = null): EffectiveColorScheme {
  if (explicitColorScheme === 'dark' || explicitColorScheme === 'light') {
    return explicitColorScheme
  }

  const documentScheme =
    getDocumentElement()?.dataset.effectiveColorScheme || getDocumentElement()?.dataset.startupColorScheme

  if (documentScheme === 'dark' || documentScheme === 'light') {
    return documentScheme
  }

  return 'light'
}

function resolveWindowEffectsPreference(explicitPreference: unknown = null): WindowEffectsPreference {
  if (explicitPreference !== null) {
    return normalizeWindowEffectsPreference(explicitPreference)
  }

  return normalizeWindowEffectsPreference(readLocalStorageValue(STARTUP_WINDOW_EFFECTS_KEY))
}

function resolveEffectiveWindowEffectsTier(preference: WindowEffectsPreference): EffectiveWindowEffectsTier {
  if (preference === 'auto') {
    return 'full'
  }

  if (preference === 'web') {
    return 'full'
  }

  return normalizeWindowEffectsTier(preference)
}

function resolveWindowSurfaceMode(effectiveTier: EffectiveWindowEffectsTier): WindowSurfaceMode {
  if (WINDOW_SURFACE_MODE !== 'native-glass') {
    return 'web'
  }

  return effectiveTier === 'off' ? 'solid' : 'native-glass'
}

function resolveWindowShellMode(windowEffectsPreference: WindowEffectsPreference): WindowShellMode {
  return windowEffectsPreference === 'web' ? 'web' : 'native'
}

function applyDocumentWindowSurface({
  theme,
  effectiveColorScheme,
  windowEffectsPreference,
  effectiveWindowEffectsTier,
  windowSurfaceMode = null,
  windowSurfaceState = 'ready',
  nativeSurfaceProfile = null,
}: ApplyDocumentWindowSurfaceRequest): void {
  const documentElement = getDocumentElement()

  if (!documentElement) {
    return
  }

  documentElement.dataset.theme = theme
  documentElement.dataset.effectiveColorScheme = effectiveColorScheme
  documentElement.dataset.windowSurface = windowSurfaceMode ?? resolveWindowSurfaceMode(effectiveWindowEffectsTier)
  documentElement.dataset.windowShell = resolveWindowShellMode(windowEffectsPreference)
  documentElement.dataset.windowSurfaceState = windowSurfaceState
  documentElement.dataset.windowEffects = effectiveWindowEffectsTier
  documentElement.dataset.windowEffectsPreference = windowEffectsPreference
  const surfaceGuard = resolveWindowSurfaceGuard(nativeSurfaceProfile)

  if (surfaceGuard) {
    documentElement.dataset.windowSurfaceGuard = surfaceGuard
    documentElement.style.setProperty(
      '--window-surface-guard-bg',
      resolveWindowSurfaceGuardBackground(theme, effectiveColorScheme),
    )
  } else {
    delete documentElement.dataset.windowSurfaceGuard
    documentElement.style.removeProperty('--window-surface-guard-bg')
  }
}

function resolveWindowSurfacePreset(
  theme: string = DEFAULT_THEME,
  effectiveColorScheme: EffectiveColorScheme = 'light',
  effectiveWindowEffectsTier: EffectiveWindowEffectsTier = 'full',
  nativeSurfaceProfile: NativeSurfaceProfile | null = null,
): ResolvedWindowSurfacePreset {
  const preset =
    WINDOW_SURFACE_PRESETS[theme] ??
    WINDOW_SURFACE_PRESETS[DEFAULT_THEME]
  const isDark = effectiveColorScheme === 'dark'

  if (effectiveWindowEffectsTier === 'off') {
    return {
      backgroundColor: isDark
        ? GLOBAL_DARK_WINDOW_SURFACE.backgroundColor
        : preset.lightBackgroundColor,
      effect: null,
      fallbackEffect: null,
    }
  }

  if (isWindows10SurfaceProfile(nativeSurfaceProfile)) {
    return {
      backgroundColor: withWindowSurfaceAlpha(
        isDark
          ? GLOBAL_DARK_WINDOW_SURFACE.glassBackgroundColor
          : preset.lightGlassBackgroundColor ?? preset.lightBackgroundColor,
      ),
      effect: Effect.Acrylic,
      fallbackEffect: null,
    }
  }

  if (isDark) {
    return {
      backgroundColor: GLOBAL_DARK_WINDOW_SURFACE.glassBackgroundColor,
      effect:
        effectiveWindowEffectsTier === 'balanced'
          ? GLOBAL_DARK_WINDOW_SURFACE.balancedEffect
          : GLOBAL_DARK_WINDOW_SURFACE.effect,
      fallbackEffect:
        effectiveWindowEffectsTier === 'balanced'
          ? GLOBAL_DARK_WINDOW_SURFACE.balancedFallbackEffect
          : GLOBAL_DARK_WINDOW_SURFACE.fallbackEffect,
    }
  }

  return {
    backgroundColor: preset.lightGlassBackgroundColor ?? preset.lightBackgroundColor,
    effect:
      effectiveWindowEffectsTier === 'balanced'
        ? preset.lightFallbackEffect ?? Effect.TabbedLight
        : (preset.lightEffect ?? preset.effect)!,
    fallbackEffect:
      effectiveWindowEffectsTier === 'balanced'
        ? null
        : preset.lightFallbackEffect ?? null,
  }
}

function buildWindowSurfaceSignature({
  theme,
  effectiveColorScheme,
  windowEffectsPreference,
  effectiveWindowEffectsTier,
  nativeSurfaceProfile = null,
}: NativeWindowSurfaceRequest): string {
  return JSON.stringify({
    theme,
    effectiveColorScheme,
    windowEffectsPreference,
    effectiveWindowEffectsTier,
    mode: resolveWindowSurfaceMode(effectiveWindowEffectsTier),
    platform: nativeSurfaceProfile?.platform ?? '',
    buildNumber: nativeSurfaceProfile?.buildNumber ?? null,
  })
}

async function applyResolvedNativeWindowSurface({
  theme = DEFAULT_THEME,
  effectiveColorScheme = 'light',
  windowEffectsPreference = DEFAULT_WINDOW_EFFECTS,
  effectiveWindowEffectsTier = 'full',
  nativeSurfaceProfile = null,
}: NativeWindowSurfaceRequest): Promise<WindowSurfaceDiagnostics> {
  const signature = buildWindowSurfaceSignature({
    theme,
    effectiveColorScheme,
    windowEffectsPreference,
    effectiveWindowEffectsTier,
    nativeSurfaceProfile,
  })
  const requestedMode = resolveWindowSurfaceMode(effectiveWindowEffectsTier)

  applyDocumentWindowSurface({
    theme,
    effectiveColorScheme,
    windowEffectsPreference,
    effectiveWindowEffectsTier,
    windowSurfaceMode: requestedMode === 'native-glass' ? 'solid' : requestedMode,
    windowSurfaceState: requestedMode === 'native-glass' ? 'preparing' : 'ready',
    nativeSurfaceProfile,
  })

  if (signature === lastAppliedSignature) {
    applyDocumentWindowSurface({
      theme,
      effectiveColorScheme,
      windowEffectsPreference,
      effectiveWindowEffectsTier,
      windowSurfaceMode: lastAppliedDiagnostics?.mode ?? requestedMode,
      windowSurfaceState: lastAppliedDiagnostics?.surfaceState ?? 'ready',
      nativeSurfaceProfile:
        lastAppliedDiagnostics?.mode === 'native-glass' ? nativeSurfaceProfile : null,
    })
    return lastAppliedDiagnostics as WindowSurfaceDiagnostics
  }

  const resolvedPreset = resolveWindowSurfacePreset(
    theme,
    effectiveColorScheme,
    effectiveWindowEffectsTier,
    nativeSurfaceProfile,
  )
  const appWindow = getCurrentWindow()
  const startedAt = nowMs()
  const diagnostics: WindowSurfaceDiagnostics = {
    requestedMode,
    mode: requestedMode,
    surfaceState: requestedMode === 'native-glass' ? 'preparing' : 'ready',
    theme,
    effectiveColorScheme,
    windowEffectsPreference,
    effectiveWindowEffects: effectiveWindowEffectsTier,
    nativeSurfaceProfile,
    nativeSurfaceGuard: resolveWindowSurfaceGuard(nativeSurfaceProfile),
    backgroundColor: resolvedPreset.backgroundColor,
    effect: resolvedPreset.effect ?? null,
    fallbackEffect: resolvedPreset.fallbackEffect ?? null,
    setThemeMs: 0,
    setThemeApplied: false,
    setBackgroundColorMs: 0,
    setBackgroundColorApplied: false,
    clearEffectsMs: 0,
    clearEffectsApplied: false,
    setEffectsMs: 0,
    setEffectsApplied: false,
    fallbackEffectsMs: 0,
    fallbackEffectsApplied: false,
    appliedEffect: null,
  }
  const finalizeDocumentSurface = (mode: WindowSurfaceMode, surfaceState: WindowSurfaceState = 'ready'): void => {
    diagnostics.mode = mode
    diagnostics.surfaceState = surfaceState
    applyDocumentWindowSurface({
      theme,
      effectiveColorScheme,
      windowEffectsPreference,
      effectiveWindowEffectsTier,
      windowSurfaceMode: mode,
      windowSurfaceState: surfaceState,
      nativeSurfaceProfile: mode === 'native-glass' ? nativeSurfaceProfile : null,
    })
  }

  const setThemeStartedAt = nowMs()
  try {
    await appWindow.setTheme(effectiveColorScheme)
    diagnostics.setThemeApplied = true
  } catch {
    // Ignore platform or permission mismatches and keep the window usable.
  }
  diagnostics.setThemeMs = Math.round(nowMs() - setThemeStartedAt)

  const setBackgroundStartedAt = nowMs()
  try {
    await appWindow.setBackgroundColor(resolvedPreset.backgroundColor)
    diagnostics.setBackgroundColorApplied = true
  } catch {
    // Background color is only a supporting tint for the native surface.
  }
  diagnostics.setBackgroundColorMs = Math.round(nowMs() - setBackgroundStartedAt)

  if (!resolvedPreset.effect) {
    const clearEffectsStartedAt = nowMs()
    try {
      await appWindow.clearEffects()
      diagnostics.clearEffectsApplied = true
    } catch {
      // If clearing effects is unavailable on this platform, keep the UI usable.
    }
    diagnostics.clearEffectsMs = Math.round(nowMs() - clearEffectsStartedAt)
    diagnostics.totalMs = Math.round(nowMs() - startedAt)
    finalizeDocumentSurface('solid')

    if (diagnostics.totalMs >= WINDOW_SURFACE_STEP_LOG_THRESHOLD_MS) {
      void logWindowSurfaceDiagnostics('[OFPlayer window surface]', 'startup', 'window_surface_apply', diagnostics)
    }

    lastAppliedSignature = signature
    lastAppliedDiagnostics = diagnostics
    return diagnostics
  }

  const setEffectsStartedAt = nowMs()
  try {
    await appWindow.setEffects({
      effects: [resolvedPreset.effect],
    })
    diagnostics.setEffectsApplied = true
    diagnostics.appliedEffect = resolvedPreset.effect ?? null
    finalizeDocumentSurface('native-glass')
  } catch {
    diagnostics.setEffectsMs = Math.round(nowMs() - setEffectsStartedAt)

    if (!resolvedPreset.fallbackEffect) {
      diagnostics.totalMs = Math.round(nowMs() - startedAt)
      diagnostics.fallbackReason = 'effect-unavailable'
      finalizeDocumentSurface('solid', 'fallback')

      if (diagnostics.totalMs >= WINDOW_SURFACE_STEP_LOG_THRESHOLD_MS) {
        void logWindowSurfaceDiagnostics('[OFPlayer window surface]', 'startup', 'window_surface_apply', diagnostics)
      }

      lastAppliedSignature = signature
      lastAppliedDiagnostics = diagnostics
      return diagnostics
    }

    const fallbackEffectsStartedAt = nowMs()
    try {
      await appWindow.setEffects({
        effects: [resolvedPreset.fallbackEffect],
      })
      diagnostics.fallbackEffectsApplied = true
      diagnostics.appliedEffect = resolvedPreset.fallbackEffect
      finalizeDocumentSurface('native-glass')
    } catch {
      // Some Windows builds can reject individual effect types; keep the app running.
      diagnostics.fallbackReason = 'fallback-effect-unavailable'
      finalizeDocumentSurface('solid', 'fallback')
    }
    diagnostics.fallbackEffectsMs = Math.round(nowMs() - fallbackEffectsStartedAt)
  }

  diagnostics.setEffectsMs = Math.round(nowMs() - setEffectsStartedAt)
  diagnostics.totalMs = Math.round(nowMs() - startedAt)

  if (diagnostics.totalMs >= WINDOW_SURFACE_STEP_LOG_THRESHOLD_MS) {
    void logWindowSurfaceDiagnostics('[OFPlayer window surface]', 'startup', 'window_surface_apply', diagnostics)
  }

  lastAppliedSignature = signature
  lastAppliedDiagnostics = diagnostics
  return diagnostics
}

export function initializeWindowSurface(
  theme: string | null = null,
  effectiveColorScheme: string | null = null,
  windowEffectsPreference: unknown = null,
): Promise<WindowSurfaceResult> {
  return syncWindowSurface(theme, effectiveColorScheme, windowEffectsPreference)
}

export async function syncWindowSurface(
  theme: string | null = null,
  effectiveColorScheme: string | null = null,
  windowEffectsPreference: unknown = null,
): Promise<WindowSurfaceResult> {
  const resolvedTheme = resolveStartupTheme(theme)
  const resolvedColorScheme = resolveEffectiveColorScheme(effectiveColorScheme)
  const resolvedPreference = resolveWindowEffectsPreference(windowEffectsPreference)
  const effectiveWindowEffectsTier = resolveEffectiveWindowEffectsTier(resolvedPreference)
  const requestedMode = resolveWindowSurfaceMode(effectiveWindowEffectsTier)

  if (WINDOW_SURFACE_MODE !== 'native-glass') {
    applyDocumentWindowSurface({
      theme: resolvedTheme,
      effectiveColorScheme: resolvedColorScheme,
      windowEffectsPreference: resolvedPreference,
      effectiveWindowEffectsTier,
      windowSurfaceMode: 'web',
      windowSurfaceState: 'ready',
    })

    return {
      mode: 'web',
      theme: resolvedTheme,
      effectiveColorScheme: resolvedColorScheme,
      windowEffectsPreference: resolvedPreference,
      effectiveWindowEffects: effectiveWindowEffectsTier,
      totalMs: 0,
    }
  }

  applyDocumentWindowSurface({
    theme: resolvedTheme,
    effectiveColorScheme: resolvedColorScheme,
    windowEffectsPreference: resolvedPreference,
    effectiveWindowEffectsTier,
    windowSurfaceMode: requestedMode === 'native-glass' ? 'solid' : requestedMode,
    windowSurfaceState: requestedMode === 'native-glass' ? 'preparing' : 'ready',
  })

  const nativeSurfaceProfile =
    requestedMode === 'native-glass' ? await resolveNativeSurfaceProfile() : null

  const nativeSurfaceRequest: NativeWindowSurfaceRequest = {
    theme: resolvedTheme,
    effectiveColorScheme: resolvedColorScheme,
    windowEffectsPreference: resolvedPreference,
    effectiveWindowEffectsTier,
    nativeSurfaceProfile,
  }

  return applyNativeWindowSurface(nativeSurfaceRequest)
}

async function applyNativeWindowSurface({
  theme,
  effectiveColorScheme,
  windowEffectsPreference,
  effectiveWindowEffectsTier,
  nativeSurfaceProfile,
}: NativeWindowSurfaceRequest): Promise<WindowSurfaceDiagnostics> {
  return applyResolvedNativeWindowSurface({
    theme,
    effectiveColorScheme,
    windowEffectsPreference,
    effectiveWindowEffectsTier,
    nativeSurfaceProfile,
  })
}
