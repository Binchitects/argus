import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { money } from '@/lib/format'
import { chargebackPath, chargebackQuery, monthOf, type ChargeRow } from './groups-api'

const kinds: Record<ChargeRow['kind'], string> = { group: 'Group', 'cost centre': 'Cost centre', none: '' }

const columns: ColumnDef<ChargeRow>[] = [
  { id: 'month', accessorFn: (r) => r.month, header: ({ column }) => <SortHeader column={column} title="Month" />, cell: ({ row: { original: r } }) => <span className="tabular-nums">{r.month}</span> },
  {
    id: 'name',
    accessorFn: (r) => `${r.name} ${r.costCentre ?? ''}`,
    header: ({ column }) => <SortHeader column={column} title="Group or cost centre" />,
    cell: ({ row: { original: r } }) => (
      <span className="flex flex-wrap items-center gap-1.5">
        <span className={r.kind === 'none' ? 'text-muted-foreground' : 'font-medium'}>{r.name}</span>
        {kinds[r.kind] && <Badge variant={r.kind === 'group' ? 'secondary' : 'outline'}>{kinds[r.kind]}</Badge>}
        {r.kind === 'group' && r.costCentre && <span className="text-xs text-muted-foreground">{r.costCentre}</span>}
      </span>
    ),
  },
  { id: 'members', header: 'People', accessorFn: (r) => r.members, cell: ({ row: { original: r } }) => <span className="tabular-nums">{r.members}</span> },
  { id: 'spend', accessorFn: (r) => r.spend, header: ({ column }) => <SortHeader column={column} title="Spend" />, cell: ({ row: { original: r } }) => <span className="tabular-nums">{money(r.spend)}</span> },
  { id: 'credit', header: 'Credit', accessorFn: (r) => r.credit ?? '', cell: ({ row: { original: r } }) => <span className="tabular-nums">{r.credit === null ? '—' : money(r.credit)}</span> },
]

const valid = (m: string) => /^\d{4}-\d{2}$/.test(m)

/** Spend per group and cost centre, month by month, to charge back; CSV for the finance team. */
export function ChargebackCard() {
  const [from, setFrom] = useState(() => monthOf(new Date(), 2))
  const [to, setTo] = useState(() => monthOf(new Date()))
  const ok = valid(from) && valid(to) && from <= to
  const report = useQuery({ ...chargebackQuery(from, to), enabled: ok })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Chargeback</CardTitle>
        <CardDescription>
          What each group&apos;s members spent, month by month (UTC), over the chat and API keys. A person in several groups counts in each group, and once in a cost centre.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {report.error ? (
          <QueryError error={report.error} retry={() => report.refetch()} />
        ) : (
          <DataTable
            columns={columns}
            data={ok ? report.data?.rows : []}
            loading={ok && report.isPending}
            noun="rows"
            getRowId={(r) => `${r.month}|${r.kind}|${r.name}`}
            initialSorting={[{ id: 'month', desc: true }]}
            empty={ok ? 'No spend in these months.' : 'Choose months from before to.'}
            toolbar={
              <div className="flex flex-wrap items-end gap-2">
                <Field label="From" className="w-36">
                  <Input type="month" value={from} onChange={(e) => setFrom(e.target.value)} />
                </Field>
                <Field label="To" className="w-36">
                  <Input type="month" value={to} onChange={(e) => setTo(e.target.value)} />
                </Field>
                <Button variant="outline" asChild disabled={!ok}>
                  <a href={chargebackPath(from, to, true)} download>
                    <Download /> CSV
                  </a>
                </Button>
              </div>
            }
          />
        )}
      </CardContent>
    </Card>
  )
}
