import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, Boxes, Database, FileStack, HardDrive, RotateCw } from 'lucide-react'
import type { ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Sparkline } from '@/components/dashboards/visuals'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { growth, origins, plural, size, states, storageQuery, uses, type Disk, type LibraryUse, type StorageReport } from './storage-api'
import { Cleanups } from './storage-cleanups'
import { StorageFiles } from './storage-files'
import { StoragePeople } from './storage-people'

const tabs = [
  { value: 'room', label: 'Where the room goes' },
  { value: 'files', label: 'Files' },
  { value: 'cleanups', label: 'Clean-ups' },
  { value: 'people', label: 'People' },
] as const

type Tab = (typeof tabs)[number]['value']

/**
 * Admin → Storage: the disks and what takes room on them (the databases, the chat's files, the model
 * library, Argus, backups, logs and metrics), how long each is kept and what the app cannot see; the
 * chat's files to browse, download and delete; the clean-ups; and each person's room for files.
 */
export function StoragePage() {
  const [params, setParams] = useSearchParams()
  const tab: Tab = tabs.some((t) => t.value === params.get('tab')) ? (params.get('tab') as Tab) : 'room'
  const report = useQuery({ ...storageQuery, staleTime: 60_000 })
  const client = useQueryClient()
  // Every folder measured now, not as measured in the last two minutes.
  const again = useMutation({
    mutationFn: () => api<StorageReport>('/api/admin/storage?fresh=true'),
    onSuccess: (r) => client.setQueryData(storageQuery.queryKey, r),
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <>
      <PageHeader
        title="Storage"
        description="What takes room and where, how long it stays, and ways to free it. Nothing of a person on legal hold is ever deleted here."
        actions={
          <Button variant="outline" size="sm" onClick={() => again.mutate()} loading={again.isPending || report.isFetching}>
            <RotateCw /> Measure again
          </Button>
        }
      />
      <Tabs value={tab} onValueChange={(v) => setParams(v === 'room' ? {} : { tab: v }, { replace: true })}>
        <TabsList className="max-w-full overflow-x-auto">
          {tabs.map((t) => (
            <TabsTrigger key={t.value} value={t.value}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="room">{report.isPending ? <PageSkeleton /> : report.error ? <QueryError error={report.error} retry={() => report.refetch()} /> : <Room r={report.data} />}</TabsContent>
        <TabsContent value="files">
          <StorageFiles />
        </TabsContent>
        <TabsContent value="cleanups">
          <Cleanups settings={report.data?.settings} />
        </TabsContent>
        <TabsContent value="people">
          <StoragePeople personMegabytes={report.data?.settings.personMegabytes ?? null} />
        </TabsContent>
      </Tabs>
    </>
  )
}

/** The disks, what takes room, and the rules: the first tab. */
function Room({ r }: { r: StorageReport }) {
  const fullest = r.disks.list[0]
  const above = r.disks.list.filter((d) => d.above)
  const databases = r.databases.list.reduce((a, d) => a + d.bytes, 0)
  return (
    <div className="grid gap-6">
      <StatGrid>
        <Stat
          icon={HardDrive}
          label="Fullest disk"
          value={fullest ? `${Math.round(fullest.percent)}%` : '—'}
          tone={fullest?.above ? 'warning' : undefined}
          hint={fullest ? `${fullest.name} · ${size(fullest.free)} free` : 'no disk seen'}
        />
        <Stat icon={Database} label="Databases" value={size(databases)} hint={plural(r.databases.list.length, 'database')} />
        <Stat icon={FileStack} label="The chat's files" value={size(r.files.total?.bytes)} hint={r.files.total ? plural(r.files.total.count, 'file') : undefined} />
        <Stat icon={Boxes} label="Model library" value={size(r.library.report?.bytes)} hint={r.library.report ? plural(r.library.report.files.length, 'model file') : 'not there'} />
        <Stat icon={Archive} label="Backups" value={r.backups.state === 'ok' ? size(r.backups.bytes) : '—'} hint={r.backups.state === 'ok' ? plural(r.backups.backups.length, 'backup') : 'not seen'} />
      </StatGrid>
      {above.map((d) => (
        <Alert
          key={d.id}
          variant="warning"
          title={`${d.name} is ${Math.round(d.percent)}% full`}
          action={
            <Button size="sm" variant="outline" asChild>
              <Link to="/admin/storage?tab=cleanups">Open the clean-ups</Link>
            </Button>
          }
        >
          {size(d.free)} free of {size(d.size)}; the alert is at {r.disks.alertPercent}% (Settings → Storage).{d.holds.length > 0 && ` On it: ${d.holds.join('; ')}.`}
        </Alert>
      ))}
      {r.disks.problem && <Alert variant="warning">{r.disks.problem}</Alert>}
      <Disks disks={r.disks.list} threshold={r.disks.alertPercent} />
      <WhatTakesRoom r={r} />
      <div className="grid gap-6 xl:grid-cols-2">
        <Tables r={r} />
        <FilesByKind r={r} />
      </div>
      <Library r={r} />
      <div className="grid gap-6 xl:grid-cols-2">
        <ArgusCard r={r} />
        <Backups r={r} />
      </div>
      <div className="grid gap-6 xl:grid-cols-2">
        <Logs r={r} />
        <Rules r={r} />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>What the app cannot see</CardTitle>
          <CardDescription>The app has no Docker socket (that would be root on the host for anyone who reaches it).</CardDescription>
        </CardHeader>
        <CardContent>
          <ul className="grid list-disc gap-1.5 pl-5 text-sm">
            {r.unseen.map((u) => (
              <li key={u}>
                <Ticks text={u} />
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>
      <p className="text-xs text-muted-foreground">Measured {ago(r.at)}. Folders are measured again at most every two minutes, or now with Measure again.</p>
    </div>
  )
}

/** `code` in a sentence, as code. */
function Ticks({ text }: { text: string }) {
  return (
    <>
      {text.split(/(`[^`]+`)/).map((part, i) =>
        part.startsWith('`') ? (
          <code key={i} className="font-mono text-xs">
            {part.slice(1, -1)}
          </code>
        ) : (
          part
        ),
      )}
    </>
  )
}

/** Used and free, as a bar whose fill turns at the alert's share. */
function Meter({ percent, above }: { percent: number; above: boolean }) {
  return (
    <div className="h-2 w-full overflow-hidden rounded-full bg-muted" aria-hidden="true">
      <div className={cn('h-full rounded-full', above ? 'bg-warning' : 'bg-primary')} style={{ width: `${Math.min(100, Math.max(0, percent))}%` }} />
    </div>
  )
}

function Disks({ disks, threshold }: { disks: Disk[]; threshold: number }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Disks</CardTitle>
        <CardDescription>The host's, from node-exporter, and the folders the app mounts, matched to the disk they are on. An alert fires past {threshold}%.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {disks.length === 0 && <p className="text-sm text-muted-foreground">No disk seen.</p>}
        {disks.map((d) => (
          <div key={d.id} className="grid gap-1.5">
            <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
              <p className="min-w-0 font-medium break-all">
                {d.name}
                <span className="ml-2 text-xs font-normal text-muted-foreground">
                  {[d.device, d.fsType, d.source === 'app' ? 'as the app sees it' : null].filter(Boolean).join(' · ')}
                </span>
              </p>
              <p className="text-sm tabular-nums">
                <span className={cn('font-semibold', d.above && 'text-warning-ink')}>{Math.round(d.percent)}%</span>
                <span className="text-muted-foreground"> · {size(d.free)} free of {size(d.size)}</span>
              </p>
            </div>
            <Meter percent={d.percent} above={d.above} />
            <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
              <span>{d.holds.length > 0 ? `On it: ${d.holds.join('; ')}` : ''}</span>
              <span className="flex items-center gap-2">
                {d.perDay !== null && (d.perDay > 0 ? `${size(d.perDay)} a day` : d.perDay < 0 ? `${size(-d.perDay)} freed a day` : 'steady')}
                {d.fullInDays !== null && d.fullInDays < 365 && <Badge variant={d.fullInDays < 14 ? 'warning' : 'secondary'}>full in about {Math.max(1, Math.round(d.fullInDays))} days</Badge>}
                <Sparkline points={d.trend} className="h-6 w-24 text-primary" />
              </span>
            </div>
          </div>
        ))}
      </CardContent>
    </Card>
  )
}

interface Row {
  what: string
  where: string
  bytes: number | null
  trend?: string
  note?: ReactNode
}

const right = { className: 'text-right whitespace-nowrap tabular-nums' }

const roomColumns = (r: StorageReport): ColumnDef<Row>[] => [
  {
    id: 'what',
    header: 'What',
    accessorKey: 'what',
    meta: { className: 'align-top' },
    cell: ({ row: { original: row } }) => (
      <>
        <p className="font-medium">{row.what}</p>
        {row.note && <p className="text-xs text-muted-foreground">{row.note}</p>}
      </>
    ),
  },
  { id: 'where', header: 'Where', accessorKey: 'where', meta: { className: 'align-top text-muted-foreground' } },
  { id: 'bytes', header: 'Takes', accessorFn: (row) => row.bytes ?? -1, meta: right, cell: ({ row }) => size(row.original.bytes) },
  {
    id: 'grew',
    header: '30 days',
    accessorFn: (row) => (row.trend ? growth(r.trends[row.trend]) : null) ?? Number.NEGATIVE_INFINITY,
    meta: { className: 'text-right whitespace-nowrap text-muted-foreground tabular-nums' },
    cell: ({ row: { original: row } }) => {
      const grew = row.trend ? growth(r.trends[row.trend]) : null
      return grew === null ? '—' : `${grew >= 0 ? '+' : '−'}${size(Math.abs(grew))}`
    },
  },
  {
    id: 'trend',
    header: () => <span className="sr-only">Trend</span>,
    enableSorting: false,
    cell: ({ row: { original: row } }) => row.trend && r.trends[row.trend] && <Sparkline points={r.trends[row.trend]!} className="h-6 w-24 text-primary" />,
  },
]

const libraryColumns: ColumnDef<LibraryUse>[] = [
  {
    id: 'path',
    header: 'File',
    accessorKey: 'path',
    meta: { className: 'align-top' },
    cell: ({ row: { original: f } }) => (
      <>
        <code className="font-mono text-xs break-all">{f.path}</code>
        <p className="text-xs text-muted-foreground">{[f.kind, f.parts > 1 ? `${f.parts} parts` : null, f.note].filter(Boolean).join(' · ')}</p>
      </>
    ),
  },
  {
    id: 'use',
    header: 'Used by',
    accessorFn: (f) => (f.use === 'engine' ? f.models.join('; ') : uses[f.use]),
    cell: ({ row: { original: f } }) => (f.use === 'unused' ? <Badge variant="warning">{uses.unused}</Badge> : f.use === 'engine' ? f.models.join('; ') : uses[f.use]),
  },
  { id: 'bytes', header: 'Takes', accessorKey: 'bytes', meta: right, cell: ({ row }) => size(row.original.bytes) },
]

/** Each kind of thing kept, its room, where it lives, and how it grew over 30 days. */
function WhatTakesRoom({ r }: { r: StorageReport }) {
  const a = r.argus.report
  const rows: Row[] = [
    ...r.databases.list.map((d) => ({ what: `Database ${d.name}`, where: 'Postgres', bytes: d.bytes, trend: `db:${d.name}`, note: d.what })),
    { what: "The chat's files", where: "Inside the app's database", bytes: r.files.total?.bytes ?? null, trend: 'files', note: 'Uploads, and what the tools made: part of the database above.' },
    { what: 'Model library', where: `MODELS_DIR (${r.library.report?.dir ?? '/library'})`, bytes: r.library.report?.bytes ?? null, trend: 'models', note: r.library.problem },
    ...(r.argus.configured
      ? [
          { what: "Argus's index", where: "Argus's volume", bytes: a ? a.index_bytes + a.mirrors_bytes + a.trees_bytes + a.other_bytes : null, trend: 'argus', note: a ? `Index ${size(a.index_bytes)}, GitLab mirrors ${size(a.mirrors_bytes)}, trees ${size(a.trees_bytes)}` : r.argus.problem },
          { what: 'Knowledge packs', where: "Argus's volume", bytes: a?.packs_bytes ?? null, trend: 'packs', note: a?.library_dir ? `The pack library (${a.library_dir}) holds ${size(a.library_bytes)} more; a loaded pack is a link to it.` : undefined },
        ]
      : []),
    { what: 'Backups', where: `BACKUP_DIR (${r.backups.dir})`, bytes: r.backups.state === 'ok' ? r.backups.bytes + r.backups.otherBytes : null, trend: 'backups', note: backupNote(r) },
    { what: 'Metrics (Prometheus)', where: "Docker's data", bytes: r.metrics.bytes, trend: 'metrics', note: r.metrics.problem ?? (r.metrics.keeps ? `Kept ${r.metrics.keeps}${r.metrics.maxSize ? `, at most ${r.metrics.maxSize}` : ''}` : undefined) },
    { what: 'Logs (Loki)', where: "Docker's data", bytes: null, note: r.logs.problem ?? `Not measured here. ${r.logs.keeps ? `Kept ${r.logs.keeps}.` : ''}` },
    { what: 'Docker images and the other volumes', where: 'Docker', bytes: null, note: 'Not visible to the app: see below.' },
  ]
  return (
    <Card>
      <CardHeader>
        <CardTitle>What takes room</CardTitle>
        <CardDescription>Each kind of thing the stack keeps, where it lives, and how it grew over the last 30 days (measured every six hours).</CardDescription>
      </CardHeader>
      <CardContent>
        <DataTable columns={roomColumns(r)} data={rows} noun="kinds" label="What takes room" compact getRowId={(row) => row.what} />
      </CardContent>
    </Card>
  )
}

function backupNote(r: StorageReport): string | undefined {
  if (r.backups.state === 'missing') return `Not mounted in the app: ${r.backups.dir} is not there.`
  if (r.backups.state === 'unreadable') return 'The app may not read them: they belong to another user (scripts/backup.sh ran as root?).'
  return r.backups.otherBytes > 0 ? `Besides the backups, ${size(r.backups.otherBytes)} of other files there.` : undefined
}

function Tables({ r }: { r: StorageReport }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Largest tables</CardTitle>
        <CardDescription>Of the app's and the gateway's databases, with their indexes and long values.</CardDescription>
      </CardHeader>
      <CardContent>
        {r.databases.problem && <Alert variant="warning" className="mb-3">{r.databases.problem}</Alert>}
        <ul className="grid gap-1.5 text-sm">
          {[...r.databases.tables].sort((a, b) => b.bytes - a.bytes).slice(0, 12).map((t) => (
            <li key={`${t.database}.${t.name}`} className="flex items-baseline justify-between gap-3">
              <span className="min-w-0">
                <code className="font-mono text-xs break-all">{t.name}</code>
                <span className="ml-2 text-xs text-muted-foreground">{[t.database, t.what, t.rows ? `${t.rows.toLocaleString('en-US')} rows` : null].filter(Boolean).join(' · ')}</span>
              </span>
              <span className="tabular-nums whitespace-nowrap">{size(t.bytes)}</span>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

function FilesByKind({ r }: { r: StorageReport }) {
  const groups = r.files.groups ?? []
  const by = <K extends string>(key: (g: (typeof groups)[number]) => K) =>
    Object.entries(groups.reduce<Record<string, { count: number; bytes: number }>>((acc, g) => {
      const k = key(g)
      acc[k] = { count: (acc[k]?.count ?? 0) + g.count, bytes: (acc[k]?.bytes ?? 0) + g.bytes }
      return acc
    }, {})).sort((a, b) => b[1].bytes - a[1].bytes)
  return (
    <Card>
      <CardHeader>
        <CardTitle>The chat's files</CardTitle>
        <CardDescription>Where they came from, and whether a chat still has them. The <strong className="font-medium">Files</strong> tab lists each.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 sm:grid-cols-2">
        {r.files.problem && <Alert variant="warning" className="sm:col-span-2">{r.files.problem}</Alert>}
        {[
          { title: 'From', rows: by((g) => g.origin).map(([k, v]) => [origins[k as keyof typeof origins] ?? k, v] as const) },
          { title: 'Where', rows: by((g) => g.state).map(([k, v]) => [states[k as keyof typeof states] ?? k, v] as const) },
        ].map((part) => (
          <div key={part.title}>
            <h3 className="mb-1.5 text-xs font-medium text-muted-foreground">{part.title}</h3>
            <ul className="grid gap-1 text-sm">
              {part.rows.map(([label, v]) => (
                <li key={label} className="flex justify-between gap-3">
                  <span>{label}</span>
                  <span className="tabular-nums text-muted-foreground">
                    {plural(v.count, 'file')} · <span className="text-foreground">{size(v.bytes)}</span>
                  </span>
                </li>
              ))}
              {part.rows.length === 0 && <li className="text-muted-foreground">No files yet.</li>}
            </ul>
          </div>
        ))}
        {(r.files.people?.length ?? 0) > 0 && (
          <div className="sm:col-span-2">
            <h3 className="mb-1.5 text-xs font-medium text-muted-foreground">Whose</h3>
            <ul className="grid gap-1 text-sm">
              {r.files.people!.slice(0, 5).map((p) => (
                <li key={p.id} className="flex justify-between gap-3">
                  <span className="min-w-0 truncate">{p.displayName || p.userName}</span>
                  <span className="tabular-nums">{size(p.bytes)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function Library({ r }: { r: StorageReport }) {
  const lib = r.library.report
  return (
    <Card>
      <CardHeader>
        <CardTitle>Model library</CardTitle>
        <CardDescription>
          Each model file and what uses it. One nothing uses can go under <strong className="font-medium">Clean-ups</strong>; models are added and loaded under{' '}
          <Link to="/admin/models" className="underline underline-offset-2">Models</Link>.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {r.library.problem && <Alert variant="warning">{r.library.problem}</Alert>}
        {lib && !lib.exists && <p className="text-sm text-muted-foreground">The library ({lib.dir}) is not there.</p>}
        {lib && lib.files.length > 0 && (
          <DataTable columns={libraryColumns} data={lib.files} noun="model files" label="Model files" compact getRowId={(f) => f.path} initialSorting={[{ id: 'bytes', desc: true }]} />
        )}
        {lib && lib.partials.length > 0 && (
          <div>
            <h3 className="mb-1.5 text-xs font-medium text-muted-foreground">Downloads not finished</h3>
            <ul className="grid gap-1 text-sm">
              {lib.partials.map((p) => (
                <li key={p.path} className="flex justify-between gap-3">
                  <span className="min-w-0">
                    <code className="font-mono text-xs break-all">{p.path}</code>
                    <span className="ml-2 text-xs text-muted-foreground">{p.download ? `the download of ${p.download}` : 'no download owns it: a leftover'}</span>
                  </span>
                  <span className="tabular-nums">{size(p.bytes)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function ArgusCard({ r }: { r: StorageReport }) {
  const a = r.argus.report
  return (
    <Card>
      <CardHeader>
        <CardTitle>Argus</CardTitle>
        <CardDescription>The code index and the knowledge packs, as Argus measures its own disk.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3 text-sm">
        {!r.argus.configured && <p className="text-muted-foreground">Argus is not set up.</p>}
        {r.argus.problem && <Alert variant="warning">{r.argus.problem}</Alert>}
        {a && (
          <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1">
            {[
              ['The index', a.index_bytes],
              ['GitLab mirrors', a.mirrors_bytes],
              ['Checked-out trees', a.trees_bytes],
              ['Packs installed', a.packs_bytes],
              ['The rest', a.other_bytes],
            ].map(([k, v]) => (
              <div key={k as string} className="contents">
                <dt>{k}</dt>
                <dd className="text-right tabular-nums">{size(v as number)}</dd>
              </div>
            ))}
            {a.disk && (
              <div className="contents text-muted-foreground">
                <dt>Its disk</dt>
                <dd className="text-right tabular-nums">{size(a.disk.free_bytes)} free of {size(a.disk.size_bytes)}</dd>
              </div>
            )}
          </dl>
        )}
        {(r.argus.packs?.length ?? 0) > 0 && (
          <ul className="grid gap-1 border-t pt-3">
            {r.argus.packs!.map((p) => (
              <li key={p.name} className="flex justify-between gap-3">
                <span>
                  {p.name} <span className="text-xs text-muted-foreground">{p.version}{p.source === 'library' ? ' · from the library (a link)' : ''}</span>
                </span>
                <span className="tabular-nums">{size(p.size_bytes)}</span>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function Backups({ r }: { r: StorageReport }) {
  const b = r.backups
  return (
    <Card>
      <CardHeader>
        <CardTitle>Backups</CardTitle>
        <CardDescription>
          What <code className="font-mono text-xs">scripts/backup.sh</code> keeps in {b.dir}: one folder a backup.
        </CardDescription>
      </CardHeader>
      <CardContent className="text-sm">
        {b.state !== 'ok' && <Alert variant="warning">{backupNote(r)}</Alert>}
        {b.state === 'ok' && b.backups.length === 0 && <p className="text-muted-foreground">No backup yet.</p>}
        <ul className="grid gap-1">
          {b.backups.map((x) => (
            <li key={x.name} className="flex flex-wrap items-center justify-between gap-2">
              <span className="flex items-center gap-2">
                <span className="font-mono text-xs">{x.name}</span>
                {x.latest && <Badge variant="secondary">latest</Badge>}
                {x.result && x.result !== 'ok' && <Badge variant="destructive">{x.result}</Badge>}
              </span>
              <span className="tabular-nums">{size(x.bytes)}</span>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

function Logs({ r }: { r: StorageReport }) {
  const most = Math.max(1, ...r.logs.week.map((w) => w.bytes))
  return (
    <Card>
      <CardHeader>
        <CardTitle>Logs and metrics</CardTitle>
        <CardDescription>
          Loki keeps logs {r.logs.keeps ? (r.logs.keeps === 'forever' ? 'forever: nothing deletes them' : `for ${r.logs.keeps}`) : '(its period is not known)'}; Prometheus keeps metrics
          {r.metrics.keeps ? ` for ${r.metrics.keeps}` : ''}{r.metrics.maxSize ? `, at most ${r.metrics.maxSize}` : ''}
          {r.metrics.oldest ? ` (the oldest from ${when(r.metrics.oldest)})` : ''}.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-2 text-sm">
        {r.logs.problem && <p className="text-muted-foreground">{r.logs.problem}</p>}
        {r.logs.week.length > 0 && (
          <>
            <h3 className="text-xs font-medium text-muted-foreground">
              What each container wrote in the last 7 days, before compression{r.logs.weekStored !== null ? ` (Loki stored ${size(r.logs.weekStored)})` : ''}
            </h3>
            <ul className="grid gap-1.5">
              {r.logs.week.slice(0, 10).map((w) => (
                <li key={w.name} className="grid grid-cols-[8rem_1fr_auto] items-center gap-2">
                  <span className="truncate">{w.name}</span>
                  <span className="h-1.5 rounded-full bg-primary/70" style={{ width: `${(100 * w.bytes) / most}%` }} aria-hidden="true" />
                  <span className="tabular-nums">{size(w.bytes)}</span>
                </li>
              ))}
            </ul>
          </>
        )}
      </CardContent>
    </Card>
  )
}

function Rules({ r }: { r: StorageReport }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>How long things stay</CardTitle>
        <CardDescription>
          The rules in force. Change them under <Link to="/admin/settings#data-retention" className="underline underline-offset-2">Settings</Link>.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <dl className="grid gap-2 text-sm">
          {r.rules.map((x) => (
            <div key={x.what}>
              <dt className="font-medium">{x.what}</dt>
              <dd className="text-muted-foreground">{x.rule}</dd>
            </div>
          ))}
        </dl>
      </CardContent>
    </Card>
  )
}
