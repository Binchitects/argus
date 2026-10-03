import { useQuery } from '@tanstack/react-query'
import { TextSearch } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { configQuery } from './api'

interface Hit {
  conversationId: string
  title: string
  archived: boolean
  messageId: string | null
  where: Place
  snippet: string
  at: string
  model: string | null
}

type Place = 'title' | 'prompt' | 'answer' | 'tool' | 'file'

const places: { key: Place; label: string }[] = [
  { key: 'title', label: 'Titles' },
  { key: 'prompt', label: 'Your messages' },
  { key: 'answer', label: 'Answers' },
  { key: 'tool', label: 'Tool results' },
  { key: 'file', label: 'File names' },
]

const ranges: Record<string, number | null> = { any: null, day: 1, week: 7, month: 31, year: 366 }
const ANY = '(any)'

/** The words found, marked, in what is around them. */
function Marked({ text, words }: { text: string; words: string }) {
  const out: ReactNode[] = []
  const lower = text.toLowerCase()
  const w = words.toLowerCase()
  let at = 0
  for (let i = lower.indexOf(w); w && i >= 0; i = lower.indexOf(w, at)) {
    out.push(text.slice(at, i), <mark key={i} className="rounded-sm bg-primary/20 text-foreground">{text.slice(i, i + w.length)}</mark>)
    at = i + w.length
  }
  out.push(text.slice(at))
  return <>{out}</>
}

/**
 * Advanced search in one's chats: words in titles, one's messages, the answers,
 * tools' results or files' names, in a time range, with a model, archived chats too.
 * A result opens its chat at the message, on its branch.
 */
export function ChatSearch({ onNavigate }: { onNavigate?: () => void }) {
  const navigate = useNavigate()
  const models = useQuery(configQuery).data?.models ?? []
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [words, setWords] = useState('')
  const [chosen, setChosen] = useState<Place[]>(places.map((p) => p.key))
  const [range, setRange] = useState('any')
  const [model, setModel] = useState(ANY)
  const [archived, setArchived] = useState(true)
  // Searched once typing pauses.
  useEffect(() => {
    const t = setTimeout(() => setWords(text.trim()), 300)
    return () => clearTimeout(t)
  }, [text])
  const days = ranges[range]
  const hits = useQuery({
    queryKey: ['chat', 'search', words, chosen, range, model, archived],
    enabled: open && words.length >= 2 && chosen.length > 0,
    queryFn: ({ signal }) => {
      const q = new URLSearchParams({ q: words, in: chosen.join(','), archived: String(archived) })
      if (days) q.set('from', new Date(Date.now() - days * 86_400_000).toISOString())
      if (model !== ANY) q.set('model', model)
      return api<Hit[]>(`/api/chat/search?${q}`, { signal })
    },
  })
  const byChat = new Map<string, Hit[]>()
  for (const h of hits.data ?? []) byChat.set(h.conversationId, [...(byChat.get(h.conversationId) ?? []), h])
  const go = (h: Hit) => {
    setOpen(false)
    onNavigate?.()
    void navigate(`/chat/${h.conversationId}${h.messageId ? `?at=${h.messageId}` : ''}`)
  }
  return (
    <>
      <Tooltip content="Search in messages, answers and files">
        <Button variant="ghost" size="icon-sm" className="size-8 shrink-0" onClick={() => setOpen(true)} aria-label="Advanced search">
          <TextSearch />
        </Button>
      </Tooltip>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="flex max-h-[min(90dvh,48rem)] flex-col gap-3 sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>Search your chats</DialogTitle>
            <DialogDescription>Words in titles, your messages, the answers, tools&apos; results or the names of files.</DialogDescription>
          </DialogHeader>
          <Input type="search" autoFocus value={text} onChange={(e) => setText(e.target.value)} placeholder="Search for…" aria-label="Search for" />
          <fieldset className="flex flex-wrap items-center gap-1.5">
            <legend className="sr-only">Search in</legend>
            {places.map((p) => {
              const on = chosen.includes(p.key)
              return (
                <button
                  key={p.key}
                  type="button"
                  aria-pressed={on}
                  onClick={() => setChosen(on ? chosen.filter((x) => x !== p.key) : [...chosen, p.key])}
                  className={cn(
                    'rounded-full border px-2.5 py-1 text-xs outline-none focus-visible:ring-[3px] focus-visible:ring-ring',
                    on ? 'border-primary bg-primary/10 text-foreground' : 'text-muted-foreground hover:text-foreground',
                  )}
                >
                  {p.label}
                </button>
              )
            })}
          </fieldset>
          <div className="flex flex-wrap items-center gap-2">
            <Select value={range} onValueChange={setRange}>
              <SelectTrigger size="sm" className="w-36" aria-label="When">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="any">Any time</SelectItem>
                <SelectItem value="day">Past day</SelectItem>
                <SelectItem value="week">Past week</SelectItem>
                <SelectItem value="month">Past month</SelectItem>
                <SelectItem value="year">Past year</SelectItem>
              </SelectContent>
            </Select>
            <Select value={model} onValueChange={setModel}>
              <SelectTrigger size="sm" className="w-48 min-w-0 [&>span]:truncate" aria-label="Model">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any model</SelectItem>
                {models.map((m) => (
                  <SelectItem key={m.name} value={m.name}>
                    {m.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <span className="ms-auto flex items-center gap-2 text-xs">
              <Switch id="search-archived" checked={archived} onCheckedChange={setArchived} />
              <label htmlFor="search-archived">Archived chats too</label>
            </span>
          </div>
          <div className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto" aria-live="polite">
            {words.length < 2 ? (
              <p className="py-6 text-center text-sm text-muted-foreground">Type two letters or more.</p>
            ) : chosen.length === 0 ? (
              <p className="py-6 text-center text-sm text-muted-foreground">Choose where to search.</p>
            ) : hits.isPending ? (
              <div className="grid gap-2">
                {Array.from({ length: 3 }, (_, i) => (
                  <Skeleton key={i} className="h-14" />
                ))}
              </div>
            ) : hits.error ? (
              <p className="text-sm text-destructive-ink">{errorMessage(hits.error)}</p>
            ) : byChat.size === 0 ? (
              <p className="py-6 text-center text-sm text-muted-foreground">Nothing found.</p>
            ) : (
              <ul className="grid gap-3" aria-label="Results">
                {[...byChat].map(([chat, found]) => (
                  <li key={chat} className="min-w-0 rounded-lg border">
                    <p className="flex min-w-0 items-center gap-2 border-b bg-muted/40 px-3 py-1.5 text-sm font-medium">
                      <span dir="auto" className="truncate">
                        <Marked text={found[0]!.title} words={words} />
                      </span>
                      {found[0]!.archived && <Badge variant="outline">Archived</Badge>}
                    </p>
                    <ul className="grid">
                      {found.map((h, i) => (
                        <li key={`${h.messageId ?? 'title'}-${h.where}-${i}`}>
                          <button type="button" onClick={() => go(h)} className="grid w-full min-w-0 gap-0.5 px-3 py-2 text-left outline-none hover:bg-accent focus-visible:bg-accent">
                            <span className="flex items-center gap-2 text-[0.6875rem] text-muted-foreground">
                              <span className="font-medium uppercase">{places.find((p) => p.key === h.where)?.label}</span>
                              <span>{new Date(h.at).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</span>
                              {h.model && <span className="truncate">· {h.model}</span>}
                            </span>
                            <span dir="auto" className="line-clamp-2 text-sm [overflow-wrap:anywhere]">
                              <Marked text={h.snippet} words={words} />
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
