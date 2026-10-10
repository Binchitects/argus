import { useQuery } from '@tanstack/react-query'
import { ArrowRight, BarChart3, CheckCircle2, KeyRound, MessageSquare, XCircle } from 'lucide-react'
import type { ReactNode } from 'react'
import { Link, useOutletContext } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { api, type Me } from '@/lib/api'
import { money } from '@/lib/format'

interface Keys {
  /** This month, every kind together. */
  spend: number
  /** Kind by kind: spent this month, their own credit, and the tightest of their groups'. */
  standing: { kind: 'chat' | 'api' | 'pictures' | 'video' | 'speech'; spent: number; credit: number | null; group: string | null; groupLeft: number | null }[] | null
}

const kindLabel = { chat: 'Chat', api: 'API keys', pictures: 'Pictures', video: 'Video', speech: 'Speech' } as const

interface Overview {
  people: number
  admins: number
  spend: number
  overCredit: string[]
  services: { name: string; purpose: string; ok: boolean; detail: string }[]
  model: string | null
}

function greeting(now = new Date()) {
  const h = now.getHours()
  return h < 5 ? 'Good evening' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'
}

export function HomePage() {
  const me = useOutletContext<Me>()
  const keys = useQuery({ queryKey: ['account', 'keys'], queryFn: () => api<Keys>('/api/account/keys') })
  const first = (me.displayName || me.userName).split(' ')[0]
  return (
    <div className="grid gap-8">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">
          {greeting()}, {first}
        </h1>
        <p className="mt-1 text-muted-foreground">What would you like to do?</p>
      </div>
      <div className="stagger grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <QuickAction to="/chat" icon={<MessageSquare />} title="Start a chat" description="Ask the model, with Argus searching the code you can read." />
        <QuickAction to="/usage" icon={<BarChart3 />} title="Your usage" description="Tokens, cache hits and cost, over time and by model." />
        <QuickAction to="/setup" icon={<KeyRound />} title="Connect your tools" description="Your API key, and setups for Claude Code, Qwen Code, your editor or scripts." />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Your credit this month</CardTitle>
          <CardDescription>One per kind: the chat&apos;s answers, your API keys&apos; requests, pictures, video and speech.</CardDescription>
        </CardHeader>
        <CardContent>
          {keys.isPending ? (
            <Skeleton className="h-8 w-48" />
          ) : keys.data?.standing ? (
            <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
              {keys.data.standing.map((s) => {
                // What binds first: their own credit or their group's.
                const left = [s.credit === null ? null : Math.max(0, s.credit - s.spent), s.groupLeft].filter((v): v is number => v !== null)
                const least = left.length ? Math.min(...left) : null
                return (
                  <li key={s.kind} className="grid gap-0.5 rounded-lg border px-3 py-2">
                    <span className="text-sm text-muted-foreground">{kindLabel[s.kind]}</span>
                    <span className="text-lg font-semibold tabular-nums">
                      {money(s.spent)} <span className="text-sm font-normal text-muted-foreground">{s.credit === null ? '' : `of ${money(s.credit)}`}</span>
                    </span>
                    <span className={least === 0 ? 'text-xs font-medium text-destructive-ink' : 'text-xs text-muted-foreground'}>
                      {least === null ? 'no limit' : least === 0 ? 'used up' : `${money(least)} left`}
                      {s.group && s.groupLeft !== null && least === s.groupLeft ? ` (${s.group})` : ''}
                    </span>
                  </li>
                )
              })}
            </ul>
          ) : (
            <p className="text-muted-foreground">Not available right now.</p>
          )}
        </CardContent>
      </Card>
      {me.isAdmin && <AdminSummary />}
    </div>
  )
}

function QuickAction({ to, icon, title, description }: { to: string; icon: ReactNode; title: string; description: string }) {
  return (
    <Link
      to={to}
      className="group flex items-start gap-4 rounded-xl border bg-card p-4 shadow-xs transition-[background-color,border-color,box-shadow,translate] duration-200 outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:bg-accent/40 hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring sm:flex-col sm:gap-3 sm:p-5"
    >
      <div className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary [&_svg]:size-4.5">{icon}</div>
      <div className="min-w-0">
        <p className="flex items-center gap-1 font-semibold">
          {title} <ArrowRight className="size-4 opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" />
        </p>
        <p className="mt-0.5 text-sm text-muted-foreground">{description}</p>
      </div>
    </Link>
  )
}

function AdminSummary() {
  const o = useQuery({ queryKey: ['admin', 'overview'], queryFn: () => api<Overview>('/api/admin/overview'), refetchInterval: 30_000 })
  const d = o.data
  const down = d?.services.filter((s) => !s.ok) ?? []
  return (
    <Card>
      <CardHeader>
        <CardTitle>System</CardTitle>
        <CardDescription>{d?.model ? `Serving ${d.model}` : 'The stack at a glance'}</CardDescription>
        <CardAction>
          <Link to="/admin" className="text-sm font-medium text-primary hover:underline">
            Overview
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        {o.isPending && <Skeleton className="h-16" />}
        {d && (
          <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <Stat label="Services">
              {down.length === 0 ? (
                <Badge variant="success">
                  <CheckCircle2 /> All {d.services.length} up
                </Badge>
              ) : (
                <Badge variant="destructive">
                  <XCircle /> {down.length} down
                </Badge>
              )}
            </Stat>
            <Stat label="People">
              {d.people} <span className="text-sm font-normal text-muted-foreground">({d.admins} admin{d.admins === 1 ? '' : 's'})</span>
            </Stat>
            <Stat label="Spend this month">{money(d.spend)}</Stat>
            <Stat label="Over credit">{d.overCredit.length}</Stat>
          </dl>
        )}
      </CardContent>
    </Card>
  )
}

function Stat({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium text-muted-foreground">{label}</dt>
      <dd className="mt-1 text-lg font-semibold tabular-nums">{children}</dd>
    </div>
  )
}
