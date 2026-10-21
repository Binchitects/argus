import { Eraser, ImagePlus, MonitorUp, PenLine, Trash2, Undo2 } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { BoardHeight, BoardWidth, drawings, sketchAsks, sketchName, type SketchAsk, type Stroke } from './sketch-prompts'

const inks = [
  { color: '#111827', name: 'Black' },
  { color: '#dc2626', name: 'Red' },
  { color: '#2563eb', name: 'Blue' },
  { color: '#16a34a', name: 'Green' },
]

const widths = [
  { width: 3, name: 'Thin' },
  { width: 8, name: 'Thick' },
]

function paint(context: CanvasRenderingContext2D, strokes: Stroke[], background: HTMLImageElement | null) {
  context.fillStyle = '#ffffff'
  context.fillRect(0, 0, BoardWidth, BoardHeight)
  if (background) {
    const scale = Math.min(BoardWidth / background.naturalWidth, BoardHeight / background.naturalHeight)
    const w = background.naturalWidth * scale
    const h = background.naturalHeight * scale
    context.drawImage(background, (BoardWidth - w) / 2, (BoardHeight - h) / 2, w, h)
  }
  for (const s of strokes) {
    context.strokeStyle = s.erase ? '#ffffff' : s.color
    context.lineWidth = s.erase ? s.width * 4 : s.width
    context.lineCap = 'round'
    context.lineJoin = 'round'
    context.beginPath()
    s.points.forEach((p, i) => (i === 0 ? context.moveTo(p.x, p.y) : context.lineTo(p.x, p.y)))
    // A dot: a stroke of one point.
    if (s.points.length === 1) context.lineTo(s.points[0]!.x + 0.1, s.points[0]!.y)
    context.stroke()
  }
}

/**
 * The whiteboard: draw with the mouse, a pen or a finger (or on a picture, or on the shared screen), then ask for what it
 * should become; the drawing goes with the question as a picture. Each chat keeps its drawing while the page is open.
 */
function Whiteboard({ chatKey, busy, onAsk, onAttach, screenFrame, onClose }: {
  chatKey: string
  busy: boolean
  onAsk: (file: File, ask: SketchAsk) => void
  onAttach: (file: File) => void
  screenFrame?: () => Promise<File | null>
  onClose: () => void
}) {
  const canvas = useRef<HTMLCanvasElement | null>(null)
  const kept = drawings.get(chatKey)
  const [strokes, setStrokes] = useState<Stroke[]>(kept?.strokes ?? [])
  const [image, setImage] = useState<HTMLImageElement | null>(kept?.background ?? null)
  const [ink, setInk] = useState(inks[0]!.color)
  const [width, setWidth] = useState(widths[0]!.width)
  const [erase, setErase] = useState(false)
  const drawing = useRef<Stroke | null>(null)
  const picker = useRef<HTMLInputElement | null>(null)

  /** A picture to draw on: shown once it has loaded. */
  const drawOn = (url: string) => {
    const img = new Image()
    img.onload = () => setImage(img)
    img.src = url
  }

  const redraw = useCallback((extra?: Stroke) => {
    const context = canvas.current?.getContext('2d')
    if (context) paint(context, extra ? [...strokes, extra] : strokes, image)
  }, [strokes, image])

  useEffect(() => {
    redraw()
    drawings.set(chatKey, { strokes, background: image })
  }, [redraw, chatKey, strokes, image])

  const point = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    return { x: ((e.clientX - r.left) / Math.max(1, r.width)) * BoardWidth, y: ((e.clientY - r.top) / Math.max(1, r.height)) * BoardHeight }
  }
  const down = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    e.currentTarget.setPointerCapture?.(e.pointerId)
    // A pen presses harder for a thicker line.
    const pressure = e.pointerType === 'pen' && e.pressure > 0 ? 0.5 + e.pressure : 1
    drawing.current = { color: ink, width: width * pressure, erase, points: [point(e)] }
    redraw(drawing.current)
  }
  const move = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return
    drawing.current.points.push(point(e))
    redraw(drawing.current)
  }
  const up = () => {
    const done = drawing.current
    drawing.current = null
    if (done) setStrokes((s) => [...s, done])
  }

  const file = () =>
    new Promise<File | null>((resolve) => canvas.current?.toBlob((b) => resolve(b ? new File([b], sketchName(new Date()), { type: 'image/png' }) : null), 'image/png') ?? resolve(null))
  const ask = async (a: SketchAsk) => {
    const f = await file()
    if (f) onAsk(f, a)
  }
  const attach = async () => {
    const f = await file()
    if (f) onAttach(f)
  }
  const onScreen = async () => {
    const frame = await screenFrame?.()
    if (frame) drawOn(URL.createObjectURL(frame))
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Sketch</DialogTitle>
        <DialogDescription>Draw what you mean, roughly, then choose what it should become. It goes with the question as a picture.</DialogDescription>
      </DialogHeader>
      <div className="flex flex-wrap items-center gap-1.5" role="toolbar" aria-label="Pen">
        {inks.map((i) => (
          <button
            key={i.color}
            type="button"
            aria-label={`${i.name} ink`}
            aria-pressed={!erase && ink === i.color}
            className={cn('size-7 rounded-full border-2 outline-none focus-visible:ring-[3px] focus-visible:ring-ring', !erase && ink === i.color ? 'border-foreground' : 'border-transparent')}
            style={{ backgroundColor: i.color }}
            onClick={() => {
              setInk(i.color)
              setErase(false)
            }}
          />
        ))}
        <span className="mx-1 h-6 w-px bg-border" aria-hidden="true" />
        {widths.map((w) => (
          <Button key={w.width} type="button" size="sm" variant={width === w.width ? 'secondary' : 'ghost'} aria-pressed={width === w.width} onClick={() => setWidth(w.width)}>
            {w.name}
          </Button>
        ))}
        <Button type="button" size="sm" variant={erase ? 'secondary' : 'ghost'} aria-pressed={erase} onClick={() => setErase(!erase)}>
          <Eraser /> Eraser
        </Button>
        <span className="flex-1" />
        <Tooltip content="Draw on a picture: a photo of a paper sketch, a screenshot of a page">
          <Button type="button" size="icon-sm" variant="ghost" aria-label="Draw on a picture" onClick={() => picker.current?.click()}>
            <ImagePlus />
          </Button>
        </Tooltip>
        {screenFrame && (
          <Tooltip content="Draw on what is on the shared screen now">
            <Button type="button" size="icon-sm" variant="ghost" aria-label="Draw on the shared screen" onClick={() => void onScreen()}>
              <MonitorUp />
            </Button>
          </Tooltip>
        )}
        <Button type="button" size="icon-sm" variant="ghost" aria-label="Undo" disabled={strokes.length === 0} onClick={() => setStrokes((s) => s.slice(0, -1))}>
          <Undo2 />
        </Button>
        <Button
          type="button"
          size="icon-sm"
          variant="ghost"
          aria-label="Clear the board"
          disabled={strokes.length === 0 && !image}
          onClick={() => {
            setStrokes([])
            setImage(null)
          }}
        >
          <Trash2 />
        </Button>
        <input
          ref={picker}
          type="file"
          accept="image/*"
          className="hidden"
          aria-label="A picture to draw on"
          onChange={(e) => {
            const f = e.target.files?.[0]
            if (f) drawOn(URL.createObjectURL(f))
            e.target.value = ''
          }}
        />
      </div>
      <canvas
        ref={canvas}
        width={BoardWidth}
        height={BoardHeight}
        aria-label="The whiteboard"
        className="aspect-[8/5] w-full cursor-crosshair touch-none rounded-lg border bg-white"
        onPointerDown={down}
        onPointerMove={move}
        onPointerUp={up}
        onPointerCancel={up}
        onPointerLeave={up}
      />
      <DialogFooter className="flex-wrap gap-2 sm:justify-between">
        <fieldset className="flex flex-wrap gap-1.5" aria-label="Make it">
          {sketchAsks.map((a) => (
            <Button key={a.key} type="button" size="sm" disabled={busy || strokes.length === 0} onClick={() => void ask(a)}>
              {a.label}
            </Button>
          ))}
        </fieldset>
        <div className="flex gap-1.5">
          <Button type="button" size="sm" variant="outline" disabled={strokes.length === 0 && !image} onClick={() => void attach()}>
            Attach it
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={onClose}>
            Close
          </Button>
        </div>
      </DialogFooter>
      {busy && <p className="text-xs text-muted-foreground">An answer is being written: ask when it is done, or attach the sketch to your next message.</p>}
    </>
  )
}

/** The box's Sketch button and its whiteboard. */
export function SketchButton(props: Omit<Parameters<typeof Whiteboard>[0], 'onClose'>) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Tooltip content="Sketch: draw it, and have it built">
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Sketch" onClick={() => setOpen(true)}>
          <PenLine />
        </Button>
      </Tooltip>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-4xl">
          {open && (
            <Whiteboard
              {...props}
              onAsk={(f, a) => {
                setOpen(false)
                props.onAsk(f, a)
              }}
              onAttach={(f) => {
                setOpen(false)
                props.onAttach(f)
              }}
              onClose={() => setOpen(false)}
            />
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
