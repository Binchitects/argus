import { useMutation, useQuery } from '@tanstack/react-query'
import { MessageSquareText, Pencil, Rewind } from 'lucide-react'
import { useState } from 'react'
import { ChoiceCard } from '@/components/app/choice-card'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/api'
import { rewindChat, rewindPreviewQuery } from './api'
import type { Message, Rewound } from './types'

const messages = (n: number) => `${n.toLocaleString('en-US')} ${n === 1 ? 'message' : 'messages'}`

/**
 * Rewind to here, as Claude Code's rewind goes back to a message: the chat goes back to just before the question (its words
 * back in the box) or to just after its answer. The confirmation says how many messages go from what the model reads (they
 * stay as another version), and offers to forget the memories written from them.
 */
export function RewindDialog({ chatId, question, onClose, onRewound }: { chatId: string; question: Message | null; onClose: () => void; onRewound: (r: Rewound, keep: boolean) => void }) {
  const open = !!question
  const before = useQuery({ ...rewindPreviewQuery(chatId, question?.id ?? '', false), enabled: open, staleTime: 0, gcTime: 0 })
  const after = useQuery({ ...rewindPreviewQuery(chatId, question?.id ?? '', true), enabled: open, staleTime: 0, gcTime: 0 })
  const [keep, setKeep] = useState(false)
  const [forget, setForget] = useState(false)
  const chosen = keep ? after : before
  const memories = chosen.data?.memories ?? []
  const go = useMutation({
    mutationFn: () => rewindChat(chatId, { messageId: question!.id, keep, forget: forget && memories.length > 0 }),
    onSuccess: (r) => {
      onRewound(r, keep)
      close()
    },
  })
  const close = () => {
    setKeep(false)
    setForget(false)
    go.reset()
    onClose()
  }
  const failed = go.error ?? chosen.error
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Rewind to here?</DialogTitle>
          <DialogDescription>
            The chat goes back to this question, and the model reads it from there. What goes leaves the chat the model reads; it stays as another version, which the arrows show once you ask something else there.
          </DialogDescription>
        </DialogHeader>
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">Back to</legend>
          <div className="grid gap-2 sm:grid-cols-2">
            <ChoiceCard
              name="rewind-to"
              checked={!keep}
              onChange={() => setKeep(false)}
              icon={Pencil}
              title="Before this question"
              hint={before.data ? `${messages(before.data.gone)} go, this question too: its words go back in the box, to edit and send again.` : 'This question and what came after it go.'}
            />
            <ChoiceCard
              name="rewind-to"
              checked={keep}
              onChange={() => setKeep(true)}
              disabled={after.data?.gone === 0}
              icon={MessageSquareText}
              title="After its answer"
              hint={after.data ? (after.data.gone === 0 ? 'Nothing comes after its answer.' : `${messages(after.data.gone)} go: this question and its answer stay.`) : 'This question and its answer stay.'}
            />
          </div>
        </fieldset>
        {chosen.isPending ? (
          <Skeleton className="h-5 w-48" />
        ) : (
          chosen.data && (
            <p className="text-sm" aria-live="polite">
              <strong>{messages(chosen.data.gone)}</strong> leave the chat the model reads. Undo puts them back while its note shows.
            </p>
          )
        )}
        {memories.length > 0 && (
          <div className="grid gap-2 rounded-lg border bg-muted/30 p-3 text-sm">
            <label className="flex items-center gap-2">
              <Checkbox checked={forget} onCheckedChange={(v) => setForget(!!v)} />
              Forget the {memories.length === 1 ? 'memory' : `${memories.length} memories`} written from them too (Undo does not bring {memories.length === 1 ? 'it' : 'them'} back)
            </label>
            <ul className="grid list-disc gap-1 pl-9 text-muted-foreground">
              {memories.map((m) => (
                <li key={m.id} dir="auto">
                  {m.text}
                  {m.proposed && <span className="text-xs"> (proposed)</span>}
                </li>
              ))}
            </ul>
          </div>
        )}
        {failed && <p className="text-sm text-destructive-ink">{errorMessage(failed)}</p>}
        <DialogFooter>
          <Button variant="outline" onClick={close}>
            Cancel
          </Button>
          <Button loading={go.isPending} disabled={!chosen.data || chosen.data.gone === 0} onClick={() => go.mutate()}>
            <Rewind /> Rewind
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
