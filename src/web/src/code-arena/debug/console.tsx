import { useEffect, useRef, useState } from 'react'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { debug, useDebug, useDebugOutput } from './session'

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
  const [typed, setTyped] = useState('')
  const at = useRef(-1)
  const end = useRef<HTMLDivElement>(null)
  const output = useDebugOutput()
  const last = output.at(-1)
  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' })
  }, [last])
  // What is typed and what it gives go into the session's output, in turn with the program's, and stay when the panel
  // shows another tab.
  const evaluate = async () => {
    const expr = typed.trim()
    if (!expr || !s) return
    debug.replHistory = [expr, ...debug.replHistory.filter((x) => x !== expr)].slice(0, 100)
    at.current = -1
    setTyped('')
    s.write('repl.in', expr)
    // gdb takes the console's text as one of its own commands: an expression is evaluated as a watch is, and "-exec"
    // runs a gdb command (as cppdbg's console does).
    const command = expr.startsWith('-exec ')
    const context = s.start.adapter === 'gdb' && !command ? 'watch' : 'repl'
    try {
      const r = await s.evaluate(command ? expr.slice(6) : expr, context)
      s.write('repl.out', r.result)
    } catch (e) {
      s.write('repl.error', e instanceof Error ? e.message : errorMessage(e))
    }
  }
  return (
    <section aria-label="Debug Console" className="flex h-full min-h-0 flex-col">
      <div className="min-h-0 flex-1 overflow-auto px-3 py-1 font-mono text-xs whitespace-pre-wrap">
        {!s && <p className="text-muted-foreground">Start debugging (F5): the program's output and the debugger's show here, and expressions can be evaluated below.</p>}
        {output.map((o, i) =>
          o.category.startsWith('repl.') ? (
            <div key={i} className={cn(o.category === 'repl.in' && 'text-primary-ink', o.category === 'repl.error' && 'text-destructive-ink')}>
              {o.category === 'repl.in' ? `> ${o.text}` : o.text}
            </div>
          ) : (
            <span key={i} className={cn(o.category === 'stderr' && 'text-destructive-ink', o.category === 'console' && 'text-muted-foreground', o.category === 'important' && 'font-semibold')}>
              {plain(o.text)}
            </span>
          ),
        )}
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
            } else if (e.key === 'ArrowUp' && debug.replHistory.length > 0) {
              e.preventDefault()
              at.current = Math.min(at.current + 1, debug.replHistory.length - 1)
              setTyped(debug.replHistory[at.current]!)
            } else if (e.key === 'ArrowDown') {
              e.preventDefault()
              at.current = Math.max(at.current - 1, -1)
              setTyped(at.current < 0 ? '' : debug.replHistory[at.current]!)
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
