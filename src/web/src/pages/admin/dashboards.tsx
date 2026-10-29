import { useQuery } from '@tanstack/react-query'
import { Activity, ArrowRight, Cpu, FileSearch, Gauge, HardDrive, LayoutDashboard, LineChart, ScrollText, SearchCode, Server, Users } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { DashboardView } from '@/components/dashboards/dashboard-view'
import type { DashboardDef } from '@/components/dashboards/types'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'

interface Listed {
  uid: string
  title: string
  /** llm (the model and its users), system or hardware: each is seen with its own permission. */
  area: 'llm' | 'system' | 'hardware'
  /** The area's own dashboard, listed first. */
  main: boolean
  panels: number
  supported: number
}

const areas: { key: Listed['area']; title: string; who: string }[] = [
  { key: 'llm', title: 'LLM and users', who: 'with usage.read' },
  { key: 'system', title: 'System', who: 'with monitoring.read' },
  { key: 'hardware', title: 'Hardware', who: 'with hardware.read' },
]

/** What each dashboard is for, and its icon: the files hold only their titles. */
const about: Record<string, { icon: typeof LineChart; text: string }> = {
  'llm-users': { icon: Users, text: 'The model at work and the people using it: requests, tokens, speed and cost, the line, and who uses what.' },
  system: { icon: Activity, text: "The stack's services: their health and alerts, their traffic and errors, the line, and the host they run on." },
  hardware: { icon: Cpu, text: 'The machine, with one GPU, many or none: what it has, how busy it is, where the models run and what holds them back.' },
  'llm-overview': { icon: LineChart, text: 'Requests, tokens, cost and latency at the gateway, and the engine behind it.' },
  'stack-performance': { icon: Gauge, text: 'Throughput, time to first token, queues and the cache: how fast the model answers.' },
  'stack-health': { icon: Activity, text: 'Which services answer, the alerts firing now, and Prometheus itself.' },
  resources: { icon: Cpu, text: 'CPU, memory and GPU of the machine and of each service.' },
  'gpu-hardware': { icon: Server, text: 'The GPU: utilisation, memory, temperature, power and clocks.' },
  'host-containers': { icon: HardDrive, text: 'The host and each container: CPU, memory, disk and network.' },
  'stack-logs': { icon: ScrollText, text: 'Every service’s logs, by container, searchable, and their volume.' },
  argus: { icon: SearchCode, text: 'Who asked Argus what: tool calls, people, refusals and timings.' },
  'stack-indexing': { icon: FileSearch, text: 'Argus’s index runs: repositories, files, symbols and failures.' },
}

export function DashboardsPage() {
  const list = useQuery({ queryKey: ['dashboards'], queryFn: ({ signal }) => api<Listed[]>('/api/dashboards/', { signal }) })
  if (list.isPending) return <PageSkeleton />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  // Usage by person is on the Usage page; each area's own dashboard first, then its detail.
  const shown = list.data.filter((d) => d.uid !== 'usage-by-user')
  return (
    <>
      <PageHeader title="Dashboards" description="The stack at work, in three areas each seen with its own permission: the model and its users, the system, the hardware. Every panel runs its query here, against Prometheus, Loki and the gateway's database." />
      <div className="grid gap-8">
        {areas.map((area) => {
          const here = shown.filter((d) => d.area === area.key).sort((a, b) => Number(b.main) - Number(a.main))
          if (here.length === 0) return null
          return (
            <section key={area.key} aria-labelledby={`area-${area.key}`} className="grid gap-3">
              <h2 id={`area-${area.key}`} className="text-sm font-semibold">
                {area.title} <span className="font-normal text-muted-foreground">· {area.who}</span>
              </h2>
              <ul className="stagger grid gap-4 sm:grid-cols-2 xl:grid-cols-3" aria-label={area.title}>
                {here.map((d) => {
                  const a = about[d.uid] ?? { icon: LayoutDashboard, text: '' }
                  return (
                    <li key={d.uid}>
                      <Link
                        to={`/admin/dashboards/${d.uid}`}
                        className={cn(
                          'group flex h-full items-start gap-4 rounded-xl border bg-card p-5 shadow-xs transition-[border-color,box-shadow,translate] duration-200 outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring',
                          d.main && 'border-primary/40',
                        )}
                      >
                        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
                          <a.icon className="size-4.5" aria-hidden="true" />
                        </span>
                        <span className="grid min-w-0 gap-1">
                          <span className="flex items-center gap-1 font-semibold">
                            {d.title}
                            <ArrowRight className="size-4 opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" />
                          </span>
                          <span className="text-sm text-muted-foreground">{a.text}</span>
                          <span className="text-xs text-muted-foreground">
                            {d.main ? 'The area’s dashboard · ' : 'In detail · '}
                            {d.panels} panels
                          </span>
                        </span>
                      </Link>
                    </li>
                  )
                })}
              </ul>
            </section>
          )
        })}
      </div>
    </>
  )
}

export function DashboardPage() {
  const { uid = '' } = useParams()
  const def = useQuery({ queryKey: ['dashboard', uid], queryFn: ({ signal }) => api<DashboardDef>(`/api/dashboards/${uid}`, { signal }) })
  return (
    <>
      <PageHeader
        title={def.data?.title ?? 'Dashboard'}
        description={about[uid]?.text}
        actions={
          <Link to="/admin/dashboards" className="text-sm font-medium text-primary-ink underline-offset-2 hover:underline">
            All dashboards
          </Link>
        }
      />
      <DashboardView key={uid} uid={uid} />
    </>
  )
}
