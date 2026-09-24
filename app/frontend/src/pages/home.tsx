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
  spend: number
  budget: number | null
}

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
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <QuickAction to="/chat" icon={<MessageSquare />} title="Start a chat" description="Ask the model, with Argus searching the code you can read." />
        <QuickAction to="/usage" icon={<BarChart3 />} title="Your usage" description="Tokens, cache hits and cost, over time and by model." />
        <QuickAction to="/account" icon={<KeyRound />} title="API key" description="Connect Qwen Code, your IDE or scripts." />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Your credit</CardTitle>
          <CardDescription>Everything you spend through the chat and your API key.</CardDescription>
        </CardHeader>
        <CardContent>
          {keys.isPending ? (
            <Skeleton className="h-8 w-48" />
          ) : keys.data ? (
            <p className="text-2xl font-semibold tabular-nums">
              {money(keys.data.spend)} <span className="text-base font-normal text-muted-foreground">of {keys.data.budget === null ? 'no limit' : money(keys.data.budget)}</span>
            </p>
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
      className="group flex items-start gap-4 rounded-xl border bg-card p-4 shadow-xs transition-colors outline-none hover:border-primary/40 hover:bg-accent/40 focus-visible:ring-[3px] focus-visible:ring-ring sm:flex-col sm:gap-3 sm:p-5"
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
            <Stat label="Total spend">{money(d.spend)}</Stat>
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
