import { useState, useSyncExternalStore } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { asked, onAsk, answerPaste } from './paste'

/** The text to compare with, pasted here when the browser does not let the page read the clipboard. */
export function PasteDialog() {
  const ask = useSyncExternalStore(onAsk, asked)
  const [text, setText] = useState('')
  if (!ask) return null
  const done = (value: string | undefined) => {
    setText('')
    answerPaste(value)
  }
  return (
    <Dialog open onOpenChange={(o) => !o && done(undefined)}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>{ask.title}</DialogTitle>
          <DialogDescription>The browser did not let the page read the clipboard: paste the text here (Ctrl+V).</DialogDescription>
        </DialogHeader>
        <textarea
          autoFocus
          value={text}
          onChange={(e) => setText(e.target.value)}
          aria-label="Text to compare with"
          className="h-56 w-full resize-y rounded-md border bg-background p-2 font-mono text-xs outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
        <DialogFooter>
          <Button variant="outline" onClick={() => done(undefined)}>
            Cancel
          </Button>
          <Button onClick={() => done(text)}>Compare</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
