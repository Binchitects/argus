import { useQuery } from '@tanstack/react-query'
import { BookOpen, ExternalLink, FileCode2, Search } from 'lucide-react'
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { ScrollRegion } from '@/components/app/scroll-region'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NotConfigured } from './argus'

interface Explore {
  configured?: boolean
  repos: { repo_id: number; path_with_namespace: string; branch: string; files: number; symbols: number; public_symbols: number }[]
  symbols: { rows: { name: string; kind: string; path: string; line: number; path_with_namespace: string; branch: string; repo_id: number; signature?: string }[]; capped: boolean }
  files: { rows: { path: string; lang: string; size: number; path_with_namespace: string; branch: string; repo_id: number; symbols: number }[]; capped: boolean }
  error?: string
}

interface Page<T> {
  configured?: boolean
  rows: T[]
  capped: boolean
  sources?: string[]
}

type Reference = { repo: string; path: string; line: number; context: string; is_definition: boolean }
type CodeHit = { repo_id: number; path_with_namespace: string; path: string; snippet: string }
type DocHit = { source: string; title: string | null; doc_path: string; url: string | null; excerpt?: string; text?: string; name?: string; kind?: string; signature?: string | null; score?: number }

/** What to open: a file of the index at a line, or a page of a pack. */
type Opened = { kind: 'file'; repoId: number; path: string; line?: number } | { kind: 'doc'; path: string; source: string }

const ALL = '__all__'
const tabs = ['index', 'references', 'code', 'docs'] as const
type Tab = (typeof tabs)[number]

/**
 * What the index and the packs hold, for "a tool found nothing: is it absent,
 * named differently, or never indexed?": names and paths, where a name is used,
 * words in the code, and the documentation. Everything, whoever may see it.
 */
export function ExplorePage() {
  const [params, setParams] = useSearchParams()
  const tab: Tab = tabs.includes(params.get('tab') as Tab) ? (params.get('tab') as Tab) : 'index'
  const [opened, setOpened] = useState<Opened | null>(null)
  const index = useQuery({
    queryKey: ['admin', 'argus', 'explore', '', ''],
    queryFn: ({ signal }) => api<Explore>('/api/admin/argus/explore?q=&repo=&limit=1', { signal }),
  })
  if (index.data?.configured === false)
    return (
      <>
        <PageHeader title="Explore the index" />
        <NotConfigured />
      </>
    )
  const repos = index.data?.repos ?? []
  // A file is opened by its repository id; references name the repository by path.
  const repoId = (path: string) => repos.find((r) => r.path_with_namespace === path)?.repo_id
  return (
    <>
      <PageHeader title="Explore the index" description="Search what Argus holds: symbols and paths, where a name is used, words in the code, and the documentation packs." />
      <Tabs value={tab} onValueChange={(v) => setParams((p) => ({ ...Object.fromEntries(p), tab: v }), { replace: true })} className="grid grid-cols-[minmax(0,1fr)]">
        {/* Two by two on a phone: four tabs do not fit one row. */}
        <TabsList className="grid h-auto w-full grid-cols-2 gap-1 sm:inline-flex sm:h-9 sm:w-fit">
          <TabsTrigger value="index" className="h-7">Symbols and paths</TabsTrigger>
          <TabsTrigger value="references" className="h-7">References</TabsTrigger>
          <TabsTrigger value="code" className="h-7">Code</TabsTrigger>
          <TabsTrigger value="docs" className="h-7">Documentation</TabsTrigger>
        </TabsList>
        <TabsContent value="index">
          <IndexTab repos={repos} loading={index.isPending} error={index.error} onOpen={setOpened} />
        </TabsContent>
        <TabsContent value="references">
          <ReferencesTab repos={repos} onOpen={(r) => repoId(r.repo) !== undefined && setOpened({ kind: 'file', repoId: repoId(r.repo)!, path: r.path, line: r.line })} />
        </TabsContent>
        <TabsContent value="code">
          <CodeTab repos={repos} onOpen={(h) => setOpened({ kind: 'file', repoId: h.repo_id, path: h.path })} />
        </TabsContent>
        <TabsContent value="docs">
          <DocsTab onOpen={(d) => setOpened({ kind: 'doc', path: d.doc_path, source: d.source })} />
        </TabsContent>
      </Tabs>
      <Dialog open={opened !== null} onOpenChange={(o) => !o && setOpened(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-5xl">{opened?.kind === 'file' ? <FileView {...opened} /> : opened?.kind === 'doc' ? <DocView {...opened} /> : null}</DialogContent>
      </Dialog>
    </>
  )
}

/** A search box with a repository filter; searches when submitted. */
function SearchForm({ label, placeholder, repos, extra, onSearch }: { label: string; placeholder: string; repos?: Explore['repos']; extra?: ReactNode; onSearch: (q: string, repo: string) => void }) {
  const [q, setQ] = useState('')
  const [repo, setRepo] = useState('')
  const names = [...new Set((repos ?? []).map((r) => r.path_with_namespace))]
  return (
    <form
      className="flex flex-wrap items-end gap-3"
      onSubmit={(e: FormEvent) => {
        e.preventDefault()
        onSearch(q.trim(), repo)
      }}
    >
      <Field label={label} className="min-w-56 flex-1">
        <Input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={placeholder} />
      </Field>
      {repos && (
        <Field label="Repository" className="w-full sm:w-64">
          <Select value={repo || ALL} onValueChange={(v) => setRepo(v === ALL ? '' : v)}>
            <SelectTrigger className="min-w-0 [&>span]:truncate">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>All repositories</SelectItem>
              {names.map((r) => (
                <SelectItem key={r} value={r}>
                  {r}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}
      {extra}
      <Button type="submit">
        <Search /> Search
      </Button>
    </form>
  )
}

function Results<T>({ query, rows, capped, empty, children }: { query: string; rows: T[] | undefined; capped?: boolean; empty: string; children: (row: T, i: number) => ReactNode }) {
  if (!query || !rows) return null
  if (rows.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>
  return (
    <div className="grid gap-2">
      <output className="text-xs text-muted-foreground">
        {rows.length} {rows.length === 1 ? 'match' : 'matches'}
        {capped ? ': more exist, narrow the search' : ''}
      </output>
      <ul className="grid divide-y rounded-lg border">{rows.map(children)}</ul>
    </div>
  )
}

/** A result that opens: the whole row is one button. */
function Hit({ onOpen, children }: { onOpen: () => void; children: ReactNode }) {
  return (
    <li>
      <button type="button" onClick={onOpen} className="grid w-full min-w-0 gap-1 px-3 py-2 text-left text-sm hover:bg-accent focus-visible:bg-accent focus-visible:outline-none">
        {children}
      </button>
    </li>
  )
}

function IndexTab({ repos, loading, error, onOpen }: { repos: Explore['repos']; loading: boolean; error: unknown; onOpen: (o: Opened) => void }) {
  const [search, setSearch] = useState({ q: '', repo: '' })
  const e = useQuery({
    queryKey: ['admin', 'argus', 'explore', search.q, search.repo],
    queryFn: ({ signal }) => api<Explore>(`/api/admin/argus/explore?${new URLSearchParams({ q: search.q, repo: search.repo, limit: '100' })}`, { signal }),
    enabled: search.q !== '',
  })
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
      <SearchForm label="Symbol or path contains" placeholder="DecodeFrame" repos={repos} onSearch={(q, repo) => setSearch({ q, repo })} />
      {e.error && <QueryError error={e.error} retry={() => e.refetch()} />}
      {e.data?.error && <Alert variant="warning">{e.data.error}</Alert>}
      {search.q && e.isPending && <PageSkeleton />}
      {e.data && search.q && (
        <div className="grid gap-6 xl:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>Symbols</CardTitle>
              {e.data.symbols.capped && <CardDescription>More matches exist; narrow the search.</CardDescription>}
            </CardHeader>
            <CardContent>
              <Results query={search.q} rows={e.data.symbols.rows} empty={`No symbol matches “${search.q}”.`}>
                {(s, i) => (
                  <Hit key={i} onOpen={() => onOpen({ kind: 'file', repoId: s.repo_id, path: s.path, line: s.line })}>
                    <span className="flex flex-wrap items-center gap-2">
                      <code className="font-mono font-medium">{s.name}</code> <Badge variant="secondary">{s.kind}</Badge>
                    </span>
                    <span className="truncate text-xs text-muted-foreground">
                      {s.path_with_namespace}@{s.branch} · {s.path}:{s.line}
                    </span>
                  </Hit>
                )}
              </Results>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>Files</CardTitle>
            </CardHeader>
            <CardContent>
              <Results query={search.q} rows={e.data.files.rows} empty={`No path matches “${search.q}”.`}>
                {(f, i) => (
                  <Hit key={i} onOpen={() => onOpen({ kind: 'file', repoId: f.repo_id, path: f.path })}>
                    <code className="truncate font-mono">{f.path}</code>
                    <span className="text-xs text-muted-foreground">
                      {f.path_with_namespace}@{f.branch} · {f.lang} · {f.symbols} symbols
                    </span>
                  </Hit>
                )}
              </Results>
            </CardContent>
          </Card>
        </div>
      )}
      {!search.q && error ? <QueryError error={error} /> : null}
      {!search.q && loading && <PageSkeleton />}
      {!search.q && !loading && (
        <DataTable
          columns={[
            { accessorKey: 'path_with_namespace', header: ({ column }) => <SortHeader column={column} title="Repository" />, cell: ({ getValue }) => <span className="font-medium">{getValue<string>()}</span> },
            { accessorKey: 'branch', header: 'Branch', cell: ({ getValue }) => <code className="font-mono text-xs">{getValue<string>()}</code> },
            { accessorKey: 'files', header: ({ column }) => <SortHeader column={column} title="Files" />, cell: ({ getValue }) => formatValue(getValue<number>()) },
            { accessorKey: 'symbols', header: ({ column }) => <SortHeader column={column} title="Symbols" />, cell: ({ getValue }) => formatValue(getValue<number>()) },
            { accessorKey: 'public_symbols', header: 'Public', cell: ({ getValue }) => formatValue(getValue<number>()) },
          ] satisfies ColumnDef<Explore['repos'][number]>[]}
          data={repos}
          noun="indexed repositories"
          getRowId={(r) => `${r.path_with_namespace}@${r.branch}`}
        />
      )}
    </div>
  )
}

function useSearch<T>(what: string, params: Record<string, string>, enabled: boolean) {
  return useQuery({
    queryKey: ['admin', 'argus', 'explore', what, params],
    queryFn: ({ signal }) => api<Page<T>>(`/api/admin/argus/explore/${what}?${new URLSearchParams({ ...params, limit: '200' })}`, { signal }),
    enabled,
    retry: false,
  })
}

function ReferencesTab({ repos, onOpen }: { repos: Explore['repos']; onOpen: (r: Reference) => void }) {
  const [search, setSearch] = useState({ q: '', repo: '' })
  const r = useSearch<Reference>('references', { name: search.q, repo: search.repo }, search.q !== '')
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-4">
      <SearchForm label="Name used in the code" placeholder="DecodeFrame" repos={repos} onSearch={(q, repo) => setSearch({ q, repo })} />
      <p className="text-xs text-muted-foreground">Every line where the name appears as a whole word, as find_references answers a model; the definitions are marked.</p>
      {r.error && <Alert variant="warning">{errorMessage(r.error)}</Alert>}
      {search.q && r.isFetching && <PageSkeleton />}
      <Results query={search.q} rows={r.data?.rows} capped={r.data?.capped} empty={`“${search.q}” is not used in any indexed file.`}>
        {(x, i) => (
          <Hit key={i} onOpen={() => onOpen(x)}>
            <span className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span className="[overflow-wrap:anywhere]">
                {x.repo} · {x.path}:{x.line}
              </span>
              {x.is_definition && <Badge variant="secondary">definition</Badge>}
            </span>
            <code className="truncate font-mono text-xs">{x.context.trim()}</code>
          </Hit>
        )}
      </Results>
    </div>
  )
}

/** A snippet with its [matches] marked. */
function Snippet({ text }: { text: string }) {
  const parts = text.split(/(\[[^\]]*\])/g)
  return (
    <code className="font-mono text-xs [overflow-wrap:anywhere] whitespace-pre-wrap">
      {parts.map((p, i) => (p.startsWith('[') && p.endsWith(']') ? <mark key={i} className="rounded-sm bg-warning/30 px-0.5 text-foreground">{p.slice(1, -1)}</mark> : p))}
    </code>
  )
}

function CodeTab({ repos, onOpen }: { repos: Explore['repos']; onOpen: (h: CodeHit) => void }) {
  const [search, setSearch] = useState({ q: '', repo: '' })
  const c = useSearch<CodeHit>('code', { q: search.q, repo: search.repo }, search.q !== '')
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-4">
      <SearchForm label="Words in the code" placeholder='frame decoder, "exact phrase", decod*' repos={repos} onSearch={(q, repo) => setSearch({ q, repo })} />
      {c.error && <Alert variant="warning">{errorMessage(c.error)}</Alert>}
      {search.q && c.isFetching && <PageSkeleton />}
      <Results query={search.q} rows={c.data?.rows} capped={c.data?.capped} empty={`No indexed file contains “${search.q}”.`}>
        {(h, i) => (
          <Hit key={i} onOpen={() => onOpen(h)}>
            <span className="text-xs text-muted-foreground [overflow-wrap:anywhere]">
              {h.path_with_namespace} · {h.path}
            </span>
            <Snippet text={h.snippet} />
          </Hit>
        )}
      </Results>
    </div>
  )
}

const docModes = [
  ['text', 'Words in the pages'],
  ['name', 'An API by its name'],
  ['meaning', 'By meaning'],
] as const

function DocsTab({ onOpen }: { onOpen: (d: DocHit) => void }) {
  const [search, setSearch] = useState({ q: '', mode: 'text', source: '' })
  const [mode, setMode] = useState<string>('text')
  const [source, setSource] = useState('')
  const d = useSearch<DocHit>('docs', { q: search.q, mode: search.mode, source: search.source }, search.q !== '')
  // The sources come with any answer; ask once for them.
  const sources = useSearch<DocHit>('docs', { q: '', mode: 'text', source: '' }, true)
  const names = sources.data?.sources ?? d.data?.sources ?? []
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-4">
      <SearchForm
        label="Search the documentation"
        placeholder="IoCompleteRequest, or how to cancel a pending IRP"
        onSearch={(q) => setSearch({ q, mode, source })}
        extra={
          <>
            <Field label="How" className="w-full sm:w-52">
              <Select value={mode} onValueChange={setMode}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {docModes.map(([v, label]) => (
                    <SelectItem key={v} value={v}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field label="Pack" className="w-full sm:w-48">
              <Select value={source || ALL} onValueChange={(v) => setSource(v === ALL ? '' : v)}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>All packs</SelectItem>
                  {names.map((n) => (
                    <SelectItem key={n} value={n}>
                      {n}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </>
        }
      />
      {sources.error && !search.q && <Alert variant="warning">{errorMessage(sources.error)}</Alert>}
      {d.error && <Alert variant="warning">{errorMessage(d.error)}</Alert>}
      {search.q && d.isFetching && <PageSkeleton />}
      <Results query={search.q} rows={d.data?.rows} capped={d.data?.capped} empty={`Nothing in the documentation matches “${search.q}”.`}>
        {(x, i) => (
          <Hit key={i} onOpen={() => onOpen(x)}>
            <span className="flex flex-wrap items-center gap-2">
              <span className="font-medium [overflow-wrap:anywhere]">{x.name ?? x.title ?? x.doc_path}</span>
              <Badge variant="secondary">{x.source}</Badge>
              {x.kind && <Badge variant="outline">{x.kind}</Badge>}
            </span>
            {x.signature && <code className="font-mono text-xs [overflow-wrap:anywhere]">{x.signature}</code>}
            {(x.excerpt ?? x.text) && <span className="line-clamp-3 text-xs text-muted-foreground">{x.excerpt ?? x.text}</span>}
            <span className="truncate font-mono text-xs text-muted-foreground">{x.doc_path}</span>
          </Hit>
        )}
      </Results>
    </div>
  )
}

interface FileContent {
  path_with_namespace: string
  path: string
  lang: string | null
  size: number
  content: string
  truncated: boolean
}

/** A file of the index, its line numbered, the one asked for marked and in view. */
function FileView({ repoId, path, line }: { repoId: number; path: string; line?: number }) {
  const f = useQuery({
    queryKey: ['admin', 'argus', 'explore', 'file', repoId, path],
    queryFn: ({ signal }) => api<FileContent>(`/api/admin/argus/explore/file?${new URLSearchParams({ repo_id: String(repoId), path })}`, { signal }),
  })
  const target = useRef<HTMLDivElement>(null)
  useEffect(() => {
    target.current?.scrollIntoView?.({ block: 'center' })
  }, [f.data])
  const lines = f.data?.content.split('\n') ?? []
  return (
    <>
      <DialogHeader>
        <DialogTitle className="flex items-center gap-2 font-mono text-base [overflow-wrap:anywhere]">
          <FileCode2 className="size-4 shrink-0" aria-hidden="true" /> {path}
        </DialogTitle>
        <DialogDescription>
          {f.data ? `${f.data.path_with_namespace} · ${f.data.lang ?? 'text'} · ${formatValue(f.data.size, 'bytes')}${line ? ` · line ${line}` : ''}` : 'Reading the file…'}
        </DialogDescription>
      </DialogHeader>
      {f.error && <Alert variant="warning">{errorMessage(f.error)}</Alert>}
      {f.data && (
        <ScrollRegion label={`The text of ${path}`} className="max-h-[70dvh] rounded-lg border bg-muted/30 font-mono text-xs">
          {lines.map((l, i) => (
            <div key={i} ref={i + 1 === line ? target : undefined} className={cn('flex', i + 1 === line && 'bg-warning/25')}>
              <span className="w-12 shrink-0 pr-3 text-right text-muted-foreground select-none">{i + 1}</span>
              <span className="whitespace-pre">{l}</span>
            </div>
          ))}
          {f.data.truncated && <p className="p-3 font-sans text-muted-foreground">The rest of the file is not shown (over 256 KB).</p>}
        </ScrollRegion>
      )}
    </>
  )
}

interface DocContent {
  source: string
  doc_path: string
  title: string | null
  url: string | null
  text: string
  truncated: boolean
  license: string | null
  attribution: string | null
}

function DocView({ path, source }: { path: string; source: string }) {
  const d = useQuery({
    queryKey: ['admin', 'argus', 'explore', 'doc', source, path],
    queryFn: ({ signal }) => api<DocContent>(`/api/admin/argus/explore/doc?${new URLSearchParams({ path, source })}`, { signal }),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle className="flex items-center gap-2 [overflow-wrap:anywhere]">
          <BookOpen className="size-4 shrink-0" aria-hidden="true" /> {d.data?.title ?? path}
        </DialogTitle>
        <DialogDescription className="[overflow-wrap:anywhere]">
          {source} · {path}
          {d.data?.license ? ` · ${d.data.license}` : ''}
        </DialogDescription>
      </DialogHeader>
      {d.error && <Alert variant="warning">{errorMessage(d.error)}</Alert>}
      {d.data?.url && (
        <a href={d.data.url} target="_blank" rel="noreferrer" className="flex w-fit items-center gap-1.5 text-sm font-medium text-primary-ink underline-offset-2 hover:underline">
          <ExternalLink className="size-4" aria-hidden="true" /> The original page
        </a>
      )}
      {d.data && (
        <ScrollRegion label={`The text of ${d.data.title ?? path}`} className="max-h-[70dvh] rounded-lg border bg-muted/30 p-4 text-sm whitespace-pre-wrap [overflow-wrap:anywhere]">
          {d.data.text}
          {d.data.truncated && <p className="mt-3 text-muted-foreground">The rest of the page is not shown.</p>}
        </ScrollRegion>
      )}
    </>
  )
}
