import { useState, type FormEvent } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'

export type FeedbackKind = 'idea' | 'problem' | 'praise' | 'other'

const kinds: { value: FeedbackKind; label: string }[] = [
  { value: 'idea', label: 'An idea' },
  { value: 'problem', label: 'A problem' },
  { value: 'praise', label: 'Praise' },
  { value: 'other', label: 'Other' },
]

/**
 * Send feedback about Arena itself: an idea, a problem, praise or anything else, with the page it is sent from. The admins
 * read it on Admin → Feedback and may answer it (the answer comes to the bell). <paramref name="send"/> sends it: Arena's
 * own call, or Code Arena's, which takes it there with the person's key.
 */
export function SendFeedbackDialog({ page, send, onClose }: { page: string; send: (body: { kind: FeedbackKind; text: string; page: string }) => Promise<unknown>; onClose: () => void }) {
  const [kind, setKind] = useState<FeedbackKind>('idea')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      await send({ kind, text: text.trim(), page })
      toast.success('Thanks: sent to the admins', { description: 'An answer, when they write one, comes to your bell.' })
      onClose()
    } catch (err) {
      toast.error(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <form onSubmit={(e) => void submit(e)} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>Send feedback</DialogTitle>
            <DialogDescription>About Arena itself: what to make better, what went wrong, what you like. The admins read it, with the page you send it from ({page}).</DialogDescription>
          </DialogHeader>
          <Segmented label="What it is" value={kind} onChange={setKind} options={kinds} />
          <Field label="What you want to say">
            <Textarea dir="auto" value={text} onChange={(e) => setText(e.target.value)} maxLength={4000} required autoFocus className="min-h-32" />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={busy} disabled={!text.trim()}>
              Send
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
