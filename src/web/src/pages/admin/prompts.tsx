import { useInfiniteQuery } from '@tanstack/react-query'
import { Ban, CircleCheck, CircleStop, CircleX, Clock, LoaderCircle } from 'lucide-react'
import { useLayoutEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { formatValue, when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Markdown } from '@/pages/chat/markdown'

type State = 'waiting' | 'running' | 'done' | 'failed' | 'stopped' | 'refused'

interface Logged {
  source: 'chat' | 'api'
  at: string
  person: { id: string; userName: string; displayName: string | null } | null
  model: string | null
  question: string
  answer: string | null
  chat: string | null
  title: string | null
  deleted: boolean
  tokens: number | null
  state: State
  error: string | null
  durationMs: number | null
}

type Source = 'all' | 'chat' | 'api'

/** Each state's badge: its words, icon and colour. */
const states: Record<State, { label: string; icon: ReactNode; variant: 'default' | 'secondary' | 'success' | 'warning' | 'destructive' }> = {
  waiting: { label: 'Waiting', icon: <Clock />, variant: 'secondary' },
  running: { label: 'Running', icon: <LoaderCircle className="motion-safe:animate-spin" />, variant: 'default' },
  done: { label: 'Done', icon: <CircleCheck />, variant: 'success' },
  failed: { label: 'Failed', icon: <CircleX />, variant: 'destructive' },
  stopped: { label: 'Stopped', icon: <CircleStop />, variant: 'warning' },
  refused: { label: 'Refused', icon: <Ban />, variant: 'destructive' },
}

/**
 * Admin → Prompt log (prompts.review): everyone's questions and answers, in the chat and with API keys, in any state (waiting,
 * running, done, failed and why, stopped, refused); each search is audited. Long questions and answers show their start, and
 * all of it on Show all. While any shown is waiting or running, the page looks again every few seconds.
 */
export function PromptLogPage() {
  const [form, setForm] = useState({ q: '', person: '' })
  const [search, setSearch] = useState({ q: '', person: '', source: 'all' as Source, state: 'all' as State | 'all' })
  const log = useInfiniteQuery({
    queryKey: ['admin', 'prompts', search],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => {
      const p = new URLSearchParams({ source: search.source })
      if (search.state !== 'all') p.set('state', search.state)
      if (search.q) p.set('q', search.q)
      if (search.person) p.set('person', search.person)
      if (pageParam) p.set('before', pageParam)
      return api<{ items: Logged[]; next: string | null }>(`/api/admin/prompts?${p}`, { signal })
    },
    getNextPageParam: (last) => last.next,
    refetchInterval: (q) => (q.state.data?.pages.some((p) => p.items.some((i) => i.state === 'running' || i.state === 'waiting')) ? 5000 : false),
  })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    setSearch({ ...search, q: form.q.trim(), person: form.person.trim() })
  }
  const items = log.data?.pages.flatMap((p) => p.items) ?? []
  return (
    <>
      <PageHeader
        title="Prompt log"
        description="Everyone's questions and the answers they got, newest first, whatever became of them: running, done, failed, stopped or refused. In the chat (as long as the chats are kept, Code Arena's sessions and rooms among them) and with API keys (Settings: Prompt log of API requests). Every search you make here is in the audit log."
      />
      <form onSubmit={submit} className="mb-5 flex flex-wrap items-end gap-3">
        <Field label="Words" className="w-64">
          <Input value={form.q} onChange={(e) => setForm({ ...form, q: e.target.value })} placeholder="in the question (and an API answer)" />
        </Field>
        <Field label="Person" className="w-48">
          <Input value={form.person} onChange={(e) => setForm({ ...form, person: e.target.value })} placeholder="user name or email" />
        </Field>
        <Field label="State" className="w-40">
          <Select value={search.state} onValueChange={(state) => setSearch({ ...search, state: state as State | 'all' })}>
            <SelectTrigger aria-label="State">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">Any state</SelectItem>
              {(Object.keys(states) as State[]).map((s) => (
                <SelectItem key={s} value={s}>
                  {states[s].label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <Segmented
          label="Where"
          value={search.source}
          onChange={(source) => setSearch({ ...search, source })}
          options={[
            { value: 'all', label: 'All' },
            { value: 'chat', label: 'Chat' },
            { value: 'api', label: 'API' },
          ]}
        />
        <Button type="submit">Search</Button>
      </form>
      {log.error && <QueryError error={log.error} retry={() => log.refetch()} />}
      {log.isPending && <Skeleton className="h-40" />}
      {log.data && items.length === 0 && <p className="text-sm text-muted-foreground">Nothing found.</p>}
      <ul className="grid gap-4" aria-label="Prompt log">
        {items.map((i, n) => (
          <li key={`${i.at}-${i.source}-${n}`}>
            <Entry item={i} />
          </li>
        ))}
      </ul>
      {log.hasNextPage && (
        <Button variant="outline" className="mt-4" loading={log.isFetchingNextPage} onClick={() => void log.fetchNextPage()}>
          Older
        </Button>
      )}
    </>
  )
}

function Entry({ item: i }: { item: Logged }) {
  const s = states[i.state] ?? states.done
  const name = i.person ? (i.person.displayName ?? i.person.userName) : 'someone removed'
  const facts = [
    i.model,
    i.tokens !== null ? `${i.tokens.toLocaleString()} tokens` : null,
    i.durationMs !== null ? `took ${formatValue(i.durationMs, 'ms')}` : null,
  ].filter(Boolean) as string[]
  return (
    <article className="overflow-hidden rounded-xl border bg-card shadow-xs" aria-label={`${name}, ${s.label.toLowerCase()}, ${when(i.at)}`}>
      <header className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b bg-muted/30 px-4 py-2.5">
        <Avatar name={name} className="size-7" />
        <div className="grid min-w-0 flex-1 gap-0.5">
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-sm">
            {i.person ? (
              <Link to={`/admin/people/${i.person.id}`} className="font-medium underline-offset-2 hover:underline">
                {name}
              </Link>
            ) : (
              <span className="text-muted-foreground">{name}</span>
            )}
            <Badge variant="outline">{i.source === 'api' ? 'API' : 'Chat'}</Badge>
            {i.title && <span className="min-w-0 truncate text-muted-foreground">in “{i.title}”</span>}
            {i.deleted && <Badge variant="outline">chat deleted</Badge>}
          </div>
          {facts.length > 0 && <p className="truncate font-mono text-xs text-muted-foreground">{facts.join(' · ')}</p>}
        </div>
        <div className="flex items-center gap-2">
          <Badge variant={s.variant}>
            {s.icon}
            {s.label}
          </Badge>
          <time dateTime={i.at} title={new Date(i.at).toLocaleString()} className="text-xs whitespace-nowrap text-muted-foreground">
            {when(i.at)}
          </time>
        </div>
      </header>
      <div className="grid gap-3 p-4 text-sm">
        <Said label="Question" text={i.question} lines={4} />
        {i.answer && <Said label={i.state === 'stopped' ? 'Answer, as far as it went' : 'Answer'} text={i.answer} lines={6} answer />}
        {!i.answer && (i.state === 'running' || i.state === 'waiting') && (
          <p className="flex items-center gap-2 text-muted-foreground">
            <LoaderCircle className="size-4 motion-safe:animate-spin" aria-hidden="true" />
            {i.state === 'waiting' ? 'Waiting for its turn…' : 'Being answered…'}
          </p>
        )}
        {i.error && (
          <p role="note" className={cn('rounded-lg px-3 py-2', i.state === 'stopped' ? 'bg-warning/15 text-warning-ink' : 'bg-destructive/10 text-destructive-ink')}>
            {i.error}
          </p>
        )}
      </div>
    </article>
  )
}

/**
 * A question or an answer: its start, and all of it on Show all when there is more. An answer is formatted as the chat
 * shows it (lists, headings, code), cut at a height that fades out; a question is its text as typed.
 */
function Said({ label, text, lines, answer }: { label: string; text: string; lines: 4 | 6; answer?: boolean }) {
  const body = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const [clamped, setClamped] = useState(false)
  useLayoutEffect(() => {
    const el = body.current
    // Measured where there is layout; without it (no layout yet), by its lines and length.
    if (el && !open) setClamped(el.scrollHeight > 0 ? el.scrollHeight > el.clientHeight + 1 : text.split('\n').length > lines || text.length > lines * 100)
  }, [text, open, lines])
  return (
    <section aria-label={label} className={cn('grid gap-1', answer && 'rounded-lg border-s-2 border-primary/40 bg-muted/40 py-2 ps-3 pe-2')}>
      <h3 className="text-xs font-medium tracking-wide text-muted-foreground uppercase">{label}</h3>
      {answer ? (
        <div
          ref={body}
          dir="auto"
          className={cn(
            'min-w-0 break-words [&_pre]:max-w-full',
            !open && 'max-h-44 overflow-hidden',
            !open && clamped && 'mask-b-from-70% mask-b-to-100%',
          )}
        >
          <Markdown text={text} />
        </div>
      ) : (
        <div ref={body} dir="auto" className={cn('break-words whitespace-pre-wrap', !open && (lines === 4 ? 'line-clamp-4' : 'line-clamp-6'))}>
          {text}
        </div>
      )}
      {(clamped || open) && (
        <button
          type="button"
          className="w-fit text-xs font-medium text-primary-ink underline-offset-2 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
          onClick={() => setOpen(!open)}
          aria-expanded={open}
        >
          {open ? 'Show less' : `Show all (${text.length.toLocaleString()} characters)`}
        </button>
      )}
    </section>
  )
}
