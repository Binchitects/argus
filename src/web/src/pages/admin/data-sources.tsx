import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Database, FileSpreadsheet, History, Pencil, Plug, Plus, RefreshCw, Trash2, TriangleAlert, Upload } from 'lucide-react'
import { useId, useRef, useState, type DragEvent } from 'react'
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

type Kind = 'postgres' | 'mysql' | 'sqlserver' | 'files'

/** Each kind of database: its name, its connection's shape, and an account that may only read, made there. */
const kinds: Record<Kind, { label: string; connection: string; reader: string }> = {
  postgres: {
    label: 'PostgreSQL',
    connection: 'Host=db.example.com;Port=5432;Database=shop;Username=arena_reader;Password=…',
    reader: "create role arena_reader login password '…'; grant usage on schema shop to arena_reader; grant select on shop.orders, shop.customers to arena_reader;",
  },
  mysql: {
    label: 'MySQL or MariaDB',
    connection: 'Server=db.example.com;Port=3306;Database=shop;User ID=arena_reader;Password=…',
    reader: "create user 'arena_reader'@'%' identified by '…'; grant select on shop.orders to 'arena_reader'@'%'; grant select on shop.customers to 'arena_reader'@'%';",
  },
  sqlserver: {
    label: 'SQL Server',
    connection: 'Server=db.example.com,1433;Database=shop;User ID=arena_reader;Password=…;Encrypt=True',
    reader: "create login arena_reader with password = '…'; create user arena_reader for login arena_reader; grant select on dbo.orders to arena_reader; grant showplan to arena_reader;",
  },
  files: { label: 'Data files', connection: '', reader: '' },
}

/** What a data files source takes, as the file picker filters them. */
const dataFiles = '.csv,.tsv,.tab,.txt,.xlsx,.json,.jsonl,.ndjson'

interface Source {
  id: string
  name: string
  description: string
  kind: Kind
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
  /** MySQL: the account's grants alone decide what a query reads, so every table it may read is open. */
  grantsOnly?: boolean
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
  const [filing, setFiling] = useState<Source | null>(null)
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
        description="Databases people may ask in words (Workspace → Data, and the chat's Company data tool): PostgreSQL, MySQL or MariaDB, and SQL Server, read only. PostgreSQL and SQL Server run a query only after its plan shows it reads only the tables you open here; MySQL only with an account that may read nothing else."
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
                    <Badge variant="outline" className="shrink-0 font-normal">{kinds[s.kind]?.label ?? s.kind}</Badge>
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
                    {s.kind === 'files' && (
                      <Button size="sm" variant="outline" onClick={() => setFiling(s)}>
                        <FileSpreadsheet /> Files
                      </Button>
                    )}
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
      <Dialog open={filing !== null} onOpenChange={(o) => !o && setFiling(null)}>
        <DialogContent className="sm:max-w-2xl">{filing && <FilesList source={sources.data.find((x) => x.id === filing.id) ?? filing} />}</DialogContent>
      </Dialog>
    </>
  )
}

/** Connect or change: the connection (tried first), its tables to open, what the model should know, who may ask. */
function SourceForm({ source, onClose }: { source: Source | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [kind, setKind] = useState<Kind>(source?.kind ?? 'postgres')
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
    mutationFn: () => api<Probed>('/api/admin/data-sources/probe', { body: { connection: connection.trim() || null, id: source?.id, kind } }),
    onSuccess: (p) => {
      setProbed(p)
      setError(null)
      setTables((t) => (p.grantsOnly ? p.tables.map((x) => x.name) : t.filter((x) => p.tables.some((y) => y.name === x))))
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(), description: description.trim(), connection: connection.trim() || undefined, tables, notes: notes.trim(), reach, groupIds: reach === 'groups' ? groupIds : [],
        mostRows: Number(mostRows), seconds: Number(seconds), kind: source ? undefined : kind,
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
  const grantsOnly = probed?.grantsOnly ?? kind === 'mysql'
  const k = kinds[kind]
  const files = kind === 'files'
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
          {files ? (
            'CSV, TSV, Excel (.xlsx) and JSON files, each a table (each sheet of a workbook too), in a part of the stack\'s own database that only this source reads. Upload them once it is made (Files).'
          ) : (
            <>
              {k.label}. Make it an account that may only read what people should ask, for example: <code className="text-xs">{k.reader}</code>
            </>
          )}
        </DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      {!source && (
        <Segmented
          label="Database"
          value={kind}
          onChange={(v) => {
            setKind(v)
            setProbed(null)
            setTables([])
          }}
          options={(Object.keys(kinds) as Kind[]).map((v) => ({ value: v, label: kinds[v].label }))}
        />
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="Shop" />
        </Field>
        <Field label="What it holds (optional)">
          <Input value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} placeholder="Orders and customers of the web shop" />
        </Field>
      </div>
      {!files && (
      <Field label={source ? 'A new connection (empty keeps the one saved)' : 'Connection'} hint={source ? `Now: ${source.where}` : k.connection}>
        <div className="flex gap-2">
          <Input type="password" autoComplete="off" value={connection} onChange={(e) => setConnection(e.target.value)} className="font-mono text-xs" />
          <Button type="button" variant="outline" onClick={() => probe.mutate()} loading={probe.isPending} disabled={!source && !changedConnection}>
            <Plug /> Try it
          </Button>
        </div>
      </Field>
      )}
      {probed && !files && (
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">
            Tables open to questions <span className="font-normal text-muted-foreground">({probed.where}, {probed.tables.length} the account may read)</span>
          </legend>
          {grantsOnly && <p className="text-xs text-muted-foreground">{k.label} does not show which tables a query reads: every table the account may read is open. To keep one out, take its SELECT away from the account.</p>}
          {probed.warnings.map((w) => (
            <p key={w} className="flex gap-1.5 text-xs text-warning-ink">
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {w}
            </p>
          ))}
          <div className="grid max-h-56 gap-1 overflow-y-auto rounded-lg border p-2 sm:grid-cols-2">
            {probed.tables.map((t) => (
              <div key={t.name} className="flex items-center gap-2 text-sm">
                <Checkbox id={`${id}-${t.name}`} checked={grantsOnly || tables.includes(t.name)} disabled={grantsOnly} onCheckedChange={(on) => setTables(on ? [...tables, t.name] : tables.filter((x) => x !== t.name))} />
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
        <Button type="submit" loading={save.isPending} disabled={!name.trim() || (!source && !files && !changedConnection) || (reach === 'groups' && groupIds.length === 0)}>
          {source ? 'Save' : files ? 'Make it' : 'Connect'}
        </Button>
      </DialogFooter>
    </form>
  )
}

/** A data files source's tables, each from a file or a sheet; files dropped or chosen become tables (one of the same name replaced). */
function FilesList({ source }: { source: Source }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const pick = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const [sending, setSending] = useState(false)
  const refresh = () => void queryClient.invalidateQueries({ queryKey: sourcesKey })
  const upload = async (chosen: File[]) => {
    if (chosen.length === 0) return
    setSending(true)
    try {
      const form = new FormData()
      for (const f of chosen) form.append('files', f, f.name)
      const r = await api<{ imported: { table: string; rows: number }[] }>(`/api/admin/data-sources/${source.id}/files`, { body: form })
      toast.success(`${r.imported.length} ${r.imported.length === 1 ? 'table' : 'tables'} ready`, { description: r.imported.map((i) => `${i.table}: ${i.rows} rows`).join(', ') })
    } catch (e) {
      toast.error('The upload stopped', { description: errorMessage(e) })
    } finally {
      setSending(false)
      refresh()
    }
  }
  const drop = (e: DragEvent) => {
    e.preventDefault()
    setOver(false)
    void upload([...e.dataTransfer.files])
  }
  return (
    <>
      <DialogHeader>
        <DialogTitle>{source.name}: files</DialogTitle>
        <DialogDescription>CSV, TSV, Excel (.xlsx) and JSON, up to 200 MB each. Each becomes a table, its columns typed from their values; a file uploaded again replaces its table.</DialogDescription>
      </DialogHeader>
      <div
        onDragOver={(e) => {
          e.preventDefault()
          setOver(true)
        }}
        onDragLeave={() => setOver(false)}
        onDrop={drop}
        className={`grid place-items-center gap-3 rounded-xl border-2 border-dashed px-4 py-6 text-center text-sm ${over ? 'border-primary bg-primary/5' : 'border-border'}`}
      >
        <Upload className="size-6 text-muted-foreground" aria-hidden="true" />
        <p className="text-muted-foreground">{sending ? 'Reading the files…' : 'Drop data files here'}</p>
        <Button variant="outline" size="sm" disabled={sending} onClick={() => pick.current?.click()}>
          <Upload /> Choose files
        </Button>
        <input ref={pick} type="file" multiple hidden accept={dataFiles} aria-label="Data files to upload" onChange={(e) => void upload([...(e.target.files ?? [])])} />
      </div>
      {source.schema.length === 0 && <p className="text-sm text-muted-foreground">No tables yet.</p>}
      <ul className="grid max-h-[45vh] gap-1 overflow-y-auto text-sm" aria-label="Tables">
        {source.schema.map((t) => (
          <li key={t.name} className="flex min-w-0 items-center gap-3 rounded-md px-2 py-1.5 hover:bg-accent">
            <div className="grid min-w-0 flex-1">
              <span className="truncate font-mono text-xs">{t.name}</span>
              <span className="truncate text-xs text-muted-foreground">
                {t.comment ? `${t.comment} · ` : ''}
                {t.columns.length} {t.columns.length === 1 ? 'column' : 'columns'}: {t.columns.map((c) => c.name).join(', ')}
              </span>
            </div>
            <Button
              variant="ghost"
              size="icon-sm"
              className="text-destructive-ink"
              aria-label={`Remove ${t.name}`}
              onClick={async () => {
                if (await confirm({ title: `Remove ${t.name}?`, description: 'The table goes, and people cannot ask it any more.', confirm: 'Remove', destructive: true })) {
                  await api(`/api/admin/data-sources/${source.id}/files/${encodeURIComponent(t.name.split('.').pop() ?? t.name)}`, { method: 'DELETE' }).catch((e: unknown) => toast.error(errorMessage(e)))
                  refresh()
                }
              }}
            >
              <Trash2 />
            </Button>
          </li>
        ))}
      </ul>
    </>
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
