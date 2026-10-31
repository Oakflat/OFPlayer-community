// How the lyric capsule looks. The host stores the same object as the `lyricCapsule`
// preference (OFPlayer.WinUI.Core.Playback.CapsuleAppearance) and pushes it here; any
// missing or unknown field falls back to its default on its own.

export const CAPSULE_THEMES = ['dark', 'light', 'cover'] as const
export const CAPSULE_ACCENTS = ['cover', 'blue', 'coral', 'teal', 'amber', 'lavender'] as const
export const CAPSULE_FONT_SIZES = ['small', 'medium', 'large'] as const
export const CAPSULE_SECOND_LINES = ['meta', 'next', 'translation', 'none'] as const
export const CAPSULE_ALIGNS = ['left', 'center'] as const
export const CAPSULE_WAVES = ['echo', 'spectrum', 'off'] as const
export const CAPSULE_WIDTHS = ['line', 'fixed'] as const
export const CAPSULE_CONTROL_MODES = ['hover', 'always'] as const
export const CAPSULE_PAUSED_MODES = ['keep', 'mini'] as const
export const CAPSULE_MIN_OPACITY = 60
export const CAPSULE_MAX_OPACITY = 100

export interface LyricCapsuleAppearance {
  theme: typeof CAPSULE_THEMES[number]
  accent: typeof CAPSULE_ACCENTS[number]
  opacity: number
  spin: boolean
  fontSize: typeof CAPSULE_FONT_SIZES[number]
  secondLine: typeof CAPSULE_SECOND_LINES[number]
  align: typeof CAPSULE_ALIGNS[number]
  wave: typeof CAPSULE_WAVES[number]
  width: typeof CAPSULE_WIDTHS[number]
  controls: typeof CAPSULE_CONTROL_MODES[number]
  whenPaused: typeof CAPSULE_PAUSED_MODES[number]
}

export const DEFAULT_CAPSULE_APPEARANCE: Readonly<LyricCapsuleAppearance> = Object.freeze({
  theme: 'dark',
  accent: 'cover',
  opacity: 100,
  spin: true,
  fontSize: 'medium',
  secondLine: 'meta',
  align: 'left',
  wave: 'echo',
  width: 'line',
  controls: 'hover',
  whenPaused: 'keep',
})

export const CAPSULE_BRAND_ACCENT = '#2a8bfd'
export const CAPSULE_ACCENT_COLORS: Readonly<Record<Exclude<LyricCapsuleAppearance['accent'], 'cover'>, string>> =
  Object.freeze({ blue: CAPSULE_BRAND_ACCENT, coral: '#ff6f61', teal: '#2ec4a6', amber: '#f2b03d', lavender: '#9a86ff' })

function pick<T extends string>(value: unknown, options: readonly T[], fallback: T): T {
  return typeof value === 'string' && (options as readonly string[]).includes(value) ? value as T : fallback
}

export function normalizeCapsuleAppearance(value: unknown): LyricCapsuleAppearance {
  const record = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  const fallback = DEFAULT_CAPSULE_APPEARANCE
  const opacity = typeof record.opacity === 'number' && Number.isFinite(record.opacity)
    ? Math.round(Math.min(CAPSULE_MAX_OPACITY, Math.max(CAPSULE_MIN_OPACITY, record.opacity)))
    : fallback.opacity
  return {
    theme: pick(record.theme, CAPSULE_THEMES, fallback.theme),
    accent: pick(record.accent, CAPSULE_ACCENTS, fallback.accent),
    opacity,
    spin: typeof record.spin === 'boolean' ? record.spin : fallback.spin,
    fontSize: pick(record.fontSize, CAPSULE_FONT_SIZES, fallback.fontSize),
    secondLine: pick(record.secondLine, CAPSULE_SECOND_LINES, fallback.secondLine),
    align: pick(record.align, CAPSULE_ALIGNS, fallback.align),
    wave: pick(record.wave, CAPSULE_WAVES, fallback.wave),
    width: pick(record.width, CAPSULE_WIDTHS, fallback.width),
    controls: pick(record.controls, CAPSULE_CONTROL_MODES, fallback.controls),
    whenPaused: pick(record.whenPaused, CAPSULE_PAUSED_MODES, fallback.whenPaused),
  }
}

// --- Geometry ------------------------------------------------------------------------

export const CAPSULE_CONTROL_ROW_HEIGHT = 40
export const CAPSULE_MINI_SIZE = 48
export const CAPSULE_EXPANDED_MIN_WIDTH = 404
export const CAPSULE_FIXED_WIDTH = 440
export const CAPSULE_LINE_MIN_WIDTH = 224
export const CAPSULE_LINE_MAX_WIDTH = 540
export const CAPSULE_EXPANDED_RADIUS = 24

export interface CapsuleMetrics {
  fontSize: number
  secondFontSize: number
  rowHeight: number
  waveWidth: number
}

export function capsuleMetrics(appearance: LyricCapsuleAppearance): CapsuleMetrics {
  const fontSize = appearance.fontSize === 'small' ? 13 : appearance.fontSize === 'large' ? 17 : 15
  const hasSecondLine = appearance.secondLine !== 'none'
  return {
    fontSize,
    secondFontSize: appearance.fontSize === 'large' ? 12 : 11,
    rowHeight: hasSecondLine ? (fontSize >= 17 ? 54 : 48) : 44,
    waveWidth: appearance.wave === 'echo' ? 66 : appearance.wave === 'spectrum' ? 51 : 0,
  }
}

export function estimateTextUnits(text: string): number {
  return Array.from(text || '').reduce((total, char) => {
    if (/\s/.test(char)) return total + 0.34
    if (/[　-鿿가-힯぀-ヿ＀-￯]/.test(char)) return total + 1.04
    if (/[A-Z0-9]/.test(char)) return total + 0.68
    if (/[il.,:;'|!]/.test(char)) return total + 0.36
    return total + 0.58
  }, 0)
}

/**
 * Follow the primary lyric, in 8px steps so near-equal lines keep one width.
 * Secondary metadata/translation fits the resulting space with an ellipsis: a long
 * song title must not pin every lyric to one width. The view supplies actual glyph
 * geometry; the estimate is only a fallback before the first browser layout.
 */
export function capsuleLineWidth(appearance: LyricCapsuleAppearance, line: string, measuredTextWidth?: number): number {
  if (appearance.width === 'fixed') return CAPSULE_FIXED_WIDTH
  const metrics = capsuleMetrics(appearance)
  const textWidth = typeof measuredTextWidth === 'number' && Number.isFinite(measuredTextWidth) && measuredTextWidth >= 0
    ? measuredTextWidth : estimateTextUnits(line) * metrics.fontSize
  // border + left inset + disc + gap + text + (gap + waveform) + right inset
  const width = 2 + 5 + 36 + 10 + textWidth + (metrics.waveWidth ? 10 + metrics.waveWidth : 0) + 14
  return Math.min(CAPSULE_LINE_MAX_WIDTH, Math.max(CAPSULE_LINE_MIN_WIDTH, Math.ceil(width / 8) * 8))
}

// --- Colour --------------------------------------------------------------------------

type Rgb = readonly [number, number, number]

function hexToRgb(hex: string): Rgb {
  const value = Number.parseInt(hex.replace('#', ''), 16)
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255]
}

function rgbToHex(rgb: readonly number[]): string {
  return `#${rgb.slice(0, 3).map((channel) => Math.round(channel).toString(16).padStart(2, '0')).join('')}`
}

function rgbToHsl([r, g, b]: Rgb): [number, number, number] {
  const red = r / 255
  const green = g / 255
  const blue = b / 255
  const max = Math.max(red, green, blue)
  const min = Math.min(red, green, blue)
  const lightness = (max + min) / 2
  if (max === min) return [0, 0, lightness]
  const delta = max - min
  const saturation = delta / (1 - Math.abs(2 * lightness - 1))
  const hue = max === red ? ((green - blue) / delta) % 6 : max === green ? (blue - red) / delta + 2 : (red - green) / delta + 4
  return [((hue * 60) + 360) % 360, saturation, lightness]
}

function hslToRgb(hue: number, saturation: number, lightness: number): Rgb {
  const chroma = (1 - Math.abs(2 * lightness - 1)) * saturation
  const x = chroma * (1 - Math.abs(((hue / 60) % 2) - 1))
  const m = lightness - chroma / 2
  const [r, g, b] = hue < 60 ? [chroma, x, 0] : hue < 120 ? [x, chroma, 0] : hue < 180 ? [0, chroma, x]
    : hue < 240 ? [0, x, chroma] : hue < 300 ? [x, 0, chroma] : [chroma, 0, x]
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255]
}

function relativeLuminance(hex: string): number {
  const [r, g, b] = hexToRgb(hex).map((channel) => {
    const value = channel / 255
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

/** Glyph colour on an accent-filled button. */
export function capsuleOnAccent(accent: string): string {
  return relativeLuminance(accent) > 0.4 ? '#141414' : '#ffffff'
}

export function capsuleLighten(accent: string, amount: number): string {
  return rgbToHex(hexToRgb(accent).map((channel) => channel + (255 - channel) * amount))
}

export function capsuleAlpha(accent: string, alpha: number): string {
  const [r, g, b] = hexToRgb(accent)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

/**
 * The cover's dominant vivid colour from RGBA pixels (a small downscaled copy), lifted
 * into a range that reads on both capsule backgrounds. Null for grey or dark covers.
 */
export function pickArtworkAccent(pixels: ArrayLike<number>): string | null {
  const buckets = Array.from({ length: 12 }, () => ({ weight: 0, r: 0, g: 0, b: 0 }))
  for (let offset = 0; offset + 3 < pixels.length; offset += 4) {
    if (pixels[offset + 3] < 128) continue
    const rgb: Rgb = [pixels[offset], pixels[offset + 1], pixels[offset + 2]]
    const [hue, saturation, lightness] = rgbToHsl(rgb)
    if (saturation < 0.22 || lightness < 0.12 || lightness > 0.92) continue
    const weight = saturation * (1 - Math.abs(lightness - 0.55) * 1.4)
    if (weight <= 0) continue
    const bucket = buckets[Math.floor(hue / 30) % 12]
    bucket.weight += weight
    bucket.r += rgb[0] * weight
    bucket.g += rgb[1] * weight
    bucket.b += rgb[2] * weight
  }
  const best = buckets.reduce((winner, bucket) => (bucket.weight > winner.weight ? bucket : winner))
  const pixelCount = Math.max(1, Math.floor(pixels.length / 4))
  if (best.weight < pixelCount * 0.02) return null
  const [hue, saturation, lightness] = rgbToHsl([best.r / best.weight, best.g / best.weight, best.b / best.weight])
  return rgbToHex(hslToRgb(hue, Math.min(0.9, Math.max(0.55, saturation)), Math.min(0.7, Math.max(0.6, lightness))))
}

export function resolveCapsuleAccent(appearance: LyricCapsuleAppearance, artworkAccent: string | null): string {
  return appearance.accent === 'cover' ? artworkAccent ?? CAPSULE_BRAND_ACCENT : CAPSULE_ACCENT_COLORS[appearance.accent]
}

export interface CapsulePalette {
  tone: 'dark' | 'light'
  background: string
  solid: string
  foreground: string
  secondary: string
  unsung: string
  border: string
  track: string
  hover: string
}

/** Colours for a theme; "cover" tints the dark capsule with the artwork colour, or stays dark without one. */
export function capsulePalette(theme: LyricCapsuleAppearance['theme'], opacity: number, artworkAccent: string | null): CapsulePalette {
  const alpha = Math.min(CAPSULE_MAX_OPACITY, Math.max(CAPSULE_MIN_OPACITY, opacity)) / 100
  if (theme === 'light') {
    return {
      tone: 'light',
      background: `rgba(250, 250, 252, ${alpha})`,
      solid: '#fafafc',
      foreground: '#16161a',
      secondary: 'rgba(22, 22, 26, 0.58)',
      unsung: 'rgba(22, 22, 26, 0.32)',
      border: 'rgba(0, 0, 0, 0.08)',
      track: 'rgba(0, 0, 0, 0.12)',
      hover: 'rgba(0, 0, 0, 0.06)',
    }
  }
  if (theme === 'cover' && artworkAccent) {
    const [hue, saturation] = rgbToHsl(hexToRgb(artworkAccent))
    const [r, g, b] = hslToRgb(hue, Math.min(0.36, saturation), 0.13).map(Math.round)
    return {
      tone: 'dark',
      background: `rgba(${r}, ${g}, ${b}, ${alpha})`,
      solid: rgbToHex([r, g, b]),
      foreground: '#fbf7f5',
      secondary: 'rgba(251, 247, 245, 0.62)',
      unsung: 'rgba(251, 247, 245, 0.38)',
      border: 'rgba(255, 255, 255, 0.1)',
      track: 'rgba(255, 255, 255, 0.18)',
      hover: 'rgba(255, 255, 255, 0.1)',
    }
  }
  // The dark capsule is pure black, like the island it is modelled on.
  return {
    tone: 'dark',
    background: `rgba(0, 0, 0, ${alpha})`,
    solid: '#000000',
    foreground: '#f5f5f7',
    secondary: 'rgba(245, 245, 247, 0.6)',
    unsung: 'rgba(245, 245, 247, 0.36)',
    border: 'rgba(255, 255, 255, 0.09)',
    track: 'rgba(255, 255, 255, 0.16)',
    hover: 'rgba(255, 255, 255, 0.1)',
  }
}

// --- Waveform ------------------------------------------------------------------------

export const CAPSULE_ECHO_BARS = 14
export const CAPSULE_ECHO_STEP_MS = 110
export const CAPSULE_SPECTRUM_ORDER = [6, 4, 2, 1, 0, 1, 3, 5, 7] as const

/** One echo sample: the frame's overall loudness, eased so quiet passages still move. */
export function capsuleEchoSample(levels: readonly number[], playing: boolean): number {
  if (!playing || levels.length === 0) return 0.04
  const mean = levels.reduce((sum, level) => sum + (Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0), 0) / levels.length
  return Math.min(1, Math.max(0.04, mean ** 0.58 * 1.1))
}
