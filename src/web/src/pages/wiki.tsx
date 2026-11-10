import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { ArrowLeft, BookOpenText, ChevronLeft, ChevronRight, Code2, Database, ExternalLink, FileText, Search } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { ago, count } from '@/lib/format'
import { CodeBlock } from '@/pages/chat/code-block'
import { Markdown } from '@/pages/chat/markdown'
import { codeIntelName } from '@/lib/brand'

type Where = 'all' | 'knowledge' | 'code' | 'data'

interface Home {
  sources: { id: string; name: string; kind: string; syncedAt: string | null; documents: number }[]
  meaning: boolean
  code: boolean
  data: boolean
}

interface KnowledgeHit {
  documentId: string
  title: string
  url: string | null
  source: string | null
  heading: string | null
  line: number
  snippet: string
  score: number
}

/** What a code match is: a symbol's definition, a file of code, or a document in a repository. */
type CodeKind = 'symbol' | 'code' | 'doc'

interface CodeHit {
  kind: CodeKind
  /** How it matched: "name and scope", "same words", "file name", "words"… */
  how: string
  repoId: number | null
  repo: string | null
  branch: string | null
  path: string | null
  lang: string | null
  line: number | null
  name: string | null
  symbolKind: string | null
  scope: string | null
  doc: string | null
  snippet: string | null
}

interface CodePage {
  total: number
  page: number
  perPage: number
  /** How far pages reach: past it the filters narrow the search. */
  reach: number
  facets: { repos: { repo: string; n: number }[]; langs: { lang: string; n: number }[]; kinds: Record<CodeKind, number> }
  branches: string[]
  branch: string | null
}

interface MeaningHit {
  repoId: number | null
  repo: string | null
  path: string | null
  line: number | null
  name: string | null
  symbolKind: string | null
  doc: string | null
  snippet: string | null
  score: number | null
}

interface DataHit {
  sourceId: string
  source: string
  table: string
  kind: string
  comment: string | null
  columns: { name: string; type: string; comment: string | null }[]
  matched: string[]
}

interface Found {
  query: string
  knowledge: KnowledgeHit[]
  code: CodeHit[]
  codePage: CodePage | null
  meaning: MeaningHit[]
  data: DataHit[]
  problems: string[]
}

interface WikiDocument {
  id: string
  title: string
  url: string | null
  sourceId: string
  source: string
  syncedAt: string
  passages: number
}

/**
 * The wiki: the company's documents, code and data, found and read by people themselves with no model in between. One
 * search (by words, and by meaning when the embedder runs), the sources to browse, a document whole, a code file. Only
 * what the person may read is ever shown.
 */
export function WikiPage() {
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''
  const where = (params.get('in') as Where | null) ?? 'all'
  const source = params.get('source')
  const filters = Object.fromEntries(codeFilters.flatMap((k) => (params.get(k) ? [[k, params.get(k)!]] : []))) as Partial<Record<CodeFilter, string>>
  const [typed, setTyped] = useState(q)
  const home = useQuery({ queryKey: ['wiki'], queryFn: ({ signal }) => api<Home>('/api/wiki', { signal }) })
  const found = useQuery({
    queryKey: ['wiki', 'search', q, where, filters],
    queryFn: ({ signal }) => api<Found>(`/api/wiki/search?${new URLSearchParams({ q, in: where, ...filters })}`, { signal }),
    enabled: q.length > 0,
    placeholderData: (previous) => previous,
  })
  const go = (next: Record<string, string | null>) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p)
        for (const [k, v] of Object.entries(next)) {
          if (v) n.set(k, v)
          else n.delete(k)
        }
        return n
      },
      { replace: false },
    )
  const submit = (e: FormEvent) => {
    e.preventDefault()
    // A new search starts on its first page; its filters stay.
    go({ q: typed.trim() || null, source: null, page: null })
  }
  return (
    <>
      <PageHeader title="Wiki" description="The company's documents, code and data, searched and read directly: no model, only what you may read." />
      <form onSubmit={submit} aria-label="Search the wiki" className="mb-4 flex flex-wrap items-center gap-2">
        <div className="relative min-w-0 flex-1 basis-80">
          <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input value={typed} onChange={(e) => setTyped(e.target.value)} aria-label="Search the wiki" placeholder="Words, a name in the code, a table…" className="pl-9" maxLength={300} />
        </div>
        <Button type="submit">Search</Button>
        <Segmented
          label="Where"
          value={where}
          onChange={(v) => go({ in: v === 'all' ? null : v, page: null })}
          options={[
            { value: 'all', label: 'Everything' },
            { value: 'knowledge', label: 'Documents' },
            ...(home.data?.code ? [{ value: 'code' as const, label: 'Code' }] : []),
            ...(home.data?.data ? [{ value: 'data' as const, label: 'Data' }] : []),
          ]}
        />
      </form>
      {home.error && <QueryError error={home.error} retry={() => home.refetch()} />}
      <div className="grid gap-6 lg:grid-cols-[16rem_minmax(0,1fr)]">
        <nav aria-label="Sources" className="grid content-start gap-1 text-sm">
          <h2 className="mb-1 text-xs font-medium tracking-wide text-muted-foreground uppercase">Sources</h2>
          {home.isPending && <Skeleton className="h-24" />}
          {home.data?.sources.length === 0 && <p className="text-muted-foreground">No documents you may read yet.</p>}
          {home.data?.sources.map((s) => (
            <button
              key={s.id}
              type="button"
              onClick={() => go({ source: s.id, q: null })}
              aria-current={source === s.id ? 'page' : undefined}
              className={`flex items-center gap-2 rounded-md px-2 py-1.5 text-left hover:bg-accent ${source === s.id ? 'bg-accent font-medium' : ''}`}
            >
              <BookOpenText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <span className="min-w-0 flex-1 truncate">{s.name}</span>
              <span className="text-xs text-muted-foreground tabular-nums">{count(s.documents)}</span>
            </button>
          ))}
          {home.data?.code && (
            <Link to="/wiki/code/repos" className="mt-2 flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent">
              <Code2 className="size-4 text-muted-foreground" aria-hidden="true" /> Repositories
            </Link>
          )}
          {home.data?.data && (
            <Link to="/data" className="flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent">
              <Database className="size-4 text-muted-foreground" aria-hidden="true" /> Data
            </Link>
          )}
        </nav>
        <section aria-label={q ? `Results for ${q}` : 'Browse'} className="min-w-0">
          {q ? <Results found={found} where={where} filters={filters} go={go} /> : source ? <SourceDocuments source={source} name={home.data?.sources.find((s) => s.id === source)?.name} /> : <Start home={home.data} />}
        </section>
      </div>
    </>
  )
}

function Start({ home }: { home: Home | undefined }) {
  if (!home) return null
  const docs = home.sources.reduce((n, s) => n + s.documents, 0)
  return (
    <Card>
      <CardContent className="grid gap-2 pt-6 text-sm text-muted-foreground">
        <p>
          {count(docs)} {docs === 1 ? 'document' : 'documents'} in {home.sources.length} {home.sources.length === 1 ? 'source' : 'sources'} you may read
          {home.code ? ', the code you may read in GitLab' : ''}
          {home.data ? ', and the tables of the databases you may ask' : ''}.
        </p>
        <p>Search above, or open a source to browse its documents. {home.meaning ? 'Searches find what holds your words, then what is near them in meaning.' : 'Searches find what holds your words.'}</p>
      </CardContent>
    </Card>
  )
}

const codeFilters = ['kind', 'repo', 'lang', 'branch', 'path', 'page'] as const
type CodeFilter = (typeof codeFilters)[number]
type Go = (next: Record<string, string | null>) => void

function Results({ found, where, filters, go }: { found: ReturnType<typeof useQuery<Found>>; where: Where; filters: Partial<Record<CodeFilter, string>>; go: Go }) {
  if (found.isPending) return <Skeleton className="h-48" />
  if (found.error) return <QueryError error={found.error} retry={() => found.refetch()} />
  const f = found.data!
  const filtered = codeFilters.some((k) => k !== 'page' && filters[k])
  const none = f.knowledge.length + f.code.length + f.meaning.length + f.data.length === 0 && !filtered
  return (
    <div className="grid gap-6">
      {f.problems.map((p) => (
        <Alert key={p} variant="warning">
          {p}
        </Alert>
      ))}
      {none && <p className="text-sm text-muted-foreground">Nothing found for “{f.query}”.</p>}
      {(where === 'all' || where === 'knowledge') && f.knowledge.length > 0 && (
        <ResultList title="Documents" icon={<FileText className="size-4" />}>
          {f.knowledge.map((h) => (
            <li key={`${h.documentId}-${h.line}`} className="grid gap-1 rounded-lg border bg-card px-4 py-3">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <Link to={`/wiki/documents/${h.documentId}`} className="min-w-0 truncate font-medium underline-offset-2 hover:underline">
                  {h.title}
                </Link>
                {h.source && <Badge variant="outline">{h.source}</Badge>}
                {h.heading && <span className="truncate text-xs text-muted-foreground">§ {h.heading}</span>}
                {h.url && (
                  <a href={h.url} target="_blank" rel="noreferrer" className="ml-auto text-xs text-muted-foreground hover:underline">
                    Open the original <ExternalLink className="inline size-3" aria-hidden="true" />
                  </a>
                )}
              </div>
              <p dir="auto" className="text-sm text-muted-foreground">
                {h.snippet}
              </p>
            </li>
          ))}
        </ResultList>
      )}
      {(where === 'all' || where === 'code') && f.codePage && (f.codePage.total > 0 || filtered) && <CodeResults hits={f.code} page={f.codePage} filters={filters} go={go} />}
      {(where === 'all' || where === 'code') && f.meaning.length > 0 && (
        <ResultList title="Near in meaning" icon={<Code2 className="size-4" />}>
          {f.meaning.map((h, i) => (
            <li key={`${h.repoId}-${h.path}-${h.line}-${i}`} className="grid gap-1 rounded-lg border bg-card px-4 py-3">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <FileLink repoId={h.repoId} repo={h.repo} path={h.path} line={h.line} />
                <Badge variant="outline">
                  {h.symbolKind ?? 'symbol'} {h.name}
                </Badge>
                {h.score !== null && <span className="text-xs text-muted-foreground tabular-nums">{Math.round(h.score * 100)}% near</span>}
              </div>
              {h.doc && <p className="text-sm text-muted-foreground">{h.doc}</p>}
            </li>
          ))}
        </ResultList>
      )}
      {(where === 'all' || where === 'data') && f.data.length > 0 && (
        <ResultList title="Data" icon={<Database className="size-4" />}>
          {f.data.map((h) => (
            <li key={`${h.sourceId}-${h.table}`} className="grid gap-1 rounded-lg border bg-card px-4 py-3">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-mono text-sm font-medium">{h.table}</span>
                <Badge variant="outline">{h.source}</Badge>
                <span className="text-xs text-muted-foreground">{h.kind}</span>
                <Link to="/data" className="ml-auto text-xs text-muted-foreground hover:underline">
                  Ask about it
                </Link>
              </div>
              {h.comment && <p className="text-sm text-muted-foreground">{h.comment}</p>}
              <p className="text-xs text-muted-foreground">
                {h.columns.map((c, i) => (
                  <span key={c.name}>
                    {i > 0 && ', '}
                    <span className={h.matched.includes(c.name) ? 'font-semibold text-foreground' : undefined}>{c.name}</span> <span className="opacity-70">{c.type}</span>
                  </span>
                ))}
              </p>
            </li>
          ))}
        </ResultList>
      )}
    </div>
  )
}

const kindWords: Record<CodeKind, string> = { symbol: 'Symbols', code: 'Code', doc: 'Docs' }

/** A path in a repository, to the line, opening the file there. */
function FileLink({ repoId, repo, path, line }: { repoId: number | null; repo: string | null; path: string | null; line: number | null }) {
  if (!repoId || !path) return <span className="font-mono text-xs">{repo}</span>
  return (
    <Link to={`/wiki/code/${repoId}?${new URLSearchParams({ path, ...(line ? { line: String(line) } : {}) })}`} className="min-w-0 truncate font-mono text-xs underline-offset-2 hover:underline">
      {repo}/{path}
      {line ? `:${line}` : ''}
    </Link>
  )
}

/**
 * The code that matches, a page at a time: each match with how it matched, the filters (kind, repository, language,
 * branch, path) with how many each would show, and the pages to every match.
 */
function CodeResults({ hits, page, filters, go }: { hits: CodeHit[]; page: CodePage; filters: Partial<Record<CodeFilter, string>>; go: Go }) {
  const [path, setPath] = useState(filters.path ?? '')
  const pages = Math.max(1, Math.ceil(Math.min(page.total, page.reach) / page.perPage))
  const kinds = (Object.keys(kindWords) as CodeKind[]).map((k) => ({ value: k, label: `${kindWords[k]} ${count(page.facets.kinds[k] ?? 0)}` }))
  const pick = (k: CodeFilter, v: string | null) => go({ [k]: v, page: null })
  const first = (page.page - 1) * page.perPage + 1
  return (
    <section aria-label="Code" className="grid gap-3">
      <h2 className="flex items-center gap-2 text-sm font-semibold">
        <Code2 className="size-4" />
        Code
        <span className="font-normal text-muted-foreground">
          {page.total === 0 ? 'nothing matches these filters' : `${count(page.total)} ${page.total === 1 ? 'match' : 'matches'}`}
        </span>
      </h2>
      <fieldset aria-label="Filter the code" className="m-0 flex min-w-0 flex-wrap items-center gap-2 border-0 p-0">
        <Segmented label="Kind" value={(filters.kind as CodeKind | undefined) ?? 'all'} onChange={(v) => pick('kind', v === 'all' ? null : v)} options={[{ value: 'all', label: 'All' }, ...kinds]} />
        <FacetSelect label="Repository" all="Every repository" value={filters.repo} options={page.facets.repos.map((r) => ({ value: r.repo, label: r.repo, n: r.n }))} onChange={(v) => pick('repo', v)} />
        <FacetSelect label="Language" all="Every language" value={filters.lang} options={page.facets.langs.map((l) => ({ value: l.lang, label: l.lang, n: l.n }))} onChange={(v) => pick('lang', v)} />
        {page.branches.length > 1 && (
          <FacetSelect label="Branch" all="Default branches" value={filters.branch} options={page.branches.map((b) => ({ value: b, label: b }))} onChange={(v) => pick('branch', v)} />
        )}
        <form
          className="contents"
          onSubmit={(e) => {
            e.preventDefault()
            pick('path', path.trim() || null)
          }}
        >
          <Input value={path} onChange={(e) => setPath(e.target.value)} onBlur={() => path.trim() !== (filters.path ?? '') && pick('path', path.trim() || null)} aria-label="Path" placeholder="Path: src/ or *.h" className="h-9 w-48 font-mono text-xs" />
        </form>
      </fieldset>
      {hits.length > 0 && (
        <ol aria-label="Code matches" start={first} className="grid gap-2">
          {hits.map((h, i) => (
            <li key={`${h.repoId}-${h.path}-${h.line}-${i}`} className="grid gap-1 rounded-lg border bg-card px-4 py-3">
              <div className="flex min-w-0 flex-wrap items-center gap-2">
                <FileLink repoId={h.repoId} repo={h.repo} path={h.path} line={h.line} />
                {h.kind === 'symbol' ? (
                  <Badge variant="secondary" className="font-mono">
                    {h.symbolKind ?? 'symbol'} {h.scope ? `${h.scope}::` : ''}
                    {h.name}
                  </Badge>
                ) : (
                  <Badge variant="outline">{h.kind === 'doc' ? 'doc' : (h.lang ?? 'code')}</Badge>
                )}
                <span className="text-xs text-muted-foreground">{h.how}</span>
                {h.branch && filters.branch && <span className="ml-auto text-xs text-muted-foreground">{h.branch}</span>}
              </div>
              {h.doc && <p className="text-sm text-muted-foreground">{h.doc}</p>}
              {h.snippet && <pre className="overflow-x-auto rounded bg-muted/50 px-2 py-1 font-mono text-xs whitespace-pre-wrap">{h.snippet}</pre>}
            </li>
          ))}
        </ol>
      )}
      {pages > 1 && (
        <nav aria-label="Pages of code matches" className="flex flex-wrap items-center gap-2 text-sm">
          <Button variant="outline" size="sm" disabled={page.page <= 1} onClick={() => go({ page: page.page > 2 ? String(page.page - 1) : null })}>
            <ChevronLeft /> Previous
          </Button>
          <span className="text-muted-foreground tabular-nums">
            Page {page.page} of {pages}
          </span>
          <Button variant="outline" size="sm" disabled={page.page >= pages} onClick={() => go({ page: String(page.page + 1) })}>
            Next <ChevronRight />
          </Button>
          {page.total > page.reach && <span className="text-xs text-muted-foreground">The first {count(page.reach)} of them: narrow the search to reach the rest.</span>}
        </nav>
      )}
    </section>
  )
}

function FacetSelect({ label, all, value, options, onChange }: { label: string; all: string; value: string | undefined; options: { value: string; label: string; n?: number }[]; onChange: (v: string | null) => void }) {
  // A value picked stays a choice even when this page's facets no longer list it.
  const shown = value && !options.some((o) => o.value === value) ? [{ value, label: value }, ...options] : options
  return (
    <Select value={value ?? '*'} onValueChange={(v) => onChange(v === '*' ? null : v)}>
      <SelectTrigger aria-label={label} className="h-9 w-auto max-w-56">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="*">{all}</SelectItem>
        {shown.map((o) => (
          <SelectItem key={o.value} value={o.value}>
            {o.label}
            {o.n !== undefined && <span className="ml-2 text-xs text-muted-foreground tabular-nums">{count(o.n)}</span>}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

function ResultList({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <section aria-label={title} className="grid gap-2">
      <h2 className="flex items-center gap-2 text-sm font-semibold">
        {icon}
        {title}
      </h2>
      <ul className="grid gap-2">{children}</ul>
    </section>
  )
}

function SourceDocuments({ source, name }: { source: string; name: string | undefined }) {
  const [filter, setFilter] = useState('')
  const docs = useInfiniteQuery({
    queryKey: ['wiki', 'documents', source, filter],
    initialPageParam: 0,
    queryFn: ({ pageParam, signal }) =>
      api<{ documents: WikiDocument[]; next: number | null }>(`/api/wiki/documents?${new URLSearchParams({ source, skip: String(pageParam), ...(filter ? { q: filter } : {}) })}`, { signal }),
    getNextPageParam: (last) => last.next,
  })
  const items = docs.data?.pages.flatMap((p) => p.documents) ?? []
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-base font-semibold">{name ?? 'Documents'}</h2>
        <Input value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter by title" placeholder="Filter by title" className="ml-auto h-8 w-56" />
      </div>
      {docs.error && <QueryError error={docs.error} retry={() => docs.refetch()} />}
      {docs.isPending && <Skeleton className="h-40" />}
      <ul aria-label="Documents" className="grid gap-1 text-sm">
        {items.map((d) => (
          <li key={d.id} className="flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent">
            <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <Link to={`/wiki/documents/${d.id}`} className="min-w-0 flex-1 truncate underline-offset-2 hover:underline">
              {d.title}
            </Link>
            <span className="shrink-0 text-xs text-muted-foreground">{ago(d.syncedAt)}</span>
          </li>
        ))}
      </ul>
      {docs.data && items.length === 0 && <p className="text-sm text-muted-foreground">No documents{filter ? ` with “${filter}” in their title` : ''}.</p>}
      {docs.hasNextPage && (
        <Button variant="outline" className="w-fit" loading={docs.isFetchingNextPage} onClick={() => void docs.fetchNextPage()}>
          More
        </Button>
      )}
    </div>
  )
}

/** A document whole, as its passages hold it, with a link to where it lives. */
export function WikiDocumentPage() {
  const { id } = useParams()
  const doc = useQuery({
    queryKey: ['wiki', 'document', id],
    queryFn: ({ signal }) =>
      api<WikiDocument & { passages: { ordinal: number; heading: string | null; line: number; endLine: number; text: string }[] }>(`/api/wiki/documents/${id}`, { signal }),
  })
  if (doc.isPending) return <Skeleton className="h-96" />
  if (doc.error) return <QueryError error={doc.error} retry={() => doc.refetch()} />
  const d = doc.data
  return (
    <>
      <Button variant="ghost" size="sm" asChild className="mb-2 w-fit">
        <Link to={`/wiki?source=${d.sourceId}`}>
          <ArrowLeft /> {d.source}
        </Link>
      </Button>
      <PageHeader
        title={d.title}
        description={`From ${d.source}, read ${ago(d.syncedAt)}.`}
        actions={
          d.url ? (
            <Button variant="outline" size="sm" asChild>
              <a href={d.url} target="_blank" rel="noreferrer">
                Open the original <ExternalLink />
              </a>
            </Button>
          ) : undefined
        }
      />
      <article dir="auto" className="max-w-4xl">
        <Markdown text={Joined(d.passages.map((p) => p.text))} />
      </article>
    </>
  )
}

/** Passages overlap at their edges: each one's start that the one before ends with is not said twice. */
function Joined(passages: string[]): string {
  let out = ''
  for (const p of passages) {
    let cut = 0
    for (let n = Math.min(400, p.length, out.length); n > 20; n--) {
      if (out.endsWith(p.slice(0, n))) {
        cut = n
        break
      }
    }
    out += (out && !cut ? '\n\n' : '') + p.slice(cut)
  }
  return out
}

/** The repositories the person may read, or a file of one. */
export function WikiFilePage() {
  const { repo } = useParams()
  const [params] = useSearchParams()
  const path = params.get('path')
  const repos = useQuery({
    queryKey: ['wiki', 'repos'],
    queryFn: ({ signal }) => api<{ repoId: number; repo: string; files: number | null; symbols: number | null; indexedAt: string | null; branches: { repoId: number; branch: string; files: number | null }[] }[]>('/api/wiki/code/repos', { signal }),
    enabled: repo === 'repos',
  })
  const file = useQuery({
    queryKey: ['wiki', 'file', repo, path],
    queryFn: ({ signal }) => api<{ path: string; language: string | null; content: string; truncated: boolean }>(`/api/wiki/code/file?${new URLSearchParams({ repo: repo!, path: path! })}`, { signal }),
    enabled: repo !== 'repos' && !!path,
  })
  if (repo === 'repos') {
    return (
      <>
        <Button variant="ghost" size="sm" asChild className="mb-2 w-fit">
          <Link to="/wiki">
            <ArrowLeft /> Wiki
          </Link>
        </Button>
        <PageHeader title="Repositories" description={`The code you may read in GitLab, as ${codeIntelName} indexed it. Search the wiki for a name or words to find a file.`} />
        {repos.error && <QueryError error={repos.error} retry={() => repos.refetch()} />}
        {repos.isPending && <Skeleton className="h-40" />}
        <ul aria-label="Repositories" className="grid gap-1 text-sm">
          {repos.data?.map((r) => (
            <li key={r.repoId} className="flex items-center gap-3 rounded-md px-2 py-1.5 hover:bg-accent">
              <Code2 className="size-4 text-muted-foreground" aria-hidden="true" />
              <span className="min-w-0 flex-1 truncate font-mono text-xs">{r.repo}</span>
              <span className="text-xs text-muted-foreground">
                {r.files !== null ? `${count(r.files)} files` : ''}
                {r.symbols !== null ? ` · ${count(r.symbols)} symbols` : ''}
                {r.indexedAt ? ` · ${ago(r.indexedAt)}` : ''}
                {r.branches.length > 1 ? ` · ${r.branches.length} branches` : ''}
              </span>
            </li>
          ))}
        </ul>
      </>
    )
  }
  return (
    <>
      <Button variant="ghost" size="sm" asChild className="mb-2 w-fit">
        <Link to="/wiki">
          <ArrowLeft /> Wiki
        </Link>
      </Button>
      <PageHeader title={path ?? 'A file'} description={file.data?.truncated ? 'Its first 64 KB.' : undefined} />
      {file.error && <QueryError error={file.error} retry={() => file.refetch()} />}
      {file.isPending && <Skeleton className="h-96" />}
      {file.data && <CodeBlock code={file.data.content} lang={file.data.language} name={path} />}
    </>
  )
}
