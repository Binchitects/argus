import { useQuery } from '@tanstack/react-query'
import { Bot, MessageSquarePlus, Plus, Search } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { assistantsQuery } from './chat/api'
import { AssistantIcon, ReachLabel } from './chat/assistant-icon'
import { NewAssistant } from './chat/assistant-new'
import type { AssistantSummary } from './chat/types'

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`

/** The gallery: the assistants a person may use (theirs, their teams', the company's), how much each is used, and a chat with one. */
export function AssistantsPage() {
  const list = useQuery(assistantsQuery)
  const [search, setSearch] = useState('')
  const [show, setShow] = useState<'all' | 'yours' | 'shared'>('all')
  const [creating, setCreating] = useState(false)
  if (list.isPending) return <PageSkeleton />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  const needle = search.trim().toLowerCase()
  const shown = list.data
    .filter((a) => show === 'all' || (show === 'yours' ? a.mine : !a.mine))
    .filter((a) => !needle || a.name.toLowerCase().includes(needle) || (a.description ?? '').toLowerCase().includes(needle))
    .sort((a, b) => b.people - a.people || a.name.localeCompare(b.name))
  return (
    <>
      <PageHeader
        title="Assistants"
        description="Instructions, files and settings a chat starts from: your own, your teams', and the company's. Share yours with your groups; an admin can share one with everyone."
        actions={
          <Button onClick={() => setCreating(true)}>
            <Plus /> New assistant
          </Button>
        }
      />
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <div className="relative min-w-0 flex-1 sm:max-w-sm">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search assistants" aria-label="Search assistants" className="pl-8" />
        </div>
        <Segmented
          label="Show"
          value={show}
          onChange={setShow}
          options={[
            { value: 'all', label: 'All' },
            { value: 'yours', label: 'Yours' },
            { value: 'shared', label: 'Shared with you' },
          ]}
        />
      </div>
      {list.data.length === 0 ? (
        <EmptyState icon={Bot} title="No assistants yet" action={<Button onClick={() => setCreating(true)}><Plus /> New assistant</Button>}>
          Make one with instructions and files for a task you repeat, then share it with your team.
        </EmptyState>
      ) : shown.length === 0 ? (
        <p className="py-8 text-center text-sm text-muted-foreground">No assistant matches.</p>
      ) : (
        <ul className="stagger grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Assistants">
          {shown.map((a) => (
            <AssistantCard key={a.id} a={a} />
          ))}
        </ul>
      )}
      <NewAssistant open={creating} onOpenChange={setCreating} />
    </>
  )
}

function AssistantCard({ a }: { a: AssistantSummary }) {
  const navigate = useNavigate()
  return (
    <li className="flex min-w-0 flex-col gap-3 rounded-xl border bg-card p-4 shadow-xs" aria-label={a.name}>
      <div className="flex min-w-0 items-start gap-3">
        <AssistantIcon icon={a.icon} color={a.color} size="lg" />
        <div className="grid min-w-0 flex-1 gap-0.5">
          <h2 className="truncate font-semibold">
            <Link to={`/chat/assistants/${a.id}`} className="outline-none hover:underline focus-visible:underline" dir="auto">
              {a.name}
            </Link>
          </h2>
          <p className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground">
            <span className="truncate">{a.mine ? 'Yours' : `By ${a.owner ?? 'someone'}`}</span>·<ReachLabel reach={a.reach} />
          </p>
        </div>
      </div>
      {a.description && (
        <p dir="auto" className="line-clamp-2 text-sm text-muted-foreground">
          {a.description}
        </p>
      )}
      <p className="mt-auto text-xs text-muted-foreground tabular-nums">
        {plural(a.chats, 'chat', 'chats')} · {plural(a.people, 'person', 'people')} in the last 30 days
      </p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => navigate(`/chat?assistant=${a.id}`)} aria-label={`Start a chat with ${a.name}`}>
          <MessageSquarePlus /> Start a chat
        </Button>
        <Button size="sm" variant="outline" asChild>
          <Link to={`/chat/assistants/${a.id}`}>Open</Link>
        </Button>
      </div>
    </li>
  )
}
