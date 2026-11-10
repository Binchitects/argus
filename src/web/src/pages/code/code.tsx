import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Compass, FolderGit2, GitMerge, GitPullRequest, Layers, MessageSquareCode, Network, ScanSearch, ShieldCheck, Waypoints, Wrench } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api, errorMessage, mayUse, meQuery } from '@/lib/api'
import { AutoReviews } from './auto-reviews'
import { askCode, mergeRequestsQuery, reviewBatchesQuery, reviewsQuery, startReview, verdictLabel, type ReviewBrief } from './api'
import { BusinessMapSection } from './business'
import { repositoriesOf } from './severity'
import { Workspaces } from './studio'
import { codeIntelName } from '@/lib/brand'

/** A review's state in a word or two, with its verdict once done. */
export function ReviewState({ r }: { r: Pick<ReviewBrief, 'status' | 'step' | 'verdict'> }) {
  if (r.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  if (r.status !== 'done') return <Badge variant="secondary">{r.step ?? 'Waiting'}…</Badge>
  return <Badge variant={r.verdict === 'changes' ? 'warning' : r.verdict === 'looks_good' ? 'success' : 'default'}>{verdictLabel[r.verdict ?? 'comments']}</Badge>
}

type Tab = 'workspaces' | 'review' | 'ask'

/**
 * Code Studio, in three parts each its permission's (Admin → Access): Workspaces (code.studio), Review (review.run starts
 * them: a merge request, a branch, a whole repository or several; code.read lists merge requests) and Ask and explore
 * (code.read: questions, code-intel's tools across repositories, business maps). Fix it and Security review are a button away.
 */
export function CodePage() {
  const me = useQuery(meQuery)
  const may = (permission: string) => mayUse(me.data, permission)
  const [params, setParams] = useSearchParams()
  const tabs: { value: Tab; label: string; shown: boolean }[] = [
    { value: 'workspaces', label: 'Workspaces', shown: may('code.studio') },
    { value: 'review', label: 'Review', shown: may('review.run') || may('code.read') },
    { value: 'ask', label: 'Ask and explore', shown: may('code.read') },
  ]
  const shown = tabs.filter((t) => t.shown)
  const asked = params.get('tab') as Tab | null
  // A person with reviews shared with them but no permission of their own still sees the list.
  const tab: Tab = shown.find((t) => t.value === asked)?.value ?? shown[0]?.value ?? 'review'
  return (
    <>
      <PageHeader
        title="Code Studio"
        description={`Code in an IDE like VS Code, on the server or your own machine; review a change, a whole repository or several with the model; and ask ${codeIntelName} about your code across repositories. Reviews read with your own access and write nothing; workspaces and Fix it push and open merge requests as you, with your own token.`}
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/security">
                <ShieldCheck /> Security review
              </Link>
            </Button>
            {may('feature.fixit') && (
              <Button variant="outline" asChild>
                <Link to="/code/fixes">
                  <Wrench /> Fix it
                </Link>
              </Button>
            )}
          </>
        }
      />
      <Tabs value={tab} onValueChange={(v) => setParams({ tab: v }, { replace: true })}>
        {shown.length > 1 && (
          <TabsList className="mb-4">
            {shown.map((t) => (
              <TabsTrigger key={t.value} value={t.value}>
                {t.label}
              </TabsTrigger>
            ))}
          </TabsList>
        )}
        <TabsContent value="workspaces">{may('code.studio') && <Workspaces />}</TabsContent>
        <TabsContent value="review">
          <ReviewTab mayRun={may('review.run')} mayRead={may('code.read')} mayAuto={may('review.run') && may('review.policy')} />
        </TabsContent>
        <TabsContent value="ask">{may('code.read') && <AskTab />}</TabsContent>
      </Tabs>
    </>
  )
}

type What = 'mr' | 'branch' | 'repository' | 'several'

function ReviewTab({ mayRun, mayRead, mayAuto }: { mayRun: boolean; mayRead: boolean; mayAuto: boolean }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const reviews = useQuery(reviewsQuery)
  const batches = useQuery({ ...reviewBatchesQuery, enabled: mayRun })
  const [what, setWhat] = useState<What>(mayRead ? 'mr' : 'branch')
  const [project, setProject] = useState('')
  const [shown, setShown] = useState('')
  const [state, setState] = useState<'opened' | 'merged' | 'all'>('opened')
  const mrs = useQuery({ ...mergeRequestsQuery(shown, state), enabled: mayRead && !!shown })
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [ref, setRef] = useState('')
  const [list, setList] = useState('')
  const [title, setTitle] = useState('')
  const [cross, setCross] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [kind, setKind] = useState<'all' | 'change' | 'repository' | 'automatic'>('all')
  const [words, setWords] = useState('')
  const repositories = repositoriesOf(list)

  const run = async (go: () => Promise<string>) => {
    setError(null)
    setBusy(true)
    try {
      const path = await go()
      await queryClient.invalidateQueries({ queryKey: ['code', 'reviews'] })
      navigate(path)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const review = (body: Parameters<typeof startReview>[0]) => run(async () => `/code/reviews/${(await startReview(body)).id}`)
  const several = () =>
    run(async () => {
      const made = await api<{ id: string }>('/api/code/reviews/batches', { body: { projects: repositories, cross, ...(ref.trim() && { ref: ref.trim() }), ...(title.trim() && { title: title.trim() }) } })
      return `/code/reviews/batches/${made.id}`
    })

  const q = words.trim().toLowerCase()
  const listed = (reviews.data ?? []).filter(
    ({ review: r }) =>
      (kind === 'all' || (kind === 'automatic' ? !!r.ruleId : kind === 'repository' ? r.scope === 'repository' : r.scope !== 'repository' && !r.ruleId)) &&
      (!q || `${r.project} ${r.title}`.toLowerCase().includes(q)),
  )
  const options: { value: What; label: string }[] = [
    ...(mayRead ? [{ value: 'mr' as const, label: 'A merge request' }] : []),
    { value: 'branch', label: 'A branch' },
    { value: 'repository', label: 'A whole repository' },
    { value: 'several', label: 'Several repositories' },
  ]

  return (
    <div className="grid gap-6">
      {error && <Alert variant="destructive">{error}</Alert>}
      {(mayRun || mayRead) && (
        <Card>
          <CardHeader>
            <CardTitle>{mayRun ? 'Start a review' : 'Merge requests'}</CardTitle>
            <CardDescription>
              A project by its path: group/project in GitLab, github:owner/repo or gitea:owner/repo (link your token in your account first). Each finding is put to a second reviewer told to refute it.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            {mayRun && <Segmented label="What to review" value={what} onChange={setWhat} options={options} />}
            {what === 'mr' && mayRead && (
              <>
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
                            {mayRun && (
                              <Button size="sm" disabled={busy} onClick={() => void review({ project: shown, iid: m.iid })}>
                                Review
                              </Button>
                            )}
                          </li>
                        ))}
                      </ul>
                    )}
                  </>
                )}
              </>
            )}
            {mayRun && what === 'branch' && (
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label="Project">
                  <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
                </Field>
                <Field label="What it goes into">
                  <Input value={from} onChange={(e) => setFrom(e.target.value)} placeholder="main" />
                </Field>
                <Field label="The branch">
                  <Input value={to} onChange={(e) => setTo(e.target.value)} placeholder="feature/x" />
                </Field>
                <div>
                  <Button disabled={busy || !project.trim() || !from.trim() || !to.trim()} onClick={() => void review({ project: project.trim(), from: from.trim(), to: to.trim() })}>
                    <GitMerge /> Review the branch
                  </Button>
                </div>
              </div>
            )}
            {mayRun && what === 'repository' && (
              <div className="grid gap-3">
                <p className="text-sm text-muted-foreground">
                  Its product&apos;s code read file by file (tests, vendored and generated code left out; the entry points and the largest files first, at most 30), then a summary of its health area by area,
                  and what to fix and test first.
                </p>
                <div className="flex flex-wrap items-end gap-2">
                  <Field label="Repository" className="min-w-56 flex-1">
                    <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
                  </Field>
                  <Field label="Branch, tag or commit" className="w-56">
                    <Input value={ref} onChange={(e) => setRef(e.target.value)} placeholder="the default branch" />
                  </Field>
                  <Button disabled={busy || !project.trim()} onClick={() => void review({ project: project.trim(), scope: 'repository', ...(ref.trim() && { ref: ref.trim() }) })}>
                    <FolderGit2 /> Review the repository
                  </Button>
                </div>
              </div>
            )}
            {mayRun && what === 'several' && (
              <div className="grid gap-3">
                <p className="text-sm text-muted-foreground">
                  Each repository reviewed whole, one after the other; then looked at across: how they depend on each other (as {codeIntelName} resolved it), problems at their boundaries, what they do alike
                  and differently, and what to fix first.
                </p>
                <Field label="Repositories" hint={`One a line, 2 to 10. ${repositories.length} named.`}>
                  <Textarea rows={4} value={list} onChange={(e) => setList(e.target.value)} placeholder={'payments/api\npayments/web\nshared/auth'} />
                </Field>
                <div className="flex flex-wrap items-end gap-2">
                  <Field label="What it is for" className="min-w-56 flex-1">
                    <Input value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} placeholder="Payments, before 2.0" />
                  </Field>
                  <Field label="Branch, tag or commit" className="w-56" hint="The same in each, or each one’s default.">
                    <Input value={ref} onChange={(e) => setRef(e.target.value)} placeholder="the default branch" />
                  </Field>
                </div>
                <div className="flex items-center gap-2 text-sm">
                  <Checkbox id="review-cross" checked={cross} onCheckedChange={(v) => setCross(v === true)} />
                  <label htmlFor="review-cross">Look across them: their links, boundaries and what to fix first</label>
                </div>
                <div>
                  <Button disabled={busy || repositories.length < 2 || repositories.length > 10} onClick={() => void several()}>
                    <Layers /> Review {repositories.length} repositories
                  </Button>
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {mayAuto && <AutoReviews />}

      {mayRun && (batches.data?.length ?? 0) > 0 && (
        <section aria-labelledby="review-batches" className="grid gap-3">
          <h2 id="review-batches" className="text-lg font-semibold">
            Several repositories
          </h2>
          <ul className="grid gap-2" aria-label="Reviews of several repositories">
            {batches.data!.map(({ batch: b, repositories: names, done, changes }) => (
              <li key={b.id}>
                <Link to={`/code/reviews/batches/${b.id}`} className="flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                  <Layers className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">{b.title ?? `${names.length} repositories`}</span>
                    <span className="block truncate text-xs text-muted-foreground">
                      {names.join(', ')}
                      {b.ref && ` @ ${b.ref}`} · {new Date(b.createdAt).toLocaleString()} · {done} of {names.length} reviewed{changes > 0 && ` · ${changes} need changes`}
                    </span>
                  </span>
                  <ReviewState r={{ status: b.status, step: b.step, verdict: b.status === 'done' ? (changes > 0 ? 'changes' : 'comments') : null }} />
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section aria-labelledby="reviews-title" className="grid gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="reviews-title" className="text-lg font-semibold">
            Reviews
          </h2>
          <div className="flex flex-wrap items-center gap-2">
            <Segmented
              label="Which reviews"
              value={kind}
              onChange={setKind}
              options={[
                { value: 'all', label: 'All' },
                { value: 'change', label: 'Changes' },
                { value: 'repository', label: 'Repositories' },
                { value: 'automatic', label: 'Automatic' },
              ]}
            />
            <Input value={words} onChange={(e) => setWords(e.target.value)} placeholder="Find a review" aria-label="Find a review" className="h-9 w-48" />
          </div>
        </div>
        {reviews.error ? (
          <QueryError error={reviews.error} retry={() => reviews.refetch()} />
        ) : reviews.isPending ? (
          <Skeleton className="h-32" />
        ) : listed.length === 0 ? (
          <EmptyState icon={ScanSearch} title={reviews.data.length === 0 ? 'No reviews yet' : 'No such review'}>
            {reviews.data.length === 0 ? 'Start one above: a merge request, a branch or a whole repository.' : 'Choose All, or find it by another word.'}
          </EmptyState>
        ) : (
          <ul className="grid gap-2" aria-label="Reviews">
            {listed.map(({ review: r, open, sharedBy }) => (
              <li key={r.id}>
                <Link to={`/code/reviews/${r.id}`} className="flex items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                  {r.scope === 'repository' ? (
                    <FolderGit2 className="size-4 shrink-0 text-muted-foreground" aria-label="A whole repository" />
                  ) : (
                    <GitPullRequest className="size-4 shrink-0 text-muted-foreground" aria-label="A change" />
                  )}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">{r.scope === 'repository' ? r.title : `${r.project} ${r.title}`}</span>
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
      </section>
    </div>
  )
}

/** Ask about code, and code-intel's tools across repositories, each asked in a chat with code-intel. */
function AskTab() {
  const navigate = useNavigate()
  const [question, setQuestion] = useState('')
  const [project, setProject] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const ask = async (text: string, about?: string) => {
    setError(null)
    setBusy(true)
    try {
      const chat = await askCode({ question: text, ...(about && { project: about }) })
      navigate(`/chat/${chat.id}?ask=${encodeURIComponent(chat.message)}`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="grid gap-6">
      {error && <Alert variant="destructive">{error}</Alert>}
      <Card>
        <CardHeader>
          <CardTitle>Ask about code</CardTitle>
          <CardDescription>Explain, find, or suggest a change: answered in a chat with {codeIntelName}, about {project.trim() || 'any project you can read'}.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          <Textarea rows={4} value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="Where do we retry failed uploads, and is it safe?" aria-label="Question about code" />
          <div className="flex flex-wrap items-end gap-2">
            <Field label="Project (optional)" className="min-w-56 flex-1">
              <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
            </Field>
            <Button disabled={busy || !question.trim()} onClick={() => void ask(question, project.trim() || undefined)}>
              <MessageSquareCode /> Ask
            </Button>
          </div>
        </CardContent>
      </Card>
      <section aria-labelledby="explore-title" className="grid gap-3">
        <div>
          <h2 id="explore-title" className="text-lg font-semibold">
            Explore across repositories
          </h2>
          <p className="text-sm text-muted-foreground">{codeIntelName}&apos;s graph of what depends on what, through packages, imports, headers, submodules, CI and images, each asked in a chat.</p>
        </div>
        <div className="grid gap-4 lg:grid-cols-2">
          <Explore
            icon={<Waypoints />}
            title="What a change reaches"
            about="Every repository that depends on one, and the lines that use a name it defines: whom to tell, what to test again."
            fields={[
              { name: 'repo', label: 'Repository', placeholder: 'shared/auth' },
              { name: 'symbol', label: 'A function, class or type (optional)', placeholder: 'TokenValidator', optional: true },
            ]}
            busy={busy}
            onAsk={(v) => void ask(`Use change_impact on ${v.repo}${v.symbol ? ` for ${v.symbol}` : ''}: which repositories and lines would a change to it reach, grouped by how far, and whom should we tell and what should we test again?`)}
          />
          <Explore
            icon={<Network />}
            title="How two repositories are linked"
            about="The surest chains of dependencies from one to the other, each step named, with how sure it is."
            fields={[
              { name: 'from', label: 'From', placeholder: 'payments/web' },
              { name: 'to', label: 'To', placeholder: 'shared/auth' },
            ]}
            busy={busy}
            onAsk={(v) => void ask(`Use dependency_path from ${v.from} to ${v.to}: how does one depend on the other, step by step, and how sure is each step? If they are not linked, say how they may still talk at run time.`)}
          />
          <Explore
            icon={<Compass />}
            title="Where a change belongs"
            about="Which repository a task, a stack trace or a symbol belongs in, with why."
            fields={[{ name: 'task', label: 'The task, error or symbol', placeholder: 'Add H.265 support to the decoder' }]}
            busy={busy}
            onAsk={(v) => void ask(`Use which_repo for: ${v.task}\nWhich repository does it belong in, why, and what else depends on that repository?`)}
          />
          <Explore
            icon={<Layers />}
            title="The estate's map"
            about="The repositories as a graph: what everything depends on, the layers, the cycles, what is linked to nothing."
            fields={[{ name: 'group', label: 'A group (optional)', placeholder: 'payments', optional: true }]}
            busy={busy}
            onAsk={(v) => void ask(`Use system_map${v.group ? ` for the group ${v.group}` : ''}: what are the hubs everything depends on, the layers from the foundations up, any cycles, and repositories linked to nothing? End with what looks risky.`)}
          />
        </div>
      </section>
      <BusinessMapSection />
    </div>
  )
}

function Explore({
  icon,
  title,
  about,
  fields,
  busy,
  onAsk,
}: {
  icon: ReactNode
  title: string
  about: string
  fields: { name: string; label: string; placeholder: string; optional?: boolean }[]
  busy: boolean
  onAsk: (values: Record<string, string>) => void
}) {
  const [values, setValues] = useState<Record<string, string>>({})
  const ready = fields.every((f) => f.optional || values[f.name]?.trim())
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base [&_svg]:size-4 [&_svg]:text-muted-foreground">
          {icon} {title}
        </CardTitle>
        <CardDescription>{about}</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="grid gap-2"
          aria-label={title}
          onSubmit={(e) => {
            e.preventDefault()
            if (ready) onAsk(Object.fromEntries(fields.map((f) => [f.name, values[f.name]?.trim() ?? ''])))
          }}
        >
          {fields.map((f) => (
            <Field key={f.name} label={f.label}>
              <Input value={values[f.name] ?? ''} onChange={(e) => setValues({ ...values, [f.name]: e.target.value })} placeholder={f.placeholder} />
            </Field>
          ))}
          <div>
            <Button type="submit" variant="outline" size="sm" disabled={busy || !ready}>
              Ask
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
