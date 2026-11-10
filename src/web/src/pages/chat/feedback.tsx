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

export interface Rating {
  up: boolean
  reason?: FeedbackReason | null
  comment?: string | null
  share?: boolean
}

/**
 * Thumbs up or down on an answer (its last message) of a chat in the Arena: <see cref="RatingButtons"/> with the chat's own
 * calls, and the chat's copy kept up to date (the answer shows it again after a branch switch or a reload).
 */
export function FeedbackButtons({ chatId, message }: { chatId: string; message: Message }) {
  const queryClient = useQueryClient()
  const url = `/api/chat/conversations/${chatId}/messages/${message.id}/feedback`
  return (
    <RatingButtons
      initial={message.feedback ?? null}
      send={async (rating) => {
        const saved = rating ? await api<Feedback>(url, { method: 'PUT', body: rating }) : (await api(url, { method: 'DELETE' }), null)
        queryClient.setQueryData<Conversation>(conversationQuery(chatId).queryKey, (c) =>
          c ? { ...c, messages: c.messages.map((m) => (m.id === message.id ? { ...m, feedback: saved } : m)) } : c,
        )
        return saved
      }}
    />
  )
}

/**
 * Thumbs up or down on an answer. Either is kept at once, and opens a box for more: words and whether to share the chat with
 * the admins, and for a down vote a reason. The thumb that is on takes the rating back. <paramref name="send"/> keeps it (null:
 * taken back) and says what was kept: the Arena's call, or Code Arena's, which takes it there with the person's key.
 */
export function RatingButtons({ initial, send }: { initial: Feedback | null; send: (rating: Rating | null) => Promise<Feedback | null> }) {
  const [mine, setMine] = useState<Feedback | null>(initial)
  const [open, setOpen] = useState<'up' | 'down' | null>(null)
  const [reason, setReason] = useState<FeedbackReason | null>(initial?.reason ?? null)
  const [comment, setComment] = useState(initial?.comment ?? '')
  const [share, setShare] = useState(initial?.shared ?? false)
  const [busy, setBusy] = useState(false)
  const shareId = useId()

  const save = async (rating: Rating | null): Promise<boolean> => {
    setBusy(true)
    try {
      setMine(await send(rating))
      if (!rating) {
        // Taken back: its words go with it.
        setReason(null)
        setComment('')
        setShare(false)
      }
      return true
    } catch (e) {
      toast.error(errorMessage(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  const up = !!mine?.up
  const down = !!mine && !mine.up
  const box = (side: 'up' | 'down') => (
    <PopoverContent align="start" className="grid w-80 gap-3" aria-label={side === 'down' ? 'What was wrong' : 'What was good'}>
      <div>
        <p className="text-sm font-medium">{side === 'down' ? 'What was wrong?' : 'What was good?'}</p>
        <p className="text-xs text-muted-foreground">It helps the admins choose and tune the models.</p>
      </div>
      {side === 'down' && (
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
      )}
      <Textarea dir="auto" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} placeholder="Tell more (optional)" aria-label="Tell more" className="min-h-16 text-sm" />
      <div className="flex items-start gap-2 text-sm">
        <Checkbox id={shareId} checked={share} onCheckedChange={(v) => setShare(v === true)} className="mt-0.5" aria-describedby={`${shareId}-hint`} />
        <span className="grid gap-0.5">
          <label htmlFor={shareId}>Share this chat with the admins</label>
          <span id={`${shareId}-hint`} className="text-xs text-muted-foreground">
            They can then read it, to see what {side === 'down' ? 'went wrong' : 'went well'}. Otherwise they see only its title, the model and your words.
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
            if (await save(null)) setOpen(null)
          }}
        >
          Take back
        </Button>
        <Button
          type="button"
          size="sm"
          disabled={busy}
          onClick={async () => {
            if (await save({ up: side === 'up', reason: side === 'down' ? reason : null, comment: comment.trim() || null, share })) {
              setOpen(null)
              toast.success(side === 'down' ? 'Thanks for saying why' : 'Thanks for saying what was good')
            }
          }}
        >
          Send
        </Button>
      </div>
    </PopoverContent>
  )
  return (
    <span className="flex items-center">
      <Popover
        open={open === 'up'}
        onOpenChange={(o) => {
          setOpen(o ? 'up' : null)
          // Up at once; the words are extra.
          if (o && !up) void save({ up: true })
        }}
      >
        <Tooltip content={up ? 'You liked this answer: say what was good, or take it back' : 'Good answer'}>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon-sm" className={cn('size-7', up && 'text-primary-ink')} aria-label="Good answer" aria-pressed={up} disabled={busy && open !== 'up'}>
              <ThumbsUp className={cn(up && 'fill-current')} />
            </Button>
          </PopoverTrigger>
        </Tooltip>
        {box('up')}
      </Popover>
      <Popover
        open={open === 'down'}
        onOpenChange={(o) => {
          setOpen(o ? 'down' : null)
          // Down at once; the reason is extra.
          if (o && !down) void save({ up: false })
        }}
      >
        <Tooltip content={down ? 'You rated this answer down: say why, or take it back' : 'Bad answer'}>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon-sm" className={cn('size-7', down && 'text-destructive-ink')} aria-label="Bad answer" aria-pressed={down} disabled={busy && open !== 'down'}>
              <ThumbsDown className={cn(down && 'fill-current')} />
            </Button>
          </PopoverTrigger>
        </Tooltip>
        {box('down')}
      </Popover>
    </span>
  )
}
