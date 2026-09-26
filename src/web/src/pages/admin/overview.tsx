import { useQuery } from '@tanstack/react-query'
import { Activity, Coins, Database, Users, WalletCards } from 'lucide-react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { money } from '@/lib/format'
import { indexExit, type IndexSummary, type Overview } from './ops-api'
import { ServiceList } from './services'

export function OverviewPage() {
  const o = useQuery({ queryKey: ['admin', 'overview'], queryFn: ({ signal }) => api<Overview>('/api/admin/overview', { signal }), refetchInterval: 30_000 })
  if (o.isPending) return <PageSkeleton />
  if (o.error) return <QueryError error={o.error} retry={() => o.refetch()} />
  const d = o.data
  const up = d.services.filter((s) => s.ok).length
  const idx = d.index.summary
  return (
    <>
      <PageHeader title="Overview" description={d.model ? `Serving ${d.model}. Refreshes every 30 seconds.` : 'The stack at a glance. Refreshes every 30 seconds.'} />
      <div className="grid gap-6">
        <StatGrid>
          <Stat icon={Activity} label="Services up" value={`${up}/${d.services.length}`} tone={up < d.services.length ? 'destructive' : undefined} hint={up < d.services.length ? `${d.services.length - up} down` : 'all healthy'} />
          <Stat icon={Users} label="People" value={d.people} hint={`${d.admins} admin${d.admins === 1 ? '' : 's'}`} />
          <Stat icon={Coins} label="Spend" value={money(d.spend)} hint="every key and chat" />
          <Stat icon={WalletCards} label="Over credit" value={d.overCredit.length} tone={d.overCredit.length ? 'warning' : undefined} hint={d.overCredit.length ? 'at or past their credit' : 'nobody'} />
          {d.index.configured && (
            <Stat
              icon={Database}
              label="Code index"
              value={d.index.error ? 'Unreachable' : idx?.repos ? `${idx.repos - (idx.stale ?? 0)}/${idx.repos}` : 'Empty'}
              tone={d.index.error || !idx?.repos ? 'destructive' : idx.stale ? 'warning' : undefined}
              hint={d.index.error ? 'Argus did not answer' : idx?.stale ? `${idx.stale} out of date` : idx?.repos ? 'repositories current' : 'nothing indexed yet'}
            />
          )}
        </StatGrid>
        {d.warning && <Alert variant="warning">{d.warning}</Alert>}
        {d.overCredit.length > 0 && (
          <Alert
            variant="warning"
            title={`${d.overCredit.length} ${d.overCredit.length === 1 ? 'person is' : 'people are'} at or past their credit`}
            action={
              <Button size="sm" variant="outline" asChild>
                <Link to="/admin/people">Open People</Link>
              </Button>
            }
          >
            {d.overCredit.slice(0, 8).join(', ')}
            {d.overCredit.length > 8 ? ` and ${d.overCredit.length - 8} more` : ''}. Their chats and API calls are refused until you raise it.
          </Alert>
        )}
        <IndexAlert configured={d.index.configured} idx={idx} error={d.index.error} />
        <Card>
          <CardHeader>
            <CardTitle>Services</CardTitle>
            <CardDescription>Probed from inside the stack.</CardDescription>
          </CardHeader>
          <CardContent>
            <ServiceList services={d.services} />
          </CardContent>
        </Card>
      </div>
    </>
  )
}

/** Why the index needs attention, with the cause when the last run's exit code gives one. */
function IndexAlert({ configured, idx, error }: { configured: boolean; idx: IndexSummary | null; error: string | null }) {
  if (!configured) return null
  const open = (
    <Button size="sm" variant="outline" asChild>
      <Link to="/admin/indexing">Open Indexing</Link>
    </Button>
  )
  if (error)
    return (
      <Alert variant="destructive" title="The code index is unreachable">
        Every answer that searches code is failing right now. {error}
      </Alert>
    )
  if (!idx?.repos)
    return (
      <Alert variant="warning" title="No repository is indexed" action={open}>
        Usually an expired GitLab token.
      </Alert>
    )
  if (!idx.stale) return null
  const cause = idx.returncode !== null && idx.returncode !== undefined && idx.returncode !== 0 ? indexExit[String(idx.returncode)] : null
  return (
    <Alert variant="warning" title={`${idx.stale} ${idx.stale === 1 ? 'repository has' : 'repositories have'} a stale index`} action={open}>
      {idx.never_run ? `${idx.never_run} have never been indexed. ` : ''}
      {cause ? `The last run ${cause}. ` : ''}Answers about them come from old data. {(idx.stale_names ?? []).slice(0, 5).join(', ')}
    </Alert>
  )
}
