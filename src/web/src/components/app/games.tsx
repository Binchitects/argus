import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'

/*
 * Something to do while a picture or a video is made: small games behind the waiting cards (five quick clicks), and a
 * few other surprises. Nothing here is sent anywhere; best scores stay in this browser.
 */

export type Game = 'snake' | 'stars'

function best(game: Game): number {
  try {
    return Number(localStorage.getItem(`arena.game.${game}.best`) ?? 0) || 0
  } catch {
    return 0
  }
}

function keepBest(game: Game, score: number) {
  try {
    if (score > best(game)) localStorage.setItem(`arena.game.${game}.best`, String(score))
  } catch {
    // A private window keeps nothing: the game plays all the same.
  }
}

type Dir = 'up' | 'down' | 'left' | 'right'
const keys: Record<string, Dir> = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', w: 'up', s: 'down', a: 'left', d: 'right' }
const opposite: Record<Dir, Dir> = { up: 'down', down: 'up', left: 'right', right: 'left' }

/** The colours the canvas draws with, read from the page's theme. */
function colours(el: HTMLElement) {
  const s = getComputedStyle(el)
  const v = (name: string, fallback: string) => s.getPropertyValue(name).trim() || fallback
  return { bg: v('--color-muted', '#1f2430'), fg: v('--color-primary', '#3b82f6'), accent: v('--color-warning', '#f5a524'), text: v('--color-foreground', '#e5e7eb') }
}

/** Snake on a grid of 20×20: eat, grow, never bite yourself or the wall. Arrows or WASD; the buttons for touch. */
function Snake({ onScore }: { onScore: (n: number) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const dir = useRef<Dir>('right')
  const queued = useRef<Dir[]>([])
  const [state, setState] = useState<'ready' | 'playing' | 'over'>('ready')
  const [score, setScore] = useState(0)
  const N = 20

  const turn = useCallback((d: Dir) => {
    const last = queued.current.at(-1) ?? dir.current
    if (d !== last && d !== opposite[last] && queued.current.length < 3) queued.current.push(d)
    setState((s) => (s === 'ready' ? 'playing' : s))
  }, [])

  useEffect(() => {
    if (state !== 'playing') return
    const el = canvas.current!
    const ctx = el.getContext('2d')
    if (!ctx) return
    const c = colours(el)
    let body = [{ x: 5, y: 10 }, { x: 4, y: 10 }, { x: 3, y: 10 }]
    const place = () => {
      for (;;) {
        const f = { x: Math.floor(Math.random() * N), y: Math.floor(Math.random() * N) }
        if (!body.some((p) => p.x === f.x && p.y === f.y)) return f
      }
    }
    let food = place()
    let eaten = 0
    dir.current = 'right'
    const cell = el.width / N
    const draw = () => {
      ctx.fillStyle = c.bg
      ctx.fillRect(0, 0, el.width, el.height)
      ctx.fillStyle = c.accent
      ctx.beginPath()
      ctx.arc((food.x + 0.5) * cell, (food.y + 0.5) * cell, cell * 0.38, 0, Math.PI * 2)
      ctx.fill()
      body.forEach((p, i) => {
        ctx.fillStyle = c.fg
        ctx.globalAlpha = i === 0 ? 1 : Math.max(0.45, 1 - i / (body.length + 6))
        ctx.fillRect(p.x * cell + 1, p.y * cell + 1, cell - 2, cell - 2)
      })
      ctx.globalAlpha = 1
    }
    draw()
    const tick = () => {
      dir.current = queued.current.shift() ?? dir.current
      const h = body[0]
      const next = { x: h.x + (dir.current === 'left' ? -1 : dir.current === 'right' ? 1 : 0), y: h.y + (dir.current === 'up' ? -1 : dir.current === 'down' ? 1 : 0) }
      if (next.x < 0 || next.y < 0 || next.x >= N || next.y >= N || body.some((p) => p.x === next.x && p.y === next.y)) {
        setState('over')
        onScore(eaten)
        return
      }
      body = [next, ...body]
      if (next.x === food.x && next.y === food.y) {
        eaten += 1
        setScore(eaten)
        food = place()
      } else {
        body.pop()
      }
      draw()
      timer = window.setTimeout(tick, Math.max(60, 140 - eaten * 3))
    }
    let timer = window.setTimeout(tick, 140)
    return () => window.clearTimeout(timer)
  }, [state, onScore])

  // The keys, while the game has the focus (its dialog).
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = box.current
    if (!el) return
    const onKey = (e: KeyboardEvent) => {
      const d = keys[e.key]
      if (d) {
        e.preventDefault()
        turn(d)
      } else if ((e.key === ' ' || e.key === 'Enter') && state !== 'playing' && e.target === canvas.current) {
        e.preventDefault()
        setScore(0)
        queued.current = []
        setState('playing')
      }
    }
    el.addEventListener('keydown', onKey)
    return () => el.removeEventListener('keydown', onKey)
  }, [turn, state])

  return (
    <div ref={box} role="application" aria-label="Snake" className="grid justify-items-center gap-3">
      <div className="relative">
        <canvas ref={canvas} width={320} height={320} className="block max-w-full rounded-lg border bg-muted" aria-label={`Snake, score ${score}`} tabIndex={0} autoFocus />
        {state !== 'playing' && (
          <div className="absolute inset-0 grid place-items-center rounded-lg bg-background/70 text-center text-sm">
            <div className="grid gap-2">
              <p className="font-medium">{state === 'over' ? `Game over: ${score}` : 'Snake'}</p>
              <p className="text-muted-foreground">Arrows or WASD to steer.</p>
              <Button
                size="sm"
                onClick={() => {
                  setScore(0)
                  queued.current = []
                  setState('playing')
                }}
              >
                {state === 'over' ? 'Again' : 'Start'}
              </Button>
            </div>
          </div>
        )}
      </div>
      <div className="grid grid-cols-3 gap-1 sm:hidden" aria-label="Steer">
        <span />
        <Button variant="outline" size="icon" aria-label="Up" onClick={() => turn('up')}>↑</Button>
        <span />
        <Button variant="outline" size="icon" aria-label="Left" onClick={() => turn('left')}>←</Button>
        <Button variant="outline" size="icon" aria-label="Down" onClick={() => turn('down')}>↓</Button>
        <Button variant="outline" size="icon" aria-label="Right" onClick={() => turn('right')}>→</Button>
      </div>
      <p className="text-sm tabular-nums" aria-live="polite">
        Score {score}
      </p>
    </div>
  )
}

/** Catch the falling stars with the basket, miss the stones; three misses and it is over. Arrows, A/D or the pointer. */
function Stars({ onScore }: { onScore: (n: number) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [state, setState] = useState<'ready' | 'playing' | 'over'>('ready')
  const [score, setScore] = useState(0)
  const [lives, setLives] = useState(3)
  const basket = useRef(0.5)
  const held = useRef<{ left: boolean; right: boolean }>({ left: false, right: false })

  useEffect(() => {
    if (state !== 'playing') return
    const el = canvas.current!
    const ctx = el.getContext('2d')
    if (!ctx) return
    const c = colours(el)
    const W = el.width
    const H = el.height
    type Drop = { x: number; y: number; v: number; star: boolean }
    let drops: Drop[] = []
    let caught = 0
    let missed = 0
    let last = performance.now()
    let spawn = 0
    let frame = 0
    const loop = (t: number) => {
      const dt = Math.min(0.05, (t - last) / 1000)
      last = t
      if (held.current.left) basket.current = Math.max(0.08, basket.current - dt * 0.9)
      if (held.current.right) basket.current = Math.min(0.92, basket.current + dt * 0.9)
      spawn -= dt
      if (spawn <= 0) {
        drops.push({ x: 0.05 + Math.random() * 0.9, y: -0.05, v: 0.25 + Math.random() * 0.2 + caught * 0.008, star: Math.random() > 0.22 })
        spawn = Math.max(0.35, 1 - caught * 0.02)
      }
      const bx = basket.current
      drops = drops.filter((d) => {
        d.y += d.v * dt
        if (d.y > 0.88 && d.y < 0.95 && Math.abs(d.x - bx) < 0.09) {
          if (d.star) {
            caught += 1
            setScore(caught)
          } else {
            missed += 1
            setLives(3 - missed)
          }
          return false
        }
        if (d.y > 1.02) {
          if (d.star) {
            missed += 1
            setLives(3 - missed)
          }
          return false
        }
        return true
      })
      ctx.fillStyle = c.bg
      ctx.fillRect(0, 0, W, H)
      for (const d of drops) {
        ctx.fillStyle = d.star ? c.accent : c.text
        ctx.globalAlpha = d.star ? 1 : 0.55
        ctx.beginPath()
        if (d.star) {
          for (let i = 0; i < 10; i++) {
            const r = i % 2 === 0 ? 9 : 4
            const a = (Math.PI / 5) * i - Math.PI / 2
            ctx.lineTo(d.x * W + Math.cos(a) * r, d.y * H + Math.sin(a) * r)
          }
        } else {
          ctx.arc(d.x * W, d.y * H, 7, 0, Math.PI * 2)
        }
        ctx.fill()
      }
      ctx.globalAlpha = 1
      ctx.fillStyle = c.fg
      ctx.fillRect(bx * W - 0.09 * W, 0.9 * H, 0.18 * W, 8)
      if (missed >= 3) {
        setState('over')
        onScore(caught)
        return
      }
      frame = requestAnimationFrame(loop)
    }
    frame = requestAnimationFrame(loop)
    return () => cancelAnimationFrame(frame)
  }, [state, onScore])

  const start = () => {
    setScore(0)
    setLives(3)
    basket.current = 0.5
    setState('playing')
  }
  // The keys, while the game has the focus (its dialog): held to move.
  const box = useRef<HTMLDivElement>(null)
  const startRef = useRef(start)
  useEffect(() => {
    startRef.current = start
  })
  useEffect(() => {
    const el = box.current
    if (!el) return
    const key = (down: boolean) => (e: KeyboardEvent) => {
      if (e.key === 'ArrowLeft' || e.key === 'a') held.current.left = down
      else if (e.key === 'ArrowRight' || e.key === 'd') held.current.right = down
      else if (down && (e.key === ' ' || e.key === 'Enter') && state !== 'playing' && e.target === canvas.current) startRef.current()
      else return
      e.preventDefault()
    }
    const press = key(true)
    const release = key(false)
    el.addEventListener('keydown', press)
    el.addEventListener('keyup', release)
    return () => {
      el.removeEventListener('keydown', press)
      el.removeEventListener('keyup', release)
    }
  }, [state])

  return (
    <div ref={box} role="application" aria-label="Catch the stars" className="grid justify-items-center gap-3">
      <div className="relative">
        <canvas
          ref={canvas}
          width={320}
          height={320}
          className="block max-w-full touch-none rounded-lg border bg-muted"
          aria-label={`Catch the stars, ${score} caught, ${lives} lives`}
          tabIndex={0}
          autoFocus
          onPointerMove={(e) => {
            const r = e.currentTarget.getBoundingClientRect()
            basket.current = Math.min(0.92, Math.max(0.08, (e.clientX - r.left) / r.width))
          }}
        />
        {state !== 'playing' && (
          <div className="absolute inset-0 grid place-items-center rounded-lg bg-background/70 text-center text-sm">
            <div className="grid gap-2">
              <p className="font-medium">{state === 'over' ? `${score} stars caught` : 'Catch the stars'}</p>
              <p className="text-muted-foreground">Arrows, A and D, or the pointer. Stones cost a life, so does a star missed.</p>
              <Button size="sm" onClick={start}>
                {state === 'over' ? 'Again' : 'Start'}
              </Button>
            </div>
          </div>
        )}
      </div>
      <p className="text-sm tabular-nums" aria-live="polite">
        {score} caught · {'★'.repeat(Math.max(0, lives))}
        <span className="text-muted-foreground">{'☆'.repeat(Math.max(0, 3 - lives))}</span>
      </p>
    </div>
  )
}

/** A game in a dialog, its best score in this browser. */
export function GameDialog({ game, onClose }: { game: Game | null; onClose: () => void }) {
  // Read again after each game (kept in this browser): the count only makes it read.
  const [, setGames] = useState(0)
  const record = game ? best(game) : 0
  const onScore = useCallback(
    (n: number) => {
      if (!game) return
      keepBest(game, n)
      setGames((g) => g + 1)
    },
    [game],
  )
  return (
    <Dialog open={game !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="w-auto max-w-[min(92vw,26rem)]">
        <DialogTitle>{game === 'stars' ? 'Catch the stars' : 'Snake'}</DialogTitle>
        <DialogDescription>While it is being made. Best in this browser: {record}.</DialogDescription>
        {game === 'snake' && <Snake onScore={onScore} />}
        {game === 'stars' && <Stars onScore={onScore} />}
      </DialogContent>
    </Dialog>
  )
}
