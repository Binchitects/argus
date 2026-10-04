import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Timer } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { RangeSelect } from '@/components/app/range-select'
import { Badge } from '@/components/ui/badge'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { api } from '@/lib/api'
import { ago, formatValue, when } from '@/lib/format'
import { resolve } from '@/lib/time'
import { seconds, toolTitle } from '@/pages/chat/format'
import { TraceSheet } from './trace-view'
import type { TraceSummary } from './traces-api'

const columns = (open: (id: string) => void): ColumnDef<TraceSummary>[] => [
  {
    id: 'ms',
    accessorFn: (t) => t.ms,
    header: ({ column }) => <SortHeader column={column} title="Time" />,
    cell: ({ row: { original: t } }) => (
      <button
        type="button"
        onClick={(e) => {
          e.stopPropagation()
          open(t.id)
        }}
        className="font-semibold whitespace-nowrap tabular-nums underline-offset-2 outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring"
        aria-label={`Open the trace of the ${seconds(t.ms)} answer`}
      >
        {seconds(t.ms)}
      </button>
    ),
  },
  {
    id: 'slowest',
    accessorFn: (t) => t.slowest?.label ?? '',
    header: 'Slowest step',
    cell: ({ row: { original: t } }) =>
      t.slowest ? (
        <span className="whitespace-nowrap">
          {t.slowest.kind === 'tool' ? toolTitle(t.slowest.label) : t.slowest.label}
          <span className="text-muted-foreground tabular-nums">
            {' '}
            · {seconds(t.slowest.ms)} ({Math.round(t.slowest.share * 100)}%)
          </span>
        </span>
      ) : (
        '—'
      ),
  },
  {
    id: 'at',
    accessorFn: (t) => new Date(t.at).getTime(),
    header: ({ column }) => <SortHeader column={column} title="When" />,
    cell: ({ row: { original: t } }) => (
      <time dateTime={t.at} title={when(t.at)} className="whitespace-nowrap text-muted-foreground">
        {ago(t.at)}
      </time>
    ),
  },
  { id: 'who', accessorFn: (t) => t.person?.displayName || t.person?.userName || '', header: 'Who', cell: ({ getValue }) => <span className="font-medium">{getValue<string>() || '—'}</span> },
  { accessorKey: 'model', header: 'Model', cell: ({ getValue }) => <span className="text-muted-foreground">{getValue<string | null>() ?? '—'}</span> },
  {
    id: 'tokens',
    accessorFn: (t) => t.tokens.prompt + t.agentTokens.prompt,
    header: 'Tokens',
    cell: ({ row: { original: t } }) => (
      <span className="whitespace-nowrap tabular-nums" title={t.agents ? `Sub-agents: ${formatValue(t.agentTokens.prompt)} in, ${formatValue(t.agentTokens.completion)} out` : undefined}>
        {formatValue(t.tokens.prompt + t.agentTokens.prompt)} in · {formatValue(t.tokens.completion + t.agentTokens.completion)} out
        {t.tokens.cacheShare != null && <span className="text-muted-foreground"> · {Math.round(t.tokens.cacheShare * 100)}% cached</span>}
      </span>
    ),
  },
  {
    id: 'tools',
    accessorFn: (t) => t.tools.map((u) => u.name).join(' '),
    header: 'Tools',
    cell: ({ row: { original: t } }) => (
      <span className="flex max-w-sm flex-wrap gap-1">
        {t.tools.slice(0, 4).map((u) => (
          <Badge key={u.name} variant="secondary">
            {toolTitle(u.name)}
            {u.count > 1 && ` ×${u.count}`}
          </Badge>
        ))}
        {t.tools.length > 4 && <span className="text-xs text-muted-foreground">+{t.tools.length - 4}</span>}
        {t.status !== 'complete' && <Badge variant={t.status === 'failed' ? 'destructive' : 'outline'}>{t.status}</Badge>}
      </span>
    ),
  },
]

/**
 * Admin → Traces: the slowest answers of a time range, across people, and where their time
 * went (the wait in line, a round of the model, a tool call, a sub-agent). Times, tokens,
 * sizes and tool names only: never what was asked or answered.
 */
export function TracesPage() {
  const [range, setRange] = useState('now-24h')
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({
    queryKey: ['admin', 'traces', 'list', range],
    queryFn: ({ signal }) => {
      const to = new Date()
      return api<{ answers: TraceSummary[] }>(`/api/admin/traces?from=${resolve(range, to).toISOString()}&to=${to.toISOString()}&limit=100`, { signal })
    },
    placeholderData: keepPreviousData,
  })
  return (
    <>
      <PageHeader
        title="Traces"
        description="The slowest answers, across people, and where their time went. Times, tokens and tool names only: never what was said."
        actions={<RangeSelect value={range} onChange={setRange} />}
      />
      {list.error ? (
        <QueryError error={list.error} retry={() => list.refetch()} />
      ) : (
        <DataTable
          columns={columns(setOpen)}
          data={list.isPending ? undefined : list.data.answers}
          loading={list.isPending}
          noun="answers"
          pageSize={25}
          getRowId={(t) => t.id}
          onRowClick={(t) => setOpen(t.id)}
          initialSorting={[{ id: 'ms', desc: true }]}
          searchPlaceholder="Search people, models, tools…"
          empty={
            <EmptyState icon={Timer} title="No answers in this time" className="py-8">
              Answers are traced as they are written. Choose a longer time.
            </EmptyState>
          }
        />
      )}
      <TraceSheet answerId={open} onClose={() => setOpen(null)} />
    </>
  )
}
