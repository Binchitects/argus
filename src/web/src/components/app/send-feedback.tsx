import { Camera, ImagePlus, X } from 'lucide-react'
import { useEffect, useRef, useState, type ClipboardEvent, type FormEvent } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { productName } from '@/lib/brand'
import { cn } from '@/lib/utils'
import { canCapture, captureScreen, maxScreenshots, screenshotOfFile, type Screenshot } from './screenshots'

export type FeedbackKind = 'idea' | 'problem' | 'praise' | 'other'

/** What is sent: the kind, the words, the page, and the screenshots (each its bytes in base64). */
export interface FeedbackBody {
  kind: FeedbackKind
  text: string
  page: string
  images?: { data: string }[]
}

const kinds: { value: FeedbackKind; label: string }[] = [
  { value: 'idea', label: 'An idea' },
  { value: 'problem', label: 'A problem' },
  { value: 'praise', label: 'Praise' },
  { value: 'other', label: 'Other' },
]

/**
 * Send feedback about the product itself: an idea, a problem, praise or anything else, with the page it is sent from and up
 * to three screenshots (a picture file, one pasted, or a picture of the screen taken here). The admins read it on Admin →
 * Feedback and may answer it (the answer comes to the bell). <paramref name="send"/> sends it: the Arena's own call, or Code
 * Arena's, which takes it there with the person's key.
 */
export function SendFeedbackDialog({ page, send, onClose }: { page: string; send: (body: FeedbackBody) => Promise<unknown>; onClose: () => void }) {
  const [kind, setKind] = useState<FeedbackKind>('idea')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [shots, setShots] = useState<Screenshot[]>([])
  // Out of the picture while the screen's is taken.
  const [hidden, setHidden] = useState(false)
  const picker = useRef<HTMLInputElement>(null)
  // The pictures' addresses let go of with the dialog.
  const kept = useRef<Screenshot[]>([])
  useEffect(() => {
    kept.current = shots
  }, [shots])
  useEffect(() => () => kept.current.forEach((s) => URL.revokeObjectURL(s.url)), [])
  const room = maxScreenshots - shots.length
  const add = async (made: Promise<Screenshot>) => {
    try {
      const shot = await made
      setShots((now) => (now.length < maxScreenshots ? [...now, shot] : (URL.revokeObjectURL(shot.url), now)))
    } catch (err) {
      // Choosing nothing to share is no error.
      if (!(err instanceof DOMException && err.name === 'NotAllowedError')) toast.error(errorMessage(err))
    }
  }
  const addFiles = (files: Iterable<File>) => {
    for (const f of [...files].slice(0, room)) void add(screenshotOfFile(f))
  }
  const onPaste = (e: ClipboardEvent) => {
    const files = [...e.clipboardData.files].filter((f) => f.type.startsWith('image/'))
    if (files.length === 0) return
    e.preventDefault()
    if (room === 0) toast.error(`At most ${maxScreenshots} screenshots.`)
    else addFiles(files)
  }
  const remove = (shot: Screenshot) => {
    URL.revokeObjectURL(shot.url)
    setShots((now) => now.filter((s) => s !== shot))
  }
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      await send({ kind, text: text.trim(), page, ...(shots.length ? { images: shots.map((s) => ({ data: s.data })) } : {}) })
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
      <DialogContent className={cn('sm:max-w-lg', hidden && 'invisible')} onPaste={onPaste}>
        <form onSubmit={(e) => void submit(e)} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>Send feedback</DialogTitle>
            <DialogDescription>
              About {productName} itself: what to make better, what went wrong, what you like. The admins read it, with the page you send it from ({page}).
            </DialogDescription>
          </DialogHeader>
          <Segmented label="What it is" value={kind} onChange={setKind} options={kinds} />
          <Field label="What you want to say">
            <Textarea dir="auto" value={text} onChange={(e) => setText(e.target.value)} maxLength={4000} required autoFocus className="min-h-32" />
          </Field>
          <div className="grid gap-2">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-medium">Screenshots</span>
              <span className="text-xs text-muted-foreground">up to {maxScreenshots}; paste one here too</span>
              <span className="ml-auto" />
              <Button type="button" variant="outline" size="sm" disabled={room === 0} onClick={() => picker.current?.click()}>
                <ImagePlus /> Add a picture
              </Button>
              {canCapture() && (
                <Button type="button" variant="outline" size="sm" disabled={room === 0} onClick={() => void add(captureScreen(setHidden))}>
                  <Camera /> Take a screenshot
                </Button>
              )}
              <input
                ref={picker}
                type="file"
                accept="image/png,image/jpeg,image/gif,image/webp"
                multiple
                hidden
                aria-label="A screenshot to send"
                onChange={(e) => {
                  addFiles(e.target.files ?? [])
                  e.target.value = ''
                }}
              />
            </div>
            {shots.length > 0 && (
              <ul aria-label="Screenshots to send" className="flex flex-wrap gap-2">
                {shots.map((s, n) => (
                  <li key={s.url} className="relative">
                    <img src={s.url} alt={`Screenshot ${n + 1}`} className="h-20 w-32 rounded-md border object-cover" />
                    <Button type="button" variant="secondary" size="icon-sm" className="absolute top-1 right-1 size-6 rounded-full" aria-label={`Remove screenshot ${n + 1}`} onClick={() => remove(s)}>
                      <X />
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </div>
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
