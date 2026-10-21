import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Eye, Globe, Hand, Undo2, X } from 'lucide-react'
import { useEffect, useState, type MouseEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

interface BrowserStatus {
  open: boolean
  url?: string
  title?: string
  takenOver?: boolean
  width?: number
  height?: number
}

/** The screen's address, made new every so often so the picture keeps up. */
function useLive(chatId: string, every: number) {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    const t = setInterval(() => setTick((n) => n + 1), every)
    return () => clearInterval(t)
  }, [every])
  return `/api/chat/browser/${chatId}/screen?t=${tick}`
}

/** The page live, larger: the person's hand on it while they have it (clicks where they click, what they type, keys, scrolling). */
function LiveView({ chatId, status, onTake, onGive }: { chatId: string; status: BrowserStatus; onTake: () => void; onGive: () => void }) {
  const src = useLive(chatId, 700)
  const [text, setText] = useState('')
  const send = useMutation({
    mutationFn: (body: object) => api(`/api/chat/browser/${chatId}/input`, { body }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const click = (e: MouseEvent<HTMLButtonElement>) => {
    if (!status.takenOver) return
    const r = e.currentTarget.getBoundingClientRect()
    send.mutate({ kind: 'click', x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height })
  }
  return (
    <>
      <DialogHeader>
        <DialogTitle className="flex items-center gap-2">
          <Globe className="size-4" aria-hidden="true" /> {status.title || 'The browser'}
        </DialogTitle>
        <DialogDescription className="truncate font-mono text-xs">{status.url}</DialogDescription>
      </DialogHeader>
      <button
        type="button"
        onClick={click}
        disabled={!status.takenOver}
        aria-label={status.takenOver ? 'Click on the page where you point' : 'The browser’s screen'}
        className={`block w-full rounded-lg p-0 outline-none focus-visible:ring-[3px] focus-visible:ring-ring disabled:cursor-default ${status.takenOver ? 'cursor-pointer ring-2 ring-primary' : ''}`}
      >
        <img src={src} alt={`The browser's screen: ${status.title ?? ''}`} className="aspect-[8/5] w-full rounded-lg border bg-muted object-contain" />
      </button>
      {status.takenOver ? (
        <div className="grid gap-2">
          <p className="text-xs text-muted-foreground">You have the browser: click on the page, type, scroll. The agent waits until you hand it back. A password or a card number is yours to type here.</p>
          <form
            className="flex gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              if (!text) return
              send.mutate({ kind: 'type', text })
              setText('')
            }}
          >
            <Input type="password" autoComplete="off" value={text} onChange={(e) => setText(e.target.value)} placeholder="Type into the page (hidden here)" aria-label="Type into the page" />
            <Button type="submit" variant="outline" disabled={!text}>
              Type
            </Button>
          </form>
          <div className="flex flex-wrap gap-1.5">
            {['Enter', 'Tab', 'Escape', 'Backspace'].map((key) => (
              <Button key={key} size="sm" variant="outline" onClick={() => send.mutate({ kind: 'key', key })}>
                {key}
              </Button>
            ))}
            <Button size="sm" variant="outline" aria-label="Scroll up" onClick={() => send.mutate({ kind: 'scroll', dy: -600 })}>
              <ArrowUp />
            </Button>
            <Button size="sm" variant="outline" aria-label="Scroll down" onClick={() => send.mutate({ kind: 'scroll', dy: 600 })}>
              <ArrowDown />
            </Button>
            <span className="flex-1" />
            <Button size="sm" onClick={onGive}>
              <Undo2 /> Hand it back
            </Button>
          </div>
        </div>
      ) : (
        <div className="flex items-center gap-2">
          <p className="flex-1 text-xs text-muted-foreground">The agent is using it. Take it over to do a part yourself: signing in, paying, a choice only you can make.</p>
          <Button size="sm" onClick={onTake}>
            <Hand /> Take over
          </Button>
        </div>
      )}
    </>
  )
}

/**
 * Above the box while the chat's browser is open: its screen live, where it is, and Watch (larger, to take it over),
 * Take over or Hand back, and Close. Asked about only in a chat whose tools include the browser.
 */
export function BrowserBar({ chatId, active }: { chatId: string | null; active: boolean }) {
  const queryClient = useQueryClient()
  const key = ['chat', 'browser', chatId]
  const status = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => api<BrowserStatus>(`/api/chat/browser/${chatId}`, { signal }),
    enabled: !!chatId && active,
    refetchInterval: (q) => (q.state.data?.open ? 1500 : 4000),
  })
  const [watching, setWatching] = useState(false)
  const refresh = () => void queryClient.invalidateQueries({ queryKey: key })
  const act = useMutation({
    mutationFn: (what: 'take' | 'give' | 'close') =>
      what === 'close' ? api(`/api/chat/browser/${chatId}`, { method: 'DELETE' }) : api(`/api/chat/browser/${chatId}/${what}`, { body: {} }),
    onSuccess: (_, what) => {
      if (what === 'take') setWatching(true)
      if (what === 'close') setWatching(false)
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const thumb = useLive(chatId ?? 'none', 1500)
  const s = status.data
  if (!chatId || !s?.open) return null
  return (
    <section className="mb-2 flex items-center gap-3 rounded-xl border bg-card px-3 py-2 text-sm shadow-sm" aria-label="The chat's browser">
      <button type="button" className="shrink-0 rounded outline-none focus-visible:ring-[3px] focus-visible:ring-ring" onClick={() => setWatching(true)} aria-label="Watch the browser">
        <img src={thumb} alt="" className="h-10 w-16 rounded border bg-muted object-cover" />
      </button>
      <output className="min-w-0 flex-1" aria-live="polite">
        <span className="block truncate font-medium">{s.takenOver ? 'You have the browser' : s.title || 'The browser'}</span>
        <span className="block truncate font-mono text-xs text-muted-foreground">{s.url}</span>
      </output>
      <Button type="button" variant="outline" size="sm" className="h-7 gap-1 px-2" onClick={() => setWatching(true)}>
        <Eye /> Watch
      </Button>
      <Button type="button" variant={s.takenOver ? 'default' : 'outline'} size="sm" className="h-7 gap-1 px-2" onClick={() => act.mutate(s.takenOver ? 'give' : 'take')}>
        {s.takenOver ? (
          <>
            <Undo2 /> Hand back
          </>
        ) : (
          <>
            <Hand /> Take over
          </>
        )}
      </Button>
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Close the browser" onClick={() => act.mutate('close')}>
        <X />
      </Button>
      <Dialog open={watching} onOpenChange={setWatching}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-5xl">
          {watching && <LiveView chatId={chatId} status={s} onTake={() => act.mutate('take')} onGive={() => act.mutate('give')} />}
        </DialogContent>
      </Dialog>
    </section>
  )
}
