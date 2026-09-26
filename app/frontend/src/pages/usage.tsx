import { useQuery } from '@tanstack/react-query'
import { Coins, Database, DatabaseZap, Hash, MessageSquareText } from 'lucide-react'
import { useState } from 'react'
import { useOutletContext } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Stat, StatGrid } from '@/components/app/stat'
import { TimeChart } from '@/components/charts/time-chart'
import { DashboardView } from '@/components/dashboards/dashboard-view'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api, type Me } from '@/lib/api'
import { formatValue, money } from '@/lib/format'
import { intervalFor, presets, resolve } from '@/lib/time'

export function UsagePage() {
  const me = useOutletContext<Me>()
  return (
    <>
      <PageHeader title="Usage & cost" description="Tokens and cost. Input is split into cache miss and cache hit, which are priced differently." />
      {me.isAdmin ? (
        <Tabs defaultValue="everyone">
          <TabsList>
            <TabsTrigger value="everyone">Everyone</TabsTrigger>
            <TabsTrigger value="mine">Mine</TabsTrigger>
          </TabsList>
          <TabsContent value="everyone">
            <DashboardView uid="usage-by-user" />
          </TabsContent>
          <TabsContent value="mine">
            <MyUsage />
          </TabsContent>
        </Tabs>
      ) : (
        <MyUsage />
      )}
    </>
  )
}

interface Mine {
  intervalMs: number
  totals: { requests: number; inputMiss: number; inputHit: number; output: number; cost: number }
  series: { name: string; points: (number | null)[][] }[]
  models: { model: string; requests: number; tokens: number; cost: number }[]
}

function MyUsage() {
  const [from, setFrom] = useState('now-30d')
  const data = useQuery({
    queryKey: ['usage', 'me', from],
    queryFn: async ({ signal }) => {
      const f = resolve(from)
      const t = new Date()
      const q = new URLSearchParams({ from: f.toISOString(), to: t.toISOString(), intervalMs: String(Math.max(intervalFor(f, t, 60), 3_600_000)) })
      return { ...(await api<Mine>(`/api/usage/me?${q}`, { signal })), from: f.getTime(), to: t.getTime() }
    },
  })
  const d = data.data
  const t = d?.totals
  const prompt = t ? t.inputMiss + t.inputHit : 0
  const tokens = d?.series.filter((s) => s.name !== 'cost') ?? []
  const cost = d?.series.filter((s) => s.name === 'cost').map((s) => ({ ...s, name: 'Cost' })) ?? []
  return (
    <div className="grid gap-6">
      <Segmented label="Time range" value={from} onChange={setFrom} options={presets.map((p) => ({ value: p.from, label: p.label.replace('Last ', '') }))} />
      {data.error && <QueryError error={data.error} retry={() => data.refetch()} />}
      {data.isPending && <Skeleton className="h-28" />}
      {t && (
        <>
          <StatGrid>
            <Stat icon={Hash} label="Requests" value={formatValue(t.requests)} />
            <Stat icon={Database} label="Input, cache miss" value={formatValue(t.inputMiss)} />
            <Stat icon={DatabaseZap} label="Input, cache hit" value={formatValue(t.inputHit)} hint={prompt ? `${formatValue((100 * t.inputHit) / prompt, 'percent')} of input` : undefined} />
            <Stat icon={MessageSquareText} label="Output" value={formatValue(t.output)} />
            <Stat icon={Coins} label="Cost" value={money(t.cost)} />
          </StatGrid>
          {t.requests === 0 ? (
            <EmptyState title="No requests in this time range">Chats and API calls show up here within a minute.</EmptyState>
          ) : (
            <div className="stagger grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
              <Card>
                <CardHeader>
                  <CardTitle>Tokens by kind</CardTitle>
                </CardHeader>
                <CardContent>
                  <TimeChart series={tokens} stacked bars label="Your tokens by kind over time" from={d.from} to={d.to} />
                </CardContent>
              </Card>
              <Card>
                <CardHeader>
                  <CardTitle>Cost</CardTitle>
                </CardHeader>
                <CardContent>
                  <TimeChart series={cost} unit="currencyUSD" bars label="Your cost over time" from={d.from} to={d.to} />
                </CardContent>
              </Card>
            </div>
          )}
          {d.models.length > 0 && (
            <Card>
              <CardHeader>
                <CardTitle>By model</CardTitle>
                <CardDescription>Everything you used in this time range.</CardDescription>
              </CardHeader>
              <CardContent>
                <div className="overflow-x-auto rounded-lg border">
                  <table className="w-full text-sm">
                    <thead className="border-b bg-muted/40">
                      <tr>
                        {['Model', 'Requests', 'Tokens', 'Cost'].map((h, i) => (
                          <th key={h} scope="col" className={`h-9 px-3 text-xs font-medium text-muted-foreground ${i ? 'text-right' : 'text-left'}`}>
                            {h}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {d.models.map((m) => (
                        <tr key={m.model} className="border-b last:border-0">
                          <td className="px-3 py-2 font-medium">{m.model}</td>
                          <td className="px-3 py-2 text-right tabular-nums">{formatValue(m.requests)}</td>
                          <td className="px-3 py-2 text-right tabular-nums">{formatValue(m.tokens)}</td>
                          <td className="px-3 py-2 text-right tabular-nums">{money(m.cost)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </CardContent>
            </Card>
          )}
        </>
      )}
    </div>
  )
}
