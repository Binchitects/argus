import { useQuery, useQueryClient } from '@tanstack/react-query'
import { GitMerge, GitPullRequest, MessageSquareCode, ScanSearch, Wrench } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/api'
import { AutoReviews } from './auto-reviews'
import { askCode, mergeRequestsQuery, reviewsQuery, startReview, verdictLabel, type ReviewBrief } from './api'
import { BusinessMapSection } from './business'
import { SecurityReviews } from './security'
import { Workspaces } from './studio'

/** A review's state in a word or two, with its verdict once done. */
export function ReviewState({ r }: { r: ReviewBrief }) {
  if (r.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  if (r.status !== 'done') return <Badge variant="secondary">{r.step ?? 'Waiting'}…</Badge>
  return <Badge variant={r.verdict === 'changes' ? 'warning' : r.verdict === 'looks_good' ? 'success' : 'default'}>{verdictLabel[r.verdict ?? 'comments']}</Badge>
}

/**
 * Code: reviews of merge requests (or of what a branch has over another) by the model, read with your own GitLab access
 * and never posted back; and the code assistant, which answers questions about a project, branch or merge request.
 */
export function CodePage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const reviews = useQuery(reviewsQuery)
  const [project, setProject] = useState('')
  const [shown, setShown] = useState('')
  const [state, setState] = useState<'opened' | 'merged' | 'all'>('opened')
  const mrs = useQuery(mergeRequestsQuery(shown, state))
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [question, setQuestion] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const review = async (body: { project: string; iid?: number; from?: string; to?: string }) => {
    setError(null)
    setBusy(true)
    try {
      const made = await startReview(body)
      await queryClient.invalidateQueries({ queryKey: ['code', 'reviews'] })
      navigate(`/code/reviews/${made.id}`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const ask = async () => {
    setError(null)
    setBusy(true)
    try {
      const chat = await askCode({ question, ...(shown && { project: shown }) })
      navigate(`/chat/${chat.id}?ask=${encodeURIComponent(chat.message)}`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader
        title="Code Studio"
        description="Code in an IDE like VS Code, on the server or your own machine; review merge requests with the model, and ask Argus about your code. Reviews read with your own access and write nothing; workspaces and Fix it push and open merge requests as you, with your own token."
        actions={
          <Button variant="outline" asChild>
            <Link to="/code/fixes">
              <Wrench /> Fix it
            </Link>
          </Button>
        }
      />
      {error && (
        <Alert variant="destructive" className="mb-4">
          {error}
        </Alert>
      )}
      <div className="mb-8">
        <Workspaces />
      </div>
      <div className="mb-8">
        <BusinessMapSection />
      </div>
      <h2 className="mb-3 text-lg font-semibold">Review and ask</h2>
      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Merge requests</CardTitle>
            <CardDescription>A project by its path: group/project in GitLab, github:owner/repo or gitea:owner/repo (link your token in your account first).</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <form
              className="flex gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                setShown(project.trim())
              }}
            >
              <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" aria-label="Project" />
              <Button type="submit" variant="outline" disabled={!project.trim()}>
                Show
              </Button>
            </form>
            {shown && (
              <>
                <Segmented
                  label="Merge requests"
                  value={state}
                  onChange={setState}
                  options={[
                    { value: 'opened', label: 'Open' },
                    { value: 'merged', label: 'Merged' },
                    { value: 'all', label: 'All' },
                  ]}
                />
                {mrs.error ? (
                  <QueryError error={mrs.error} retry={() => mrs.refetch()} />
                ) : mrs.isPending ? (
                  <Skeleton className="h-24" />
                ) : mrs.data.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No merge requests here.</p>
                ) : (
                  <ul className="grid gap-1" aria-label="Merge requests">
                    {mrs.data.map((m) => (
                      <li key={m.iid} className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm">
                        <GitPullRequest className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate font-medium">
                            !{m.iid} {m.title}
                          </span>
                          <span className="block truncate text-xs text-muted-foreground">
                            {m.author} · {m.source_branch} → {m.target_branch}
                          </span>
                        </span>
                        <Button size="sm" disabled={busy} onClick={() => void review({ project: shown, iid: m.iid })}>
                          Review
                        </Button>
                      </li>
                    ))}
                  </ul>
                )}
                <details className="text-sm">
                  <summary className="cursor-pointer text-muted-foreground">Review a branch without a merge request</summary>
                  <div className="mt-3 grid gap-3 sm:grid-cols-2">
                    <Field label="What it goes into">
                      <Input value={from} onChange={(e) => setFrom(e.target.value)} placeholder="main" />
                    </Field>
                    <Field label="The branch">
                      <Input value={to} onChange={(e) => setTo(e.target.value)} placeholder="feature/x" />
                    </Field>
                    <div>
                      <Button size="sm" variant="outline" disabled={busy || !from.trim() || !to.trim()} onClick={() => void review({ project: shown, from: from.trim(), to: to.trim() })}>
                        <GitMerge /> Review the branch
                      </Button>
                    </div>
                  </div>
                </details>
              </>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Ask about code</CardTitle>
            <CardDescription>Explain, find, or suggest a change: answered in a chat with Argus, about {shown || 'any project you can read'}.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-3">
            <Textarea rows={4} value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="Where do we retry failed uploads, and is it safe?" aria-label="Question about code" />
            <div>
              <Button disabled={busy || !question.trim()} onClick={() => void ask()}>
                <MessageSquareCode /> Ask
              </Button>
            </div>
          </CardContent>
        </Card>
      </div>
      <AutoReviews />
      <SecurityReviews />
      <h2 className="mt-8 mb-3 text-lg font-semibold">Reviews</h2>
      {reviews.error ? (
        <QueryError error={reviews.error} retry={() => reviews.refetch()} />
      ) : reviews.isPending ? (
        <Skeleton className="h-32" />
      ) : reviews.data.length === 0 ? (
        <EmptyState icon={ScanSearch} title="No reviews yet">
          Show a project's merge requests above and press Review.
        </EmptyState>
      ) : (
        <ul className="grid gap-2" aria-label="Reviews">
          {reviews.data.map(({ review: r, open, sharedBy }) => (
            <li key={r.id}>
              <Link to={`/code/reviews/${r.id}`} className="flex items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">
                    {r.project} {r.title}
                  </span>
                  <span className="block text-xs text-muted-foreground">
                    {new Date(r.createdAt).toLocaleString()}
                    {r.trigger && ` · ${r.trigger === 'schedule' ? 'scheduled' : 'automatic'}`}
                    {sharedBy ? ` · shared by ${sharedBy}` : r.shared ? ' · shared' : ''}
                  </span>
                </span>
                {r.status === 'done' && open > 0 && <span className="text-xs text-muted-foreground">{open} open</span>}
                <ReviewState r={r} />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
