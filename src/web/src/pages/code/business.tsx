import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Boxes, BookOpen, ExternalLink, FileCode2, GitMerge, Library, Loader2, MessageSquareText, Network, RefreshCw, Scale, Search, Trash2, Workflow } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { errorMessage, meQuery } from '@/lib/api'
import { askCode, businessQuery, businessSystemQuery, mapAgain, mapProject, removeMap, sourceLabel, sourceUrl, type BusinessSource, type BusinessSystem } from './api'

const when = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** A map's state in a word or two: its step while it is mapped, why it failed, or how much it holds. */
function MapState({ s }: { s: BusinessSystem }) {
  if (s.status === 'building')
    return (
      <Badge variant="secondary">
        <Loader2 className="animate-spin" aria-hidden="true" />
        {s.step ?? 'Mapping'}…
      </Badge>
    )
  if (s.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  const c = s.counts
  return <Badge variant="success">{c ? `${c.capabilities} capabilities · ${c.rules} rules · ${c.processes} processes` : 'Ready'}</Badge>
}

/**
 * Code Studio's business map: each project mapped into what its software does for the business (its domain, what it
 * lets the business do, the rules it enforces, its processes, the words people use), read by a model with Argus's
 * read-only tools from the code, the documents, the issues and the wiki. Argus answers about the business from it.
 */
export function BusinessMapSection() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const list = useQuery(businessQuery)
  const [project, setProject] = useState('')
  const map = useMutation({
    mutationFn: () => mapProject(project.trim()),
    onSuccess: (s) => {
      setProject('')
      void queryClient.invalidateQueries({ queryKey: businessQuery.queryKey })
      navigate(`/code/business/${s.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (list.isPending) return <Skeleton className="h-40 rounded-xl" />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  if (!list.data.argus) return null
  const systems = list.data.systems
  return (
    <section className="grid gap-4" aria-label="Business map">
      <div>
        <h2 className="text-lg font-semibold">Business map</h2>
        <p className="text-sm text-muted-foreground">
          What each system does for the business, read from its code, documents, issues and wiki: Argus explains the business from it, not only the code.
        </p>
      </div>
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          map.mutate()
        }}
      >
        <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" aria-label="Project to map" className="max-w-sm" />
        <Button type="submit" variant="outline" loading={map.isPending} disabled={!project.trim().includes('/')}>
          <Network /> Map it
        </Button>
      </form>
      {systems.length === 0 ? (
        <p className="text-sm text-muted-foreground">No project you can read is mapped yet. Map one: a model reads it with your access, at your cost, and every item says where it was read.</p>
      ) : (
        <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-label="Mapped systems">
          {systems.map((s) => (
            <li key={s.id}>
              <Link
                to={`/code/business/${s.id}`}
                className="grid h-full gap-1.5 rounded-xl border bg-card p-4 text-sm shadow-xs outline-none transition-colors hover:border-primary/40 focus-visible:ring-[3px] focus-visible:ring-ring"
              >
                <span className="flex items-center gap-2">
                  <Boxes className="size-4 shrink-0 text-primary-ink" aria-hidden="true" />
                  <span className="min-w-0 truncate font-semibold">{s.name}</span>
                </span>
                <span className="truncate text-xs text-muted-foreground">{s.project}</span>
                {s.domain && <span className="text-muted-foreground">{s.domain}</span>}
                <span>
                  <MapState s={s} />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/** A source's chip: where the item was read, opening in GitLab when its address is known. */
function Source({ s, web }: { s: BusinessSource; web: string | null }) {
  const Icon = s.kind === 'code' ? FileCode2 : s.kind === 'doc' ? BookOpen : s.kind === 'mr' ? GitMerge : s.kind === 'wiki' ? Library : MessageSquareText
  const url = sourceUrl(web, s)
  const chip = (
    <>
      <Icon className="size-3 shrink-0" aria-hidden="true" />
      <span className="min-w-0 truncate font-mono">{sourceLabel(s)}</span>
    </>
  )
  const cls = 'inline-flex max-w-full items-center gap-1 rounded-md border bg-muted/40 px-1.5 py-0.5 text-[0.6875rem] text-muted-foreground'
  return url ? (
    <a href={url} target="_blank" rel="noreferrer" className={`${cls} hover:text-foreground hover:underline`} title={`Open ${sourceLabel(s)} in GitLab`}>
      {chip}
    </a>
  ) : (
    <span className={cls}>{chip}</span>
  )
}

function Sources({ sources, web }: { sources: BusinessSource[]; web: string | null }) {
  return (
    <span className="flex flex-wrap gap-1" aria-label="Sources">
      {sources.map((s) => (
        <Source key={`${s.kind}:${s.ref}`} s={s} web={web} />
      ))}
    </span>
  )
}

/** One part of the map: its items, each with its words and sources. */
function Part({ title, icon: Icon, count, children }: { title: string; icon: typeof Boxes; count: number; children: ReactNode }) {
  if (count === 0) return null
  return (
    <Card aria-label={title}>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Icon className="size-4 text-primary-ink" aria-hidden="true" />
          {title}
          <span className="text-sm font-normal text-muted-foreground">{count}</span>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="grid gap-3 text-sm">{children}</ul>
      </CardContent>
    </Card>
  )
}

/** One mapped system: its summary, then what it does for the business, part by part, with the sources of every item. */
export function BusinessPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const me = useQuery(meQuery)
  const got = useQuery(businessSystemQuery(id))
  const [filter, setFilter] = useState('')
  const again = useMutation({
    mutationFn: () => mapAgain(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['code', 'business'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => removeMap(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: businessQuery.queryKey })
      navigate('/code')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const ask = useMutation({
    mutationFn: (question: string) => askCode({ question, project: got.data!.system.project }),
    onSuccess: (chat) => navigate(`/chat/${chat.id}?ask=${encodeURIComponent(chat.message)}`),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (got.isPending) return <PageSkeleton />
  if (got.error) return <QueryError error={got.error} retry={() => got.refetch()} />
  const { system: s, profile: p, web } = got.data
  const q = filter.trim().toLowerCase()
  const has = (...texts: string[]) => !q || texts.some((t) => t.toLowerCase().includes(q))
  const terms = p.glossary.filter((g) => has(g.term, g.meaning))
  return (
    <>
      <Button variant="ghost" size="sm" className="mb-2 -ml-2" asChild>
        <Link to="/code">
          <ArrowLeft /> Code Studio
        </Link>
      </Button>
      <PageHeader
        title={s.name}
        description={s.domain ?? s.project}
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => ask.mutate(`Explain what ${s.name} does for the business: its main processes and the rules that matter most, with the code that does them.`)} loading={ask.isPending}>
              <MessageSquareText /> Ask Argus
            </Button>
            <Button variant="outline" onClick={() => again.mutate()} disabled={s.status === 'building'} loading={again.isPending}>
              <RefreshCw /> Map again
            </Button>
            {me.data?.isAdmin && (
              <Button
                variant="outline"
                aria-label="Remove the map"
                onClick={async () => {
                  if (await confirm({ title: `Remove the map of ${s.project}?`, description: 'Argus no longer explains it from the map until someone maps it again.', confirm: 'Remove', destructive: true }))
                    remove.mutate()
                }}
              >
                <Trash2 />
              </Button>
            )}
          </div>
        }
      />
      <div className="grid gap-6">
        <div className="grid gap-2 text-sm">
          <p className="flex flex-wrap items-center gap-2 text-muted-foreground">
            {web ? (
              <a href={web} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 font-mono hover:text-foreground hover:underline">
                {s.project} <ExternalLink className="size-3" aria-hidden="true" />
              </a>
            ) : (
              <span className="font-mono">{s.project}</span>
            )}
            <MapState s={s} />
            {s.builtAt && s.status !== 'building' && <span className="text-xs">Mapped {when.format(new Date(s.builtAt))}{s.model ? ` with ${s.model}` : ''}</span>}
          </p>
          {s.error && <Alert variant={s.status === 'failed' ? 'destructive' : 'warning'}>{s.error}</Alert>}
          {s.summary && <p className="max-w-3xl text-[0.9375rem] leading-relaxed">{s.summary}</p>}
        </div>
        {s.status === 'building' && !s.summary && <Skeleton className="h-40 rounded-xl" />}
        {s.counts && s.status !== 'building' && (s.counts.capabilities + s.counts.rules + s.counts.glossary > 0) && (
          <div className="relative max-w-sm">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input className="ps-8" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Find in the map" aria-label="Find in the map" />
          </div>
        )}
        <div className="grid gap-6 xl:grid-cols-2">
          <Part title="What it lets the business do" icon={Boxes} count={p.capabilities.filter((c) => has(c.name, c.description)).length}>
            {p.capabilities.filter((c) => has(c.name, c.description)).map((c) => (
              <li key={c.name} className="grid gap-1">
                <span className="font-medium">{c.name}</span>
                {c.description && <span className="text-muted-foreground">{c.description}</span>}
                <Sources sources={c.sources} web={web} />
              </li>
            ))}
          </Part>
          <Part title="Processes" icon={Workflow} count={p.processes.filter((x) => has(x.name, ...x.steps)).length}>
            {p.processes.filter((x) => has(x.name, ...x.steps)).map((x) => (
              <li key={x.name} className="grid gap-1">
                <span className="font-medium">{x.name}</span>
                <ol className="ms-5 list-decimal text-muted-foreground">
                  {x.steps.map((step, i) => (
                    <li key={i}>{step}</li>
                  ))}
                </ol>
                <Sources sources={x.sources} web={web} />
              </li>
            ))}
          </Part>
          <Part title="Rules" icon={Scale} count={p.rules.filter((r) => has(r.rule, r.why)).length}>
            {p.rules.filter((r) => has(r.rule, r.why)).map((r) => (
              <li key={r.rule} className="grid gap-1">
                <span className="font-medium">{r.rule}</span>
                {r.why && <span className="text-muted-foreground">Why: {r.why}</span>}
                <Sources sources={r.sources} web={web} />
              </li>
            ))}
          </Part>
          <Part title="What it deals in" icon={Boxes} count={p.entities.filter((e) => has(e.name, e.meaning)).length}>
            {p.entities.filter((e) => has(e.name, e.meaning)).map((e) => (
              <li key={e.name} className="grid gap-1">
                <span className="font-medium">{e.name}</span>
                {e.meaning && <span className="text-muted-foreground">{e.meaning}</span>}
                <Sources sources={e.sources} web={web} />
              </li>
            ))}
          </Part>
          <Part title="Who it talks to" icon={Network} count={p.integrations.filter((i) => has(i.system, i.how)).length}>
            {p.integrations.filter((i) => has(i.system, i.how)).map((i) => (
              <li key={i.system} className="grid gap-1">
                <span className="font-medium">{i.system}</span>
                {i.how && <span className="text-muted-foreground">{i.how}</span>}
                <Sources sources={i.sources} web={web} />
              </li>
            ))}
          </Part>
          <Part title="Glossary" icon={Library} count={terms.length}>
            {terms.map((g) => (
              <li key={g.term} className="grid gap-1">
                <span>
                  <dfn className="font-medium not-italic">{g.term}</dfn>
                  {g.meaning && <span className="text-muted-foreground"> — {g.meaning}</span>}
                </span>
                <Sources sources={g.sources} web={web} />
              </li>
            ))}
          </Part>
        </div>
        {s.status === 'ready' && q && p.capabilities.length + p.processes.length + p.rules.length + p.entities.length + p.integrations.length + p.glossary.length > 0 &&
          !(p.capabilities.some((c) => has(c.name, c.description)) || p.processes.some((x) => has(x.name, ...x.steps)) || p.rules.some((r) => has(r.rule, r.why)) ||
            p.entities.some((e) => has(e.name, e.meaning)) || p.integrations.some((i) => has(i.system, i.how)) || terms.length > 0) && (
            <p className="text-sm text-muted-foreground">Nothing on the map has “{filter}”.</p>
          )}
      </div>
    </>
  )
}
