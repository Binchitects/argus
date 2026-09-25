import { ArrowUp, EyeOff, FileText, Paperclip, Square, X } from 'lucide-react'
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ChatModel } from './types'
import type { Uploads } from './uploads'

/**
 * Where the question is written. Enter sends, Shift+Enter adds a line; files come
 * by the button, by pasting, or by dropping them on the page. A question that did
 * not reach the server comes back here rather than being lost.
 */
export function Composer({
  streaming,
  onSend,
  onStop,
  uploads,
  model,
  autoFocus,
  big,
  tools,
}: {
  streaming: boolean
  onSend: (text: string) => Promise<boolean>
  onStop: () => void
  uploads: Uploads
  model?: ChatModel
  autoFocus?: boolean
  big?: boolean
  /** The chat's tools picker, beside the attach button. */
  tools?: ReactNode
}) {
  const [text, setText] = useState('')
  const area = useRef<HTMLTextAreaElement>(null)
  const picker = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const el = area.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 280)}px`
  }, [text])

  const canSend = !streaming && !uploads.busy && (text.trim().length > 0 || uploads.attachments.length > 0)
  const submit = async () => {
    if (!canSend) return
    const t = text.trim()
    setText('')
    if (!(await onSend(t))) setText((now) => now || t)
    area.current?.focus()
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void submit()
    }
  }
  const blindImages = model && !model.vision && uploads.uploads.some((u) => u.isImage && !u.error)

  return (
    <form
      className={cn('rounded-2xl border bg-card shadow-sm transition-shadow focus-within:border-primary/50 focus-within:shadow-md', big && 'shadow-md')}
      onSubmit={(e) => {
        e.preventDefault()
        void submit()
      }}
    >
      {uploads.uploads.length > 0 && (
        <ul className="flex flex-wrap gap-2 px-3 pt-3" aria-label="Attachments">
          {uploads.uploads.map((u) => (
            <li key={u.key} className={cn('relative flex items-center gap-2 overflow-hidden rounded-lg border bg-muted/40 text-sm', u.error && 'border-destructive/50', u.isImage && u.preview ? 'p-0' : 'py-1.5 pr-8 pl-2.5')}>
              {u.isImage && u.preview ? (
                <img src={u.preview} alt={u.name} className="size-16 object-cover" />
              ) : (
                <>
                  <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="grid">
                    <span className="max-w-44 truncate">{u.name}</span>
                    <span className={cn('text-xs', u.error ? 'text-destructive-ink' : 'text-muted-foreground')}>
                      {u.error ?? (u.attachment ? `${formatValue(u.size, 'bytes')}${u.attachment.truncated ? ', cut to fit' : ''}` : `Uploading ${Math.round(u.progress * 100)}%`)}
                    </span>
                  </span>
                </>
              )}
              {!u.attachment && !u.error && (
                <span className="absolute inset-x-0 bottom-0 h-0.5 bg-muted" aria-hidden="true">
                  <span className="block h-full bg-primary transition-[width]" style={{ width: `${u.progress * 100}%` }} />
                </span>
              )}
              {u.isImage && u.error && <span className="px-2 text-xs text-destructive-ink">{u.error}</span>}
              <button
                type="button"
                onClick={() => uploads.remove(u.key)}
                className="absolute top-1 right-1 flex size-5 items-center justify-center rounded-full bg-background/90 text-muted-foreground shadow-sm outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
                aria-label={`Remove ${u.name}`}
              >
                <X className="size-3" />
              </button>
            </li>
          ))}
        </ul>
      )}
      {blindImages && (
        <p className="flex items-center gap-1.5 px-4 pt-2 text-xs text-muted-foreground">
          <EyeOff className="size-3.5" aria-hidden="true" /> {model.name} cannot see images: it gets their names only.
        </p>
      )}
      <textarea
        ref={area}
        rows={big ? 3 : 1}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKey}
        onPaste={(e) => {
          if (e.clipboardData.files.length) {
            e.preventDefault()
            uploads.add(e.clipboardData.files)
          }
        }}
        placeholder="Message"
        aria-label="Message"
        // oxlint-disable-next-line jsx-a11y/no-autofocus -- the chat's whole purpose is this box
        autoFocus={autoFocus}
        className={cn('block max-h-72 w-full resize-none bg-transparent px-4 pt-3 text-[0.9375rem] leading-relaxed outline-none placeholder:text-muted-foreground', big && 'min-h-20')}
      />
      <div className="flex items-center gap-2 px-2 pt-1 pb-2">
        <input ref={picker} type="file" multiple hidden onChange={(e) => { if (e.target.files) uploads.add(e.target.files); e.target.value = '' }} aria-label="Attach files" />
        <Tooltip content="Attach files: text, code, PDFs, images">
          <Button type="button" variant="ghost" size="icon-sm" onClick={() => picker.current?.click()} aria-label="Attach">
            <Paperclip />
          </Button>
        </Tooltip>
        {tools}
        <span className="hidden text-xs text-muted-foreground lg:inline">Enter to send · Shift+Enter for a new line</span>
        <span className="ml-auto" />
        {streaming ? (
          <Button type="button" size="icon-sm" variant="secondary" className="rounded-full" onClick={onStop} aria-label="Stop">
            <Square className="fill-current" />
          </Button>
        ) : (
          <Button type="submit" size="icon-sm" className="rounded-full" disabled={!canSend} aria-label="Send">
            <ArrowUp />
          </Button>
        )}
      </div>
    </form>
  )
}
