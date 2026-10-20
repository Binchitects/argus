import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import type { SafeguardAnswer, SafeguardQuestion } from './safeguard'

export function SafeguardAsk({ question, onAnswer }: { question: SafeguardQuestion; onAnswer: (a: SafeguardAnswer) => void }) {
  const [reason, setReason] = useState('')
  const justify = question.kind === 'justify'
  return (
    <Dialog open onOpenChange={(open) => !open && onAnswer({ go: false })}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{justify ? 'Why do you need to send it?' : 'Send it anyway?'}</DialogTitle>
          <DialogDescription>{question.message}</DialogDescription>
        </DialogHeader>
        {justify && (
          <Field label="Your reason" hint="It is recorded with the message, for the people who look after the safeguards.">
            <Textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={2000} autoFocus />
          </Field>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={() => onAnswer({ go: false })}>
            Keep it
          </Button>
          <Button onClick={() => onAnswer({ go: true, justification: justify ? reason.trim() : undefined })} disabled={justify && !reason.trim()}>
            Send it
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** "Ask for a review" of a refusal: the person says why it should be allowed; those who look after the safeguards answer. */
export function AskReview({ eventId }: { eventId: string }) {
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [asked, setAsked] = useState(false)
  if (asked) return <span className="text-xs">Sent for review: you will hear in your bell.</span>
  const submit = async () => {
    setBusy(true)
    try {
      await api(`/api/safeguards/events/${eventId}/review`, { method: 'POST', body: { reason: reason.trim() } })
      setAsked(true)
      setOpen(false)
      toast.success('Sent for review', { description: 'You will hear in your bell.' })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <Button variant="outline" size="sm" className="mt-2" onClick={() => setOpen(true)}>
        Ask for a review
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Ask for a review</DialogTitle>
            <DialogDescription>Say why this message should be allowed. If it is, you may send it again within a day.</DialogDescription>
          </DialogHeader>
          <Field label="Why it should be allowed">
            <Textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={2000} autoFocus />
          </Field>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button onClick={() => void submit()} loading={busy} disabled={!reason.trim()}>
              Send for review
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}
