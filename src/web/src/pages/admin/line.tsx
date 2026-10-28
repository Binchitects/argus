import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { seconds } from '@/pages/chat/format'

interface LineEntry {
  person: { id: string; userName: string | null; displayName: string | null }
  model: string
  /** chat: an answer; api: a request with a key. */
  kind: 'chat' | 'api'
  since: string
  seconds: number
  /** Waiting: 1 is served next. */
  position: number | null
  priority: number | null
}

interface Line {
  running: LineEntry[]
  waiting: LineEntry[]
  /** Each model's places (its answers at once); 0: no limit of its own. */
  places: Record<string, number>
  answersAtOnce: number
  answersPerPerson: number
  queueLength: number
  queueTimeoutMinutes: number
}

const who = (e: LineEntry) => (e.person.displayName ?? e.person.userName ?? 'someone') + (e.person.userName && e.person.displayName ? ` (${e.person.userName})` : '')

function Entries({ title, description, rows, waiting }: { title: string; description: string; rows: LineEntry[]; waiting?: boolean }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {title} <span className="font-normal text-muted-foreground tabular-nums">{rows.length}</span>
        </CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">{waiting ? 'Nobody waits.' : 'Nothing runs now.'}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm" aria-label={title}>
              <thead>
                <tr className="text-left text-xs text-muted-foreground">
                  {waiting && <th className="py-1.5 pr-3 font-medium">Place</th>}
                  <th className="py-1.5 pr-3 font-medium">Person</th>
                  <th className="py-1.5 pr-3 font-medium">Model</th>
                  <th className="py-1.5 pr-3 font-medium">Kind</th>
                  <th className="py-1.5 pr-3 text-right font-medium">{waiting ? 'Waiting' : 'Running'}</th>
                  {waiting && <th className="py-1.5 text-right font-medium">Priority</th>}
                </tr>
              </thead>
              <tbody>
                {rows.map((e) => (
                  <tr key={`${e.person.id}-${e.since}-${e.position ?? ''}`} className="border-t">
                    {waiting && <td className="py-1.5 pr-3 tabular-nums">{e.position}</td>}
                    <td className="py-1.5 pr-3">
                      {e.person.userName ? <Link to={`/admin/people/${e.person.id}`} className="underline-offset-2 hover:underline">{who(e)}</Link> : who(e)}
                    </td>
                    <td className="py-1.5 pr-3 font-mono text-xs">{e.model}</td>
                    <td className="py-1.5 pr-3">
                      <Badge variant={e.kind === 'api' ? 'outline' : 'secondary'}>{e.kind === 'api' ? 'API' : 'Chat'}</Badge>
                    </td>
                    <td className="py-1.5 pr-3 text-right tabular-nums">{seconds(e.seconds * 1000)}</td>
                    {waiting && <td className="py-1.5 text-right tabular-nums">{e.priority ?? 0}</td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

/** Admin → Line: who has a request running and who waits, in the order they will be served, every two seconds. */
export function LinePage() {
  const line = useQuery({ queryKey: ['admin', 'line'], queryFn: ({ signal }) => api<Line>('/api/admin/line', { signal }), refetchInterval: 2000 })
  if (line.isPending) return <PageSkeleton />
  if (line.error) return <QueryError error={line.error} retry={() => line.refetch()} />
  const l = line.data
  const models = Object.entries(l.places)
  return (
    <>
      <PageHeader
        title="Line"
        description="Chat answers and API requests running now, and those waiting their turn in the order they will be served: a higher priority first, then whoever has fewest running, then whoever was served longest ago."
      />
      <p className="mb-4 text-sm text-muted-foreground">
        {l.answersAtOnce > 0 ? `${l.answersAtOnce} at once for everyone` : 'No limit for everyone beyond each model’s places'} · {l.answersPerPerson} per person unless set for them or their group ·{' '}
        {l.queueLength > 0 ? `at most ${l.queueLength} waiting, the next refused as busy` : 'no limit on the line’s length'} · a wait gives up after {l.queueTimeoutMinutes} minutes.{' '}
        <Link to="/admin/settings" className="underline underline-offset-2">
          Settings → Chat
        </Link>
      </p>
      {models.length > 0 && (
        <p className="mb-4 flex flex-wrap gap-2 text-xs text-muted-foreground">
          {models.map(([m, n]) => (
            <span key={m} className="rounded-md border px-2 py-0.5">
              <span className="font-mono">{m}</span>: {n > 0 ? `${n} at once` : 'no limit of its own'}
            </span>
          ))}
        </p>
      )}
      <div className="grid gap-6 xl:grid-cols-2">
        <Entries title="Waiting" description="In the order they will be served." rows={l.waiting} waiting />
        <Entries title="Running" description="The longest running first." rows={l.running} />
      </div>
    </>
  )
}
