import { EyeOff, Pin, PinOff } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import type { Message } from './types'

/** How a message counts in what the model reads: pinned (kept word for word through compactions), left out (not read, not deleted). */
export interface ContextChange {
  pinned?: boolean
  leftOut?: boolean
}

/** Under a question or an answer: pin it, or leave it out of what the model reads (and back). */
export function MessageContextButtons({ m, what, onChange }: { m: Message; what: 'question' | 'answer'; onChange: (m: Message, change: ContextChange) => void }) {
  return (
    <>
      {!m.leftOut && (
        <Tooltip content={m.pinned ? 'Pinned: kept word for word when the chat compacts. Unpin' : 'Pin: kept word for word when the chat compacts'}>
          <Button variant="ghost" size="icon-sm" className="size-7" aria-pressed={!!m.pinned} aria-label={m.pinned ? `Unpin this ${what}` : `Pin this ${what}`} onClick={() => onChange(m, { pinned: !m.pinned })}>
            {m.pinned ? <PinOff /> : <Pin />}
          </Button>
        </Tooltip>
      )}
      {!m.pinned && (
        <Tooltip content={m.leftOut ? 'Left out: the model does not read it. Put it back' : 'Leave out of what the model reads (it stays here)'}>
          <Button variant="ghost" size="icon-sm" className="size-7" aria-pressed={!!m.leftOut} aria-label={m.leftOut ? `Put this ${what} back` : `Leave this ${what} out`} onClick={() => onChange(m, { leftOut: !m.leftOut })}>
            <EyeOff />
          </Button>
        </Tooltip>
      )}
    </>
  )
}

/** What a message pinned or left out says of itself. */
export function MessageContextNote({ m }: { m: Message }) {
  if (m.pinned) return <span className="inline-flex items-center gap-1 text-xs text-primary-ink"><Pin className="size-3" aria-hidden="true" /> Pinned</span>
  if (m.leftOut) return <span className="inline-flex items-center gap-1 text-xs text-muted-foreground"><EyeOff className="size-3" aria-hidden="true" /> Left out: the model does not read it</span>
  return null
}
