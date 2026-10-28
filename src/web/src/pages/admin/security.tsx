import { useQuery } from '@tanstack/react-query'
import { AlarmClock, CalendarClock, CheckCheck, FlameKindling, ShieldAlert, Sparkles } from 'lucide-react'
import { Link, useNavigate } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { api } from '@/lib/api'
import { ago } from '@/lib/format'

type Severity = 'critical' | 'high' | 'medium' | 'low'
const severities: Severity[] = ['critical', 'high', 'medium', 'low']
const severityVariant = { critical: 'destructive', high: 'destructive', medium: 'warning', low: 'secondary' } as const

interface Repository {
  scanId: string
  project: string
  ref: string | null
  commit: string | null
  finishedAt: string
  advisoriesAsOf: string | null
  recheckedAt: string | null
  sbom: boolean
  by: string | null
  open: Record<Severity, number>
  exploited: number
  overdue: number
  fresh: number
}

interface Row {
  id: string
  scanId: string
  project: string
  ref?: string | null
  kind: 'code' | 'dependency' | 'secret' | 'config'
  severity: Severity
  exploited?: boolean
  title: string
  path?: string
  line?: number
  cwe?: string
  foundAt?: string
  overdue?: boolean
}

interface Overview {
  repositories: Repository[]
  totals: { open: Record<Severity, number>; exploited: number; overdue: number; fresh: number; fixedThisWeek: number }
  fresh: Row[]
  exploited: Row[]
  overdue: Row[]
  fixedThisWeek: (Row & { decidedAt: string; by: string | null })[]
  accepted: (Row & { why: string | null; until: string; decidedAt: string | null; by: string | null; lapsed: boolean })[]
  advisories: { asOf: string; daysOld: number } | null
}

const securityOverviewQuery = {
  queryKey: ['admin', 'security'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Overview>('/api/admin/security', { signal }),
}

const kindWord = { code: 'code', dependency: 'package', secret: 'secret', config: 'configuration' } as const

function FindingList({ rows, label, empty }: { rows: Row[]; label: string; empty: string }) {
  if (rows.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>
  return (
    <ul className="grid gap-2" aria-label={label}>
      {rows.map((f) => (
        <li key={f.id} className="flex flex-wrap items-center gap-2 rounded-lg border p-3 text-sm">
          <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
          {f.exploited && <Badge variant="destructive">known exploited</Badge>}
          {f.overdue && <Badge variant="warning">past its time to fix</Badge>}
          <Badge variant="outline">{kindWord[f.kind]}</Badge>
          <Link to={`/code/security/${f.scanId}`} className="min-w-0 flex-1 font-medium underline-offset-2 hover:underline [overflow-wrap:anywhere]">
            {f.title}
          </Link>
          <span className="text-xs text-muted-foreground [overflow-wrap:anywhere]">
            {f.project}
            {f.path && ` · ${f.path}${f.line ? `:${f.line}` : ''}`}
            {f.foundAt && ` · found ${ago(f.foundAt)}`}
          </span>
        </li>
      ))}
    </ul>
  )
}

/** Admin → Security: every repository's latest security review, what is open and urgent, what changed this week, the risks accepted. */
export function SecurityPage() {
  const navigate = useNavigate()
  const q = useQuery(securityOverviewQuery)
  if (q.error) return <QueryError error={q.error} retry={() => q.refetch()} />
  if (!q.data) return <PageSkeleton />
  const d = q.data
  const t = d.totals
  const columns: ColumnDef<Repository>[] = [
    {
      accessorKey: 'project',
      header: 'Repository',
      cell: ({ row }) => (
        <span className="[overflow-wrap:anywhere]">
          {row.original.project}
          {row.original.ref && <span className="text-muted-foreground"> @ {row.original.ref}</span>}
        </span>
      ),
    },
    ...severities.map(
      (v): ColumnDef<Repository> => ({
        id: v,
        header: v[0].toUpperCase() + v.slice(1),
        accessorFn: (r) => r.open[v],
        cell: ({ row }) => (row.original.open[v] > 0 ? <Badge variant={severityVariant[v]}>{row.original.open[v]}</Badge> : <span className="text-muted-foreground">0</span>),
      }),
    ),
    { accessorKey: 'exploited', header: 'Exploited', cell: ({ row }) => (row.original.exploited > 0 ? <Badge variant="destructive">{row.original.exploited}</Badge> : '0') },
    { accessorKey: 'overdue', header: 'Past due', cell: ({ row }) => (row.original.overdue > 0 ? <Badge variant="warning">{row.original.overdue}</Badge> : '0') },
    {
      accessorKey: 'finishedAt',
      header: 'Reviewed',
      cell: ({ row }) => (
        <span className="text-xs text-muted-foreground">
          {ago(row.original.finishedAt)}
          {row.original.by && ` by ${row.original.by}`}
          {row.original.recheckedAt && `, packages checked ${ago(row.original.recheckedAt)}`}
        </span>
      ),
    },
    {
      id: 'sbom',
      header: 'SBOM',
      cell: ({ row }) =>
        row.original.sbom ? (
          <a href={`/api/code/security/${row.original.scanId}/sbom`} download onClick={(e) => e.stopPropagation()} className="text-xs underline-offset-2 hover:underline">
            Download
          </a>
        ) : null,
    },
  ]
  return (
    <>
      <PageHeader
        title="Security"
        description="Every repository's latest security review: what is open, what cannot wait, what changed this week, and the risks accepted. Reviews are run in Code Studio → Security."
      />
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
        {d.advisories === null ? (
          <Alert variant="warning">No advisories pack is installed: packages are not checked for known vulnerabilities (Admin → Packs).</Alert>
        ) : (
          d.advisories.daysOld > 7 && (
            <Alert variant="warning">
              The advisories are {d.advisories.daysOld} days old ({d.advisories.asOf}): renew them in Admin → Packs.
            </Alert>
          )
        )}
        <StatGrid>
          <Stat label="Open" value={severities.reduce((n, v) => n + t.open[v], 0)} hint={severities.map((v) => `${t.open[v]} ${v}`).join(' · ')} icon={ShieldAlert} />
          <Stat label="Known exploited" value={t.exploited} icon={FlameKindling} tone={t.exploited > 0 ? 'destructive' : undefined} hint="On CISA's list" />
          <Stat label="Past their time to fix" value={t.overdue} icon={AlarmClock} tone={t.overdue > 0 ? 'warning' : undefined} hint="7, 30, 90, 180 days by severity" />
          <Stat label="New this week" value={t.fresh} icon={Sparkles} hint="Found in the last 7 days" />
          <Stat label="Fixed this week" value={t.fixedThisWeek} icon={CheckCheck} hint={d.advisories ? `Advisories of ${d.advisories.asOf}` : undefined} />
        </StatGrid>
        {d.repositories.length === 0 ? (
          <EmptyState icon={ShieldAlert} title="No repository has been reviewed yet">
            Review one in Code Studio → Security: its packages, its code and, with the scanners, its secrets and configuration.
          </EmptyState>
        ) : (
          <Card>
            <CardHeader>
              <CardTitle>Repositories</CardTitle>
              <CardDescription>Each repository and branch's latest review, the known-exploited and most severe first.</CardDescription>
            </CardHeader>
            <CardContent>
              <DataTable columns={columns} data={d.repositories} noun="repositories" getRowId={(r) => r.scanId} onRowClick={(r) => navigate(`/code/security/${r.scanId}`)} />
            </CardContent>
          </Card>
        )}
        <div className="grid gap-6 xl:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>Cannot wait</CardTitle>
              <CardDescription>Known to be exploited, and past their time to fix.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <FindingList rows={d.exploited} label="Known exploited" empty="Nothing open is known to be exploited." />
              <FindingList rows={d.overdue.filter((f) => !f.exploited)} label="Past their time to fix" empty="Nothing open is past its time to fix." />
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>This week</CardTitle>
              <CardDescription>Found in the last 7 days (reviews, and packages checked against renewed advisories), and fixed.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <FindingList rows={d.fresh} label="New this week" empty="Nothing new this week." />
              {d.fixedThisWeek.length > 0 && (
                <ul className="grid gap-1 text-sm" aria-label="Fixed this week">
                  {d.fixedThisWeek.map((f) => (
                    <li key={f.id} className="flex flex-wrap items-center gap-2">
                      <CheckCheck className="size-4 text-success-ink" aria-hidden="true" />
                      <Link to={`/code/security/${f.scanId}`} className="underline-offset-2 hover:underline">
                        {f.title}
                      </Link>
                      <span className="text-xs text-muted-foreground">
                        {f.project}, {ago(f.decidedAt)}
                        {f.by && ` by ${f.by}`}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        </div>
        <Card>
          <CardHeader>
            <CardTitle>Risks accepted</CardTitle>
            <CardDescription>Kept as they are on purpose: by whom, why, and until when they are looked at again.</CardDescription>
          </CardHeader>
          <CardContent>
            {d.accepted.length === 0 ? (
              <p className="text-sm text-muted-foreground">No risk is accepted.</p>
            ) : (
              <ul className="grid gap-2" aria-label="Risks accepted">
                {d.accepted.map((f) => (
                  <li key={f.id} className="grid gap-1 rounded-lg border p-3 text-sm">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
                      <Link to={`/code/security/${f.scanId}`} className="font-medium underline-offset-2 hover:underline">
                        {f.title}
                      </Link>
                      <span className="text-xs text-muted-foreground">{f.project}</span>
                      <Badge variant={f.lapsed ? 'warning' : 'secondary'} className="ml-auto">
                        <CalendarClock aria-hidden="true" /> {f.lapsed ? 'lapsed' : 'until'} {new Date(f.until).toLocaleDateString()}
                      </Badge>
                    </div>
                    <p className="text-muted-foreground">
                      {f.by ? `${f.by}: ` : ''}
                      {f.why}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>
    </>
  )
}
