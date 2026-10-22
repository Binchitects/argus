import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, MessagesSquare, RefreshCw, Wrench } from 'lucide-react'
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { Markdown } from '@/pages/chat/markdown'
import { decideFinding, reviewAgain, reviewQuery, reviewRoom, type Finding } from './api'
import { ReviewState } from './code'

const severityVariant = { critical: 'destructive', major: 'warning', minor: 'default', nit: 'secondary' } as const

function FindingCard({ f, onDecide, onFix }: { f: Finding; onDecide: (state: Finding['state']) => void; onFix: () => void }) {
  const refuted = f.verified === false
  return (
    <li className={`grid gap-2 rounded-lg border p-4 ${refuted || f.state !== 'open' ? 'opacity-70' : ''}`}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
        {f.category && <Badge variant="outline">{f.category}</Badge>}
        <code className="text-xs text-muted-foreground">
          {f.path}
          {f.line ? `:${f.line}` : ''}
        </code>
        {f.state !== 'open' && <Badge variant="secondary">{f.state}</Badge>}
      </div>
      <p className="font-medium">{f.title}</p>
      <div className="text-sm">
        <Markdown text={f.body} />
      </div>
      {f.suggestion && (
        <pre className="overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs">
          <code>{f.suggestion}</code>
        </pre>
      )}
      {f.verifyNote && (
        <p className="text-xs text-muted-foreground">
          {refuted ? 'A second check doubts it: ' : 'Checked: '}
          {f.verifyNote}
        </p>
      )}
      <div className="flex gap-2">
        {f.state === 'open' ? (
          <>
            <Button size="sm" variant="outline" onClick={() => onDecide('resolved')}>
              Resolved
            </Button>
            <Button size="sm" variant="ghost" onClick={() => onDecide('dismissed')}>
              Dismiss
            </Button>
            <Button size="sm" variant="ghost" onClick={onFix}>
              <Wrench /> Fix it
            </Button>
          </>
        ) : (
          <Button size="sm" variant="ghost" onClick={() => onDecide('open')}>
            Open again
          </Button>
        )}
      </div>
    </li>
  )
}

/** One review: its summary, verdict and CI, its findings by file (doubted ones last), a room to discuss it, and a review again. */
export function ReviewPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const review = useQuery(reviewQuery(id))
  const [busy, setBusy] = useState(false)
  if (review.error) return <QueryError error={review.error} retry={() => review.refetch()} />
  if (!review.data) return <Skeleton className="h-64" />
  const d = review.data
  const r = d.review
  const decide = async (findingId: string, state: Finding['state']) => {
    try {
      await decideFinding(id, findingId, state)
      await queryClient.invalidateQueries({ queryKey: reviewQuery(id).queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  // Fix it: an agent's change for the finding, from the branch reviewed; the person's own GitLab token is needed.
  const fix = async (findingId: string) => {
    try {
      const made = await api<{ id: string }>('/api/fixes', { body: { findingId } })
      navigate(`/code/fixes/${made.id}`)
    } catch (e) {
      toast.error(errorMessage(e))
      navigate('/code/fixes')
    }
  }
  const go = async (work: () => Promise<{ id: string }>, to: (made: string) => string) => {
    setBusy(true)
    try {
      const made = await work()
      await queryClient.invalidateQueries({ queryKey: ['code'] })
      navigate(to(made.id))
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const kept = d.findings.filter((f) => f.verified !== false)
  const doubted = d.findings.filter((f) => f.verified === false)
  return (
    <>
      <PageHeader
        title={`${r.project} ${r.title}`}
        description={`Reviewed for ${d.by}${d.model ? ` with ${d.model}` : ''}${r.files ? ` · ${r.files} files` : ''}. Nothing is posted to GitLab.`}
        actions={
          <div className="flex flex-wrap gap-2">
            {r.webUrl && (
              <Button variant="outline" asChild>
                <a href={r.webUrl} target="_blank" rel="noreferrer">
                  <ExternalLink /> GitLab
                </a>
              </Button>
            )}
            {r.status === 'done' && (
              <Button variant="outline" disabled={busy} onClick={() => void go(() => reviewRoom(id), (room) => `/chat/${room}`)}>
                <MessagesSquare /> {d.roomId ? 'Its room' : 'Discuss in a room'}
              </Button>
            )}
            {(r.status === 'done' || r.status === 'failed') && (
              <Button disabled={busy} onClick={() => void go(() => reviewAgain(id), (again) => `/code/reviews/${again}`)}>
                <RefreshCw /> Review again
              </Button>
            )}
          </div>
        }
      />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <ReviewState r={r} />
        {r.risk && <Badge variant={r.risk === 'high' ? 'destructive' : r.risk === 'medium' ? 'warning' : 'secondary'}>{r.risk} risk</Badge>}
        {d.ci && <span className="text-sm text-muted-foreground">CI: {d.ci}</span>}
      </div>
      {r.status === 'failed' && <Alert variant="destructive">{r.error}</Alert>}
      {d.summary && (
        <Card className="mb-6">
          <CardHeader>
            <CardTitle>Summary</CardTitle>
          </CardHeader>
          <CardContent>
            <Markdown text={d.summary} />
          </CardContent>
        </Card>
      )}
      {r.status === 'done' && (
        <>
          <h2 className="mb-3 text-lg font-semibold">Findings ({kept.length})</h2>
          {kept.length === 0 ? (
            <p className="text-sm text-muted-foreground">Nothing that survived checking.</p>
          ) : (
            <ul className="grid gap-3" aria-label="Findings">
              {kept.map((f) => (
                <FindingCard key={f.id} f={f} onDecide={(state) => void decide(f.id, state)} onFix={() => void fix(f.id)} />
              ))}
            </ul>
          )}
          {doubted.length > 0 && (
            <details className="mt-6">
              <summary className="cursor-pointer text-sm text-muted-foreground">Doubted by the second check ({doubted.length})</summary>
              <ul className="mt-3 grid gap-3" aria-label="Doubted findings">
                {doubted.map((f) => (
                  <FindingCard key={f.id} f={f} onDecide={(state) => void decide(f.id, state)} onFix={() => void fix(f.id)} />
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </>
  )
}
