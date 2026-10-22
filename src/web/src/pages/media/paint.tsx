import { useEffect, useImperativeHandle, useRef, type Ref } from 'react'

/** What the editor asks of the paint layer. */
export interface PaintHandle {
  /** The picture as drawn here and where it was painted (white, its edge soft), both PNG data URLs of the working size; null when nothing is painted. */
  export: () => { picture: string; mask: string } | null
  clear: () => void
  undo: () => void
}

/** How soft a painted edge is, in pixels of the working size: about a 170th of its longer side. */
const softness = (width: number, height: number) => Math.max(2, Math.round(Math.max(width, height) / 170))

/**
 * Over a picture, where to change it: painted with the pointer (a finger, a pen, a mouse), shown half see-through.
 * Drawn at the size the picture model works at, whatever its size on the screen.
 */
export function PaintLayer({ image, width, height, brush, erase, onChange, ref }: {
  image: HTMLImageElement | null
  width: number
  height: number
  /** The brush's width, in pixels of the working size. */
  brush: number
  erase: boolean
  /** Whether anything is painted now. */
  onChange: (painted: boolean) => void
  ref: Ref<PaintHandle>
}) {
  const canvas = useRef<HTMLCanvasElement | null>(null)
  const last = useRef<{ x: number; y: number } | null>(null)
  const undos = useRef<ImageData[]>([])

  const ctx = () => canvas.current?.getContext('2d', { willReadFrequently: true }) ?? null

  const painted = () => {
    const c = ctx()
    if (!c) return false
    const data = c.getImageData(0, 0, width, height).data
    for (let i = 3; i < data.length; i += 4 * 7) if (data[i] > 0) return true
    return false
  }

  // A new picture or size starts clean.
  useEffect(() => {
    undos.current = []
    canvas.current?.getContext('2d')?.clearRect(0, 0, width, height)
    onChange(false)
  }, [width, height, image, onChange])

  useImperativeHandle(ref, () => ({
    export: () => {
      const c = canvas.current
      if (!c || !image || !painted()) return null
      const make = () => Object.assign(document.createElement('canvas'), { width, height })
      const picture = make()
      picture.getContext('2d')!.drawImage(image, 0, 0, width, height)
      // White where painted, on black, its edge blurred.
      const white = make()
      const w = white.getContext('2d')!
      w.drawImage(c, 0, 0)
      w.globalCompositeOperation = 'source-in'
      w.fillStyle = '#fff'
      w.fillRect(0, 0, width, height)
      const mask = make()
      const m = mask.getContext('2d')!
      m.fillStyle = '#000'
      m.fillRect(0, 0, width, height)
      m.filter = `blur(${softness(width, height)}px)`
      m.drawImage(white, 0, 0)
      return { picture: picture.toDataURL('image/png'), mask: mask.toDataURL('image/png') }
    },
    clear: () => {
      const c = ctx()
      if (!c) return
      undos.current.push(c.getImageData(0, 0, width, height))
      c.clearRect(0, 0, width, height)
      onChange(false)
    },
    undo: () => {
      const c = ctx()
      const before = undos.current.pop()
      if (!c || !before) return
      c.putImageData(before, 0, 0)
      onChange(painted())
    },
  }))

  const at = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect()
    return { x: ((e.clientX - box.left) / box.width) * width, y: ((e.clientY - box.top) / box.height) * height }
  }

  const stroke = (from: { x: number; y: number }, to: { x: number; y: number }) => {
    const c = ctx()
    if (!c) return
    c.globalCompositeOperation = erase ? 'destination-out' : 'source-over'
    c.strokeStyle = '#ec4899'
    c.fillStyle = '#ec4899'
    c.lineWidth = brush
    c.lineCap = 'round'
    c.lineJoin = 'round'
    c.beginPath()
    if (from.x === to.x && from.y === to.y) {
      c.arc(to.x, to.y, brush / 2, 0, Math.PI * 2)
      c.fill()
    } else {
      c.moveTo(from.x, from.y)
      c.lineTo(to.x, to.y)
      c.stroke()
    }
  }

  return (
    <canvas
      ref={canvas}
      width={width}
      height={height}
      aria-label="Paint over the part to change"
      className="absolute inset-0 size-full cursor-crosshair touch-none opacity-55"
      onPointerDown={(e) => {
        const c = ctx()
        if (!c) return
        e.currentTarget.setPointerCapture(e.pointerId)
        undos.current.push(c.getImageData(0, 0, width, height))
        if (undos.current.length > 20) undos.current.shift()
        const p = at(e)
        last.current = p
        stroke(p, p)
      }}
      onPointerMove={(e) => {
        if (!last.current) return
        const p = at(e)
        stroke(last.current, p)
        last.current = p
      }}
      onPointerUp={() => {
        last.current = null
        onChange(painted())
      }}
      onPointerCancel={() => {
        last.current = null
        onChange(painted())
      }}
    />
  )
}
