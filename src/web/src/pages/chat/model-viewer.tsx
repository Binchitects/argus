import { Lightbulb, RotateCcw, Sun } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { type GltfModel, parseGlb } from './gltf'

/**
 * A .glb in the chat, turned with the mouse, a finger or the arrow keys, zoomed with the wheel, a pinch or +/-: its
 * colours, textures, glass and glow, lit by daylight or by its own lights. WebGL2, nothing to download.
 */
export default function ModelViewer({ url, fileName }: { url: string; fileName: string }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const scene = useRef<Scene | null>(null)
  const [state, setState] = useState<'loading' | 'ready' | string>('loading')
  const [ownLights, setOwnLights] = useState(false)
  const [hasLights, setHasLights] = useState(false)

  useEffect(() => {
    const el = canvas.current
    if (!el) return
    let stopped = false
    const gl = el.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: false })
    if (!gl) {
      setState('This browser cannot show 3D here: download the .glb and open it in a 3D viewer.')
      return
    }
    fetch(url)
      .then((r) => {
        if (!r.ok) throw new Error(`The model could not be read (${r.status}).`)
        return r.arrayBuffer()
      })
      .then(async (buffer) => {
        const model = parseGlb(buffer)
        if (stopped) return
        scene.current = await Scene.create(gl, el, model)
        setHasLights(model.lights.length > 0)
        setState('ready')
      })
      .catch((e: unknown) => !stopped && setState(e instanceof Error ? e.message : 'The model could not be shown.'))
    return () => {
      stopped = true
      scene.current?.dispose()
      scene.current = null
    }
  }, [url])

  useEffect(() => scene.current?.lightBy(ownLights), [ownLights, state])

  return (
    <figure className="mt-2 grid max-w-2xl gap-1">
      <div className="relative aspect-[4/3] w-full overflow-hidden rounded-xl border bg-gradient-to-b from-muted/30 to-muted">
        <canvas
          ref={canvas}
          tabIndex={0}
          aria-label={`${fileName} in 3D: drag or use the arrow keys to turn it, scroll, pinch or + and - to zoom`}
          className="size-full touch-none outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
        />
        {state !== 'ready' && (
          <p className="absolute inset-0 flex items-center justify-center p-4 text-center text-sm text-muted-foreground" role={state === 'loading' ? 'status' : 'alert'}>
            {state === 'loading' ? 'Loading the 3D model…' : state}
          </p>
        )}
        {state === 'ready' && (
          <div className="absolute top-2 right-2 flex gap-1">
            {hasLights && (
              <Button
                type="button"
                variant="secondary"
                size="icon-sm"
                onClick={() => setOwnLights((v) => !v)}
                aria-pressed={ownLights}
                aria-label={ownLights ? 'Light it by day' : 'Only its own lights'}
                title={ownLights ? 'Light it by day' : 'Only its own lights'}
              >
                {ownLights ? <Sun /> : <Lightbulb />}
              </Button>
            )}
            <Button type="button" variant="secondary" size="icon-sm" onClick={() => scene.current?.reset()} aria-label="Back to the first view" title="Back to the first view">
              <RotateCcw />
            </Button>
          </div>
        )}
      </div>
      <figcaption className="text-xs text-muted-foreground">Drag to turn it, scroll or pinch to zoom.</figcaption>
    </figure>
  )
}

const vertex = `#version 300 es
in vec3 aPos; in vec3 aNormal; in vec2 aUv;
uniform mat4 uViewProj; uniform vec2 uUvScale; uniform vec2 uUvOffset;
out vec3 vPos; out vec3 vNormal; out vec2 vUv;
void main() { vPos = aPos; vNormal = aNormal; vUv = aUv * uUvScale + uUvOffset; gl_Position = uViewProj * vec4(aPos, 1.0); }`

const fragment = `#version 300 es
precision highp float;
in vec3 vPos; in vec3 vNormal; in vec2 vUv;
uniform vec4 uColor; uniform bool uHasTex; uniform sampler2D uTex;
uniform float uMetallic; uniform float uRoughness; uniform float uTransmission; uniform vec3 uEmissive;
uniform vec3 uEye; uniform vec3 uKeyDir; uniform float uAmbient; uniform float uKey;
uniform int uLights; uniform vec3 uLightPos[8]; uniform vec3 uLightColor[8]; uniform float uLightPower[8]; uniform vec3 uLightDir[8]; uniform int uLightKind[8];
out vec4 outColor;
void main() {
  vec4 base = uColor;
  if (uHasTex) base *= texture(uTex, vUv);
  vec3 N = normalize(vNormal);
  if (!gl_FrontFacing) N = -N;
  vec3 V = normalize(uEye - vPos);
  vec3 diffuse = base.rgb * (1.0 - uMetallic);
  vec3 F0 = mix(vec3(0.04), base.rgb, uMetallic);
  float shine = mix(256.0, 4.0, clamp(uRoughness, 0.0, 1.0));
  float sky = 0.5 + 0.5 * N.y;
  vec3 ambient = mix(vec3(0.32, 0.30, 0.28), vec3(0.78, 0.84, 0.95), sky) * uAmbient;
  vec3 color = diffuse * ambient + F0 * ambient * 0.6;
  vec3 L = normalize(uKeyDir);
  float nl = max(dot(N, L), 0.0);
  float spec = pow(max(dot(N, normalize(L + V)), 0.0), shine) * (shine + 8.0) / 25.0;
  color += (diffuse * nl + F0 * spec * nl) * uKey;
  for (int i = 0; i < 8; i++) {
    if (i >= uLights) break;
    vec3 Ld; float power;
    if (uLightKind[i] == 2) { Ld = -uLightDir[i]; power = uLightPower[i]; }
    else {
      vec3 d = uLightPos[i] - vPos;
      float d2 = max(dot(d, d), 0.01);
      Ld = d * inversesqrt(d2);
      power = uLightPower[i] / d2;
      if (uLightKind[i] == 1) power *= smoothstep(0.45, 0.7, dot(-Ld, uLightDir[i]));
    }
    float n = max(dot(N, Ld), 0.0);
    float s = pow(max(dot(N, normalize(Ld + V)), 0.0), shine) * (shine + 8.0) / 25.0;
    color += (diffuse * n + F0 * s * n) * uLightColor[i] * power;
  }
  color += uEmissive;
  color = color / (color + vec3(0.8)) * 1.45;
  outColor = vec4(pow(color, vec3(1.0 / 2.2)), base.a * (1.0 - uTransmission * 0.7));
}`

interface Drawn {
  vao: WebGLVertexArrayObject
  count: number
  buffers: WebGLBuffer[]
  material: GltfModel['meshes'][number]['material']
  centre: [number, number, number]
}

/** The model on the GPU, its camera turning around it, drawn when something changed. */
class Scene {
  private yaw = -0.6
  private pitch = 0.35
  private distance = 1
  private frame = 0
  private ambient = 1
  private key = 1.6
  private readonly cleanups: (() => void)[] = []
  private readonly gl: WebGL2RenderingContext
  private readonly canvas: HTMLCanvasElement
  private readonly program: WebGLProgram
  private readonly drawn: Drawn[]
  private readonly textures: WebGLTexture[]
  private readonly model: GltfModel
  private readonly centre: [number, number, number]
  private readonly radius: number

  private constructor(gl: WebGL2RenderingContext, canvas: HTMLCanvasElement, program: WebGLProgram, drawn: Drawn[], textures: WebGLTexture[], model: GltfModel,
    centre: [number, number, number], radius: number) {
    this.gl = gl
    this.canvas = canvas
    this.program = program
    this.drawn = drawn
    this.textures = textures
    this.model = model
    this.centre = centre
    this.radius = radius
    this.distance = radius * 2.6
    this.listen()
    this.draw()
  }

  static async create(gl: WebGL2RenderingContext, canvas: HTMLCanvasElement, model: GltfModel): Promise<Scene> {
    const program = link(gl, vertex, fragment)
    // Decoded first, all at once; then each bound and filled in turn (a texture bound before an await is not the one bound after it).
    const bitmaps = await Promise.all(model.images.map((blob) => createImageBitmap(blob, { colorSpaceConversion: 'none' }).catch(() => null)))
    const textures = bitmaps.map((bitmap) => {
      const t = gl.createTexture()!
      gl.bindTexture(gl.TEXTURE_2D, t)
      if (bitmap) {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.SRGB8_ALPHA8, gl.RGBA, gl.UNSIGNED_BYTE, bitmap)
        bitmap.close()
      } else {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.SRGB8_ALPHA8, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([200, 200, 200, 255]))
      }
      gl.generateMipmap(gl.TEXTURE_2D)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR)
      return t
    })
    const drawn = model.meshes.map((m): Drawn => {
      const vao = gl.createVertexArray()!
      gl.bindVertexArray(vao)
      const buffers: WebGLBuffer[] = []
      const attribute = (name: string, data: Float32Array, size: number) => {
        const at = gl.getAttribLocation(program, name)
        const b = gl.createBuffer()!
        buffers.push(b)
        gl.bindBuffer(gl.ARRAY_BUFFER, b)
        gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW)
        if (at >= 0) {
          gl.enableVertexAttribArray(at)
          gl.vertexAttribPointer(at, size, gl.FLOAT, false, 0, 0)
        }
      }
      attribute('aPos', m.positions, 3)
      attribute('aNormal', m.normals, 3)
      attribute('aUv', m.uvs[m.material.texture?.uv ?? 0] ?? m.uvs[0] ?? new Float32Array((m.positions.length / 3) * 2), 2)
      const ib = gl.createBuffer()!
      buffers.push(ib)
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib)
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, m.indices, gl.STATIC_DRAW)
      gl.bindVertexArray(null)
      const c: [number, number, number] = [0, 0, 0]
      for (let i = 0; i < m.positions.length; i += 3) for (let k = 0; k < 3; k++) c[k] += m.positions[i + k] / (m.positions.length / 3)
      return { vao, count: m.indices.length, buffers, material: m.material, centre: c }
    })
    const centre: [number, number, number] = [0, 1, 2].map((k) => (model.min[k] + model.max[k]) / 2) as [number, number, number]
    const radius = Math.max(Math.hypot(model.max[0] - model.min[0], model.max[1] - model.min[1], model.max[2] - model.min[2]) / 2, 0.01)
    return new Scene(gl, canvas, program, drawn, textures, model, centre, radius)
  }

  reset() {
    this.yaw = -0.6
    this.pitch = 0.35
    this.distance = this.radius * 2.6
    this.draw()
  }

  lightBy(own: boolean) {
    this.ambient = own ? 0.06 : 1
    this.key = own ? 0 : 1.6
    this.draw()
  }

  private eye(): [number, number, number] {
    const [cx, cy, cz] = this.centre
    return [cx + Math.sin(this.yaw) * Math.cos(this.pitch) * this.distance, cy + Math.sin(this.pitch) * this.distance, cz + Math.cos(this.yaw) * Math.cos(this.pitch) * this.distance]
  }

  draw() {
    cancelAnimationFrame(this.frame)
    this.frame = requestAnimationFrame(() => this.render())
  }

  private render() {
    const { gl, canvas, program } = this
    const ratio = Math.min(window.devicePixelRatio || 1, 2)
    const w = Math.max(1, Math.round(canvas.clientWidth * ratio))
    const h = Math.max(1, Math.round(canvas.clientHeight * ratio))
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w
      canvas.height = h
    }
    gl.viewport(0, 0, w, h)
    gl.clearColor(0, 0, 0, 0)
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT)
    gl.enable(gl.DEPTH_TEST)
    gl.useProgram(program)
    const eye = this.eye()
    const viewProj = mul(perspective(Math.PI / 4, w / h, this.distance / 200, this.distance * 20), lookAt(eye, this.centre))
    const u = (name: string) => gl.getUniformLocation(program, name)
    gl.uniformMatrix4fv(u('uViewProj'), false, viewProj)
    gl.uniform3fv(u('uEye'), eye)
    gl.uniform3fv(u('uKeyDir'), [0.45, 0.85, 0.55])
    gl.uniform1f(u('uAmbient'), this.ambient)
    gl.uniform1f(u('uKey'), this.key)
    const lights = this.model.lights.slice(0, 8)
    gl.uniform1i(u('uLights'), lights.length)
    lights.forEach((l, i) => {
      gl.uniform3fv(u(`uLightPos[${i}]`), l.position)
      gl.uniform3fv(u(`uLightColor[${i}]`), l.color)
      // glTF's candela (lux for a sun), brought to this simple shading's range.
      gl.uniform1f(u(`uLightPower[${i}]`), l.kind === 'directional' ? l.intensity * 0.35 : l.intensity / 1500)
      gl.uniform3fv(u(`uLightDir[${i}]`), l.direction)
      gl.uniform1i(u(`uLightKind[${i}]`), l.kind === 'directional' ? 2 : l.kind === 'spot' ? 1 : 0)
    })
    const far = (d: Drawn) => Math.hypot(d.centre[0] - eye[0], d.centre[1] - eye[1], d.centre[2] - eye[2])
    const order = [...this.drawn.filter((d) => !d.material.blend), ...this.drawn.filter((d) => d.material.blend).sort((a, b) => far(b) - far(a))]
    for (const d of order) {
      const m = d.material
      if (m.doubleSided || m.blend) gl.disable(gl.CULL_FACE)
      else gl.enable(gl.CULL_FACE)
      if (m.blend) {
        gl.enable(gl.BLEND)
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA)
        gl.depthMask(false)
      } else {
        gl.disable(gl.BLEND)
        gl.depthMask(true)
      }
      gl.uniform4fv(u('uColor'), m.color)
      gl.uniform1f(u('uMetallic'), m.metallic)
      gl.uniform1f(u('uRoughness'), m.roughness)
      gl.uniform1f(u('uTransmission'), m.transmission)
      gl.uniform3fv(u('uEmissive'), m.emissive)
      const tex = m.texture ? this.textures[m.texture.image] : undefined
      gl.uniform1i(u('uHasTex'), tex ? 1 : 0)
      gl.uniform2fv(u('uUvScale'), m.texture?.scale ?? [1, 1])
      gl.uniform2fv(u('uUvOffset'), m.texture?.offset ?? [0, 0])
      if (tex) {
        gl.activeTexture(gl.TEXTURE0)
        gl.bindTexture(gl.TEXTURE_2D, tex)
        gl.uniform1i(u('uTex'), 0)
      }
      gl.bindVertexArray(d.vao)
      gl.drawElements(gl.TRIANGLES, d.count, gl.UNSIGNED_INT, 0)
    }
    gl.bindVertexArray(null)
    gl.depthMask(true)
  }

  private listen() {
    const c = this.canvas
    const pointers = new Map<number, { x: number; y: number }>()
    let pinch = 0
    const down = (e: PointerEvent) => {
      c.setPointerCapture(e.pointerId)
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
    }
    const move = (e: PointerEvent) => {
      const last = pointers.get(e.pointerId)
      if (!last) return
      if (pointers.size === 1) {
        this.yaw -= (e.clientX - last.x) * 0.008
        this.pitch = Math.max(-1.5, Math.min(1.5, this.pitch + (e.clientY - last.y) * 0.008))
      }
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
      if (pointers.size === 2) {
        const [a, b] = [...pointers.values()]
        const span = Math.hypot(a.x - b.x, a.y - b.y)
        if (pinch) this.zoom(pinch / span)
        pinch = span
      }
      this.draw()
    }
    const up = (e: PointerEvent) => {
      pointers.delete(e.pointerId)
      pinch = 0
    }
    const wheel = (e: WheelEvent) => {
      e.preventDefault()
      this.zoom(Math.exp(e.deltaY * 0.0015))
    }
    const key = (e: KeyboardEvent) => {
      const step: Record<string, () => void> = {
        ArrowLeft: () => (this.yaw += 0.15),
        ArrowRight: () => (this.yaw -= 0.15),
        ArrowUp: () => (this.pitch = Math.min(1.5, this.pitch + 0.1)),
        ArrowDown: () => (this.pitch = Math.max(-1.5, this.pitch - 0.1)),
        '+': () => this.zoom(0.85),
        '=': () => this.zoom(0.85),
        '-': () => this.zoom(1.18),
      }
      if (step[e.key]) {
        e.preventDefault()
        step[e.key]()
        this.draw()
      }
    }
    const reset = () => this.reset()
    const resize = new ResizeObserver(() => this.draw())
    resize.observe(c)
    c.addEventListener('pointerdown', down)
    c.addEventListener('pointermove', move)
    c.addEventListener('pointerup', up)
    c.addEventListener('pointercancel', up)
    c.addEventListener('wheel', wheel, { passive: false })
    c.addEventListener('keydown', key)
    c.addEventListener('dblclick', reset)
    this.cleanups.push(() => {
      resize.disconnect()
      c.removeEventListener('pointerdown', down)
      c.removeEventListener('pointermove', move)
      c.removeEventListener('pointerup', up)
      c.removeEventListener('pointercancel', up)
      c.removeEventListener('wheel', wheel)
      c.removeEventListener('keydown', key)
      c.removeEventListener('dblclick', reset)
    })
  }

  private zoom(by: number) {
    this.distance = Math.max(this.radius * 0.3, Math.min(this.radius * 20, this.distance * by))
    this.draw()
  }

  dispose() {
    cancelAnimationFrame(this.frame)
    for (const c of this.cleanups) c()
    const { gl } = this
    for (const d of this.drawn) {
      gl.deleteVertexArray(d.vao)
      for (const b of d.buffers) gl.deleteBuffer(b)
    }
    for (const t of this.textures) gl.deleteTexture(t)
    gl.deleteProgram(this.program)
  }
}

function link(gl: WebGL2RenderingContext, vs: string, fs: string): WebGLProgram {
  const compile = (type: number, source: string) => {
    const s = gl.createShader(type)!
    gl.shaderSource(s, source)
    gl.compileShader(s)
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(`The 3D viewer could not start: ${gl.getShaderInfoLog(s)}`)
    return s
  }
  const p = gl.createProgram()!
  gl.attachShader(p, compile(gl.VERTEX_SHADER, vs))
  gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fs))
  gl.linkProgram(p)
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(`The 3D viewer could not start: ${gl.getProgramInfoLog(p)}`)
  return p
}

function perspective(fov: number, aspect: number, near: number, far: number): Float32Array {
  const f = 1 / Math.tan(fov / 2)
  return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, (2 * far * near) / (near - far), 0])
}

function lookAt(eye: number[], at: number[]): Float32Array {
  const z = norm([eye[0] - at[0], eye[1] - at[1], eye[2] - at[2]])
  const x = norm(cross([0, 1, 0], z))
  const y = cross(z, x)
  return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1])
}

function mul(a: Float32Array, b: Float32Array): Float32Array {
  const o = new Float32Array(16)
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3]
  return o
}

const cross = (a: number[], b: number[]) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
const dot = (a: number[], b: number[]) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const norm = (a: number[]) => {
  const l = Math.hypot(a[0], a[1], a[2]) || 1
  return [a[0] / l, a[1] / l, a[2] / l]
}
