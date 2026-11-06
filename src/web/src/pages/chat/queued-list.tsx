import { Clock3, ListStart, Pencil, TextQuote, X } from 'lucide-react'
import { useState, type KeyboardEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/input'
import { Tooltip } from '@/components/ui/tooltip'
import type { SkillCall } from '@/lib/skills'
import type { Attachment } from './types'

/** A message written while an answer runs, waiting on the server: the answer reads it at its next step, or it goes when the answer ends. */
export interface Queued {
  key: string
  text: string
  attachments: Attachment[]
  /** The part of an answer it quotes. */
  quote?: string | null
  /** The skill it uses. */
  skill?: SkillCall | null
}

/** Whether a queued message waits for the answer to end in Arena's chat: the model reads files with the history, and a skill sets up a turn of its own. */
const waitsForEnd = (q: Queued, filesGoIn: boolean) => (!filesGoIn && q.attachments.length > 0) || !!q.skill

const label = (q: Queued) => q.text || (q.skill ? `/${q.skill.name}` : 'files')

/**
 * The messages waiting, at the end of the thread, as Claude Code shows what was typed while it works: each pending, until the
 * answer takes it in at its next step (after its tool calls) or, with files or a skill, until the answer ends. Until then each
 * can be edited (Enter saves, Esc leaves it as it was), put first, or cancelled. Stop is the composer's, apart: these never stop
 * the answer.
 */
export function QueuedList({
  queued,
  elsewhere,
  filesGoIn = false,
  onEdit,
  onPutFirst,
  onCancel,
  onClear,
}: {
  queued: Queued[]
  /** Answering elsewhere ("Code Arena is answering"): each goes when that turn ends. */
  elsewhere?: string
  /** Code Arena: one with files goes into the answer too (it reads them itself). */
  filesGoIn?: boolean
  /** Saves new words (false: they were not taken, and the box stays open). */
  onEdit?: (key: string, text: string) => Promise<boolean>
  /** Moves one ahead of the others; none: the line keeps its order. */
  onPutFirst?: (key: string) => void
  onCancel: (key: string) => void
  /** Takes every one out of line. */
  onClear?: () => void
}) {
  const [editing, setEditing] = useState<{ key: string; text: string } | null>(null)
  const [saving, setSaving] = useState(false)
  if (queued.length === 0) return null
  const save = async () => {
    if (!editing || !onEdit || saving) return
    setSaving(true)
    const done = await onEdit(editing.key, editing.text.trim())
    setSaving(false)
    if (done) setEditing(null)
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Escape') {
      e.preventDefault()
      setEditing(null)
    } else if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void save()
    }
  }
  return (
    <section aria-label="Pending messages" className="grid animate-enter justify-items-end gap-1.5">
      <ul className="grid w-full max-w-[85%] justify-items-end gap-1.5" aria-label="Queued messages">
        {queued.map((q, i) => (
          <li key={q.key} className="grid w-full justify-items-end gap-1">
            {editing?.key === q.key ? (
              <form
                className="grid w-full gap-2"
                onSubmit={(e) => {
                  e.preventDefault()
                  void save()
                }}
              >
                <Textarea
                  dir="auto"
                  value={editing.text}
                  onChange={(e) => setEditing({ key: q.key, text: e.target.value })}
                  onKeyDown={onKey}
                  aria-label="Edit the queued message"
                  // oxlint-disable-next-line jsx-a11y/no-autofocus -- the box opened by its Edit button
                  autoFocus
                  className="min-h-20"
                />
                <div className="flex justify-end gap-2">
                  <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(null)}>
                    Cancel
                  </Button>
                  <Button type="submit" size="sm" disabled={saving || (!editing.text.trim() && q.attachments.length === 0)}>
                    Save
                  </Button>
                </div>
              </form>
            ) : (
              <div className="flex max-w-full min-w-0 items-start gap-2 rounded-2xl rounded-br-md border border-dashed bg-secondary/50 px-3 py-2 text-[0.9375rem] leading-relaxed text-secondary-foreground">
                <Clock3 className="mt-1 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span className="sr-only">Queued:</span>
                {q.quote && (
                  <>
                    <TextQuote className="mt-1 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="sr-only">Quoting part of an answer:</span>
                  </>
                )}
                <span dir="auto" className="bidi-lines min-w-0 break-words whitespace-pre-wrap">
                  {q.skill && <span className="font-mono text-[0.8125rem] font-medium">/{q.skill.name} </span>}
                  {q.text || (q.skill ? '' : `${q.attachments.length} file${q.attachments.length === 1 ? '' : 's'}`)}
                  {q.text && q.attachments.length > 0 && <span className="text-muted-foreground"> · {q.attachments.length} file{q.attachments.length === 1 ? '' : 's'}</span>}
                </span>
              </div>
            )}
            {editing?.key !== q.key && (
              <div className="flex items-center gap-0.5 text-xs text-muted-foreground">
                <span className="px-1">{elsewhere ? 'Waiting' : waitsForEnd(q, filesGoIn) ? 'Waits for the answer to end' : filesGoIn || i === 0 ? 'Read at the next step' : 'Waiting'}</span>
                {onEdit && !q.skill && (
                  <Tooltip content="Edit">
                    <Button type="button" variant="ghost" size="icon-sm" className="size-7" onClick={() => setEditing({ key: q.key, text: q.text })} aria-label={`Edit queued message: ${label(q)}`}>
                      <Pencil />
                    </Button>
                  </Tooltip>
                )}
                {i > 0 && onPutFirst && (
                  <Tooltip content="Put first">
                    <Button type="button" variant="ghost" size="icon-sm" className="size-7" onClick={() => onPutFirst(q.key)} aria-label={`Put first: ${label(q)}`}>
                      <ListStart />
                    </Button>
                  </Tooltip>
                )}
                <Tooltip content="Cancel">
                  <Button type="button" variant="ghost" size="icon-sm" className="size-7" onClick={() => onCancel(q.key)} aria-label={`Cancel queued message: ${label(q)}`}>
                    <X />
                  </Button>
                </Tooltip>
              </div>
            )}
          </li>
        ))}
      </ul>
      <p className="max-w-[85%] text-end text-xs text-muted-foreground">
        {elsewhere
          ? `${elsewhere}, and nothing here stops it: these are sent in turn when its turn ends, even if you close the page.`
          : filesGoIn
            ? 'The answer reads them at its next step (after its tool calls) and goes on; Stop stops the answer.'
            : 'The answer reads the first at its next step (after its tool calls) and goes on with it; one with files or a skill goes when the answer ends. They wait even if you close the page. Stop stops the answer.'}
        {onClear && (
          <Button type="button" variant="ghost" size="sm" className="ms-1 h-6 px-2 text-xs" onClick={onClear}>
            Clear all
          </Button>
        )}
      </p>
    </section>
  )
}
