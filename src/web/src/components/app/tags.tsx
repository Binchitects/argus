import { useMutation, useQuery } from '@tanstack/react-query'
import { Sparkles, Tag, X } from 'lucide-react'
import { useId, useState, type KeyboardEvent } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cleanTag, tagsQuery } from '@/lib/tags'

/** Tags as small badges (nothing when there are none). */
export function TagList({ tags, className }: { tags?: string[] | null; className?: string }) {
  if (!tags?.length) return null
  return (
    <span className={className ?? 'flex flex-wrap gap-1'} aria-label="Tags">
      {tags.map((t) => (
        <Badge key={t} variant="outline" className="px-1.5 py-0 text-[11px] font-normal">
          #{t}
        </Badge>
      ))}
    </span>
  )
}

/** Tags chosen: the known ones offered first, any other typed (Enter or comma). */
export function TagEditor({ value, onChange, label = 'Tags' }: { value: string[]; onChange: (tags: string[]) => void; label?: string }) {
  const known = useQuery(tagsQuery)
  const [typed, setTyped] = useState('')
  const id = useId()
  const add = (raw: string) => {
    const t = cleanTag(raw)
    if (t && !value.includes(t) && value.length < 10) onChange([...value, t])
    setTyped('')
  }
  const offered = [...(known.data?.known ?? []), ...(known.data?.used.map((u) => u.tag) ?? [])].filter((t, i, all) => all.indexOf(t) === i && !value.includes(t))
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if ((e.key === 'Enter' || e.key === ',') && typed.trim()) {
      e.preventDefault()
      add(typed)
    } else if (e.key === 'Backspace' && !typed && value.length > 0) {
      onChange(value.slice(0, -1))
    }
  }
  return (
    <div className="grid gap-2">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <div className="flex min-h-9 flex-wrap items-center gap-1 rounded-md border bg-background px-2 py-1 focus-within:ring-2 focus-within:ring-ring">
        {value.map((t) => (
          <Badge key={t} variant="secondary" className="gap-1 pe-1">
            #{t}
            <button type="button" className="rounded-sm opacity-70 hover:opacity-100" aria-label={`Remove ${t}`} onClick={() => onChange(value.filter((x) => x !== t))}>
              <X className="size-3" />
            </button>
          </Badge>
        ))}
        <input
          id={id}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          onKeyDown={onKey}
          onBlur={() => typed.trim() && add(typed)}
          placeholder={value.length === 0 ? 'review, security, …' : ''}
          className="min-w-24 flex-1 bg-transparent py-1 text-sm outline-none"
          aria-describedby={`${id}-hint`}
        />
      </div>
      <p id={`${id}-hint`} className="sr-only">
        Enter or comma adds a tag; Backspace removes the last.
      </p>
      {offered.length > 0 && (
        <div className="flex flex-wrap gap-1" aria-label="Suggested tags">
          {offered.slice(0, 16).map((t) => (
            <button key={t} type="button" onClick={() => add(t)} className="rounded-full border px-2 py-0.5 text-xs text-muted-foreground hover:bg-accent hover:text-foreground">
              + {t}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * Tags and summary of a chat, a project or a prompt: the tags by hand, and Summarize and tag (the model for small steps
 * writes what it is about and adds the known tags that fit).
 */
export function TagsDialog({
  open,
  onOpenChange,
  what,
  tags,
  brief,
  save,
  summarize,
  onDone,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** What is tagged, for the title: "this chat", "the project". */
  what: string
  tags: string[]
  brief?: string | null
  /** Where the tags go (PUT or PATCH). */
  save: (tags: string[]) => Promise<unknown>
  /** Summarize and tag on the server; undefined: not offered. */
  summarize?: () => Promise<{ brief: string | null; tags: string[] }>
  onDone?: () => void
}) {
  const [chosen, setChosen] = useState(tags)
  const [said, setSaid] = useState(brief ?? null)
  const [seen, setSeen] = useState(open)
  // Opened again: what it holds now.
  if (open !== seen) {
    setSeen(open)
    if (open) {
      setChosen(tags)
      setSaid(brief ?? null)
    }
  }
  const saving = useMutation({
    mutationFn: () => save(chosen),
    onSuccess: () => {
      onDone?.()
      onOpenChange(false)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const summarizing = useMutation({
    mutationFn: () => summarize!(),
    onSuccess: (r) => {
      setSaid(r.brief)
      setChosen(r.tags)
      onDone?.()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Tag className="size-4" aria-hidden="true" /> Tags and summary
          </DialogTitle>
          <DialogDescription>Tag {what} to find it again and to count its use by tag (Accounting → By tag).</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          {said ? (
            <p dir="auto" className="rounded-md bg-muted px-3 py-2 text-sm" aria-label="Summary">
              {said}
            </p>
          ) : (
            summarize && <p className="text-sm text-muted-foreground">No summary yet.</p>
          )}
          <TagEditor value={chosen} onChange={setChosen} />
        </div>
        <DialogFooter className="gap-2 sm:justify-between">
          {summarize ? (
            <Button variant="outline" loading={summarizing.isPending} onClick={() => summarizing.mutate()}>
              <Sparkles /> Summarize and tag
            </Button>
          ) : (
            <span />
          )}
          <Button loading={saving.isPending} onClick={() => saving.mutate()}>
            Save tags
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
