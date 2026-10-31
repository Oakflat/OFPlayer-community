import type { BackdropPainter } from './backdrop.ts'
import { BACKDROP_SIDE, emptyBackdropField } from './backdropField.ts'
import { BACKDROP_CELLS, BACKDROP_FLOATS, BACKDROP_LAYERS, backdropReach, backdropVertices } from './backdropFlow.ts'

// The picture has no detail, so it is painted small and stretched by the page: about a
// megapixel at most, whatever the window and its scaling.
const MAX_PIXELS = 1_000_000

const VERTEX_SHADER = `
attribute vec2 a_position;
attribute vec2 a_uv;
uniform vec2 u_resolution;
varying vec2 v_uv;

void main() {
  vec2 clip = a_position / u_resolution * 2.0 - 1.0;
  gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
  v_uv = a_uv;
}
`

// A smooth dark gradient shows its 8-bit steps as contour lines, and here they would crawl.
// A grain this faint is not seen as grain; it only keeps the steps from lining up.
const FRAGMENT_SHADER = `
precision highp float;
uniform sampler2D u_field;
uniform float u_alpha;
uniform float u_seed;
varying vec2 v_uv;

float noise(vec2 pixel) {
  return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

void main() {
  vec2 pixel = gl_FragCoord.xy + u_seed;
  float grain = noise(pixel) + noise(pixel + vec2(19.19, 47.77)) - 1.0;
  gl_FragColor = vec4(texture2D(u_field, v_uv).rgb + grain * (1.5 / 255.0), u_alpha);
}
`

type Flow = {
  gl: WebGLRenderingContext
  positions: WebGLBuffer
  resolution: WebGLUniformLocation | null
  alpha: WebGLUniformLocation | null
  seed: WebGLUniformLocation | null
}

function compile(gl: WebGLRenderingContext, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type)!
  gl.shaderSource(shader, source)
  gl.compileShader(shader)
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? 'shader')
  return shader
}

/** Everything the mesh needs, bound once: the page draws nothing else with this context. */
function prepare(gl: WebGLRenderingContext): Flow {
  const program = gl.createProgram()!
  gl.attachShader(program, compile(gl, gl.VERTEX_SHADER, VERTEX_SHADER))
  gl.attachShader(program, compile(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER))
  gl.linkProgram(program)
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'program')
  gl.useProgram(program)

  const side = BACKDROP_CELLS + 1
  const uv = new Float32Array(BACKDROP_FLOATS)
  const indices = new Uint16Array(BACKDROP_CELLS * BACKDROP_CELLS * 6)
  for (let row = 0, i = 0, n = 0; row <= BACKDROP_CELLS; row++) {
    for (let column = 0; column <= BACKDROP_CELLS; column++) {
      uv[i++] = column / BACKDROP_CELLS
      uv[i++] = row / BACKDROP_CELLS
      if (row === BACKDROP_CELLS || column === BACKDROP_CELLS) continue
      const corner = row * side + column
      indices.set([corner, corner + 1, corner + side, corner + 1, corner + side + 1, corner + side], n)
      n += 6
    }
  }
  const attribute = (name: string, data: Float32Array | null) => {
    const buffer = gl.createBuffer()!
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer)
    if (data) gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW)
    else gl.bufferData(gl.ARRAY_BUFFER, BACKDROP_FLOATS * 4, gl.DYNAMIC_DRAW)
    const location = gl.getAttribLocation(program, name)
    gl.enableVertexAttribArray(location)
    gl.vertexAttribPointer(location, 2, gl.FLOAT, false, 0, 0)
    return buffer
  }
  attribute('a_uv', uv)
  // Left bound: every frame writes the mesh's new places into it.
  const positions = attribute('a_position', null)
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, gl.createBuffer())
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW)

  gl.bindTexture(gl.TEXTURE_2D, gl.createTexture())
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
  gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA)

  return {
    gl,
    positions,
    resolution: gl.getUniformLocation(program, 'u_resolution'),
    alpha: gl.getUniformLocation(program, 'u_alpha'),
    seed: gl.getUniformLocation(program, 'u_seed'),
  }
}

/**
 * Paints the ground on [canvas]. Without WebGL the same picture is painted flat and still:
 * the cover the right way up, as the first layer lies before it has moved.
 */
export function createBackdropRenderer(canvas: HTMLCanvasElement): BackdropPainter & { resize(): void; dispose(): void } {
  const ground = emptyBackdropField()
  const groundStyle = `rgb(${ground[0]}, ${ground[1]}, ${ground[2]})`
  const vertices = new Float32Array(BACKDROP_FLOATS)
  let field: Uint8ClampedArray | null = null
  let seconds = 0

  const gl = canvas.getContext('webgl', { alpha: false, antialias: false, depth: false, stencil: false })
  let flow: Flow | null = gl ? prepare(gl) : null
  const flat = gl ? null : canvas.getContext('2d')
  const tile = gl ? null : document.createElement('canvas')
  if (tile) tile.width = tile.height = BACKDROP_SIDE

  function upload() {
    if (!field) return
    if (flow) {
      const { gl } = flow
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, BACKDROP_SIDE, BACKDROP_SIDE, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(field.buffer, field.byteOffset, field.length))
    } else if (tile) {
      tile.getContext('2d')?.putImageData(new ImageData(new Uint8ClampedArray(field), BACKDROP_SIDE, BACKDROP_SIDE), 0, 0)
    }
  }

  function draw(at: number) {
    seconds = at
    const { width, height } = canvas
    if (flow) {
      const { gl } = flow
      gl.viewport(0, 0, width, height)
      if (!field) {
        gl.clearColor(ground[0] / 255, ground[1] / 255, ground[2] / 255, 1)
        gl.clear(gl.COLOR_BUFFER_BIT)
        return
      }
      gl.uniform2f(flow.resolution, width, height)
      BACKDROP_LAYERS.forEach((layer, index) => {
        backdropVertices(layer, width, height, seconds, vertices)
        gl.bindBuffer(gl.ARRAY_BUFFER, flow!.positions)
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, vertices)
        if (layer.alpha < 1) gl.enable(gl.BLEND)
        else gl.disable(gl.BLEND)
        gl.uniform1f(flow!.alpha, layer.alpha)
        gl.uniform1f(flow!.seed, index * 31)
        gl.drawElements(gl.TRIANGLES, BACKDROP_CELLS * BACKDROP_CELLS * 6, gl.UNSIGNED_SHORT, 0)
      })
    } else if (flat) {
      flat.fillStyle = groundStyle
      flat.fillRect(0, 0, width, height)
      if (!field || !tile) return
      const reach = backdropReach(BACKDROP_LAYERS[0], width, height)
      flat.drawImage(tile, width / 2 - reach, height / 2 - reach, reach * 2, reach * 2)
    }
  }

  function resize() {
    const width = canvas.clientWidth
    const height = canvas.clientHeight
    if (width <= 0 || height <= 0) return
    const scale = Math.min(window.devicePixelRatio || 1, Math.sqrt(MAX_PIXELS / (width * height)))
    const pixelWidth = Math.max(1, Math.round(width * scale))
    const pixelHeight = Math.max(1, Math.round(height * scale))
    if (canvas.width === pixelWidth && canvas.height === pixelHeight) return
    canvas.width = pixelWidth
    canvas.height = pixelHeight
    draw(seconds)
  }

  // A lost context takes its buffers and texture with it; a restored one starts empty.
  const lost = (event: Event) => {
    event.preventDefault()
    flow = null
  }
  const restored = () => {
    if (!gl) return
    flow = prepare(gl)
    upload()
    draw(seconds)
  }
  canvas.addEventListener('webglcontextlost', lost)
  canvas.addEventListener('webglcontextrestored', restored)
  resize()

  return {
    setField(pixels) {
      field = pixels
      upload()
    },
    draw,
    resize,
    dispose() {
      canvas.removeEventListener('webglcontextlost', lost)
      canvas.removeEventListener('webglcontextrestored', restored)
    },
  }
}
