import { useInfiniteQuery } from '@tanstack/react-query'
import { CheckCircle2, Download, XCircle } from 'lucide-react'
import { useMemo, useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { api } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { toCsv } from './audit-csv'

export interface AuditEvent {
  id: number
  at: string
  actor: string | null
  action: string
  target: string | null
  success: boolean
  ip: string | null
  detail: string | null
}

const PAGE = 500

/** What kind of event, for the filter. */
function kind(action: string): 'sign-in' | 'people' | 'settings' | 'argus' | 'other' {
  if (action.startsWith('sign_') || action.startsWith('account.')) return 'sign-in'
  if (action.startsWith('person.') || action === 'ldap.sync') return 'people'
  if (action.startsWith('settings.')) return 'settings'
  if (action.startsWith('argus.')) return 'argus'
  return 'other'
}

const columns: ColumnDef<AuditEvent>[] = [
  {
    id: 'at',
    accessorFn: (e) => new Date(e.at).getTime(),
    header: ({ column }) => <SortHeader column={column} title="When" />,
    cell: ({ row: { original: e } }) => (
      <time dateTime={e.at} title={when(e.at)} aria-label={`${ago(e.at)}, ${when(e.at)}`} className="whitespace-nowrap text-muted-foreground">
        {ago(e.at)}
      </time>
    ),
  },
  { accessorKey: 'actor', header: 'Who', cell: ({ getValue }) => <span className="font-medium">{getValue<string | null>() ?? '—'}</span> },
  {
    accessorKey: 'action',
    header: 'What',
    cell: ({ row: { original: e } }) => (
      <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
        {e.success ? <CheckCircle2 className="size-3.5 text-success" aria-hidden="true" /> : <XCircle className="size-3.5 text-destructive" aria-hidden="true" />}
        <code className="font-mono text-xs">{e.action}</code>
        {!e.success && <Badge variant="destructive">Failed</Badge>}
      </span>
    ),
  },
  { accessorKey: 'target', header: 'To whom / what', cell: ({ getValue }) => getValue<string | null>() ?? '—' },
  { accessorKey: 'ip', header: 'From', cell: ({ getValue }) => <span className="font-mono text-xs text-muted-foreground">{getValue<string | null>() ?? '—'}</span> },
  { accessorKey: 'detail', header: 'Detail', cell: ({ getValue }) => <span className="line-clamp-2 max-w-md text-muted-foreground">{getValue<string | null>() ?? ''}</span> },
]

export function AuditPage() {
  const [filter, setFilter] = useState<'all' | 'failed' | 'sign-in' | 'people' | 'settings' | 'argus'>('all')
  const events = useInfiniteQuery({
    queryKey: ['admin', 'audit'],
    initialPageParam: null as number | null,
    queryFn: ({ pageParam, signal }) => api<AuditEvent[]>(`/api/admin/audit?take=${PAGE}${pageParam ? `&before=${pageParam}` : ''}`, { signal }),
    getNextPageParam: (last) => (last.length === PAGE ? last[last.length - 1]!.id : undefined),
  })
  const all = useMemo(() => events.data?.pages.flat() ?? [], [events.data])
  const shown = all.filter((e) => (filter === 'all' ? true : filter === 'failed' ? !e.success : kind(e.action) === filter))
  const download = () => {
    const blob = new Blob([toCsv(shown)], { type: 'text/csv' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `audit-${new Date().toISOString().slice(0, 10)}.csv`
    a.click()
    URL.revokeObjectURL(a.href)
  }
  return (
    <>
      <PageHeader
        title="Audit log"
        description="Every sign-in and every change to people, settings and the code index: who, to whom, from where."
        actions={
          <Button variant="outline" onClick={download} disabled={!shown.length}>
            <Download /> Export CSV
          </Button>
        }
      />
      {events.error ? (
        <QueryError error={events.error} retry={() => events.refetch()} />
      ) : (
        <DataTable
          columns={columns}
          data={events.isPending ? undefined : shown}
          loading={events.isPending}
          noun="events"
          pageSize={50}
          getRowId={(e) => String(e.id)}
          initialSorting={[{ id: 'at', desc: true }]}
          toolbar={
            <Segmented
              label="Show"
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'all', label: 'All' },
                { value: 'failed', label: 'Failed' },
                { value: 'sign-in', label: 'Sign-ins' },
                { value: 'people', label: 'People' },
                { value: 'settings', label: 'Settings' },
                { value: 'argus', label: 'Argus' },
              ]}
            />
          }
        />
      )}
      {events.hasNextPage && (
        <div className="mt-3 flex justify-center">
          <Button variant="outline" loading={events.isFetchingNextPage} onClick={() => events.fetchNextPage()}>
            Load older events
          </Button>
        </div>
      )}
    </>
  )
}
