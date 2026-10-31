import type { ImmersiveBackgroundColors, RgbColor } from './colorExtractor'

type ColorVector = [number, number, number]

interface ImmersiveBackgroundRenderOptions {
  flow?: number
}

interface ImmersiveBackgroundRenderer {
  gl: WebGLRenderingContext
  program: WebGLProgram
  buffer: WebGLBuffer | null
  positionLocation: number
  uniforms: {
    resolution: WebGLUniformLocation | null
    seed: WebGLUniformLocation | null
    flow: WebGLUniformLocation | null
    base: WebGLUniformLocation | null
    topLeft: WebGLUniformLocation | null
    topRight: WebGLUniformLocation | null
    center: WebGLUniformLocation | null
    bottomLeft: WebGLUniformLocation | null
    bottomRight: WebGLUniformLocation | null
    rareAccent: WebGLUniformLocation | null
    rareAccentStrength: WebGLUniformLocation | null
  }
}

const rendererCache = new WeakMap<HTMLCanvasElement, ImmersiveBackgroundRenderer>()

const VERTEX_SHADER_SOURCE = `
attribute vec2 a_position;

void main() {
  gl_Position = vec4(a_position, 0.0, 1.0);
}
`

const FRAGMENT_SHADER_SOURCE = `
precision highp float;

uniform vec2 u_resolution;
uniform float u_seed;
uniform float u_flow;
uniform vec3 u_base;
uniform vec3 u_top_left;
uniform vec3 u_top_right;
uniform vec3 u_center;
uniform vec3 u_bottom_left;
uniform vec3 u_bottom_right;
uniform vec3 u_rare_accent;
uniform float u_rare_accent_strength;

float srgbToLinearChannel(float value) {
  return value <= 0.04045
    ? value / 12.92
    : pow((value + 0.055) / 1.055, 2.4);
}

float linearToSrgbChannel(float value) {
  return value <= 0.0031308
    ? value * 12.92
    : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
}

vec3 srgbToLinear(vec3 color) {
  return vec3(
    srgbToLinearChannel(color.r),
    srgbToLinearChannel(color.g),
    srgbToLinearChannel(color.b)
  );
}

vec3 linearToSrgb(vec3 color) {
  return vec3(
    linearToSrgbChannel(color.r),
    linearToSrgbChannel(color.g),
    linearToSrgbChannel(color.b)
  );
}

vec3 linearSrgbToOklab(vec3 color) {
  float l = 0.4122214708 * color.r + 0.5363325363 * color.g + 0.0514459929 * color.b;
  float m = 0.2119034982 * color.r + 0.6806995451 * color.g + 0.1073969566 * color.b;
  float s = 0.0883024619 * color.r + 0.2817188376 * color.g + 0.6299787005 * color.b;
  vec3 lms = pow(max(vec3(l, m, s), vec3(0.0)), vec3(1.0 / 3.0));

  return vec3(
    0.2104542553 * lms.x + 0.7936177850 * lms.y - 0.0040720468 * lms.z,
    1.9779984951 * lms.x - 2.4285922050 * lms.y + 0.4505937099 * lms.z,
    0.0259040371 * lms.x + 0.7827717662 * lms.y - 0.8086757660 * lms.z
  );
}

vec3 oklabToLinearSrgb(vec3 color) {
  float l_ = color.x + 0.3963377774 * color.y + 0.2158037573 * color.z;
  float m_ = color.x - 0.1055613458 * color.y - 0.0638541728 * color.z;
  float s_ = color.x - 0.0894841775 * color.y - 1.2914855480 * color.z;
  float l = l_ * l_ * l_;
  float m = m_ * m_ * m_;
  float s = s_ * s_ * s_;

  return vec3(
     4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
  );
}

vec3 mixOklab(vec3 a, vec3 b, float t) {
  vec3 labA = linearSrgbToOklab(srgbToLinear(a));
  vec3 labB = linearSrgbToOklab(srgbToLinear(b));
  return clamp(linearToSrgb(oklabToLinearSrgb(mix(labA, labB, t))), 0.0, 1.0);
}

vec3 mixLinear(vec3 a, vec3 b, float t) {
  return clamp(linearToSrgb(mix(srgbToLinear(a), srgbToLinear(b), t)), 0.0, 1.0);
}

vec3 blendLinear(vec3 base, vec3 layer, float alpha) {
  return clamp(linearToSrgb(mix(srgbToLinear(base), srgbToLinear(layer), clamp(alpha, 0.0, 1.0))), 0.0, 1.0);
}

float relativeLuminance(vec3 color) {
  return dot(srgbToLinear(color), vec3(0.2126, 0.7152, 0.0722));
}

float ease(float t) {
  return t * t * (3.0 - 2.0 * t);
}

float softBand(float axis, float center, float width) {
  float distanceFromCenter = abs(axis - center) / max(width, 0.0001);
  return pow(1.0 - smoothstep(0.0, 1.0, distanceFromCenter), 1.35);
}

vec3 sampleRamp(float t) {
  float eased = ease(t);
  vec3 diagonal = t <= 0.5
    ? mixOklab(u_top_left, u_center, ease(t * 2.0))
    : mixOklab(u_center, u_bottom_right, ease((t - 0.5) * 2.0));
  vec3 cross = mixOklab(u_top_right, u_bottom_left, eased);
  float crossWeight = sin(3.14159265359 * t) * 0.18;

  return mixOklab(diagonal, cross, crossWeight);
}

float gradientNoise(vec2 pixel) {
  return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

float triangularNoise(vec2 pixel) {
  return gradientNoise(pixel) + gradientNoise(pixel + vec2(19.19, 47.77)) - 1.0;
}

void main() {
  vec2 uv = gl_FragCoord.xy / u_resolution;
  uv.y = 1.0 - uv.y;

  vec2 flowOffset = vec2(
    sin(u_flow * 0.19) * 0.018 + cos(u_flow * 0.11) * 0.008,
    cos(u_flow * 0.17) * 0.014 + sin(u_flow * 0.13) * 0.006
  );
  vec2 fieldUv = clamp(
    uv + flowOffset + vec2((uv.y - 0.5) * sin(u_flow * 0.09), (uv.x - 0.5) * cos(u_flow * 0.08)) * 0.018,
    0.0,
    1.0
  );

  float diagonalPosition = clamp((fieldUv.x + fieldUv.y) * 0.5 + sin(u_flow * 0.07) * 0.018, 0.0, 1.0);
  vec3 diagonal = sampleRamp(diagonalPosition);
  vec3 topMix = mixOklab(u_top_left, u_top_right, ease(fieldUv.x));
  vec3 bottomMix = mixOklab(u_bottom_left, u_bottom_right, ease(fieldUv.x));
  vec3 sheet = mixOklab(topMix, bottomMix, ease(fieldUv.y));
  vec3 color = mixOklab(sheet, diagonal, 0.5);

  vec3 shadow = mixLinear(u_base, vec3(0.0), 0.42);

  float leftField = 1.0 - smoothstep(0.02, 0.96, fieldUv.x + fieldUv.y * 0.1);
  float rightField = smoothstep(0.18, 1.02, fieldUv.x - fieldUv.y * 0.08);

  color = blendLinear(color, u_top_left, leftField * 0.14);
  color = blendLinear(color, u_bottom_right, rightField * 0.14);

  float rareAccentMask =
    softBand(fieldUv.x * 0.76 + fieldUv.y * 0.24, 0.68 + sin(u_flow * 0.16) * 0.025, 0.34) * 0.16 +
    softBand(fieldUv.x * 0.30 + (1.0 - fieldUv.y) * 0.70, 0.42 + cos(u_flow * 0.14) * 0.025, 0.36) * 0.08;
  color = blendLinear(color, u_rare_accent, rareAccentMask * u_rare_accent_strength);

  float topShade = smoothstep(0.0, 0.58, uv.y) * 0.12;
  float bottomShade = smoothstep(0.62, 1.0, uv.y) * 0.2;
  float sideShade = smoothstep(0.42, 0.0, uv.x) * 0.1;
  color = blendLinear(color, shadow, topShade + bottomShade + sideShade);

  vec2 pixel = gl_FragCoord.xy + vec2(u_seed * 113.0, u_seed * 37.0);
  float lumaNoise = triangularNoise(pixel);
  vec3 chromaNoise = vec3(
    triangularNoise(pixel + vec2(11.0, 3.0)),
    triangularNoise(pixel + vec2(5.0, 17.0)),
    triangularNoise(pixel + vec2(23.0, 29.0))
  );

  float ditherScale = mix(0.46, 0.9, smoothstep(0.045, 0.24, relativeLuminance(color)));
  color += vec3(lumaNoise) * ((1.62 * ditherScale) / 255.0);
  color += chromaNoise * ((0.24 * ditherScale) / 255.0);

  gl_FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
`

function colorToVector(color: RgbColor | undefined, fallback: RgbColor): ColorVector {
  const resolved = color ?? fallback
  return [
    Math.max(0, Math.min(255, resolved.r ?? fallback.r)) / 255,
    Math.max(0, Math.min(255, resolved.g ?? fallback.g)) / 255,
    Math.max(0, Math.min(255, resolved.b ?? fallback.b)) / 255,
  ]
}

function createShader(gl: WebGLRenderingContext, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type)
  if (!shader) {
    throw new Error('Failed to create shader')
  }

  gl.shaderSource(shader, source)
  gl.compileShader(shader)

  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const message = gl.getShaderInfoLog(shader) || 'Unknown shader compilation error'
    gl.deleteShader(shader)
    throw new Error(message)
  }

  return shader
}

function createProgram(gl: WebGLRenderingContext): WebGLProgram {
  const vertexShader = createShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER_SOURCE)
  const fragmentShader = createShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER_SOURCE)
  const program = gl.createProgram()
  if (!program) {
    gl.deleteShader(vertexShader)
    gl.deleteShader(fragmentShader)
    throw new Error('Failed to create shader program')
  }

  gl.attachShader(program, vertexShader)
  gl.attachShader(program, fragmentShader)
  gl.linkProgram(program)
  gl.deleteShader(vertexShader)
  gl.deleteShader(fragmentShader)

  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const message = gl.getProgramInfoLog(program) || 'Unknown shader link error'
    gl.deleteProgram(program)
    throw new Error(message)
  }

  return program
}

function createRenderer(canvas: HTMLCanvasElement): ImmersiveBackgroundRenderer | null {
  const gl = canvas.getContext('webgl', {
    alpha: false,
    antialias: false,
    depth: false,
    stencil: false,
    preserveDrawingBuffer: false,
    powerPreference: 'high-performance',
  })

  if (!gl) return null

  const program = createProgram(gl)
  const positionLocation = gl.getAttribLocation(program, 'a_position')
  const buffer = gl.createBuffer()
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer)
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([
      -1, -1,
       1, -1,
      -1,  1,
      -1,  1,
       1, -1,
       1,  1,
    ]),
    gl.STATIC_DRAW,
  )

  return {
    gl,
    program,
    buffer,
    positionLocation,
    uniforms: {
      resolution: gl.getUniformLocation(program, 'u_resolution'),
      seed: gl.getUniformLocation(program, 'u_seed'),
      flow: gl.getUniformLocation(program, 'u_flow'),
      base: gl.getUniformLocation(program, 'u_base'),
      topLeft: gl.getUniformLocation(program, 'u_top_left'),
      topRight: gl.getUniformLocation(program, 'u_top_right'),
      center: gl.getUniformLocation(program, 'u_center'),
      bottomLeft: gl.getUniformLocation(program, 'u_bottom_left'),
      bottomRight: gl.getUniformLocation(program, 'u_bottom_right'),
      rareAccent: gl.getUniformLocation(program, 'u_rare_accent'),
      rareAccentStrength: gl.getUniformLocation(program, 'u_rare_accent_strength'),
    },
  }
}

function resolveNoiseSeed(...vectors: ColorVector[]): number {
  const value = vectors.reduce((total, vector, index) => {
    return total + vector.reduce((sum, channel, channelIndex) => {
      return sum + channel * (17.17 + index * 11.31 + channelIndex * 7.73)
    }, 0)
  }, 0)
  return value - Math.floor(value)
}

function resolveRenderer(canvas: HTMLCanvasElement): ImmersiveBackgroundRenderer | null {
  const cached = rendererCache.get(canvas)
  if (cached) return cached

  const renderer = createRenderer(canvas)
  if (renderer) {
    rendererCache.set(canvas, renderer)
  }
  return renderer
}

export function renderImmersiveBackground(
  canvas: HTMLCanvasElement,
  colors: ImmersiveBackgroundColors,
  options: ImmersiveBackgroundRenderOptions = {},
): boolean {
  const renderer = resolveRenderer(canvas)
  if (!renderer) return false

  const rect = canvas.getBoundingClientRect()
  if (rect.width <= 0 || rect.height <= 0) return false

  const devicePixelRatio = Math.max(1, window.devicePixelRatio || 1)
  const cssPixels = rect.width * rect.height
  const maxPixels = 4200000
  const cappedRatio = Math.min(devicePixelRatio, Math.sqrt(maxPixels / Math.max(cssPixels, 1)))
  const pixelRatio = Math.max(1, cappedRatio)
  const width = Math.max(1, Math.round(rect.width * pixelRatio))
  const height = Math.max(1, Math.round(rect.height * pixelRatio))

  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width
    canvas.height = height
  }

  const fallback = colors.center ?? { r: 20, g: 22, b: 28 }
  const topLeft = colorToVector(colors.topLeft, fallback)
  const topRight = colorToVector(colors.topRight, colors.center ?? fallback)
  const center = colorToVector(colors.center, fallback)
  const bottomLeft = colorToVector(colors.bottomLeft, colors.center ?? fallback)
  const bottomRight = colorToVector(colors.bottomRight, fallback)
  const base = colorToVector(colors.base, colors.center ?? fallback)
  const rareAccent = colorToVector(colors.rareAccent, colors.center ?? fallback)
  const rareAccentStrength = Math.max(0, Math.min(1, colors.rareAccentStrength ?? 0))
  const flow = typeof options.flow === 'number' && Number.isFinite(options.flow) ? options.flow : 0
  const noiseSeed = resolveNoiseSeed(base, topLeft, topRight, center, bottomLeft, bottomRight, rareAccent)

  const { gl, program, buffer, positionLocation, uniforms } = renderer
  gl.viewport(0, 0, width, height)
  gl.disable(gl.BLEND)
  gl.useProgram(program)
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer)
  gl.enableVertexAttribArray(positionLocation)
  gl.vertexAttribPointer(positionLocation, 2, gl.FLOAT, false, 0, 0)
  gl.uniform2f(uniforms.resolution, width, height)
  gl.uniform1f(uniforms.seed, noiseSeed)
  gl.uniform1f(uniforms.flow, flow)
  gl.uniform3fv(uniforms.base, base)
  gl.uniform3fv(uniforms.topLeft, topLeft)
  gl.uniform3fv(uniforms.topRight, topRight)
  gl.uniform3fv(uniforms.center, center)
  gl.uniform3fv(uniforms.bottomLeft, bottomLeft)
  gl.uniform3fv(uniforms.bottomRight, bottomRight)
  gl.uniform3fv(uniforms.rareAccent, rareAccent)
  gl.uniform1f(uniforms.rareAccentStrength, rareAccentStrength)
  gl.drawArrays(gl.TRIANGLES, 0, 6)

  return true
}
