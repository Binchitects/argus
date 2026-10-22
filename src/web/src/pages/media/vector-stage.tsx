import { Pause, Play, Repeat } from 'lucide-react'
import { useCallback, useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

/** What the editor asks of the stage: where the drawing's time is, and to move it. */
export interface StageHandle {
  play: () => void
  pause: () => void
  seek: (t: number) => void
}

/**
 * The drawing on a stage of its own: a frame with no script (sandboxed; the drawing was made safe on the server too) whose
 * page this one drives — its animations played, paused and sought (SMIL's and CSS's), and a part picked by a click.
 */
function page(markup: string) {
  return `<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;height:100%;overflow:hidden;background:transparent}
body{display:grid;place-items:center}
body>svg{display:block;width:100%;height:100%;max-width:100%;max-height:100%}
.arena-picking{cursor:crosshair}
.arena-hover{filter:drop-shadow(0 0 2px rgba(124,58,237,.9))}
.arena-picked{filter:drop-shadow(0 0 2px #7c3aed) drop-shadow(0 0 5px rgba(124,58,237,.8))}
</style></head><body>${markup}</body></html>`
}

type Doc = Document & { defaultView: (Window & typeof globalThis) | null }

export function VectorStage({
  markup, seconds, loops, picking, picked, onPick, ref, className,
}: {
  markup: string
  seconds: number
  loops: boolean
  /** Clicking picks a part (its id) instead of nothing. */
  picking?: boolean
  picked?: string | null
  onPick?: (id: string | null) => void
  ref?: Ref<StageHandle>
  className?: string
}) {
  const frame = useRef<HTMLIFrameElement | null>(null)
  const [doc, setDoc] = useState<Doc | null>(null)
  const [playing, setPlaying] = useState(true)
  const [time, setTime] = useState(0)
  const [loop, setLoop] = useState(true)
  const started = useRef(0)
  const svgOf = useCallback((d: Doc | null) => d?.querySelector('body > svg') as SVGSVGElement | null, [])

  const seekTo = useCallback(
    (t: number) => {
      const svg = svgOf(doc)
      if (!doc || !svg) return
      try {
        svg.setCurrentTime(t)
      } catch {
        // No SMIL in it.
      }
      for (const a of doc.getAnimations()) a.currentTime = t * 1000
      setTime(t)
    },
    [doc, svgOf],
  )
  const pause = useCallback(() => {
    const svg = svgOf(doc)
    if (!doc || !svg) return
    svg.pauseAnimations()
    for (const a of doc.getAnimations()) a.pause()
    setPlaying(false)
  }, [doc, svgOf])
  const play = useCallback(() => {
    const svg = svgOf(doc)
    if (!doc || !svg) return
    // From the start again when it had played through.
    if (!loops && seconds > 0 && time >= seconds - 0.02) seekTo(0)
    svg.unpauseAnimations()
    for (const a of doc.getAnimations()) a.play()
    started.current = performance.now() - time * 1000
    setPlaying(true)
  }, [doc, svgOf, loops, seconds, time, seekTo])

  useImperativeHandle(ref, () => ({ play, pause, seek: seekTo }), [play, pause, seekTo])

  // Its time as it plays: from the clock (the drawing's own animations all start at its load).
  useEffect(() => {
    if (!playing || !doc || seconds <= 0) return
    let raf = 0
    const tick = () => {
      let t = (performance.now() - started.current) / 1000
      if (t >= seconds) {
        if (loop || loops) {
          // Played through: from the start again, every animation with it.
          started.current = performance.now()
          seekTo(0)
          for (const a of doc.getAnimations()) a.play()
          svgOf(doc)?.unpauseAnimations()
          t = 0
        } else {
          seekTo(seconds)
          pause()
          return
        }
      }
      setTime(t)
      raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [playing, doc, seconds, loop, loops, seekTo, pause, svgOf])

  // Picking: a part under the pointer is lit, a click picks it (its nearest element with an id, not the stage's own).
  useEffect(() => {
    if (!doc || !picking) return
    let lit: Element | null = null
    const partOf = (e: Event) => {
      const t = e.target as Element | null
      const part = t?.closest?.('[id]')
      return part && part.localName !== 'svg' && !part.closest('defs') ? part : null
    }
    const over = (e: Event) => {
      lit?.classList.remove('arena-hover')
      lit = partOf(e)
      lit?.classList.add('arena-hover')
    }
    const click = (e: Event) => onPick?.(partOf(e)?.id ?? null)
    doc.addEventListener('pointerover', over)
    doc.addEventListener('click', click)
    doc.body.classList.add('arena-picking')
    return () => {
      lit?.classList.remove('arena-hover')
      doc.removeEventListener('pointerover', over)
      doc.removeEventListener('click', click)
      doc.body.classList.remove('arena-picking')
    }
  }, [doc, picking, onPick])

  useEffect(() => {
    if (!doc) return
    doc.querySelectorAll('.arena-picked').forEach((e) => e.classList.remove('arena-picked'))
    if (picked) doc.getElementById(picked)?.classList.add('arena-picked')
  }, [doc, picked])

  return (
    <div className={cn('grid gap-2', className)}>
      <div className="relative min-h-64 overflow-hidden rounded-xl border bg-[repeating-conic-gradient(var(--color-muted)_0%_25%,transparent_0%_50%)] bg-[length:20px_20px]">
        <iframe
          ref={frame}
          title="The drawing"
          sandbox="allow-same-origin"
          srcDoc={page(markup)}
          className="block aspect-[4/3] max-h-[62vh] w-full"
          onLoad={(e) => {
            const d = e.currentTarget.contentDocument as Doc | null
            started.current = performance.now()
            setTime(0)
            setPlaying(true)
            setDoc(d)
          }}
        />
      </div>
      {seconds > 0 && (
        <div className="flex items-center gap-2">
          <Button type="button" variant="ghost" size="icon-sm" aria-label={playing ? 'Pause' : 'Play'} onClick={() => (playing ? pause() : play())}>
            {playing ? <Pause /> : <Play />}
          </Button>
          <input
            type="range"
            min={0}
            max={seconds}
            step={0.01}
            value={Math.min(time, seconds)}
            aria-label="Time"
            className="min-w-0 flex-1 accent-primary"
            onChange={(e) => {
              pause()
              seekTo(Number(e.target.value))
            }}
          />
          <span className="w-20 text-right text-xs text-muted-foreground tabular-nums">
            {Math.min(time, seconds).toFixed(1)} / {seconds.toFixed(1)} s
          </span>
          {!loops && (
            <Button type="button" variant={loop ? 'secondary' : 'ghost'} size="icon-sm" aria-pressed={loop} aria-label="Play it again and again" onClick={() => setLoop(!loop)}>
              <Repeat />
            </Button>
          )}
        </div>
      )}
    </div>
  )
}
