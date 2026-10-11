import { useEffect, useRef, useState } from 'react'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useDebug } from './session'

/** ANSI colour codes are dropped: the console shows the text. */
const escape = new RegExp(`${String.fromCharCode(27)}\\[[0-9;]*[A-Za-z]`, 'g')
const plain = (text: string) => text.replace(escape, '')

/**
 * The Debug Console (Ctrl+Shift+Y), as VS Code's: the session's output (the program's, and what the debugger says), and
 * a box that evaluates an expression in the frame chosen while stopped (↑ and ↓ for what was typed before).
 */
export function DebugConsole() {
  const d = useDebug()
  const s = d.active
  const [lines, setLines] = useState<{ kind: 'in' | 'out' | 'error'; text: string }[]>([])
  const [typed, setTyped] = useState('')
  const history = useRef<string[]>([])
  const at = useRef(-1)
  const end = useRef<HTMLDivElement>(null)
  const output = s?.output ?? []
  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' })
  }, [output.length, lines.length])
  const evaluate = async () => {
    const expr = typed.trim()
    if (!expr || !s) return
    history.current = [expr, ...history.current.filter((x) => x !== expr)].slice(0, 100)
    at.current = -1
    setTyped('')
    setLines((l) => [...l, { kind: 'in', text: expr }])
    try {
      const r = await s.evaluate(expr, 'repl')
      setLines((l) => [...l, { kind: 'out', text: r.result }])
    } catch (e) {
      setLines((l) => [...l, { kind: 'error', text: errorMessage(e) }])
    }
  }
  return (
    <section aria-label="Debug Console" className="flex h-full min-h-0 flex-col">
      <div className="min-h-0 flex-1 overflow-auto px-3 py-1 font-mono text-xs whitespace-pre-wrap">
        {!s && <p className="text-muted-foreground">Start debugging (F5): the program's output and the debugger's show here, and expressions can be evaluated below.</p>}
        {output.map((o, i) => (
          <span key={i} className={cn(o.category === 'stderr' && 'text-destructive-ink', o.category === 'console' && 'text-muted-foreground', o.category === 'important' && 'font-semibold')}>
            {plain(o.text)}
          </span>
        ))}
        {lines.map((l, i) => (
          <div key={`r${i}`} className={cn(l.kind === 'in' && 'text-primary-ink', l.kind === 'error' && 'text-destructive-ink')}>
            {l.kind === 'in' ? `> ${l.text}` : l.text}
          </div>
        ))}
        <div ref={end} />
      </div>
      <div className="flex shrink-0 items-center gap-1 border-t px-2">
        <span className="font-mono text-xs text-primary-ink" aria-hidden="true">
          ›
        </span>
        <input
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              void evaluate()
            } else if (e.key === 'ArrowUp' && history.current.length > 0) {
              e.preventDefault()
              at.current = Math.min(at.current + 1, history.current.length - 1)
              setTyped(history.current[at.current]!)
            } else if (e.key === 'ArrowDown') {
              e.preventDefault()
              at.current = Math.max(at.current - 1, -1)
              setTyped(at.current < 0 ? '' : history.current[at.current]!)
            }
          }}
          disabled={!s || s.state !== 'stopped'}
          placeholder={s?.state === 'stopped' ? 'Evaluate an expression in the frame chosen' : 'Evaluates while the program is stopped'}
          aria-label="Expression to evaluate"
          className="h-7 min-w-0 flex-1 bg-transparent font-mono text-xs outline-none"
        />
      </div>
    </section>
  )
}
