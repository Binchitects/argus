import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Database, History, Pencil, Plug, Plus, RefreshCw, Trash2, TriangleAlert } from 'lucide-react'
import { useId, useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import type { DataTable } from '../data/data-api'

interface Source {
  id: string
  name: string
  description: string
  kind: string
  tables: string[]
  notes: string
  reach: 'company' | 'groups'
  groupIds: string[]
  mostRows: number
  seconds: number
  schemaReadAt: string | null
  where: string
  schema: DataTable[]
}

interface Probed {
  tables: DataTable[]
  warnings: string[]
  where: string
}

interface AskedQuery {
  id: string
  at: string
  via: string
  question: string | null
  sql: string
  rows: number
  milliseconds: number
  error: string | null
  person: string
}

const sourcesKey = ['admin', 'data-sources'] as const

/** Admin → Data sources: the databases people may ask, which tables, who; what was asked of them. */
export function DataSourcesPage() {
  const sources = useQuery({ queryKey: sourcesKey, queryFn: ({ signal }) => api<Source[]>('/api/admin/data-sources', { signal }) })
  const [editing, setEditing] = useState<Source | 'new' | null>(null)
  const [reading, setReading] = useState<Source | null>(null)
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const again = useMutation({
    mutationFn: (s: Source) => api<{ warnings: string[] }>(`/api/admin/data-sources/${s.id}/schema`, { body: {} }),
    onSuccess: (r) => {
      void queryClient.invalidateQueries({ queryKey: sourcesKey })
      if (r.warnings.length > 0) toast.error(r.warnings.join(' '))
      else toast.success('Its tables were read again')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (sources.isPending) return <PageSkeleton />
  if (sources.error) return <QueryError error={sources.error} retry={() => sources.refetch()} />
  return (
    <>
      <PageHeader
        title="Data sources"
        description="Databases people may ask in words (Workspace → Data, and the chat's Company data tool). PostgreSQL, read only: every query runs in a read-only transaction, after its plan shows it reads only the tables you open here."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> Connect a database
          </Button>
        }
      />
      {sources.data.length === 0 ? (
        <EmptyState icon={Database} title="No database connected">
          Give the stack a role that may only read the tables people should ask (never a superuser), then connect it here and choose its tables.
        </EmptyState>
      ) : (
        <ul className="grid gap-3 lg:grid-cols-2" aria-label="Data sources">
          {sources.data.map((s) => (
            <li key={s.id}>
              <Card className="h-full">
                <CardHeader>
                  <CardTitle className="flex min-w-0 items-center gap-2 text-base">
                    <Database className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="truncate">{s.name}</span>
                  </CardTitle>
                  <CardDescription className="min-w-0 truncate font-mono text-xs">{s.where}</CardDescription>
                  <CardAction>
                    <Badge variant="outline">{s.reach === 'company' ? 'Everyone' : `${s.groupIds.length} ${s.groupIds.length === 1 ? 'group' : 'groups'}`}</Badge>
                  </CardAction>
                </CardHeader>
                <CardContent className="grid gap-3 text-sm">
                  {s.description && <p className="text-muted-foreground">{s.description}</p>}
                  <p>
                    {s.tables.length === 0 ? (
                      <span className="text-warning-ink">No table is open to questions yet.</span>
                    ) : (
                      <>
                        Open: <span className="font-mono text-xs">{s.tables.join(', ')}</span>
                      </>
                    )}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    <Button size="sm" variant="outline" onClick={() => setEditing(s)}>
                      <Pencil /> Change
                    </Button>
                    <Button size="sm" variant="outline" onClick={() => setReading(s)}>
                      <History /> What was asked
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => again.mutate(s)} loading={again.isPending && again.variables?.id === s.id}>
                      <RefreshCw /> Read its tables again
                    </Button>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Remove ${s.name}`}
                      onClick={async () => {
                        if (await confirm({ title: `Remove ${s.name}?`, description: 'People cannot ask it any more, and the charts pinned from it go.', confirm: 'Remove', destructive: true })) {
                          await api(`/api/admin/data-sources/${s.id}`, { method: 'DELETE' }).catch((e: unknown) => toast.error(errorMessage(e)))
                          void queryClient.invalidateQueries({ queryKey: sourcesKey })
                        }
                      }}
                    >
                      <Trash2 />
                    </Button>
                  </div>
                </CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="sm:max-w-2xl">{editing && <SourceForm source={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}</DialogContent>
      </Dialog>
      <Dialog open={reading !== null} onOpenChange={(o) => !o && setReading(null)}>
        <DialogContent className="sm:max-w-3xl">{reading && <QueriesList source={reading} />}</DialogContent>
      </Dialog>
    </>
  )
}

/** Connect or change: the connection (tried first), its tables to open, what the model should know, who may ask. */
function SourceForm({ source, onClose }: { source: Source | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [name, setName] = useState(source?.name ?? '')
  const [description, setDescription] = useState(source?.description ?? '')
  const [connection, setConnection] = useState('')
  const [probed, setProbed] = useState<Probed | null>(source ? { tables: source.schema, warnings: [], where: source.where } : null)
  const [tables, setTables] = useState<string[]>(source?.tables ?? [])
  const [notes, setNotes] = useState(source?.notes ?? '')
  const [reach, setReach] = useState<'company' | 'groups'>(source?.reach ?? 'company')
  const [groupIds, setGroupIds] = useState<string[]>(source?.groupIds ?? [])
  const [mostRows, setMostRows] = useState(String(source?.mostRows ?? 1000))
  const [seconds, setSeconds] = useState(String(source?.seconds ?? 15))
  const [error, setError] = useState<string | null>(null)
  const groups = useQuery({ queryKey: ['admin', 'groups', 'names'], queryFn: ({ signal }) => api<{ id: string; name: string }[]>('/api/admin/groups', { signal }), enabled: reach === 'groups' })
  const probe = useMutation({
    mutationFn: () => api<Probed>('/api/admin/data-sources/probe', { body: { connection: connection.trim() || null, id: source?.id } }),
    onSuccess: (p) => {
      setProbed(p)
      setError(null)
      setTables((t) => t.filter((x) => p.tables.some((y) => y.name === x)))
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(), description: description.trim(), connection: connection.trim() || undefined, tables, notes: notes.trim(), reach, groupIds: reach === 'groups' ? groupIds : [],
        mostRows: Number(mostRows), seconds: Number(seconds),
      }
      return source ? api(`/api/admin/data-sources/${source.id}`, { method: 'PATCH', body }) : api('/api/admin/data-sources', { body })
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: sourcesKey })
      void queryClient.invalidateQueries({ queryKey: ['data'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const changedConnection = connection.trim().length > 0
  return (
    <form
      className="grid max-h-[80vh] gap-4 overflow-y-auto pr-1"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{source ? `Change ${source.name}` : 'Connect a database'}</DialogTitle>
        <DialogDescription>
          PostgreSQL. Make it a role that may only read what people should ask, for example: <code className="text-xs">create role arena_reader login password '…'; grant usage on schema shop to arena_reader; grant select on shop.orders, shop.customers to arena_reader;</code>
        </DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="Shop" />
        </Field>
        <Field label="What it holds (optional)">
          <Input value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} placeholder="Orders and customers of the web shop" />
        </Field>
      </div>
      <Field label={source ? 'A new connection (empty keeps the one saved)' : 'Connection'} hint={source ? `Now: ${source.where}` : 'Host=db.example.com;Port=5432;Database=shop;Username=arena_reader;Password=…'}>
        <div className="flex gap-2">
          <Input type="password" autoComplete="off" value={connection} onChange={(e) => setConnection(e.target.value)} className="font-mono text-xs" />
          <Button type="button" variant="outline" onClick={() => probe.mutate()} loading={probe.isPending} disabled={!source && !changedConnection}>
            <Plug /> Try it
          </Button>
        </div>
      </Field>
      {probed && (
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">
            Tables open to questions <span className="font-normal text-muted-foreground">({probed.where}, {probed.tables.length} the role may read)</span>
          </legend>
          {probed.warnings.map((w) => (
            <p key={w} className="flex gap-1.5 text-xs text-warning-ink">
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {w}
            </p>
          ))}
          <div className="grid max-h-56 gap-1 overflow-y-auto rounded-lg border p-2 sm:grid-cols-2">
            {probed.tables.map((t) => (
              <div key={t.name} className="flex items-center gap-2 text-sm">
                <Checkbox id={`${id}-${t.name}`} checked={tables.includes(t.name)} onCheckedChange={(on) => setTables(on ? [...tables, t.name] : tables.filter((x) => x !== t.name))} />
                <label htmlFor={`${id}-${t.name}`} className="min-w-0 truncate font-mono text-xs">
                  {t.name}
                </label>
                {t.kind !== 'table' && <span className="text-xs text-muted-foreground">{t.kind}</span>}
              </div>
            ))}
          </div>
        </fieldset>
      )}
      <Field label="What the model should know (optional)" hint="Read with every question: units, what a status means, which dates count.">
        <Textarea rows={3} maxLength={4000} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="Totals are in euros, without VAT. An order with status 'void' was cancelled." />
      </Field>
      <Segmented label="Who may ask it" value={reach} onChange={setReach} options={[{ value: 'company', label: 'Everyone' }, { value: 'groups', label: 'Some groups' }]} />
      {reach === 'groups' && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Groups</legend>
          {(groups.data ?? []).map((g) => (
            <div key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${id}-g-${g.id}`} checked={groupIds.includes(g.id)} onCheckedChange={(on) => setGroupIds(on ? [...groupIds, g.id] : groupIds.filter((x) => x !== g.id))} />
              <label htmlFor={`${id}-g-${g.id}`}>{g.name}</label>
            </div>
          ))}
        </fieldset>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Most rows an answer brings back">
          <Input type="number" min={1} max={10000} value={mostRows} onChange={(e) => setMostRows(e.target.value)} />
        </Field>
        <Field label="Seconds a query may run">
          <Input type="number" min={1} max={120} value={seconds} onChange={(e) => setSeconds(e.target.value)} />
        </Field>
      </div>
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={!name.trim() || (!source && !changedConnection) || (reach === 'groups' && groupIds.length === 0)}>
          {source ? 'Save' : 'Connect'}
        </Button>
      </DialogFooter>
    </form>
  )
}

/** The latest queries run on a source: who, the question, the SQL, how it went. */
function QueriesList({ source }: { source: Source }) {
  const queries = useQuery({ queryKey: [...sourcesKey, source.id, 'queries'], queryFn: ({ signal }) => api<AskedQuery[]>(`/api/admin/data-sources/${source.id}/queries`, { signal }) })
  return (
    <>
      <DialogHeader>
        <DialogTitle>What was asked of {source.name}</DialogTitle>
        <DialogDescription>The latest 100 queries: from questions, SQL people ran, the chat and the boards.</DialogDescription>
      </DialogHeader>
      {queries.data?.length === 0 && <p className="text-sm text-muted-foreground">Nothing yet.</p>}
      <ul className="grid max-h-[60vh] gap-2 overflow-y-auto" aria-label="Queries">
        {(queries.data ?? []).map((q) => (
          <li key={q.id} className="rounded-lg border p-3 text-sm">
            <p className="text-xs text-muted-foreground">
              {new Date(q.at).toLocaleString()} · {q.person} · {q.via} · {q.error ? 'failed' : `${q.rows} ${q.rows === 1 ? 'row' : 'rows'}`} · {q.milliseconds} ms
            </p>
            {q.question && <p className="mt-1">{q.question}</p>}
            <pre className="mt-1 overflow-x-auto rounded bg-muted p-2 font-mono text-xs whitespace-pre-wrap">{q.sql}</pre>
            {q.error && <p className="mt-1 text-xs text-destructive-ink">{q.error}</p>}
          </li>
        ))}
      </ul>
    </>
  )
}
