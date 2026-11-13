import { useQuery } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Calculator, ChevronDown, ChevronRight, Coins, Cpu, Gauge, Hash, Settings2, Timer, TrendingUp, Users, Wrench, Zap } from 'lucide-react'
import { Fragment, useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { ExportMenu, TimeWindow } from '@/components/app/time-window'
import { TimeChart } from '@/components/charts/time-chart'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api } from '@/lib/api'
import { formatValue, money } from '@/lib/format'
import { useTimeWindow } from '@/lib/use-time-window'
import { cn } from '@/lib/utils'

export type Basis = 'spend' | 'tokens' | 'time'

export interface AccountRow {
  kind: 'person' | 'group' | 'cost centre' | 'project' | 'model' | 'source' | 'kind' | 'tag'
  id: string | null
  name: string
  email: string | null
  costCentre: string | null
  people: number
  requests: number
  tokens: number
  /** Input not read from the prompt cache, input read from it, and output (adding up to tokens). */
  input: number
  cached: number
  output: number
  modelHours: number
  charged: number
  share: number
  cost: number
  /** A group's credits a month, every kind together; null: none. */
  credit?: number | null
  /** A group's or cost centre's people who used it. */
  members?: AccountRow[] | null
}

export interface AccountUse {
  requests: number
  tokens: number
  input: number
  cached: number
  output: number
  modelHours: number
  charged: number
  people: number
}

export interface AccountingReport {
  from: string
  to: string
  basis: Basis
  settings: { powerPricePerKwh: number; pue: number; otherPowerWatts: number; hardwarePrice: number; hardwareMonths: number; servicePerMonth: number; personHourly: number }
  running: { hours: number; gpuKwh: number | null; otherKwh: number; kwh: number; power: number; hardware: number; service: number; total: number }
  used: AccountUse
  unit: { perMillionTokens: number | null; perRequest: number | null; perPerson: number | null; perModelHour: number | null }
  health: { gpuBusy: number | null; idleCost: number | null; monthlyRunning: number; monthlyCharged: number; recovered: number | null }
  before: { from: string; to: string; total: number; used: AccountUse; totalChange: number | null; chargedChange: number | null; tokensChange: number | null } | null
  series: { at: string; power: number; hardware: number; service: number; total: number; charged: number; tokens: number; requests: number; kwh: number }[]
  people: AccountRow[]
  groups: AccountRow[]
  costCentres: AccountRow[]
  projects: AccountRow[]
  models: AccountRow[]
  sources: AccountRow[]
  kinds: AccountRow[]
  tags: AccountRow[]
  /** Where the chats' time went: the model's, the tools', the people's (null: not read). */
  time?: AccountTime | null
}

/** A person's or project's chat time (or all of it), in hours. */
export interface TimeRow {
  kind: 'all' | 'person' | 'project'
  id: string | null
  name: string
  email: string | null
  modelHours: number
  toolHours: number
  humanHours: number
  hours: number
}

export interface AccountTime {
  all: TimeRow
  people: TimeRow[]
  projects: TimeRow[]
}

const bases: { value: Basis; label: string }[] = [
  { value: 'spend', label: 'By spend' },
  { value: 'tokens', label: 'By tokens' },
  { value: 'time', label: 'By model time' },
]

const n = (v: number) => formatValue(v, 'short')
const pct = (v: number) => formatValue(v, 'percentunit')
const maybe = (v: number | null, f: (v: number) => string) => (v === null ? '—' : f(v))
const kwh = (v: number) => formatValue(v, 'kwatth')

/**
 * Admin → Accounting (billing.read): what running the models cost over a time (power measured on the GPUs and set for the
 * rest, the hardware written off, service), over time and against the window before; what a unit of use costs; the GPUs'
 * idle time and what it cost; a month at this rate; and the cost shared out to groups (with their members), cost centres,
 * people, projects, models, sources, kinds of work and tags. The costs are set in Settings → Costs.
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
    queryFn: async ({ signal }) => {
      const r = time.resolved()
      return { ...(await api<AccountingReport>(`/api/admin/accounting?${query()}`, { signal })), fromMs: r.from.getTime(), toMs: r.to.getTime() }
    },
  })
  if (report.isPending) return <PageSkeleton />
  if (report.error) return <QueryError error={report.error} retry={() => report.refetch()} />
  const r = report.data
  const { running, used, unit, settings, health, before } = r
  const basisLabel = bases.find((b) => b.value === r.basis)!.label.toLowerCase()
  return (
    <>
      <PageHeader
        title="Accounting"
        description="What running the models really cost over the time chosen, power, hardware and service, against the time before; what a unit of use costs; and that cost shared out to each group, cost centre, person, project, model and kind of work. Spend is the use at the prices of Settings → Prices; cost is the machine's own running cost."
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
        <ExportMenu path="/api/admin/accounting/export" query={query} also={{ label: 'With every dashboard', param: ['dashboards', 'true'], suffix: ', with the dashboards' }} />
        <Button variant="ghost" size="sm" className="h-9" asChild>
          <Link to="/admin/settings#costs">
            <Settings2 /> Costs
          </Link>
        </Button>
      </div>

      <div className="grid gap-6">
        {(settings.hardwarePrice === 0 || settings.servicePerMonth === 0 || running.gpuKwh === null) && (
          <Alert>
            {[
              running.gpuKwh === null && 'The GPUs’ power was not measured in this time (monitoring off or no NVIDIA GPU): only the rest of the machine’s draw is counted.',
              settings.hardwarePrice === 0 && 'No hardware price is set.',
              settings.servicePerMonth === 0 && 'No monthly service cost is set.',
            ]
              .filter(Boolean)
              .join(' ')}{' '}
            <Link to="/admin/settings#costs" className="font-medium underline underline-offset-2">
              Set the costs
            </Link>{' '}
            for the machine&apos;s whole cost.
          </Alert>
        )}

        <section aria-label="The totals">
          <StatGrid className="xl:grid-cols-4">
            <Stat icon={Calculator} label="Running cost" value={money(running.total)} hint={<Change value={before?.totalChange ?? null} worse="up" />} />
            <Stat
              icon={Coins}
              label="Spend at the prices"
              value={money(used.charged)}
              hint={
                <>
                  {health.recovered === null ? 'No running cost set' : `${pct(health.recovered)} of the running cost`} · <Change value={before?.chargedChange ?? null} worse="down" />
                </>
              }
            />
            <Stat icon={TrendingUp} label="A month at this rate" value={money(health.monthlyRunning)} hint={`Spend at the prices ${money(health.monthlyCharged)} a month`} />
            <Stat
              icon={Cpu}
              label="GPU idle"
              value={maybe(health.idleCost, money)}
              hint={health.gpuBusy === null ? 'The GPUs not measured' : `The GPUs busy ${pct(health.gpuBusy)} of the time: the rest is room for more`}
            />
            <Stat icon={Hash} label="A million tokens" value={maybe(unit.perMillionTokens, money)} hint={<>{n(used.tokens)} tokens ({n(used.input)} in, {n(used.cached)} cached, {n(used.output)} out) · <Change value={before?.tokensChange ?? null} /></>} />
            <Stat icon={Zap} label="A request" value={maybe(unit.perRequest, money)} hint={`${n(used.requests)} requests`} />
            <Stat icon={Users} label="A person" value={maybe(unit.perPerson, money)} hint={`${used.people} ${used.people === 1 ? 'person' : 'people'} used it`} />
            <Stat icon={Gauge} label="A model hour" value={maybe(unit.perModelHour, money)} hint={`${formatValue(used.modelHours * 3600, 'dtdurations')} of the models’ time`} />
          </StatGrid>
        </section>

        <div className="grid gap-4 xl:grid-cols-3">
          <Card className="xl:col-span-1">
            <CardHeader>
              <CardTitle>Where the cost comes from</CardTitle>
              <CardDescription>{formatValue(running.hours * 3600, 'dtdurations')} of running.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <Structure running={running} />
              <dl className="grid gap-2 text-sm">
                <Line label="Power" value={money(running.power)}>
                  {kwh(running.kwh)}: the GPUs {running.gpuKwh === null ? 'not measured' : kwh(running.gpuKwh)}, the rest {kwh(running.otherKwh)} ({settings.otherPowerWatts} W), × {settings.pue} overhead, at{' '}
                  {money(settings.powerPricePerKwh)} a kWh
                </Line>
                <Line label="Hardware" value={money(running.hardware)}>
                  {settings.hardwarePrice > 0 ? `${money(settings.hardwarePrice)} over ${settings.hardwareMonths} months` : 'No price set'}
                </Line>
                <Line label="Service" value={money(running.service)}>
                  {settings.servicePerMonth > 0 ? `${money(settings.servicePerMonth)} a month` : 'No monthly cost set'}
                </Line>
              </dl>
            </CardContent>
          </Card>
          <Card className="xl:col-span-2">
            <CardHeader>
              <CardTitle>Running cost over time</CardTitle>
              <CardDescription>Power, hardware and service, bucket by bucket.</CardDescription>
            </CardHeader>
            <CardContent>
              <TimeChart
                series={[
                  { name: 'Power', points: r.series.map((b) => [Date.parse(b.at), b.power]) },
                  { name: 'Hardware', points: r.series.map((b) => [Date.parse(b.at), b.hardware]) },
                  { name: 'Service', points: r.series.map((b) => [Date.parse(b.at), b.service]) },
                ]}
                unit="currencyUSD"
                stacked
                bars
                label="Running cost over time"
                from={r.fromMs}
                to={r.toMs}
              />
            </CardContent>
          </Card>
        </div>

        <div className="grid gap-4 xl:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>Spend against the running cost</CardTitle>
              <CardDescription>Above the running cost, the prices charge more than it costs; below, the machine costs more than its use is priced at.</CardDescription>
            </CardHeader>
            <CardContent>
              <TimeChart
                series={[
                  { name: 'Spend at the prices', points: r.series.map((b) => [Date.parse(b.at), b.charged]) },
                  { name: 'Running cost', points: r.series.map((b) => [Date.parse(b.at), b.total]) },
                ]}
                unit="currencyUSD"
                label="Spend against the running cost"
                from={r.fromMs}
                to={r.toMs}
              />
            </CardContent>
          </Card>
          {before && <Compare report={r} before={before} />}
        </div>

        <Card>
          <CardHeader>
            <CardTitle>Shared out</CardTitle>
            <CardDescription>
              Each one&apos;s share of the use ({basisLabel}) and of the running cost. A person in two groups counts in both, and once in a cost centre. Open a group or cost centre for its people.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Tabs defaultValue="groups">
              <TabsList className="flex-wrap">
                <TabsTrigger value="groups">Groups</TabsTrigger>
                <TabsTrigger value="centres">Cost centres</TabsTrigger>
                <TabsTrigger value="people">People</TabsTrigger>
                <TabsTrigger value="projects">Projects</TabsTrigger>
                <TabsTrigger value="models">Models</TabsTrigger>
                <TabsTrigger value="sources">Where it came from</TabsTrigger>
                <TabsTrigger value="kinds">What it was for</TabsTrigger>
                <TabsTrigger value="tags">By tag</TabsTrigger>
              </TabsList>
              <TabsContent value="groups">
                <Shares rows={r.groups} label="Groups" first="Group" credit />
              </TabsContent>
              <TabsContent value="centres">
                <Shares rows={r.costCentres} label="Cost centres" first="Cost centre" empty="No group has a cost centre (Admin → Groups)." />
              </TabsContent>
              <TabsContent value="people">
                <Shares rows={r.people} label="People" first="Person" search />
              </TabsContent>
              <TabsContent value="projects">
                <Shares rows={r.projects} label="Projects" first="Project" />
              </TabsContent>
              <TabsContent value="models">
                <Shares rows={r.models} label="Models" first="Model" />
              </TabsContent>
              <TabsContent value="sources">
                <Shares rows={r.sources} label="Where it came from" first="From" />
              </TabsContent>
              <TabsContent value="kinds">
                <Shares rows={r.kinds} label="What it was for" first="Kind" />
              </TabsContent>
              <TabsContent value="tags">
                <Shares
                  rows={r.tags}
                  label="By tag"
                  first="Tag"
                  empty="No chat or project in this window has a tag. Tag them on the chat's menu or the project's page."
                />
                <p className="mt-2 text-xs text-muted-foreground">A request counts under each tag of its chat and its project, so the tags can add up to more than the whole.</p>
              </TabsContent>
            </Tabs>
          </CardContent>
        </Card>
        {r.time && <TimeCard time={r.time} />}
      </div>
    </>
  )
}

const hours = (h: number) => `${h.toLocaleString(undefined, { maximumFractionDigits: 2 })} h`

/** Where the chats' time went: the model's, the tools', the people's; by person and by project. */
function TimeCard({ time }: { time: AccountTime }) {
  const share = (h: number) => (time.all.hours > 0 ? `${Math.round((h / time.all.hours) * 100)}%` : '–')
  return (
    <Card>
      <CardHeader>
        <CardTitle>Time</CardTitle>
        <CardDescription>
          Where the chats&apos; time went, Code Arena&apos;s too: the model&apos;s (its answers), the tools&apos; (their runs, sub-agents included), and the people&apos;s (before each question: reading the
          answer, writing the next, at most 30 minutes a question).
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <StatGrid>
          <Stat label="All" value={hours(time.all.hours)} hint="The model, tools and people together" icon={Timer} />
          <Stat label="The model" value={hours(time.all.modelHours)} hint={share(time.all.modelHours)} icon={Cpu} />
          <Stat label="Tools" value={hours(time.all.toolHours)} hint={share(time.all.toolHours)} icon={Wrench} />
          <Stat label="People" value={hours(time.all.humanHours)} hint={share(time.all.humanHours)} icon={Users} />
        </StatGrid>
        <Tabs defaultValue="people">
          <TabsList>
            <TabsTrigger value="people">People</TabsTrigger>
            <TabsTrigger value="projects">Projects</TabsTrigger>
          </TabsList>
          <TabsContent value="people">
            <TimeTable rows={time.people} label="Time by person" first="Person" />
          </TabsContent>
          <TabsContent value="projects">
            <TimeTable rows={time.projects} label="Time by project" first="Project" />
          </TabsContent>
        </Tabs>
      </CardContent>
    </Card>
  )
}

function TimeTable({ rows, label, first }: { rows: TimeRow[]; label: string; first: string }) {
  if (rows.length === 0) return <p className="py-6 text-sm text-muted-foreground">No chat in this time.</p>
  return (
    <div className="overflow-x-auto pt-2">
      <table className="w-full text-sm" aria-label={label}>
        <thead>
          <tr className="text-left text-xs text-muted-foreground">
            <th className="py-1.5 pr-3 font-medium">{first}</th>
            <th className="py-1.5 pr-3 text-right font-medium">The model</th>
            <th className="py-1.5 pr-3 text-right font-medium">Tools</th>
            <th className="py-1.5 pr-3 text-right font-medium">People</th>
            <th className="py-1.5 pr-3 text-right font-medium">All</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((x) => (
            <tr key={`${x.id ?? x.name}`} className="border-t">
              <td className="py-1.5 pr-3">
                <bdi>{x.name}</bdi>
              </td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{hours(x.modelHours)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{hours(x.toolHours)}</td>
              <td className="py-1.5 pr-3 text-right tabular-nums">{hours(x.humanHours)}</td>
              <td className="py-1.5 pr-3 text-right font-medium tabular-nums">{hours(x.hours)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** A change on the time before: up or down, coloured when it is the worse way (worse: which way is bad; none: neither). */
function Change({ value, worse }: { value: number | null; worse?: 'up' | 'down' }) {
  if (value === null) return <span>nothing to compare with</span>
  const up = value >= 0
  const bad = worse === 'up' ? up && value > 0 : worse === 'down' ? !up : false
  const Icon = up ? ArrowUp : ArrowDown
  return (
    <span className={cn('inline-flex items-center gap-0.5', bad && 'text-destructive-ink')}>
      <Icon className="size-3" aria-hidden="true" />
      {pct(Math.abs(value))} on the time before
    </span>
  )
}

const parts = [
  // The running cost chart's first three series colours (components/charts/palette.ts), light and dark.
  { key: 'power', label: 'Power', className: 'bg-[#2a78d6] dark:bg-[#3987e5]' },
  { key: 'hardware', label: 'Hardware', className: 'bg-[#eb6834] dark:bg-[#d95926]' },
  { key: 'service', label: 'Service', className: 'bg-[#1baf7a] dark:bg-[#199e70]' },
] as const

/** The running cost's three parts as one bar, each its share. */
function Structure({ running }: { running: AccountingReport['running'] }) {
  if (running.total <= 0) return <p className="text-sm text-muted-foreground">No running cost in this time.</p>
  return (
    <div className="grid gap-2">
      <div className="flex h-3 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
        {parts.map((p) => running[p.key] > 0 && <span key={p.key} className={cn('h-full', p.className)} style={{ width: `${(100 * running[p.key]) / running.total}%` }} />)}
      </div>
      <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
        {parts.map((p) => (
          <li key={p.key} className="flex items-center gap-1.5">
            <span className={cn('size-2 rounded-full', p.className)} aria-hidden="true" />
            {p.label} {pct(running[p.key] / running.total)}
          </li>
        ))}
      </ul>
    </div>
  )
}

function Line({ label, value, children }: { label: string; value: string; children: React.ReactNode }) {
  return (
    <div className="grid gap-0.5">
      <div className="flex items-baseline justify-between gap-2">
        <dt className="font-medium">{label}</dt>
        <dd className="tabular-nums">{value}</dd>
      </div>
      <p className="text-xs text-muted-foreground">{children}</p>
    </div>
  )
}

/** This time against the one before it, as long. */
function Compare({ report: r, before: b }: { report: AccountingReport; before: NonNullable<AccountingReport['before']> }) {
  const rows: { label: string; was: number; now: number; f: (v: number) => string }[] = [
    { label: 'Running cost', was: b.total, now: r.running.total, f: money },
    { label: 'Spend at the prices', was: b.used.charged, now: r.used.charged, f: money },
    { label: 'Requests', was: b.used.requests, now: r.used.requests, f: n },
    { label: 'Tokens', was: b.used.tokens, now: r.used.tokens, f: n },
    { label: 'Input, not cached', was: b.used.input, now: r.used.input, f: n },
    { label: 'Input, cached', was: b.used.cached, now: r.used.cached, f: n },
    { label: 'Output', was: b.used.output, now: r.used.output, f: n },
    { label: 'Model hours', was: b.used.modelHours, now: r.used.modelHours, f: (v) => v.toFixed(1) },
    { label: 'People', was: b.used.people, now: r.used.people, f: (v) => String(v) },
  ]
  return (
    <Card>
      <CardHeader>
        <CardTitle>Against the time before</CardTitle>
        <CardDescription>
          {new Date(b.from).toLocaleDateString(undefined, { dateStyle: 'medium' })} to {new Date(b.to).toLocaleDateString(undefined, { dateStyle: 'medium' })}, as long as this one, at today&apos;s costs.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <table className="w-full text-sm" aria-label="Against the time before">
          <thead>
            <tr className="text-left text-xs text-muted-foreground">
              <th className="py-1.5 pr-3 font-medium">
                <span className="sr-only">What</span>
              </th>
              <th className="py-1.5 pr-3 text-right font-medium">Before</th>
              <th className="py-1.5 pr-3 text-right font-medium">Now</th>
              <th className="py-1.5 text-right font-medium">Change</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((x) => (
              <tr key={x.label} className="border-t">
                <td className="py-1.5 pr-3">{x.label}</td>
                <td className="py-1.5 pr-3 text-right tabular-nums text-muted-foreground">{x.f(x.was)}</td>
                <td className="py-1.5 pr-3 text-right tabular-nums">{x.f(x.now)}</td>
                <td className="py-1.5 text-right tabular-nums">{x.was > 0 ? `${x.now >= x.was ? '+' : '−'}${pct(Math.abs(x.now - x.was) / x.was)}` : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </CardContent>
    </Card>
  )
}

type SortKey = 'name' | 'people' | 'requests' | 'tokens' | 'input' | 'cached' | 'output' | 'modelHours' | 'charged' | 'share' | 'cost'

const columns: { key: SortKey; title: string }[] = [
  { key: 'people', title: 'People' },
  { key: 'requests', title: 'Requests' },
  { key: 'tokens', title: 'Tokens' },
  { key: 'input', title: 'In' },
  { key: 'cached', title: 'Cached' },
  { key: 'output', title: 'Out' },
  { key: 'modelHours', title: 'Model hours' },
  { key: 'charged', title: 'Spend' },
  { key: 'share', title: 'Share' },
  { key: 'cost', title: 'Cost' },
]

/** One way the cost is shared out: sortable by any column; a group's or cost centre's people open under it. */
function Shares({ rows, label, first, empty = 'Nothing used in this time.', credit, search }: { rows: AccountRow[]; label: string; first: string; empty?: string; credit?: boolean; search?: boolean }) {
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'cost', desc: true })
  const [open, setOpen] = useState<string | null>(null)
  const [words, setWords] = useState('')
  if (rows.length === 0) return <p className="py-6 text-sm text-muted-foreground">{empty}</p>
  const q = words.trim().toLowerCase()
  const shown = rows
    .filter((x) => !q || x.name.toLowerCase().includes(q) || x.email?.toLowerCase().includes(q))
    .toSorted((a, b) => {
      // The rows in brackets ("in no group") stay last.
      const quiet = Number(a.name.startsWith('(')) - Number(b.name.startsWith('('))
      if (quiet) return quiet
      const x = a[sort.key]
      const y = b[sort.key]
      const c = typeof x === 'string' && typeof y === 'string' ? x.localeCompare(y) : Number(x) - Number(y)
      return sort.desc ? -c : c
    })
  const most = Math.max(...rows.map((x) => x.cost), 0.000001)
  const header = (key: SortKey, title: string, right = true) => (
    <th className={cn('py-1.5 pr-3 font-medium', right && 'text-right')} aria-sort={sort.key === key ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className="hover:text-foreground" onClick={() => setSort({ key, desc: sort.key === key ? !sort.desc : key !== 'name' })}>
        {title}
        {sort.key === key && (sort.desc ? ' ↓' : ' ↑')}
      </button>
    </th>
  )
  return (
    <div className="grid gap-3 pt-2">
      {search && <Input value={words} onChange={(e) => setWords(e.target.value)} placeholder="Find a person" aria-label="Find a person" className="h-9 max-w-xs" />}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={label}>
          <thead>
            <tr className="text-left text-xs text-muted-foreground">
              {header('name', first, false)}
              {credit && <th className="py-1.5 pr-3 text-right font-medium">Credit a month</th>}
              {columns.map((c) => (
                <Fragment key={c.key}>{header(c.key, c.title, c.key !== 'cost')}</Fragment>
              ))}
            </tr>
          </thead>
          <tbody>
            {shown.map((x) => {
              const key = `${x.kind}|${x.id ?? x.name}`
              const members = x.members ?? []
              const isOpen = open === key
              return (
                <Fragment key={key}>
                  <tr className="border-t">
                    <td className="py-1.5 pr-3">
                      {members.length > 0 ? (
                        <button type="button" className="flex items-center gap-1 text-left" aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : key)}>
                          {isOpen ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                          <Name row={x} />
                        </button>
                      ) : (
                        <span className={cn((x.kind === 'group' || x.kind === 'cost centre') && 'ps-5')}>
                          <Name row={x} />
                        </span>
                      )}
                    </td>
                    {credit && <td className="py-1.5 pr-3 text-right tabular-nums">{x.credit == null ? '—' : money(x.credit)}</td>}
                    <Cells row={x} most={most} />
                  </tr>
                  {isOpen &&
                    members.map((m) => (
                      <tr key={`${key}|${m.email ?? m.name}`} className="text-muted-foreground">
                        <td className="py-1 pr-3 ps-9">
                          <Name row={m} />
                        </td>
                        {credit && (
                          <td>
                            <span className="sr-only">No credit of their own here</span>
                          </td>
                        )}
                        <Cells row={m} most={most} />
                      </tr>
                    ))}
                </Fragment>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Cells({ row: x, most }: { row: AccountRow; most: number }) {
  return (
    <>
      <td className="py-1.5 pr-3 text-right tabular-nums">{x.people}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.requests)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.tokens)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.input)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.cached)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{n(x.output)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{x.modelHours.toFixed(2)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{money(x.charged)}</td>
      <td className="py-1.5 pr-3 text-right tabular-nums">{pct(x.share)}</td>
      <td className="py-1.5 pr-3">
        <span className="flex items-center gap-2 tabular-nums">
          <span className="h-1.5 w-24 overflow-hidden rounded-full bg-muted" aria-hidden="true">
            <span className="block h-full rounded-full bg-primary" style={{ width: `${(100 * x.cost) / most}%` }} />
          </span>
          {money(x.cost)}
        </span>
      </td>
    </>
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
