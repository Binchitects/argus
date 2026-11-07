import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Fragment, useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { ExportMenu, TimeWindow } from '@/components/app/time-window'
import { Card, CardContent } from '@/components/ui/card'
import { api } from '@/lib/api'
import { formatValue, money } from '@/lib/format'
import { useTimeWindow } from '@/lib/use-time-window'

interface Used {
  requests: number
  inputMiss: number
  inputHit: number
  output: number
  tokens: number
  cost: number
}

interface Member {
  id: string | null
  userName: string | null
  displayName: string | null
  email: string
  used: Used
}

interface GroupRow {
  id: string | null
  name: string
  members: number
  active: number
  used: Used
  people: Member[]
}

const n = (v: number) => formatValue(v, 'short')

/** The share of input tokens the engine read from its cache. */
const cacheShare = (u: Used) => (u.inputMiss + u.inputHit > 0 ? `${Math.round((100 * u.inputHit) / (u.inputMiss + u.inputHit))}%` : '–')

/** Admin → Usage by group: each group's requests, tokens and spend over a time, its members who used it, and the people in no group. */
export function UsageByGroupPage() {
  const time = useTimeWindow({ from: 'now-7d', to: 'now' })
  const [open, setOpen] = useState<string | null>(null)
  const usage = useQuery({
    queryKey: ['admin', 'usage', 'groups', time.key],
    queryFn: ({ signal }) => api<{ groups: GroupRow[]; none: GroupRow; total: Used }>(`/api/admin/usage/groups?${time.query()}`, { signal }),
  })
  if (usage.isPending) return <PageSkeleton />
  if (usage.error) return <QueryError error={usage.error} retry={() => usage.refetch()} />
  const { groups, none, total } = usage.data
  const rows = [...[...groups].sort((a, b) => b.used.cost - a.used.cost || b.used.requests - a.used.requests), none]
  const most = Math.max(...rows.map((r) => r.used.cost), 0.000001)
  return (
    <>
      <PageHeader
        title="Usage by group"
        description="Each group's requests, tokens and spend, chat and API keys together, with the members who used it. A person in two groups counts in both, so the groups can add up to more than everyone's together. Each person's own is on the LLM and users dashboard."
      />
      <div className="mb-4 flex flex-wrap items-center gap-4">
        <TimeWindow value={time.range} onChange={time.set} />
        <ExportMenu path="/api/admin/usage/groups/export" query={time.query} />
        <p className="text-sm text-muted-foreground">
          Everyone: {n(total.requests)} requests, {n(total.tokens)} tokens, {money(total.cost)}
        </p>
      </div>
      <Card>
        <CardContent className="overflow-x-auto pt-4">
          <table className="w-full text-sm" aria-label="Usage by group">
            <thead>
              <tr className="text-left text-xs text-muted-foreground">
                <th className="py-1.5 pr-3 font-medium">Group</th>
                <th className="py-1.5 pr-3 text-right font-medium">Members</th>
                <th className="py-1.5 pr-3 text-right font-medium">Used it</th>
                <th className="py-1.5 pr-3 text-right font-medium">Requests</th>
                <th className="py-1.5 pr-3 text-right font-medium">Tokens</th>
                <th className="py-1.5 pr-3 text-right font-medium">From the cache</th>
                <th className="py-1.5 pr-3 font-medium">Spend</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((g) => {
                const key = g.id ?? 'none'
                const shown = open === key
                return (
                  <Fragment key={key}>
                    <tr className="border-t">
                      <td className="py-1.5 pr-3">
                        <button
                          type="button"
                          className="flex items-center gap-1 text-left font-medium disabled:font-normal disabled:text-muted-foreground"
                          aria-expanded={shown}
                          disabled={g.people.length === 0}
                          onClick={() => setOpen(shown ? null : key)}
                        >
                          {g.people.length > 0 ? shown ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" /> : <span className="size-4" />}
                          {g.name}
                        </button>
                      </td>
                      <td className="py-1.5 pr-3 text-right tabular-nums">{g.members}</td>
                      <td className="py-1.5 pr-3 text-right tabular-nums">{g.active}</td>
                      <td className="py-1.5 pr-3 text-right tabular-nums">{n(g.used.requests)}</td>
                      <td className="py-1.5 pr-3 text-right tabular-nums">{n(g.used.tokens)}</td>
                      <td className="py-1.5 pr-3 text-right tabular-nums">{cacheShare(g.used)}</td>
                      <td className="flex items-center gap-2 py-1.5 pr-3 tabular-nums">
                        <span className="h-1.5 w-24 overflow-hidden rounded-full bg-muted" aria-hidden="true">
                          <span className="block h-full rounded-full bg-primary" style={{ width: `${(100 * g.used.cost) / most}%` }} />
                        </span>
                        {money(g.used.cost)}
                      </td>
                    </tr>
                    {shown &&
                      g.people.map((p) => (
                        <tr key={`${key}-${p.email}`} className="text-muted-foreground">
                          <td colSpan={3} className="py-1 pr-3 pl-6">
                            {p.id ? (
                              <Link to={`/admin/people/${p.id}`} className="underline-offset-2 hover:underline">
                                {p.displayName ?? p.userName}
                              </Link>
                            ) : (
                              p.email
                            )}
                          </td>
                          <td className="py-1 pr-3 text-right tabular-nums">{n(p.used.requests)}</td>
                          <td className="py-1 pr-3 text-right tabular-nums">{n(p.used.tokens)}</td>
                          <td className="py-1 pr-3 text-right tabular-nums">{cacheShare(p.used)}</td>
                          <td className="py-1 pr-3 tabular-nums">{money(p.used.cost)}</td>
                        </tr>
                      ))}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </>
  )
}
