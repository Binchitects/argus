import { useMutation, useQuery } from '@tanstack/react-query'
import { FileCode2, History, MessagesSquare, Rewind } from 'lucide-react'
import { useState } from 'react'
import { ChoiceCard } from '@/components/app/choice-card'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/api'
import type { Message } from '@/pages/chat/types'
import { rewindPreviewQuery, rewindSession } from './api'

type Back = 'both' | 'chat' | 'code'

const many = (n: number, one: string) => `${n.toLocaleString('en-US')} ${one}${n === 1 ? '' : 's'}`

/**
 * Rewind to here, as /rewind in the terminal (the same checkpoints): back to before the question, the conversation and the
 * code, only the conversation, or only the code. It shows first how many messages go and the files that would change.
 */
export function RewindDialog({ question, onClose, onRewound }: { question: Message | null; onClose: () => void; onRewound: (said: string, chat: boolean) => void }) {
  const open = !!question
  const preview = useQuery({ ...rewindPreviewQuery(question?.id ?? ''), enabled: open, staleTime: 0, gcTime: 0 })
  const [picked, setPicked] = useState<Back | null>(null)
  const code = preview.data?.code ?? false
  // Both, when the code can go back; else only the conversation.
  const back: Back = picked ?? (code ? 'both' : 'chat')
  const go = useMutation({
    mutationFn: () => rewindSession({ message: question!.id, chat: back !== 'code', code: back !== 'chat' }),
    onSuccess: (r) => {
      onRewound(r.said, back !== 'code')
      close()
    },
  })
  const close = () => {
    setPicked(null)
    go.reset()
    onClose()
  }
  const data = preview.data
  const failed = go.error ?? preview.error
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Rewind to here?</DialogTitle>
          <DialogDescription>Back to before this question, as /rewind does. The chat in Arena keeps the turns gone back from, as another branch.</DialogDescription>
        </DialogHeader>
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">What goes back</legend>
          <div className="grid gap-2 sm:grid-cols-3">
            <ChoiceCard name="rewind-back" checked={back === 'both'} onChange={() => setPicked('both')} disabled={!code} icon={History} title="The conversation and the code" hint="As before this question." />
            <ChoiceCard name="rewind-back" checked={back === 'chat'} onChange={() => setPicked('chat')} icon={MessagesSquare} title="Only the conversation" hint="The files stay as they are." />
            <ChoiceCard name="rewind-back" checked={back === 'code'} onChange={() => setPicked('code')} disabled={!code} icon={FileCode2} title="Only the code" hint="The conversation stays." />
          </div>
        </fieldset>
        {preview.isPending ? (
          <Skeleton className="h-16" />
        ) : (
          data && (
            <div className="grid gap-2 text-sm" aria-live="polite">
              {back !== 'code' && (
                <p>
                  <strong>{many(data.messages, 'message')}</strong> of the session go, this question too: its words go back in the box, to edit and send again.
                </p>
              )}
              {back !== 'chat' &&
                (data.files.length === 0 ? (
                  <p className="text-muted-foreground">No file changes: the files are as they were before this question.</p>
                ) : (
                  <div className="grid gap-1">
                    <p>{many(data.files.length, 'file')} would change:</p>
                    <ul className="grid max-h-48 gap-0.5 overflow-y-auto rounded-md border bg-muted/30 p-2 font-mono text-xs" aria-label="Files that would change">
                      {data.files.map((f) => (
                        <li key={f.path} className="flex items-center justify-between gap-2">
                          <span className="truncate" title={f.path}>
                            {f.path}
                          </span>
                          <span className="shrink-0 font-sans text-muted-foreground">{f.removed ? 'removed' : 'put back'}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                ))}
              {back !== 'chat' && data.lost.length > 0 && <p className="text-muted-foreground">Too big to keep, so they stay as they are: {data.lost.join(', ')}.</p>}
              {back !== 'chat' && <p className="text-xs text-muted-foreground">What commands changed is not put back: git has it.</p>}
              {!code && <p className="text-muted-foreground">Code Arena kept no checkpoint of that turn (it was before this run, or too long ago): only the conversation can go back.</p>}
            </div>
          )
        )}
        {failed && <p className="text-sm text-destructive-ink">{errorMessage(failed)}</p>}
        <DialogFooter>
          <Button variant="outline" onClick={close}>
            Cancel
          </Button>
          <Button loading={go.isPending} disabled={!data} onClick={() => go.mutate()}>
            <Rewind /> Rewind
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
