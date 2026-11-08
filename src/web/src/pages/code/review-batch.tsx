import { useQuery, useQueryClient } from '@tanstack/react-query'
import { FolderGit2, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { Markdown } from '@/pages/chat/markdown'
import { reviewBatchQuery, type ReviewBatch } from './api'
import { ReviewState } from './code'

const severityVariant = { critical: 'destructive', major: 'warning', minor: 'secondary', nit: 'outline' } as const
const severities = ['critical', 'major', 'minor', 'nit'] as const
type Severity = ReviewBatch['findings'][number]['severity']

/** A code review of several whole repositories: each one's review, how they depend on each other, the look across them and every finding together. */
export function ReviewBatchPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const batch = useQuery(reviewBatchQuery(id))
  const [repo, setRepo] = useState('all')
  const [severity, setSeverity] = useState<'all' | Severity>('all')
  if (batch.error) return <QueryError error={batch.error} retry={() => batch.refetch()} />
  if (!batch.data) return <Skeleton className="h-64" />
  const d = batch.data
  const b = d.batch
  const ended = b.status === 'done' || b.status === 'failed'
  const shown = d.findings.filter((f) => (repo === 'all' || f.project === repo) && (severity === 'all' || f.severity === severity))
  const remove = async () => {
    if (!(await confirm({ title: 'Delete this review?', description: 'Each repository’s review in it goes too.', confirm: 'Delete', destructive: true }))) return
    try {
      await api(`/api/code/reviews/batches/${id}`, { method: 'DELETE' })
      await queryClient.invalidateQueries({ queryKey: ['code', 'reviews'] })
      navigate('/code?tab=review')
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <>
      <PageHeader
        title={b.title ?? `${d.reviews.length} repositories`}
        description={`Each reviewed whole for ${d.by}${b.ref ? `, at ${b.ref}` : ''}${b.model ? `; looked across with ${b.model}` : ''}. Read with ${d.mine ? 'your' : 'their'} own access; nothing is written to the code host.`}
        actions={
          ended && (
            <Button variant="ghost" onClick={() => void remove()}>
              <Trash2 /> Delete
            </Button>
          )
        }
      />
      <div className="grid gap-6">
        <div className="flex flex-wrap items-center gap-3">
          <ReviewState r={{ status: b.status, step: b.step, verdict: b.status === 'done' ? 'comments' : null }} />
          {b.error && <span className="text-sm text-destructive-ink">{b.error}</span>}
        </div>
        <Card>
          <CardHeader>
            <CardTitle>The repositories</CardTitle>
            <CardDescription>Reviewed one after the other; each opens to its own review.</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="grid gap-2" aria-label="The repositories">
              {d.reviews.map((r) => (
                <li key={r.id}>
                  <Link to={`/code/reviews/${r.id}`} className="flex flex-wrap items-center gap-3 rounded-md border px-3 py-2 text-sm hover:bg-accent">
                    <FolderGit2 className="size-4 text-muted-foreground" aria-hidden="true" />
                    <span className="min-w-0 flex-1 truncate font-medium">
                      {r.project}
                      {r.status === 'done' && <span className="ms-2 text-xs font-normal text-muted-foreground">{r.files} files read</span>}
                    </span>
                    {severities.map((v) => (r.open[v] ? <Badge key={v} variant={severityVariant[v]}>{`${r.open[v]} ${v}`}</Badge> : null))}
                    <ReviewState r={r} />
                  </Link>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
        {ended && (
          <Card>
            <CardHeader>
              <CardTitle>Across the repositories</CardTitle>
              <CardDescription>How they depend on each other, as Argus resolved it, and the model&apos;s look at them together.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              {d.report ? <Markdown text={d.report} /> : <p className="text-sm text-muted-foreground">{b.cross ? 'No look across: fewer than two repositories were reviewed.' : 'Not asked to look across them.'}</p>}
              {d.links && (
                <div className="grid gap-1">
                  <h3 className="text-sm font-semibold">How they depend on each other</h3>
                  <ul className="grid gap-1 text-sm text-muted-foreground" aria-label="How they depend on each other">
                    {d.links.split('\n').map((l) => (
                      <li key={l} className="break-words">
                        {l}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </CardContent>
          </Card>
        )}
        {d.findings.length > 0 && (
          <Card>
            <CardHeader>
              <CardTitle>Every finding</CardTitle>
              <CardDescription>All the repositories&apos; findings that survived checking, the worst first; each opens in its repository&apos;s review.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-3">
              <div className="flex flex-wrap gap-2">
                <Select value={repo} onValueChange={setRepo}>
                  <SelectTrigger className="h-9 w-56" aria-label="Repository">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Every repository</SelectItem>
                    {d.reviews.map((r) => (
                      <SelectItem key={r.id} value={r.project}>
                        {r.project}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Select value={severity} onValueChange={(v) => setSeverity(v as 'all' | Severity)}>
                  <SelectTrigger className="h-9 w-40" aria-label="Severity">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Every severity</SelectItem>
                    {severities.map((v) => (
                      <SelectItem key={v} value={v}>
                        {v}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <span className="self-center text-sm text-muted-foreground">{shown.length} shown</span>
              </div>
              <ul className="grid gap-1" aria-label="Every finding">
                {shown.map((f) => (
                  <li key={f.id}>
                    <Link to={`/code/reviews/${f.reviewId}`} className="flex flex-wrap items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
                      <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
                      {f.category && <Badge variant="outline">{f.category}</Badge>}
                      {f.state !== 'open' && <Badge variant="secondary">{f.state}</Badge>}
                      <span className="min-w-0 flex-1 truncate">{f.title}</span>
                      <code className="text-xs text-muted-foreground">
                        {f.project} · {f.path}
                        {f.line ? `:${f.line}` : ''}
                      </code>
                    </Link>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        )}
      </div>
    </>
  )
}
