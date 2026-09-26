import { useQuery } from '@tanstack/react-query'
import { Activity, ArrowRight, Cpu, FileSearch, Gauge, HardDrive, LayoutDashboard, LineChart, ScrollText, SearchCode, Server } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { DashboardView } from '@/components/dashboards/dashboard-view'
import type { DashboardDef } from '@/components/dashboards/types'
import { api } from '@/lib/api'

interface Listed {
  uid: string
  title: string
  panels: number
  supported: number
}

/** What each dashboard is for, and its icon: the files hold only their titles. */
const about: Record<string, { icon: typeof LineChart; text: string }> = {
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
  // Usage by person is on the Usage page.
  const shown = list.data.filter((d) => d.uid !== 'usage-by-user')
  return (
    <>
      <PageHeader title="Dashboards" description="The stack at work: the model, the machine, the services and their logs. Every panel runs its query here, against Prometheus, Loki and the gateway's database." />
      <ul className="stagger grid gap-4 sm:grid-cols-2 xl:grid-cols-3" aria-label="Dashboards">
        {shown.map((d) => {
          const a = about[d.uid] ?? { icon: LayoutDashboard, text: '' }
          return (
            <li key={d.uid}>
              <Link
                to={`/admin/dashboards/${d.uid}`}
                className="group flex h-full items-start gap-4 rounded-xl border bg-card p-5 shadow-xs transition-[border-color,box-shadow,translate] duration-200 outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring"
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
                  <span className="text-xs text-muted-foreground">{d.panels} panels</span>
                </span>
              </Link>
            </li>
          )
        })}
      </ul>
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
