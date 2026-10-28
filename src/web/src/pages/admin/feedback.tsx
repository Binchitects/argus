import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'

type Status = 'new' | 'read' | 'done'

interface Person {
  id: string
  userName: string
  displayName: string | null
}

interface Item {
  id: string
  kind: 'idea' | 'problem' | 'praise' | 'other'
  text: string
  page: string | null
  status: Status
  answer: string | null
  answeredAt: string | null
  createdAt: string
  person: Person | null
  answeredBy: Person | null
}

const kindLabel: Record<Item['kind'], string> = { idea: 'Idea', problem: 'Problem', praise: 'Praise', other: 'Other' }
const name = (p: Person | null) => (p ? (p.displayName ?? p.userName) : 'someone removed')

function FeedbackItem({ item }: { item: Item }) {
  const queryClient = useQueryClient()
  const [answer, setAnswer] = useState(item.answer ?? '')
  const change = useMutation({
    mutationFn: (body: { status?: Status; answer?: string }) => api(`/api/admin/feedback/${item.id}`, { method: 'PUT', body }),
    onSuccess: (_, body) => {
      if (body.answer) toast.success('Answered', { description: `${name(item.person)} gets it in their bell.` })
      return queryClient.invalidateQueries({ queryKey: ['admin', 'feedback'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card>
      <CardContent className="grid gap-3 pt-5">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <Badge variant={item.kind === 'problem' ? 'destructive' : item.kind === 'praise' ? 'secondary' : 'outline'}>{kindLabel[item.kind]}</Badge>
          {item.person ? (
            <Link to={`/admin/people/${item.person.id}`} className="font-medium underline-offset-2 hover:underline">
              {name(item.person)}
            </Link>
          ) : (
            <span className="font-medium">{name(item.person)}</span>
          )}
          {item.page && <span className="text-muted-foreground">from {item.page}</span>}
          <span className="ml-auto text-xs text-muted-foreground">{when(item.createdAt)}</span>
        </div>
        <p dir="auto" className="text-sm whitespace-pre-wrap">
          {item.text}
        </p>
        {item.answer && item.answeredAt && (
          <p className="rounded-lg bg-muted/50 px-3 py-2 text-sm">
            <span className="text-xs text-muted-foreground">
              {name(item.answeredBy)} answered {when(item.answeredAt)}:{' '}
            </span>
            {item.answer}
          </p>
        )}
        <Textarea dir="auto" value={answer} onChange={(e) => setAnswer(e.target.value)} maxLength={4000} placeholder="Answer (they get it in their bell)" aria-label={`Answer ${name(item.person)}`} className="min-h-16 text-sm" />
        <div className="flex flex-wrap gap-2">
          <Button size="sm" disabled={!answer.trim() || answer.trim() === item.answer} loading={change.isPending} onClick={() => change.mutate({ answer: answer.trim() })}>
            Answer
          </Button>
          {item.status !== 'read' && (
            <Button size="sm" variant="outline" onClick={() => change.mutate({ status: 'read' })}>
              Mark read
            </Button>
          )}
          {item.status !== 'done' && (
            <Button size="sm" variant="outline" onClick={() => change.mutate({ status: 'done' })}>
              Done
            </Button>
          )}
          {item.status !== 'new' && (
            <Button size="sm" variant="ghost" onClick={() => change.mutate({ status: 'new' })}>
              Mark new
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  )
}

/** Admin → Feedback: what people send about Arena (Send feedback), by status, to read, mark and answer. */
export function FeedbackPage() {
  const [status, setStatus] = useState<Status | 'all'>('new')
  const list = useQuery({ queryKey: ['admin', 'feedback', status], queryFn: ({ signal }) => api<{ items: Item[]; counts: Record<Status, number> }>(`/api/admin/feedback?status=${status}`, { signal }) })
  if (list.isPending) return <PageSkeleton />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  const { items, counts } = list.data
  return (
    <>
      <PageHeader title="Feedback" description="What people send about Arena and Code Arena (Send feedback, in their menu): ideas, problems and praise, with the page they sent it from. Answer it, and they get the answer in their bell." />
      <Segmented
        label="Which"
        value={status}
        onChange={setStatus}
        options={[
          { value: 'new', label: `New (${counts.new})` },
          { value: 'read', label: `Read (${counts.read})` },
          { value: 'done', label: `Done (${counts.done})` },
          { value: 'all', label: 'All' },
        ]}
        className="mb-4"
      />
      {items.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nothing here.</p>
      ) : (
        <ul className="grid gap-4" aria-label="Feedback">
          {items.map((i) => (
            <li key={i.id}>
              <FeedbackItem item={i} />
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
