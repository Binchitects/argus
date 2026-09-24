import { useQuery } from '@tanstack/react-query'
import { ExternalLink } from 'lucide-react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { serviceUrl, type Probe } from './ops-api'
import { ServiceList } from './services'

export function MonitoringPage() {
  const s = useQuery({ queryKey: ['admin', 'services'], queryFn: ({ signal }) => api<Probe[]>('/api/admin/services', { signal }), refetchInterval: 15_000 })
  const links: [string, string, string][] = [
    ['Grafana', serviceUrl('grafana'), 'The dashboards not in the app yet: engine, GPU, host, logs, Argus.'],
    ['Prometheus', serviceUrl('metrics'), 'Raw metrics and alert rules.'],
    ['Alertmanager', serviceUrl('alerts'), 'Firing alerts.'],
    ['Argus MCP', `${serviceUrl('argus')}/mcp`, 'The code index, for agents.'],
  ]
  return (
    <>
      <PageHeader title="Monitoring" description="Live probes of every service, refreshed every 15 seconds." />
      <div className="grid gap-6">
        {s.isPending ? <PageSkeleton /> : s.error ? <QueryError error={s.error} retry={() => s.refetch()} /> : <ServiceList services={s.data} />}
        <Card>
          <CardHeader>
            <CardTitle>Elsewhere</CardTitle>
            <CardDescription>They use the same sign-in as this app, so an open tab is usually signed in already.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-2 sm:grid-cols-2">
            {links.map(([name, url, what]) => (
              <a key={name} href={url} target="_blank" rel="noopener noreferrer" className="group rounded-lg border p-3 transition-colors outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                <p className="flex items-center gap-1.5 font-medium">
                  {name} <ExternalLink className="size-3.5 text-muted-foreground" aria-hidden="true" />
                  <span className="sr-only">(opens in a new tab)</span>
                </p>
                <p className="text-sm text-muted-foreground">{what}</p>
              </a>
            ))}
          </CardContent>
        </Card>
      </div>
    </>
  )
}
