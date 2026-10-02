import { RotateCw, ShieldAlert, TriangleAlert } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { useTheme } from '@/lib/theme'
import { isFromRunner, previewLabels, type PreviewKind, type ToRunner } from '@/preview/kind'

/**
 * Code the model wrote, running: in a frame whose page (/preview.html) the
 * server sandboxes into an origin of its own, with no network. The frame is
 * sandboxed here too, so neither side alone makes it safe.
 */
export function LivePreview({ kind, code, name }: { kind: PreviewKind; code: string; name: string }) {
  const { resolved } = useTheme()
  const [run, setRun] = useState(0)
  // A new version, theme or run is a new frame, with its own state.
  return <Frame key={`${run}:${resolved}:${kind}:${hash(code)}`} kind={kind} code={code} name={name} theme={resolved} onRunAgain={() => setRun((r) => r + 1)} />
}

function Frame({ kind, code, name, theme, onRunAgain }: { kind: PreviewKind; code: string; name: string; theme: 'light' | 'dark'; onRunAgain: () => void }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [state, setState] = useState<{ shown: boolean; errors: string[]; blocked: string[] }>({ shown: false, errors: [], blocked: [] })

  // The runner's origin is opaque: there is no name to address it by but '*'. The code is no secret.
  // Sent on "ready" and again on load (whichever comes first wins; the runner draws once).
  const send = useCallback(() => frame.current?.contentWindow?.postMessage({ type: 'render', kind, code, theme } satisfies ToRunner, '*'), [kind, code, theme])

  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.source !== frame.current?.contentWindow || !isFromRunner(e.data)) return
      const m = e.data
      if (m.type === 'ready') {
        send()
      } else if (m.type === 'rendered') {
        setState((s) => ({ ...s, shown: true }))
      } else if (m.type === 'error') {
        setState((s) => (s.errors.includes(m.message) ? s : { ...s, shown: true, errors: [...s.errors, m.message].slice(-5) }))
      } else if (m.type === 'blocked') {
        setState((s) => (s.blocked.includes(m.uri) ? s : { ...s, blocked: [...s.blocked, m.uri].slice(-5) }))
      }
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [send])

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2">
      <div className="relative min-h-80 flex-1 overflow-hidden rounded-lg border bg-white dark:bg-[#0b0b0c]">
        <iframe ref={frame} onLoad={send} src="/preview.html" title={`Preview of ${name}`} sandbox="allow-scripts allow-forms allow-modals" className="absolute inset-0 size-full" />
        {!state.shown && (
          <div className="absolute inset-0 flex items-center justify-center bg-card/60">
            <Spinner label={`Drawing the ${previewLabels[kind]}`} />
          </div>
        )}
      </div>
      <div className="flex items-start gap-2">
        <div className="grid min-w-0 flex-1 gap-1.5">
          {state.errors.map((e) => (
            <p key={e} role="alert" className="flex gap-1.5 text-xs text-destructive-ink">
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">{e}</span>
            </p>
          ))}
          {state.blocked.length > 0 && (
            <output className="flex gap-1.5 text-xs text-muted-foreground">
              <ShieldAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-all">Previews have no network, so this was not loaded: {state.blocked.join(', ')}</span>
            </output>
          )}
        </div>
        <Button variant="ghost" size="sm" className="h-7 shrink-0 gap-1 px-2 text-xs" onClick={onRunAgain}>
          <RotateCw /> Run again
        </Button>
      </div>
    </div>
  )
}

/** A short fingerprint of the code: a new version reloads the frame. */
function hash(s: string): string {
  let h = 5381
  for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) | 0
  return (h >>> 0).toString(36)
}

/**
 * A diagram drawn where it is written: in the answer, by the same sandboxed runner,
 * in a frame as tall as the drawing. What went wrong goes to onError (the code is shown instead).
 */
export function InlineDiagram({ code, title, onError }: { code: string; title: string; onError: (message: string) => void }) {
  const { resolved } = useTheme()
  return <InlineFrame key={`${resolved}:${hash(code)}`} code={code} title={title} theme={resolved} onError={onError} />
}

/** Taller than this, the drawing scrolls in its frame. */
const INLINE_MAX = 1200

function InlineFrame({ code, title, theme, onError }: { code: string; title: string; theme: 'light' | 'dark'; onError: (message: string) => void }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState<number | null>(null)
  const [shown, setShown] = useState(false)
  const send = useCallback(() => frame.current?.contentWindow?.postMessage({ type: 'render', kind: 'mermaid', code, theme, inline: true } satisfies ToRunner, '*'), [code, theme])
  const failed = useRef(onError)
  useEffect(() => {
    failed.current = onError
  })

  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.source !== frame.current?.contentWindow || !isFromRunner(e.data)) return
      const m = e.data
      if (m.type === 'ready') send()
      else if (m.type === 'rendered') setShown(true)
      else if (m.type === 'size') setHeight(Math.min(Math.max(m.height, 48), INLINE_MAX))
      else if (m.type === 'error') failed.current(m.message)
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [send])

  return (
    <div className="relative" style={{ height: height ?? 160 }}>
      <iframe ref={frame} onLoad={send} src="/preview.html" title={title} sandbox="allow-scripts allow-forms allow-modals" className="absolute inset-0 size-full" style={{ colorScheme: theme }} />
      {!shown && (
        <div className="absolute inset-0 flex items-center justify-center">
          <Spinner label="Drawing the diagram" />
        </div>
      )}
    </div>
  )
}
