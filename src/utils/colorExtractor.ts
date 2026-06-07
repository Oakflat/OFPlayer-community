/**
 * CN: 专辑封面颜色提取工具
 * CN: 从图片中提取主色调，用于生成沉浸式播放页背景
 * CN:
 * CN: 算法：高密度空间分区采样 + 全局明度画像
 * CN: 白色/纸面占比决定背景底色明度，少量高饱和像素决定流动色晕的 hue
 *
 * EN: Album cover color extraction tool
 * EN: Extracts dominant colors from images for generating immersive player backgrounds
 * EN:
 * EN: Algorithm: high-density spatial partitioned sampling + global luminance profile
 * EN: White/paper ratio determines background base luminance, few high-saturation pixels determine flowing accent hue
 */

export interface RgbColor {
  r: number
  g: number
  b: number
}

interface HslColor {
  h: number
  s: number
  l: number
}

export type ColorProfile = RgbColor & HslColor

interface RgbAccumulator {
  r: number
  g: number
  b: number
  weight: number
}

interface RegionProfile {
  average: ColorProfile
  accent: ColorProfile
  paper: ColorProfile
  count: number
  lightRatio: number
  whiteRatio: number
  darkRatio: number
  colorRatio: number
  accentWeightRatio: number
}

interface SalientAccentProfile {
  color: ColorProfile
  strength: number
  salienceRatio: number
  hueDistance: number
}

interface HueBin {
  acc: RgbAccumulator
  count: number
  salience: number
  hueDistance: number
}

interface BestHueBin {
  bin: HueBin
  score: number
  salienceRatio: number
  averageHueDistance: number
}

export interface ImmersiveBackgroundColors {
  base: RgbColor
  topLeft: RgbColor
  topRight: RgbColor
  center: RgbColor
  bottomLeft: RgbColor
  bottomRight: RgbColor
  rareAccent: RgbColor
  rareAccentStrength: number
  rareAccentSalience?: number
  rareAccentHueDistance?: number
  whiteRatio?: number
  lightRatio?: number
  darkRatio?: number
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value))
}

function smoothstep(edge0: number, edge1: number, value: number): number {
  const t = clamp((value - edge0) / (edge1 - edge0), 0, 1)
  return t * t * (3 - 2 * t)
}

function hueDistanceDegrees(a: number, b: number): number {
  const diff = Math.abs((((a - b) % 360) + 540) % 360 - 180)
  return diff
}

/**
 * CN: RGB 转 HSL
 *
 * EN: RGB to HSL conversion
 */
function rgbToHsl(r: number, g: number, b: number): HslColor {
  r /= 255
  g /= 255
  b /= 255

  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  let h = 0
  let s = 0
  const l = (max + min) / 2

  if (max !== min) {
    const d = max - min
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min)

    switch (max) {
      case r:
        h = ((g - b) / d + (g < b ? 6 : 0)) / 6
        break
      case g:
        h = ((b - r) / d + 2) / 6
        break
      case b:
        h = ((r - g) / d + 4) / 6
        break
    }
  }

  return { h: h * 360, s: s * 100, l: l * 100 }
}

/**
 * CN: HSL 转 RGB
 *
 * EN: HSL to RGB conversion
 */
function hslToRgb(h: number, s: number, l: number): ColorProfile {
  h = ((h % 360) + 360) % 360 / 360
  s = clamp(s, 0, 100) / 100
  l = clamp(l, 0, 100) / 100

  let r, g, b

  if (s === 0) {
    r = g = b = l
  } else {
    const hue2rgb = (p: number, q: number, t: number) => {
      if (t < 0) t += 1
      if (t > 1) t -= 1
      if (t < 1 / 6) return p + (q - p) * 6 * t
      if (t < 1 / 2) return q
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6
      return p
    }
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s
    const p = 2 * l - q
    r = hue2rgb(p, q, h + 1 / 3)
    g = hue2rgb(p, q, h)
    b = hue2rgb(p, q, h - 1 / 3)
  }

  const color = {
    r: Math.round(r * 255),
    g: Math.round(g * 255),
    b: Math.round(b * 255),
  }
  return { ...color, ...rgbToHsl(color.r, color.g, color.b) }
}

/**
 * CN: 将 sRGB 值（0-255）转换到线性光照空间（0-1）
 * CN: 用于 gamma 正确的颜色插值，消除色阶问题
 *
 * EN: Convert sRGB values (0-255) to linear light space (0-1)
 * EN: Used for gamma-correct color interpolation, eliminating banding issues
 */
function toLinear(c: number): number {
  const f = c / 255
  return f <= 0.04045 ? f / 12.92 : Math.pow((f + 0.055) / 1.055, 2.4)
}

/**
 * CN: 将线性光照值（0-1）转换回 sRGB（0-255）
 *
 * EN: Convert linear light values (0-1) back to sRGB (0-255)
 */
function toSRGB(c: number): number {
  const f = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055
  return Math.round(Math.max(0, Math.min(1, f)) * 255)
}

/**
 * CN: 在线性光照空间中插值两个颜色（gamma 正确，避免色阶/posterization）
 *
 * EN: Interpolate two colors in linear light space (gamma-correct, avoids banding/posterization)
 */
function lerpColorLinear(c1: RgbColor, c2: RgbColor, t: number): RgbColor {
  const r1l = toLinear(c1.r), g1l = toLinear(c1.g), b1l = toLinear(c1.b)
  const r2l = toLinear(c2.r), g2l = toLinear(c2.g), b2l = toLinear(c2.b)
  return {
    r: toSRGB(r1l + (r2l - r1l) * t),
    g: toSRGB(g1l + (g2l - g1l) * t),
    b: toSRGB(b1l + (b2l - b1l) * t),
  }
}

function easeInOut(t: number): number {
  return t * t * (3 - 2 * t)
}

function sampleBackgroundRamp(colors: Pick<ImmersiveBackgroundColors, 'topLeft' | 'topRight' | 'center' | 'bottomLeft' | 'bottomRight'>, t: number): RgbColor {
  const {
    topLeft,
    topRight,
    center,
    bottomLeft,
    bottomRight,
  } = colors
  const diagonal = t <= 0.5
    ? lerpColorLinear(topLeft, center, easeInOut(t * 2))
    : lerpColorLinear(center, bottomRight, easeInOut((t - 0.5) * 2))
  const cross = lerpColorLinear(topRight, bottomLeft, easeInOut(t))
  const crossWeight = Math.sin(Math.PI * t) * 0.16

  return lerpColorLinear(diagonal, cross, crossWeight)
}

function createRgbAccumulator(): RgbAccumulator {
  return { r: 0, g: 0, b: 0, weight: 0 }
}

function addRgb(acc: RgbAccumulator, r: number, g: number, b: number, weight = 1): void {
  if (weight <= 0) return
  acc.r += toLinear(r) * weight
  acc.g += toLinear(g) * weight
  acc.b += toLinear(b) * weight
  acc.weight += weight
}

function resolveRgb(acc: RgbAccumulator, fallback: RgbColor = { r: 40, g: 40, b: 50 }): ColorProfile {
  if (acc.weight <= 0) {
    const color = { ...fallback }
    return { ...color, ...rgbToHsl(color.r, color.g, color.b) }
  }

  const color = {
    r: toSRGB(acc.r / acc.weight),
    g: toSRGB(acc.g / acc.weight),
    b: toSRGB(acc.b / acc.weight),
  }
  return { ...color, ...rgbToHsl(color.r, color.g, color.b) }
}

/**
 * CN: 提取 ImageData 特定像素区域的颜色画像
 *
 * EN: Extract color profile from a specific pixel region of ImageData
 */
function extractRegionProfile(
  imageData: ImageData,
  width: number,
  xStart: number,
  yStart: number,
  xEnd: number,
  yEnd: number,
): RegionProfile {
  const data = imageData.data
  const average = createRgbAccumulator()
  const accent = createRgbAccumulator()
  const paper = createRgbAccumulator()
  let count = 0
  let lightCount = 0
  let whiteCount = 0
  let darkCount = 0
  let colorCount = 0
  let accentWeight = 0

  for (let y = yStart; y < yEnd; y++) {
    for (let x = xStart; x < xEnd; x++) {
      const i = (y * width + x) * 4
      if (data[i + 3] < 128) continue

      const r = data[i]
      const g = data[i + 1]
      const b = data[i + 2]
      const hsl = rgbToHsl(r, g, b)

      addRgb(average, r, g, b)
      count++

      if (hsl.l > 72) lightCount++
      if (hsl.l > 84 && hsl.s < 24) whiteCount++
      if (hsl.l < 18) darkCount++

      const paperWeight = smoothstep(78, 96, hsl.l) * (1 - smoothstep(16, 42, hsl.s))
      addRgb(paper, r, g, b, paperWeight)

      const saturationWeight = smoothstep(14, 52, hsl.s)
      const lightnessWeight = smoothstep(8, 22, hsl.l) * (1 - smoothstep(88, 98, hsl.l))
      const weight = saturationWeight * lightnessWeight
      if (weight > 0.01) {
        addRgb(accent, r, g, b, weight * weight)
        accentWeight += weight
        colorCount++
      }
    }
  }

  const averageColor = resolveRgb(average)
  return {
    average: averageColor,
    accent: resolveRgb(accent, averageColor),
    paper: resolveRgb(paper, averageColor),
    count,
    lightRatio: count > 0 ? lightCount / count : 0,
    whiteRatio: count > 0 ? whiteCount / count : 0,
    darkRatio: count > 0 ? darkCount / count : 0,
    colorRatio: count > 0 ? colorCount / count : 0,
    accentWeightRatio: count > 0 ? accentWeight / count : 0,
  }
}

function hasUsableAccent(profile: RegionProfile): boolean {
  return profile.accentWeightRatio > 0.004 || profile.colorRatio > 0.008
}

function makeDisplayAccentColor(color: ColorProfile): ColorProfile {
  return hslToRgb(
    color.h,
    clamp(color.s * 1.08, 38, 74),
    clamp(color.l * 0.72, 22, 48),
  )
}

function extractSalientAccentProfile(
  imageData: ImageData,
  width: number,
  height: number,
  global: RegionProfile,
): SalientAccentProfile {
  const data = imageData.data
  const binCount = 24
  const bins = Array.from({ length: binCount }, () => ({
    acc: createRgbAccumulator(),
    count: 0,
    salience: 0,
    hueDistance: 0,
  }))
  const dominantHue = hasUsableAccent(global) ? global.accent.h : global.average.h
  let total = 0

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4
      if (data[i + 3] < 128) continue

      const r = data[i]
      const g = data[i + 1]
      const b = data[i + 2]
      const hsl = rgbToHsl(r, g, b)
      total++

      if (hsl.s < 18 || hsl.l < 7 || hsl.l > 92) continue

      const saturationWeight = smoothstep(24, 70, hsl.s)
      const lightnessWeight = smoothstep(7, 22, hsl.l) * (1 - smoothstep(82, 97, hsl.l))
      const hueDistance = hueDistanceDegrees(hsl.h, dominantHue)
      const distinctWeight = smoothstep(24, 112, hueDistance)
      const weight = saturationWeight * lightnessWeight * (0.32 + distinctWeight * 1.34)

      if (weight <= 0.01) continue

      const binIndex = clamp(Math.floor((hsl.h / 360) * binCount), 0, binCount - 1)
      const bin = bins[binIndex]
      addRgb(bin.acc, r, g, b, weight * weight)
      bin.count++
      bin.salience += weight
      bin.hueDistance += hueDistance * weight
    }
  }

  if (total <= 0) {
    return { color: global.accent, strength: 0, salienceRatio: 0, hueDistance: 0 }
  }

  let best: BestHueBin | null = null
  for (const bin of bins) {
    if (bin.count <= 0 || bin.salience <= 0) continue

    const salienceRatio = bin.salience / total
    const averageHueDistance = bin.hueDistance / bin.salience
    const distinctScore = 0.5 + smoothstep(28, 128, averageHueDistance) * 1.5
    const score = salienceRatio * distinctScore

    if (!best || score > best.score) {
      best = { bin, score, salienceRatio, averageHueDistance }
    }
  }

  if (!best || best.salienceRatio < 0.00018) {
    return { color: global.accent, strength: 0, salienceRatio: 0, hueDistance: 0 }
  }

  const rawAccent = resolveRgb(best.bin.acc, global.accent)
  const color = makeDisplayAccentColor(rawAccent)
  const distinctStrength = 0.55 + smoothstep(32, 132, best.averageHueDistance) * 0.45
  const strength = clamp(smoothstep(0.00018, 0.0085, best.salienceRatio) * distinctStrength, 0, 1)

  return {
    color,
    strength,
    salienceRatio: best.salienceRatio,
    hueDistance: best.averageHueDistance,
  }
}

function selectHueSource(region: RegionProfile, global: RegionProfile): ColorProfile {
  if (hasUsableAccent(region)) return region.accent
  if (hasUsableAccent(global)) return global.accent
  return region.average
}

function makeBaseColor(global: RegionProfile): ColorProfile {
  const whiteBias = smoothstep(0.22, 0.72, global.whiteRatio + global.lightRatio * 0.25)
  const paperMix = smoothstep(0.16, 0.58, global.whiteRatio)
  const source = lerpColorLinear(global.average, global.paper, paperMix * 0.55)
  const sourceHsl = rgbToHsl(source.r, source.g, source.b)
  const accentBias = hasUsableAccent(global) ? smoothstep(0.002, 0.035, global.accentWeightRatio) : 0
  const hue = sourceHsl.s < 10 && accentBias > 0 ? global.accent.h : sourceHsl.h
  const saturation = accentBias > 0
    ? clamp(sourceHsl.s * 0.35 + global.accent.s * 0.2 * accentBias, 4, whiteBias > 0.45 ? 24 : 32)
    : clamp(sourceHsl.s * 0.5, 0, 14)
  const lightness = clamp(
    sourceHsl.l * (0.4 + whiteBias * 0.23),
    9 + whiteBias * 34,
    30 + whiteBias * 29,
  )

  return hslToRgb(hue, saturation, lightness)
}

/**
 * CN: 调整颜色使其更适合作为沉浸背景。
 * CN: 保留白底封面的明度，同时从彩色笔触中取流动色晕。
 *
 * EN: Adjust colors to be more suitable as immersive background.
 * EN: Preserves luminance of white-background covers while extracting flowing accents from colorful strokes.
 */
function adjustForBackground(region: RegionProfile, global: RegionProfile): ColorProfile {
  const whiteBias = smoothstep(0.22, 0.72, global.whiteRatio + global.lightRatio * 0.25)
  const source = selectHueSource(region, global)
  const hasAccent = hasUsableAccent(region) || hasUsableAccent(global)
  const colorPresence = clamp(region.accentWeightRatio * 18 + global.accentWeightRatio * 7, 0, 1)
  const regionLightness = region.average.l
  const minLightness = 15 + whiteBias * 28
  const maxLightness = 38 + whiteBias * 25
  const lightness = clamp(
    regionLightness * (0.5 + whiteBias * 0.18),
    minLightness,
    maxLightness,
  )
  const saturation = hasAccent
    ? clamp(source.s * (0.62 + whiteBias * 0.26) * (0.58 + colorPresence * 0.42), whiteBias > 0.45 ? 14 : 10, whiteBias > 0.45 ? 62 : 54)
    : clamp(region.average.s * 0.55, 0, 16)

  return hslToRgb(source.h, saturation, lightness)
}

function drawCoverImageToSquareCanvas(
  ctx: CanvasRenderingContext2D,
  img: HTMLImageElement,
  size: number,
): void {
  const width = img.naturalWidth || img.width || size
  const height = img.naturalHeight || img.height || size
  const sourceSize = Math.min(width, height)
  const sourceX = Math.max(0, (width - sourceSize) / 2)
  const sourceY = Math.max(0, (height - sourceSize) / 2)

  ctx.drawImage(img, sourceX, sourceY, sourceSize, sourceSize, 0, 0, size, size)
}

/**
 * CN: 从图片 URL 提取沉浸背景颜色
 * CN: 先按封面 object-fit: cover 的方形裁切取样，再采样四角和中心区域。
 * CN: @param {string} imageUrl
 * CN: @returns {Promise<{ base, topLeft, topRight, center, bottomLeft, bottomRight, rareAccent, rareAccentStrength, whiteRatio, lightRatio, darkRatio }>}
 *
 * EN: Extract immersive background colors from image URL
 * EN: First samples with object-fit: cover square crop, then samples four corners and center region.
 * EN: @param {string} imageUrl
 * EN: @returns {Promise<{ base, topLeft, topRight, center, bottomLeft, bottomRight, rareAccent, rareAccentStrength, whiteRatio, lightRatio, darkRatio }>}
 */
export async function extractDominantColors(imageUrl: string): Promise<ImmersiveBackgroundColors> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.crossOrigin = 'anonymous'

    img.onload = () => {
      try {
        const canvas = document.createElement('canvas')
        const size = 384
        canvas.width = size
        canvas.height = size

        const ctx = canvas.getContext('2d')
        if (!ctx) {
          throw new Error('Failed to create canvas context')
        }
        drawCoverImageToSquareCanvas(ctx, img, size)
        const imageData = ctx.getImageData(0, 0, size, size)

        // CN: 高密度采样四角和中心，同时统计全局明度画像。
        // EN: High-density sampling of four corners and center, while computing global luminance profile.
        const q = Math.floor(size * 0.38)
        const mid = Math.floor(size * 0.3)
        const midEnd = Math.floor(size * 0.7)

        const global = extractRegionProfile(imageData, size, 0, 0, size, size)
        const topLeft = extractRegionProfile(imageData, size, 0, 0, q, q)
        const topRight = extractRegionProfile(imageData, size, size - q, 0, size, q)
        const center = extractRegionProfile(imageData, size, mid, mid, midEnd, midEnd)
        const bottomLeft = extractRegionProfile(imageData, size, 0, size - q, q, size)
        const bottomRight = extractRegionProfile(imageData, size, size - q, size - q, size, size)
        const rareAccent = extractSalientAccentProfile(imageData, size, size, global)

        resolve({
          base: makeBaseColor(global),
          topLeft: adjustForBackground(topLeft, global),
          topRight: adjustForBackground(topRight, global),
          center: adjustForBackground(center, global),
          bottomLeft: adjustForBackground(bottomLeft, global),
          bottomRight: adjustForBackground(bottomRight, global),
          rareAccent: rareAccent.color,
          rareAccentStrength: rareAccent.strength,
          rareAccentSalience: rareAccent.salienceRatio,
          rareAccentHueDistance: rareAccent.hueDistance,
          whiteRatio: global.whiteRatio,
          lightRatio: global.lightRatio,
          darkRatio: global.darkRatio,
        })
      } catch (err) {
        reject(err)
      }
    }

    img.onerror = () => reject(new Error('Failed to load image'))
    img.src = imageUrl
  })
}

/**
 * CN: 生成对角线 CSS 背景渐变兜底
 * CN: 使用 gamma 正确的线性光照空间插值 + 高密度停止点，消除色阶问题。
 * CN: 叠加层使用线性方向，避免大面积径向渐变在横向窗口中形成环形色阶。
 * CN: 颜色从封面左上角流向右下角，产生自然的沉浸感
 * CN: @param {{ topLeft, topRight, center, bottomLeft, bottomRight }} colors
 * CN: @returns {string} CSS background 值
 *
 * EN: Generate diagonal CSS background gradient fallback
 * EN: Uses gamma-correct linear light space interpolation + high-density stops, eliminating banding issues.
 * EN: Overlay layers use linear direction to avoid ring-shaped banding in landscape windows from large radial gradients.
 * EN: Colors flow from cover's top-left to bottom-right, creating natural immersive feel
 * EN: @param {{ topLeft, topRight, center, bottomLeft, bottomRight }} colors
 * EN: @returns {string} CSS background value
 */
export function generateBackgroundGradient(colors: ImmersiveBackgroundColors): string {
  const { topLeft, center, bottomRight } = colors
  const topRight = colors.topRight ?? lerpColorLinear(topLeft, center, 0.42)
  const bottomLeft = colors.bottomLeft ?? lerpColorLinear(center, bottomRight, 0.58)
  const base = colors.base ?? center
  const upperGlow = lerpColorLinear(topRight, base, 0.22)
  const lowerGlow = lerpColorLinear(bottomLeft, base, 0.3)
  const shadow = lerpColorLinear(base, { r: 0, g: 0, b: 0 }, 0.32)
  const rareAccent = colors.rareAccent ?? center
  const rareAccentStrength = clamp(colors.rareAccentStrength ?? 0, 0, 1)
  const rampColors = {
    topLeft,
    topRight,
    center,
    bottomLeft,
    bottomRight,
  }
  const stops: string[] = []
  const N = 96

  for (let i = 0; i <= N; i++) {
    const t = i / N
    const color = sampleBackgroundRamp(rampColors, t)
    stops.push(`rgb(${color.r}, ${color.g}, ${color.b}) ${(t * 100).toFixed(3)}%`)
  }

  const layers = [
    `linear-gradient(96deg in oklab, rgba(${upperGlow.r}, ${upperGlow.g}, ${upperGlow.b}, 0) 0%, rgba(${upperGlow.r}, ${upperGlow.g}, ${upperGlow.b}, 0.24) 42%, rgba(${upperGlow.r}, ${upperGlow.g}, ${upperGlow.b}, 0) 76%)`,
    `linear-gradient(18deg in oklab, rgba(${lowerGlow.r}, ${lowerGlow.g}, ${lowerGlow.b}, 0) 6%, rgba(${lowerGlow.r}, ${lowerGlow.g}, ${lowerGlow.b}, 0.18) 48%, rgba(${lowerGlow.r}, ${lowerGlow.g}, ${lowerGlow.b}, 0) 86%)`,
    `linear-gradient(180deg in oklab, rgba(${shadow.r}, ${shadow.g}, ${shadow.b}, 0.26), rgba(${shadow.r}, ${shadow.g}, ${shadow.b}, 0) 44%, rgba(0, 0, 0, 0.16) 100%)`,
    `linear-gradient(135deg in oklab, ${stops.join(', ')})`,
  ]

  if (rareAccentStrength > 0.02) {
    const accentAlpha = (0.2 * rareAccentStrength).toFixed(3)
    layers.unshift(
      `linear-gradient(105deg in oklab, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, 0) 4%, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, ${accentAlpha}) 44%, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, 0) 82%)`,
      `linear-gradient(24deg in oklab, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, 0) 12%, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, ${(0.1 * rareAccentStrength).toFixed(3)}) 54%, rgba(${rareAccent.r}, ${rareAccent.g}, ${rareAccent.b}, 0) 90%)`,
    )
  }

  return layers.join(', ')
}

/**
 * CN: 默认颜色（无封面时使用）
 *
 * EN: Default colors (used when no cover image)
 */
export const defaultColors = {
  base: { r: 18, g: 16, b: 24 },
  topLeft: { r: 30, g: 25, b: 40 },
  topRight: { r: 24, g: 30, b: 42 },
  center: { r: 25, g: 20, b: 35 },
  bottomLeft: { r: 26, g: 22, b: 36 },
  bottomRight: { r: 20, g: 15, b: 30 },
  rareAccent: { r: 38, g: 28, b: 40 },
  rareAccentStrength: 0,
  rareAccentSalience: 0,
  rareAccentHueDistance: 0,
  whiteRatio: 0,
  lightRatio: 0,
  darkRatio: 0.6,
} satisfies ImmersiveBackgroundColors
