import { useQueryClient } from '@tanstack/react-query'
import { ThumbsDown, ThumbsUp } from 'lucide-react'
import { useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Textarea } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { conversationQuery } from './api'
import { feedbackReasons } from './quality'
import type { Conversation, Feedback, FeedbackReason, Message } from './types'

interface Rating {
  up: boolean
  reason?: FeedbackReason | null
  comment?: string | null
  share?: boolean
}

/**
 * Thumbs up or down on an answer (its last message). Down is kept at once, and asks
 * why: a reason, words, and whether to share the chat with the admins. The thumb
 * that is on takes the rating back.
 */
export function FeedbackButtons({ chatId, message }: { chatId: string; message: Message }) {
  const queryClient = useQueryClient()
  const [mine, setMine] = useState<Feedback | null>(message.feedback ?? null)
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState<FeedbackReason | null>(message.feedback?.reason ?? null)
  const [comment, setComment] = useState(message.feedback?.comment ?? '')
  const [share, setShare] = useState(message.feedback?.shared ?? false)
  const [busy, setBusy] = useState(false)
  const shareId = useId()
  const url = `/api/chat/conversations/${chatId}/messages/${message.id}/feedback`

  const save = async (rating: Rating | null): Promise<boolean> => {
    setBusy(true)
    try {
      const saved = rating ? await api<Feedback>(url, { method: 'PUT', body: rating }) : (await api(url, { method: 'DELETE' }), null)
      setMine(saved)
      // The chat's copy too: the answer shows it again after a branch switch or a reload.
      queryClient.setQueryData<Conversation>(conversationQuery(chatId).queryKey, (c) =>
        c ? { ...c, messages: c.messages.map((m) => (m.id === message.id ? { ...m, feedback: saved } : m)) } : c,
      )
      return true
    } catch (e) {
      toast.error(errorMessage(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  const down = mine && !mine.up
  return (
    <span className="flex items-center">
      <Tooltip content={mine?.up ? 'You liked this answer: take it back' : 'Good answer'}>
        <Button
          variant="ghost"
          size="icon-sm"
          className={cn('size-7', mine?.up && 'text-primary-ink')}
          aria-label="Good answer"
          aria-pressed={!!mine?.up}
          disabled={busy}
          onClick={() => void save(mine?.up ? null : { up: true })}
        >
          <ThumbsUp className={cn(mine?.up && 'fill-current')} />
        </Button>
      </Tooltip>
      <Popover
        open={open}
        onOpenChange={(o) => {
          setOpen(o)
          // Down at once; the reason is extra.
          if (o && !down) void save({ up: false })
        }}
      >
        <Tooltip content={down ? 'You rated this answer down: say why, or take it back' : 'Bad answer'}>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon-sm" className={cn('size-7', down && 'text-destructive-ink')} aria-label="Bad answer" aria-pressed={!!down} disabled={busy && !open}>
              <ThumbsDown className={cn(down && 'fill-current')} />
            </Button>
          </PopoverTrigger>
        </Tooltip>
        <PopoverContent align="start" className="grid w-80 gap-3" aria-label="What was wrong">
          <div>
            <p className="text-sm font-medium">What was wrong?</p>
            <p className="text-xs text-muted-foreground">It helps the admins choose and tune the models.</p>
          </div>
          <fieldset className="m-0 flex min-w-0 flex-wrap gap-1.5 border-0 p-0">
            <legend className="sr-only">Reason</legend>
            {feedbackReasons.map((r) => (
              <Button
                key={r.value}
                type="button"
                variant={reason === r.value ? 'secondary' : 'outline'}
                size="sm"
                className={cn('h-7 rounded-full px-2.5 text-xs', reason === r.value && 'text-primary-ink')}
                aria-pressed={reason === r.value}
                onClick={() => setReason(reason === r.value ? null : r.value)}
              >
                {r.label}
              </Button>
            ))}
          </fieldset>
          <Textarea dir="auto" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} placeholder="Tell more (optional)" aria-label="Tell more" className="min-h-16 text-sm" />
          <div className="flex items-start gap-2 text-sm">
            <Checkbox id={shareId} checked={share} onCheckedChange={(v) => setShare(v === true)} className="mt-0.5" aria-describedby={`${shareId}-hint`} />
            <span className="grid gap-0.5">
              <label htmlFor={shareId}>Share this chat with the admins</label>
              <span id={`${shareId}-hint`} className="text-xs text-muted-foreground">
                They can then read it, to see what went wrong. Otherwise they see only its title, the model and your reason.
              </span>
            </span>
          </div>
          <div className="flex justify-between gap-2">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={busy}
              onClick={async () => {
                if (await save(null)) setOpen(false)
              }}
            >
              Take back
            </Button>
            <Button
              type="button"
              size="sm"
              disabled={busy}
              onClick={async () => {
                if (await save({ up: false, reason, comment: comment.trim() || null, share })) {
                  setOpen(false)
                  toast.success('Thanks for saying why')
                }
              }}
            >
              Send
            </Button>
          </div>
        </PopoverContent>
      </Popover>
    </span>
  )
}
