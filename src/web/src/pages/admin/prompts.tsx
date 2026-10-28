import { useInfiniteQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { when } from '@/lib/format'

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
}

type Source = 'all' | 'chat' | 'api'

/** Admin → Prompt log (prompts.review): everyone's questions and answers, in the chat and with API keys; each search is audited. */
export function PromptLogPage() {
  const [form, setForm] = useState({ q: '', person: '' })
  const [search, setSearch] = useState({ q: '', person: '', source: 'all' as Source })
  const log = useInfiniteQuery({
    queryKey: ['admin', 'prompts', search],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => {
      const p = new URLSearchParams({ source: search.source })
      if (search.q) p.set('q', search.q)
      if (search.person) p.set('person', search.person)
      if (pageParam) p.set('before', pageParam)
      return api<{ items: Logged[]; next: string | null }>(`/api/admin/prompts?${p}`, { signal })
    },
    getNextPageParam: (last) => last.next,
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
        description="Everyone's questions and the answers they got, newest first: in the chat (as long as the chats are kept, Code Arena's sessions and rooms among them) and with API keys (Settings: Prompt log of API requests). Every search you make here is in the audit log."
      />
      <form onSubmit={submit} className="mb-4 flex flex-wrap items-end gap-3">
        <Field label="Words" className="w-64">
          <Input value={form.q} onChange={(e) => setForm({ ...form, q: e.target.value })} placeholder="in the question (and an API answer)" />
        </Field>
        <Field label="Person" className="w-48">
          <Input value={form.person} onChange={(e) => setForm({ ...form, person: e.target.value })} placeholder="user name or email" />
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
      <ul className="grid gap-3" aria-label="Prompt log">
        {items.map((i, n) => (
          <li key={`${i.at}-${n}`}>
            <Card>
              <CardContent className="grid gap-2 pt-4 text-sm">
                <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                  <Badge variant={i.source === 'api' ? 'outline' : 'secondary'}>{i.source === 'api' ? 'API' : 'Chat'}</Badge>
                  {i.person ? (
                    <Link to={`/admin/people/${i.person.id}`} className="font-medium text-foreground underline-offset-2 hover:underline">
                      {i.person.displayName ?? i.person.userName}
                    </Link>
                  ) : (
                    <span>someone removed</span>
                  )}
                  {i.model && <span className="font-mono">{i.model}</span>}
                  {i.title && <span>in “{i.title}”</span>}
                  {i.deleted && <Badge variant="outline">chat deleted</Badge>}
                  {i.tokens !== null && <span>{i.tokens.toLocaleString()} tokens</span>}
                  <span className="ml-auto">{when(i.at)}</span>
                </div>
                <p dir="auto" className="whitespace-pre-wrap">
                  <span className="text-xs font-medium text-muted-foreground">Asked: </span>
                  {i.question}
                </p>
                {i.answer && (
                  <p dir="auto" className="line-clamp-6 rounded-md bg-muted/50 px-2.5 py-1.5 whitespace-pre-wrap">
                    <span className="text-xs font-medium text-muted-foreground">Answered: </span>
                    {i.answer}
                  </p>
                )}
              </CardContent>
            </Card>
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
