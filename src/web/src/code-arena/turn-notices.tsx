import { Bell, CircleCheck, CircleX, KeyRound, ShieldQuestion, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { tellDesktop } from '@/lib/desktop'
import { cn } from '@/lib/utils'

/**
 * What the agent tells the person while they look elsewhere: it waits for them (a tool to allow, a VM's sign-in), it is
 * ready (the turn ended), or it failed. Each is a bubble in the corner, answered there (Allow, Always or Deny; a reply), and a
 * desktop notification while the window is hidden.
 */
export type TurnNotice =
  | { id: string; kind: 'approval'; callId: string; title: string; detail?: string | null; always?: string | null }
  | { id: string; kind: 'sign-in'; title: string; detail?: string | null }
  | { id: string; kind: 'ready'; title: string; detail?: string | null }
  | { id: string; kind: 'failed'; title: string; detail?: string | null }

/** How long a bubble that asks nothing stays. */
const quietFor = 20_000

/** Whether the person sees the chat now: the window in front and focused, and the chat on screen. */
const looking = (chatShown: boolean) => typeof document !== 'undefined' && !document.hidden && document.hasFocus() && chatShown

export function useTurnNotices({
  chatShown,
  onDecide,
  onReply,
  onShowChat,
}: {
  chatShown: boolean
  onDecide: (callId: string, answer: 'allow' | 'always' | 'deny') => void
  onReply: (text: string) => void
  onShowChat?: () => void
}) {
  const [notices, setNotices] = useState<TurnNotice[]>([])
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  // The calls answered already: a turn replayed to the page (it came back to it) does not ask about them again.
  const done = useRef(new Set<string>())
  useEffect(() => {
    const all = timers.current
    return () => all.forEach((t) => clearTimeout(t))
  }, [])
  const drop = useCallback((id: string) => {
    setNotices((now) => now.filter((n) => n.id !== id))
    const t = timers.current.get(id)
    if (t) clearTimeout(t)
    timers.current.delete(id)
  }, [])
  /** Said to the person, unless they are looking at the chat (the thread says it already). */
  const notify = useCallback(
    (n: TurnNotice) => {
      if (looking(chatShown) || (n.kind === 'approval' && done.current.has(n.callId))) return
      setNotices((now) => [...now.filter((x) => x.id !== n.id), n].slice(-4))
      tellDesktop(n.title, n.detail, `code-arena-${n.kind}`, onShowChat)
      if (n.kind === 'ready' || n.kind === 'failed') timers.current.set(n.id, setTimeout(() => drop(n.id), quietFor))
    },
    [chatShown, drop, onShowChat],
  )
  /** A call answered (here, in the thread, or elsewhere): its bubble goes. */
  const answered = useCallback((callId: string) => {
    done.current.add(callId)
    setNotices((now) => now.filter((n) => n.kind !== 'approval' || n.callId !== callId))
  }, [])
  const bubbles: ReactNode =
    notices.length === 0 ? null : (
      <ol aria-label="Code Arena tells you" className="pointer-events-none fixed right-4 bottom-10 z-[60] grid w-80 gap-2">
        {notices.map((n) => (
          <Bubble
            key={n.id}
            notice={n}
            onClose={() => drop(n.id)}
            onDecide={(answer) => {
              if (n.kind === 'approval') {
                done.current.add(n.callId)
                onDecide(n.callId, answer)
              }
              drop(n.id)
            }}
            onReply={(text) => {
              onReply(text)
              drop(n.id)
            }}
            onOpen={() => {
              onShowChat?.()
              drop(n.id)
            }}
          />
        ))}
      </ol>
    )
  return { notify, answered, bubbles, notices }
}

function Bubble({
  notice: n,
  onClose,
  onDecide,
  onReply,
  onOpen,
}: {
  notice: TurnNotice
  onClose: () => void
  onDecide: (answer: 'allow' | 'always' | 'deny') => void
  onReply: (text: string) => void
  onOpen: () => void
}) {
  const [reply, setReply] = useState('')
  const Icon = n.kind === 'approval' ? ShieldQuestion : n.kind === 'sign-in' ? KeyRound : n.kind === 'ready' ? CircleCheck : n.kind === 'failed' ? CircleX : Bell
  const send = (e: FormEvent) => {
    e.preventDefault()
    if (reply.trim()) onReply(reply.trim())
  }
  return (
    <li
      role={n.kind === 'approval' || n.kind === 'sign-in' ? 'alertdialog' : 'status'}
      aria-label={n.title}
      className="pointer-events-auto grid animate-pop gap-2 rounded-xl border bg-popover p-3 text-sm text-popover-foreground shadow-lg"
    >
      <div className="flex items-start gap-2">
        <Icon className={cn('mt-0.5 size-4 shrink-0', n.kind === 'failed' ? 'text-destructive-ink' : n.kind === 'ready' ? 'text-success-ink' : 'text-warning-ink')} aria-hidden="true" />
        <button type="button" className="grid min-w-0 flex-1 gap-0.5 text-start" onClick={onOpen} aria-label={`${n.title}: open the chat`}>
          <span className="font-medium">{n.title}</span>
          {n.detail && <span className="line-clamp-3 text-xs text-muted-foreground">{n.detail}</span>}
        </button>
        <Button variant="ghost" size="icon-sm" className="-m-1 size-6 shrink-0" aria-label="Close" onClick={onClose}>
          <X />
        </Button>
      </div>
      {n.kind === 'approval' && (
        <div className="flex flex-wrap gap-1.5">
          <Button size="sm" onClick={() => onDecide('allow')}>
            Allow
          </Button>
          {n.always && (
            <Button size="sm" variant="outline" title={n.always} onClick={() => onDecide('always')}>
              Always
            </Button>
          )}
          <Button size="sm" variant="outline" onClick={() => onDecide('deny')}>
            Deny
          </Button>
        </div>
      )}
      {n.kind === 'ready' && (
        <form onSubmit={send} className="flex gap-1.5">
          <input
            value={reply}
            onChange={(e) => setReply(e.target.value)}
            placeholder="Reply…"
            aria-label="Reply to the agent"
            className="min-w-0 flex-1 rounded-md border bg-background px-2 py-1 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
          <Button type="submit" size="sm" disabled={!reply.trim()}>
            Send
          </Button>
        </form>
      )}
    </li>
  )
}
