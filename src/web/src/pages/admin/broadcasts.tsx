import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'
import { groupsQuery } from './groups-api'

type Level = 'info' | 'warning' | 'critical'

interface Sent {
  id: string
  title: string
  body: string | null
  level: Level
  createdAt: string
  until: string | null
  endedAt: string | null
  sent: number
  on: boolean
  groups: { id: string; name: string }[]
  by: string | null
  dismissed: number
}

const levels: { value: Level; label: string }[] = [
  { value: 'info', label: 'Info' },
  { value: 'warning', label: 'Warning' },
  { value: 'critical', label: 'Critical' },
]

const broadcastsQuery = { queryKey: ['admin', 'broadcasts'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<Sent[]>('/api/admin/broadcasts', { signal }) }

/**
 * Admin → Broadcasts: a message to everyone, or to some groups. It rings their bell (and their phone), shows above every page
 * and in Code Arena until they dismiss it, it ends by itself, or it is ended here; a critical one cannot be dismissed.
 */
export function BroadcastsPage() {
  const list = useQuery(broadcastsQuery)
  return (
    <div className="grid gap-6">
      <PageHeader title="Broadcasts" description="Tell everyone, or some groups: in their bell, above every page and in Code Arena, until they dismiss it or it ends." />
      <NewBroadcast />
      {list.isPending ? <PageSkeleton /> : list.isError ? <QueryError error={list.error} /> : <SentList items={list.data} />}
    </div>
  )
}

function NewBroadcast() {
  const queryClient = useQueryClient()
  const groups = useQuery(groupsQuery)
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [level, setLevel] = useState<Level>('info')
  const [chosen, setChosen] = useState<string[]>([])
  const [until, setUntil] = useState('')
  const id = useId()
  const send = useMutation({
    mutationFn: () =>
      api<{ id: string; sent: number }>('/api/admin/broadcasts', {
        body: { title: title.trim(), body: body.trim() || null, level, groups: chosen, until: until ? new Date(until).toISOString() : null },
      }),
    onSuccess: (r) => {
      toast.success(`Sent to ${r.sent} ${r.sent === 1 ? 'person' : 'people'}`)
      setTitle('')
      setBody('')
      setChosen([])
      setUntil('')
      void queryClient.invalidateQueries({ queryKey: broadcastsQuery.queryKey })
      void queryClient.invalidateQueries({ queryKey: ['broadcasts'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (title.trim()) send.mutate()
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle>New broadcast</CardTitle>
        <CardDescription>Everyone gets it unless you choose groups.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="grid gap-4" aria-label="New broadcast">
          <Field label="Title">
            <Input dir="auto" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
          </Field>
          <Field label="Words (optional)">
            <Textarea dir="auto" value={body} onChange={(e) => setBody(e.target.value)} maxLength={4000} className="min-h-20" />
          </Field>
          <Segmented label="How loud" value={level} onChange={setLevel} options={levels} />
          <fieldset className="grid gap-2">
            <legend className="text-sm font-medium">To</legend>
            <p className="text-xs text-muted-foreground">{chosen.length === 0 ? 'Everyone.' : `The members of ${chosen.length} group${chosen.length === 1 ? '' : 's'}.`}</p>
            <div className="flex flex-wrap gap-x-4 gap-y-2">
              {(groups.data ?? []).map((g) => (
                <label key={g.id} className="flex items-center gap-2 text-sm">
                  <Checkbox checked={chosen.includes(g.id)} onCheckedChange={(on) => setChosen((c) => (on === true ? [...c, g.id] : c.filter((x) => x !== g.id)))} />
                  {g.name}
                </label>
              ))}
            </div>
          </fieldset>
          <Field label="Ends by itself (optional)" className="max-w-xs">
            <Input id={`${id}-until`} type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} />
          </Field>
          <div>
            <Button type="submit" loading={send.isPending} disabled={!title.trim()}>
              Send
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

function SentList({ items }: { items: Sent[] }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const end = useMutation({
    mutationFn: (id: string) => api(`/api/admin/broadcasts/${id}/end`, { body: {} }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: broadcastsQuery.queryKey })
      void queryClient.invalidateQueries({ queryKey: ['broadcasts'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (items.length === 0) return <p className="text-sm text-muted-foreground">No broadcast sent yet.</p>
  return (
    <ul aria-label="Broadcasts sent" className="grid gap-3">
      {items.map((b) => (
        <li key={b.id}>
          <Card>
            <CardContent className="grid gap-2 pt-5 text-sm">
              <div className="flex flex-wrap items-center gap-2">
                <Badge variant={b.level === 'critical' ? 'destructive' : b.level === 'warning' ? 'secondary' : 'outline'}>{levels.find((l) => l.value === b.level)?.label}</Badge>
                <span dir="auto" className="font-medium">
                  {b.title}
                </span>
                <span className="ml-auto text-xs text-muted-foreground">{when(b.createdAt)}</span>
              </div>
              {b.body && (
                <p dir="auto" className="whitespace-pre-wrap text-muted-foreground">
                  {b.body}
                </p>
              )}
              <p className="text-xs text-muted-foreground">
                To {b.groups.length === 0 ? 'everyone' : b.groups.map((g) => g.name).join(', ')} · {b.sent} {b.sent === 1 ? 'person' : 'people'} · {b.dismissed} dismissed
                {b.by ? ` · by ${b.by}` : ''} · {b.on ? (b.until ? `until ${when(b.until)}` : 'on until ended') : `ended ${when(b.endedAt ?? b.until ?? b.createdAt)}`}
              </p>
              {b.on && (
                <div>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={end.isPending}
                    onClick={async () => {
                      if (await confirm({ title: `End “${b.title}”?`, description: 'It leaves every page and Code Arena now; it stays listed here.', confirm: 'End it' })) end.mutate(b.id)
                    }}
                  >
                    End it
                  </Button>
                </div>
              )}
            </CardContent>
          </Card>
        </li>
      ))}
    </ul>
  )
}
