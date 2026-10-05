import { FitAddon } from '@xterm/addon-fit'
import { Terminal, type ITheme } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, Plus, RotateCw, SquareTerminal, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError, errorMessage } from '@/lib/api'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { closeTerminal, openTerminal, terminalSocketUrl, terminalsQuery, type TerminalInfo } from './ide-api'

// The page's colours around the ANSI palette editors use, for a light and a dark background.
const themes: Record<'light' | 'dark', ITheme> = {
  light: {
    background: '#fbfcfe',
    foreground: '#12161d',
    cursor: '#2474cf',
    cursorAccent: '#fbfcfe',
    selectionBackground: '#2474cf40',
    black: '#000000',
    red: '#cd3131',
    green: '#00a000',
    yellow: '#949800',
    blue: '#0451a5',
    magenta: '#bc05bc',
    cyan: '#0598bc',
    white: '#555555',
    brightBlack: '#666666',
    brightRed: '#cd3131',
    brightGreen: '#14ce14',
    brightYellow: '#b5ba00',
    brightBlue: '#0451a5',
    brightMagenta: '#bc05bc',
    brightCyan: '#0598bc',
    brightWhite: '#a5a5a5',
  },
  dark: {
    background: '#0c0e13',
    foreground: '#e9ebee',
    cursor: '#4c94ec',
    cursorAccent: '#0c0e13',
    selectionBackground: '#4c94ec4d',
    black: '#000000',
    red: '#cd3131',
    green: '#0dbc79',
    yellow: '#e5e510',
    blue: '#2472c8',
    magenta: '#bc3fbc',
    cyan: '#11a8cd',
    white: '#e5e5e5',
    brightBlack: '#666666',
    brightRed: '#f14c4c',
    brightGreen: '#23d18b',
    brightYellow: '#f5f543',
    brightBlue: '#3b8eea',
    brightMagenta: '#d670d6',
    brightCyan: '#29b8db',
    brightWhite: '#e5e5e5',
  },
}

const label = (t: TerminalInfo) => `${t.title} ${t.id}`

/**
 * The bottom panel's terminals: shells in the working directory, on real
 * pseudo-terminals that code-arena web runs; each tab is one, its screen kept
 * by the server (a page reloaded sees it again). The panel's height is the
 * workbench's (its top edge is dragged); the terminals follow it.
 */
export default function TerminalPanel({ onHide, focusKey }: { onHide: () => void; focusKey: number }) {
  const queryClient = useQueryClient()
  const list = useQuery(terminalsQuery)
  const [activeId, setActiveId] = useState<string | null>(null)
  const [opening, setOpening] = useState(false)
  const firstDone = useRef(false)
  const terminals = list.data ?? []
  const active = terminals.find((t) => t.id === activeId) ?? terminals.at(-1) ?? null
  const size = useRef({ cols: 80, rows: 24 })

  const add = useCallback(async () => {
    setOpening(true)
    try {
      const t = await openTerminal(size.current.cols, size.current.rows)
      queryClient.setQueryData<TerminalInfo[]>(terminalsQuery.queryKey, (all) => [...(all ?? []), t])
      setActiveId(t.id)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setOpening(false)
    }
  }, [queryClient])

  const kill = async (t: TerminalInfo) => {
    try {
      await closeTerminal(t.id)
    } catch (e) {
      // Already gone (it was closed in another tab): it leaves this list as well.
      if (!(e instanceof ApiError && e.http === 404)) {
        toast.error(errorMessage(e))
        return
      }
    }
    const left = terminals.filter((x) => x.id !== t.id)
    queryClient.setQueryData<TerminalInfo[]>(terminalsQuery.queryKey, left)
    if (active?.id === t.id) setActiveId(left.at(-1)?.id ?? null)
    if (left.length === 0) onHide()
  }

  // Shown with no terminal: one opens, as an editor's panel does.
  useEffect(() => {
    if (!list.data || firstDone.current) return
    if (list.data.length > 0) {
      firstDone.current = true
      return
    }
    const soon = setTimeout(() => {
      firstDone.current = true
      void add()
    })
    return () => clearTimeout(soon)
  }, [list.data, add])

  return (
    <section aria-label="Terminal" className="flex h-full min-h-0 flex-col bg-background">
      <div className="flex h-9 shrink-0 items-center gap-1 border-b pr-1.5 pl-3">
        <h2 className="mr-2 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Terminal</h2>
        <div role="tablist" aria-label="Terminals" className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto">
          {terminals.map((t) => {
            const selected = t.id === active?.id
            return (
              <button
                key={t.id}
                type="button"
                role="tab"
                aria-selected={selected}
                aria-controls={`terminal-${t.id}`}
                onClick={() => setActiveId(t.id)}
                className={cn(
                  'flex h-6 shrink-0 items-center gap-1.5 rounded-sm px-2 text-xs outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring',
                  selected ? 'bg-accent text-foreground' : 'text-muted-foreground',
                )}
              >
                <SquareTerminal className="size-3.5" aria-hidden="true" />
                {label(t)}
                {t.exitCode !== null && <span className="text-muted-foreground">(ended)</span>}
              </button>
            )
          })}
        </div>
        <Tooltip content="New terminal">
          <Button variant="ghost" size="icon-sm" className="size-6" aria-label="New terminal" onClick={() => void add()} disabled={opening}>
            <Plus />
          </Button>
        </Tooltip>
        {active && (
          <Tooltip content="Close this terminal (its shell ends)">
            <Button variant="ghost" size="icon-sm" className="size-6" aria-label={`Close ${label(active)}`} onClick={() => void kill(active)}>
              <Trash2 />
            </Button>
          </Tooltip>
        )}
        <Tooltip content="Hide the panel (Ctrl+`)">
          <Button variant="ghost" size="icon-sm" className="size-6" aria-label="Hide the terminal" onClick={onHide}>
            <ChevronDown />
          </Button>
        </Tooltip>
      </div>
      <div className="relative min-h-0 flex-1">
        {(list.isPending || (opening && terminals.length === 0)) && (
          <div className="absolute inset-0 grid place-items-center">
            <Spinner label="Starting a terminal" />
          </div>
        )}
        {list.error && <p className="p-3 text-sm text-muted-foreground">{errorMessage(list.error)}</p>}
        {terminals.map((t) => (
          <XtermView
            key={t.id}
            terminal={t}
            active={t.id === active?.id}
            focusKey={focusKey}
            onSize={(cols, rows) => {
              size.current = { cols, rows }
            }}
            onExit={(code) => queryClient.setQueryData<TerminalInfo[]>(terminalsQuery.queryKey, (all) => all?.map((x) => (x.id === t.id ? { ...x, exitCode: code } : x)))}
          />
        ))}
      </div>
    </section>
  )
}

/**
 * One terminal: xterm.js on the server's socket. Keys go as {"type":"input"},
 * a new size as {"type":"resize"}; the shell's output comes as binary messages,
 * its end as {"type":"exit"}.
 */
function XtermView({ terminal, active, focusKey, onSize, onExit }: { terminal: TerminalInfo; active: boolean; focusKey: number; onSize: (cols: number, rows: number) => void; onExit: (code: number) => void }) {
  const host = useRef<HTMLDivElement>(null)
  const { resolved } = useTheme()
  const theme = useRef(resolved)
  const term = useRef<Terminal | null>(null)
  const fit = useRef<FitAddon | null>(null)
  const events = useRef({ onSize, onExit })
  const [state, setState] = useState<'connecting' | 'open' | 'ended' | 'lost'>('connecting')
  const [attempt, setAttempt] = useState(0)

  // Before the terminal is made (effects run in order): the colours and the callbacks it starts with.
  useEffect(() => {
    events.current = { onSize, onExit }
  }, [onSize, onExit])
  useEffect(() => {
    theme.current = resolved
    if (term.current) term.current.options.theme = themes[resolved]
  }, [resolved])

  useEffect(() => {
    const el = host.current
    if (!el) return
    const t = new Terminal({
      fontFamily: "'JetBrains Mono Variable', ui-monospace, 'SFMono-Regular', monospace",
      fontSize: 13,
      lineHeight: 1.2,
      cursorBlink: true,
      scrollback: 10_000,
      theme: themes[theme.current],
    })
    const f = new FitAddon()
    t.loadAddon(f)
    t.open(el)
    term.current = t
    fit.current = f
    let ended = false
    const ws = new WebSocket(terminalSocketUrl(terminal.id))
    ws.binaryType = 'arraybuffer'
    const send = (message: object) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message))
    }
    ws.onopen = () => {
      setState('open')
      send({ type: 'resize', cols: t.cols, rows: t.rows })
    }
    ws.onmessage = (e: MessageEvent) => {
      if (typeof e.data !== 'string') {
        t.write(new Uint8Array(e.data as ArrayBuffer))
        return
      }
      const message = JSON.parse(e.data) as { type: string; code?: number }
      if (message.type === 'exit') {
        ended = true
        setState('ended')
        t.write(`\r\n\x1b[2m[The shell ended with exit code ${message.code ?? 0}.]\x1b[0m\r\n`)
        events.current.onExit(message.code ?? 0)
      }
    }
    ws.onclose = () => {
      if (!ended) setState('lost')
    }
    const subscriptions = [
      t.onData((data) => send({ type: 'input', data })),
      // Mouse reports some programs ask for: bytes, as they are.
      t.onBinary((data) => {
        if (ws.readyState === WebSocket.OPEN) ws.send(Uint8Array.from(data, (c) => c.charCodeAt(0)))
      }),
      t.onResize(({ cols, rows }) => {
        events.current.onSize(cols, rows)
        send({ type: 'resize', cols, rows })
      }),
    ]
    // The panel resized (dragged, the window, the side panels): the terminal fits it, when it is shown.
    let frame = 0
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => {
        if (el.offsetParent !== null && el.clientWidth > 0) f.fit()
      })
    })
    observer.observe(el)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
      for (const s of subscriptions) s.dispose()
      ws.onclose = null
      ws.close()
      t.dispose()
      term.current = null
      fit.current = null
    }
  }, [terminal.id, attempt])

  // Shown: it fits the panel, and takes the keys.
  useEffect(() => {
    if (!active) return
    fit.current?.fit()
    term.current?.focus()
  }, [active, focusKey, attempt])

  return (
    <div id={`terminal-${terminal.id}`} role="tabpanel" aria-label={label(terminal)} hidden={!active} className="absolute inset-0 py-1 pl-3">
      <div ref={host} data-testid={`xterm-${terminal.id}`} className="h-full w-full" />
      {state === 'lost' && (
        <div className="absolute inset-x-3 bottom-3 flex items-center gap-2 rounded-md border bg-card px-3 py-2 text-sm shadow-md">
          <span className="min-w-0 flex-1 text-muted-foreground">Disconnected from this terminal: is code-arena web still running?</span>
          <Button size="sm" variant="outline" className="h-7" onClick={() => setAttempt((n) => n + 1)}>
            <RotateCw /> Reconnect
          </Button>
        </div>
      )}
    </div>
  )
}
