import { ArrowUp, Clock3, EyeOff, FileText, ListEnd, Paperclip, Square, X } from 'lucide-react'
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ContextView } from './context'
import { ContextGauge } from './context-gauge'
import type { Attachment, ChatModel } from './types'

/** A message written while an answer runs: sent when it ends, or at once with Send now. */
export interface Queued {
  key: string
  text: string
  attachments: Attachment[]
}
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
  context,
  onCompact,
  queued,
  onQueue,
  onSendNow,
  onUnqueue,
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
  /** How full the model's context is, and what fills it (from the last answer). */
  context?: ContextView
  /** Summarize the chat's older messages (also: send /compact). */
  onCompact?: () => void
  /** While an answer runs: messages waiting to be sent, and how to add, hurry or drop one. */
  queued?: Queued[]
  onQueue?: (text: string) => void
  onSendNow?: (key: string) => void
  onUnqueue?: (key: string) => void
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

  const hasContent = text.trim().length > 0 || uploads.attachments.length > 0
  const canSend = (!streaming || !!onQueue) && !uploads.busy && hasContent
  const submit = async () => {
    if (!canSend) return
    const t = text.trim()
    setText('')
    // An answer is running: this one waits its turn (or goes at once with Send now).
    if (streaming && onQueue) onQueue(t)
    else if (!(await onSend(t))) setText((now) => now || t)
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
      {queued && queued.length > 0 && (
        <div className="grid gap-1 px-3 pt-3">
          <ul className="grid gap-1" aria-label="Queued messages">
            {queued.map((q) => (
              <li key={q.key} className="flex min-w-0 items-center gap-2 rounded-lg border border-dashed bg-muted/40 py-1 ps-2.5 pe-1 text-sm">
                <Clock3 className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span dir="auto" className="min-w-0 flex-1 truncate">
                  {q.text || `${q.attachments.length} file${q.attachments.length === 1 ? '' : 's'}`}
                  {q.text && q.attachments.length > 0 && <span className="text-muted-foreground"> · {q.attachments.length} file{q.attachments.length === 1 ? '' : 's'}</span>}
                </span>
                <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => onSendNow?.(q.key)}>
                  Send now
                </Button>
                <Button type="button" variant="ghost" size="icon-sm" className="size-7" onClick={() => onUnqueue?.(q.key)} aria-label={`Remove queued message: ${q.text || 'files'}`}>
                  <X />
                </Button>
              </li>
            ))}
          </ul>
          <p className="text-xs text-muted-foreground">Sent in turn when the answer ends; Send now stops the answer and sends it at once.</p>
        </div>
      )}
      {uploads.uploads.length > 0 && (
        <ul className="flex flex-wrap gap-2 px-3 pt-3" aria-label="Files to send">
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
        dir="auto"
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
        placeholder={streaming && onQueue ? 'Queue a message…' : 'Message'}
        aria-label="Message"
        // oxlint-disable-next-line jsx-a11y/no-autofocus -- the chat's whole purpose is this box
        autoFocus={autoFocus}
        className={cn('block max-h-72 w-full resize-none bg-transparent px-4 pt-3 text-[0.9375rem] leading-relaxed outline-none placeholder:text-muted-foreground', big && 'min-h-20')}
      />
      <div className="flex items-center gap-2 px-2 pt-1 pb-2">
        <input ref={picker} type="file" multiple hidden onChange={(e) => { if (e.target.files) uploads.add(e.target.files); e.target.value = '' }} aria-label="Attach files" />
        <Tooltip content="Attach files: Word, Excel, PowerPoint, PDF, text, code, images">
          <Button type="button" variant="ghost" size="icon-sm" onClick={() => picker.current?.click()} aria-label="Attach">
            <Paperclip />
          </Button>
        </Tooltip>
        {tools}
        <span className="hidden text-xs text-muted-foreground lg:inline">Enter to send · Shift+Enter for a new line</span>
        <span className="ml-auto" />
        {context && <ContextGauge context={context} onCompact={onCompact} busy={streaming} />}
        {streaming && onQueue && hasContent && (
          <Tooltip content="Queue: sent when the answer ends (Enter)">
            <Button type="submit" size="icon-sm" variant="outline" className="animate-pop rounded-full" disabled={!canSend} aria-label="Queue">
              <ListEnd />
            </Button>
          </Tooltip>
        )}
        {streaming ? (
          <Button type="button" size="icon-sm" variant="secondary" className="animate-pop rounded-full" onClick={onStop} aria-label="Stop">
            <Square className="fill-current" />
          </Button>
        ) : (
          <Button type="submit" size="icon-sm" className="animate-pop rounded-full" disabled={!canSend} aria-label="Send">
            <ArrowUp />
          </Button>
        )}
      </div>
    </form>
  )
}
