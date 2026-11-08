import { useQuery } from '@tanstack/react-query'
import { Download, Settings2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { TimeWindow } from '@/components/app/time-window'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api } from '@/lib/api'
import { formatValue, money } from '@/lib/format'
import { useTimeWindow } from '@/lib/use-time-window'

export type Basis = 'spend' | 'tokens' | 'time'

export interface AccountRow {
  kind: 'person' | 'group' | 'cost centre' | 'project'
  id: string | null
  name: string
  email: string | null
  costCentre: string | null
  people: number
  requests: number
  tokens: number
  modelHours: number
  charged: number
  share: number
  cost: number
}

export interface AccountingReport {
  basis: Basis
  settings: { powerPricePerKwh: number; pue: number; otherPowerWatts: number; hardwarePrice: number; hardwareMonths: number; servicePerMonth: number }
  running: { hours: number; gpuKwh: number | null; otherKwh: number; kwh: number; power: number; hardware: number; service: number; total: number }
  used: { requests: number; tokens: number; modelHours: number; charged: number; people: number }
  unit: { perMillionTokens: number | null; perRequest: number | null; perPerson: number | null; perKwh: number | null }
  people: AccountRow[]
  groups: AccountRow[]
  costCentres: AccountRow[]
  projects: AccountRow[]
}

const bases: { value: Basis; label: string }[] = [
  { value: 'spend', label: 'By spend' },
  { value: 'tokens', label: 'By tokens' },
  { value: 'time', label: 'By model time' },
]

const n = (v: number) => formatValue(v, 'short')
const maybe = (v: number | null, f: (v: number) => string) => (v === null ? '—' : f(v))
const kwh = (v: number) => formatValue(v, 'kwatth')

/**
 * Admin → Accounting (billing.read): what running the machine cost over a time (power measured on the GPUs and set for
 * the rest, the hardware written off, service), what a unit of use costs, and that cost shared out to each group, cost
 * centre, person and project by their use. The costs are set in Settings → Costs.
 */
export function AccountingPage() {
  const time = useTimeWindow({ from: 'now-30d', to: 'now' })
  const [basis, setBasis] = useState<Basis | null>(null)
  const query = () => {
    const q = time.query()
    if (basis) q.set('basis', basis)
    return q
  }
  const report = useQuery({
    queryKey: ['admin', 'accounting', time.key, basis],
    queryFn: ({ signal }) => api<AccountingReport>(`/api/admin/accounting?${query()}`, { signal }),
  })
  if (report.isPending) return <PageSkeleton />
  if (report.error) return <QueryError error={report.error} retry={() => report.refetch()} />
  const r = report.data
  const { running, used, unit, settings } = r
  const csv = () => {
    const q = query()
    q.set('format', 'csv')
    return `/api/admin/accounting?${q}`
  }
  const recovered = running.total > 0 ? used.charged / running.total : null
  return (
    <>
      <PageHeader
        title="Accounting"
        description="What running the models cost over the time chosen, power, hardware and service, and that cost shared out to each group, cost centre, person and project by their use. Spend is the use at the prices of Settings → Prices; cost is the machine's real running cost."
      />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <TimeWindow value={time.range} onChange={time.set} />
        <Select value={r.basis} onValueChange={(v) => setBasis(v as Basis)}>
          <SelectTrigger className="h-9 w-40" aria-label="Share the cost out">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {bases.map((b) => (
              <SelectItem key={b.value} value={b.value}>
                {b.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button variant="outline" size="sm" className="h-9" asChild>
          <a href={csv()} download>
            <Download /> CSV
          </a>
        </Button>
        <Button variant="ghost" size="sm" className="h-9" asChild>
          <Link to="/admin/settings#costs">
            <Settings2 /> Costs
          </Link>
        </Button>
      </div>

      <section aria-label="Running cost" className="mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Tile title="Running cost" value={money(running.total)} detail={`${formatValue(running.hours * 3600, 'dtdurations')} of running`} />
        <Tile
          title="Power"
          value={money(running.power)}
          detail={`${kwh(running.kwh)}: the GPUs ${running.gpuKwh === null ? 'not measured' : kwh(running.gpuKwh)}, the rest ${kwh(running.otherKwh)}, × ${settings.pue} overhead, at ${money(settings.powerPricePerKwh)} a kWh`}
        />
        <Tile
          title="Hardware"
          value={money(running.hardware)}
          detail={settings.hardwarePrice > 0 ? `${money(settings.hardwarePrice)} over ${settings.hardwareMonths} months` : 'No price set'}
        />
        <Tile title="Service" value={money(running.service)} detail={settings.servicePerMonth > 0 ? `${money(settings.servicePerMonth)} a month` : 'No monthly cost set'} />
      </section>
      <section aria-label="What a unit costs" className="mb-6 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Tile title="A million tokens" value={maybe(unit.perMillionTokens, money)} detail={`${n(used.tokens)} tokens in all`} />
        <Tile title="A request" value={maybe(unit.perRequest, money)} detail={`${n(used.requests)} requests, ${formatValue(used.modelHours * 3600, 'dtdurations')} of the model's time`} />
        <Tile title="A person" value={maybe(unit.perPerson, money)} detail={`${used.people} ${used.people === 1 ? 'person' : 'people'} used it`} />
        <Tile
          title="Spend at the prices"
          value={money(used.charged)}
          detail={recovered === null ? 'No running cost set' : `${formatValue(recovered, 'percentunit')} of the running cost`}
        />
      </section>

      <Card>
        <CardHeader>
          <CardTitle>Shared out</CardTitle>
          <CardDescription>
            Each one&apos;s share of the use ({bases.find((b) => b.value === r.basis)!.label.toLowerCase()}) and of the running cost. A person in two groups counts in both, and once in a cost centre.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Tabs defaultValue="groups">
            <TabsList>
              <TabsTrigger value="groups">Groups</TabsTrigger>
              <TabsTrigger value="centres">Cost centres</TabsTrigger>
              <TabsTrigger value="people">People</TabsTrigger>
              <TabsTrigger value="projects">Projects</TabsTrigger>
            </TabsList>
            <TabsContent value="groups">
              <Shares rows={r.groups} label="Groups" first="Group" />
            </TabsContent>
            <TabsContent value="centres">
              <Shares rows={r.costCentres} label="Cost centres" first="Cost centre" empty="No group has a cost centre (Admin → Groups)." />
            </TabsContent>
            <TabsContent value="people">
              <Shares rows={r.people} label="People" first="Person" />
            </TabsContent>
            <TabsContent value="projects">
              <Shares rows={r.projects} label="Projects" first="Project" />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>
    </>
  )
}

function Tile({ title, value, detail }: { title: string; value: string; detail: string }) {
  return (
    <Card className="gap-1 py-4">
      <CardHeader className="px-4">
        <CardDescription>{title}</CardDescription>
        <CardTitle className="text-2xl tabular-nums">{value}</CardTitle>
      </CardHeader>
      <CardContent className="px-4 text-xs text-muted-foreground">{detail}</CardContent>
    </Card>
  )
}

function Shares({ rows, label, first, empty = 'Nothing used in this time.' }: { rows: AccountRow[]; label: string; first: string; empty?: string }) {
  if (rows.length === 0) return <p className="py-6 text-sm text-muted-foreground">{empty}</p>
  const most = Math.max(...rows.map((x) => x.cost), 0.000001)
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm" aria-label={label}>
        <thead>
          <tr className="text-left text-xs text-muted-foreground">
            <th className="py-1.5 pr-3 font-medium">{first}</th>
            <th className="py-1.5 pr-3 text-right font-medium">People</th>
            <th className="py-1.5 pr-3 text-right font-medium">Requests</th>
            <th className="py-1.5 pr-3 text-right font-medium">Tokens</th>
            <th className="py-1.5 pr-3 text-right font-medium">Model hours</th>
            <th className="py-1.5 pr-3 text-right font-medium">Spend</th>
            <th className="py-1.5 pr-3 text-right font-medium">Share</th>
            <th className="py-1.5 pr-3 font-medium">Cost</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((x) => (
            <tr key={`${x.kind}|${x.id ?? x.name}`} className="border-t">
              <td className="py-1.5 pr-3">
                <Name row={x} />
              </td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{x.people}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.requests)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.tokens)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{x.modelHours.toFixed(2)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{money(x.charged)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{formatValue(x.share, 'percentunit')}</td>
              <td className="py-1.5 pr-3">
                <span className="flex items-center gap-2 tabular-nums">
                  <span className="h-1.5 w-24 overflow-hidden rounded-full bg-muted" aria-hidden="true">
                    <span className="block h-full rounded-full bg-primary" style={{ width: `${(100 * x.cost) / most}%` }} />
                  </span>
                  {money(x.cost)}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Name({ row }: { row: AccountRow }) {
  const quiet = row.id === null && row.name.startsWith('(')
  if (row.kind === 'person' && row.id) {
    return (
      <Link to={`/admin/people/${row.id}`} className="font-medium underline-offset-2 hover:underline">
        {row.name}
      </Link>
    )
  }
  if (row.kind === 'project' && row.id) {
    return (
      <Link to={`/projects/${row.id}`} className="font-medium underline-offset-2 hover:underline">
        {row.name}
      </Link>
    )
  }
  return (
    <span className={quiet ? 'text-muted-foreground' : 'font-medium'}>
      {row.name}
      {row.kind === 'group' && row.costCentre && <span className="ml-1.5 text-xs font-normal text-muted-foreground">{row.costCentre}</span>}
    </span>
  )
}
