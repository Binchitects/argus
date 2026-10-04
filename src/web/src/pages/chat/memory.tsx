import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Brain, Loader2 } from 'lucide-react'
import { useState } from 'react'
import { MemoryManager } from '@/components/app/memory'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { memoriesQuery } from '@/lib/memory'
import { cn } from '@/lib/utils'
import type { MemoryOffer, Message } from './types'

const titles: Record<MemoryOffer['state'], string> = {
  offered: 'Remember this?',
  kept: 'Remembered',
  declined: 'Not remembered',
  forgotten: 'Forgotten',
}

/**
 * The model's remember call, as a card: what it asked to keep at once (Undo takes it back),
 * or what it offers, kept only when the person says so (they may change the words first).
 * Nothing waits on the server: the answer went on, and the choice can come later.
 */
export function MemoryCard({ result }: { result: Message }) {
  const queryClient = useQueryClient()
  const [card, setCard] = useState<MemoryOffer>(() => result.details!.memory!)
  const [words, setWords] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const decide = async (keep: boolean) => {
    setBusy(true)
    try {
      const now = await api<MemoryOffer>(`/api/account/memories/offers/${result.id}`, { body: { keep, ...(keep && words?.trim() ? { text: words.trim() } : {}) } })
      setCard(now)
      setWords(null)
      void queryClient.invalidateQueries({ queryKey: memoriesQuery.queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const asking = card.state === 'offered'
  return (
    <section aria-label={titles[card.state]} className={cn('my-2 flex animate-enter flex-wrap items-center gap-2 rounded-lg border px-3 py-2 text-sm', asking ? 'border-primary/40 bg-primary/5' : 'bg-card')}>
      <Brain className={cn('size-4 shrink-0', asking ? 'text-primary-ink' : 'text-muted-foreground')} aria-hidden="true" />
      <span className="shrink-0 font-medium">{titles[card.state]}</span>
      {words !== null ? (
        <Input dir="auto" className="h-8 min-w-48 flex-1" value={words} onChange={(e) => setWords(e.target.value)} aria-label="What to remember" />
      ) : (
        <span dir="auto" className={cn('min-w-0 flex-1 [overflow-wrap:anywhere]', card.state === 'declined' || card.state === 'forgotten' ? 'text-muted-foreground line-through' : '')}>
          {card.text}
        </span>
      )}
      <span className="ml-auto flex shrink-0 items-center gap-1.5">
        {busy && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-hidden="true" />}
        {asking ? (
          <>
            {words === null && (
              <Button size="sm" variant="ghost" className="h-7" disabled={busy} onClick={() => setWords(card.text)}>
                Edit
              </Button>
            )}
            <Button size="sm" variant="outline" className="h-7" disabled={busy} onClick={() => void decide(false)}>
              No thanks
            </Button>
            <Button size="sm" className="h-7" disabled={busy} onClick={() => void decide(true)}>
              Remember
            </Button>
          </>
        ) : card.state === 'kept' ? (
          <Button size="sm" variant="ghost" className="h-7" disabled={busy} onClick={() => void decide(false)}>
            Undo
          </Button>
        ) : (
          <Button size="sm" variant="ghost" className="h-7" disabled={busy} onClick={() => void decide(true)}>
            Remember after all
          </Button>
        )}
      </span>
    </section>
  )
}

/** The chat's Memory button: what it remembers about the person, to see and change without leaving the chat. */
export function MemoryButton() {
  const view = useQuery(memoriesQuery)
  const [open, setOpen] = useState(false)
  // Only where memory is on for everyone (the person may have turned theirs off: they turn it on here).
  if (!view.data?.enabled) return null
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <Tooltip content="What the chat remembers about you">
        <Button variant="ghost" size="icon-sm" onClick={() => setOpen(true)} aria-label={`Memory (${view.data.memories.length})`}>
          <Brain className={view.data.on ? undefined : 'opacity-50'} />
        </Button>
      </Tooltip>
      <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>Memory</DialogTitle>
          <DialogDescription>What the chat remembers about you, for every answer in every chat. Nobody else sees it, admins included.</DialogDescription>
        </DialogHeader>
        <MemoryManager />
      </DialogContent>
    </Dialog>
  )
}
