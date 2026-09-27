import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, CircleDot, ExternalLink, Package, Plus, PowerOff, RefreshCw, Trash2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NotConfigured } from './argus'

interface PackDetails {
  docs: number | null
  chunks: number | null
  symbols: number | null
  source_repo: string | null
  source_branch: string | null
  attribution: string | null
  license_url: string | null
}

interface PackMeta {
  name: string
  version: string
  model: string
  dim: string | number
  size_bytes: number
  license: string | null
  commit?: string | null
  compatible: boolean
  incompatible_reason: string | null
  details?: PackDetails
}

interface Packs {
  configured?: boolean
  /** What Argus searches: loaded from the library (a link) or installed from a URL (a copy). */
  packs: (PackMeta & { source?: 'library' | 'installed' })[]
  /** The pack library's files, and which are loaded. */
  library?: (PackMeta & { file: string; loaded: boolean })[]
  library_dir?: string | null
  job: { state: string; action: string | null; target: string | null; returncode: number | null; tail: string[]; finished: number | null }
  index_url: string | null
  error?: string
}

const count = (n: number | null | undefined, noun: string) => (n === null || n === undefined ? null : `${n.toLocaleString('en-US')} ${noun}`)

/** One line for a card: its contents and size. */
function contents(k: PackMeta) {
  return [count(k.details?.docs, 'pages'), count(k.details?.symbols, 'API symbols'), formatValue(k.size_bytes, 'bytes'), k.license || 'no licence given'].filter(Boolean).join(' · ')
}

/**
 * Knowledge packs, as the models are: the packs Argus searches, each on a card,
 * and "Add a pack" to pick one from the pack library, see what it holds, and
 * install it (a link, so instant and nothing copied), or to install one from a URL.
 */
export function PacksPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [adding, setAdding] = useState(false)
  const p = useQuery({
    queryKey: ['admin', 'argus', 'packs'],
    queryFn: ({ signal }) => api<Packs>('/api/admin/argus/packs', { signal }),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : false),
  })
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: object }) => api(`/api/admin/argus/packs/${action}`, { body }),
    onSuccess: (_, { action, body }) => {
      if (action === 'remove') toast.success(`${(body as { name: string }).name} removed`, { description: 'Argus no longer searches it.' })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'packs'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (p.isPending) return <PageSkeleton />
  if (p.error) return <QueryError error={p.error} retry={() => p.refetch()} />
  if (p.data.configured === false)
    return (
      <>
        <PageHeader title="Knowledge packs" />
        <NotConfigured />
      </>
    )
  const running = p.data.job.state === 'running'
  const remove = async (name: string, copy: boolean) => {
    if (
      await confirm({
        title: `Remove ${name}?`,
        description: copy ? 'Argus stops searching it, and its file is deleted: it was installed from a URL.' : 'Argus stops searching it. It stays in the pack library, to add again.',
        confirm: 'Remove',
        destructive: true,
      })
    )
      act.mutate({ action: 'remove', body: { name } })
  }
  return (
    <>
      <PageHeader
        title="Knowledge packs"
        description="Prebuilt indexes of documentation (SDKs, standards) that Argus searches next to your code. A pack added here is searched from the next question."
        actions={
          <>
            {p.data.index_url && (
              <Button variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: {} })}>
                <RefreshCw /> Update all
              </Button>
            )}
            <Button onClick={() => setAdding(true)} disabled={running}>
              <Plus /> Add a pack
            </Button>
          </>
        }
      />
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
        {p.data.error && <Alert variant="warning">{p.data.error}</Alert>}
        {running && <Alert title={`${p.data.job.action} of ${p.data.job.target ?? 'packs'} running`}>This page follows it; the log is below.</Alert>}
        {p.data.packs.length === 0 ? (
          <EmptyState icon={Package} title="No packs yet">
            Add one from the pack library: Argus then answers from its documentation next to your code.
          </EmptyState>
        ) : (
          <div className="stagger grid gap-4 xl:grid-cols-2">
            {p.data.packs.map((k) => {
              const copy = k.source !== 'library'
              return (
                <Card key={k.name} className={cn(k.compatible && 'border-success/40')}>
                  <CardHeader className="flex flex-row flex-wrap items-start gap-3">
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
                      <BookOpen className="size-4.5" aria-hidden="true" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <CardTitle className="flex flex-wrap items-center gap-2 [overflow-wrap:anywhere]">
                        {k.name} {k.version && <Badge variant="secondary">{k.version}</Badge>}
                        <Badge variant="outline">{copy ? 'From a URL' : 'Library'}</Badge>
                      </CardTitle>
                      <CardDescription className="[overflow-wrap:anywhere]">{contents(k)}</CardDescription>
                      {k.details?.source_repo && <p className="mt-1 truncate text-xs text-muted-foreground">{k.details.source_repo}</p>}
                    </div>
                    {k.compatible ? (
                      <span className="flex items-center gap-1.5 text-sm font-medium text-success-ink">
                        <CircleDot className="size-4" aria-hidden="true" /> Searched
                      </span>
                    ) : (
                      <Badge variant="destructive">Not searched</Badge>
                    )}
                  </CardHeader>
                  <CardContent className="grid gap-3">
                    {!k.compatible && k.incompatible_reason && <Alert variant="warning">{k.incompatible_reason}</Alert>}
                    <div className="flex flex-wrap gap-2">
                      {p.data.index_url && copy && (
                        <Button size="sm" variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: { name: k.name } })}>
                          <RefreshCw /> Update
                        </Button>
                      )}
                      <Button size="sm" variant="outline" className="text-destructive-ink" disabled={running} onClick={() => remove(k.name, copy)}>
                        {copy ? <Trash2 /> : <PowerOff />} Remove
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              )
            })}
          </div>
        )}
        {(running || p.data.job.finished) && (
          <Card>
            <CardHeader>
              <CardTitle>Last pack operation</CardTitle>
              <CardDescription>
                {running ? `${p.data.job.action} of ${p.data.job.target ?? 'packs'} running…` : `${p.data.job.action}: exit ${p.data.job.returncode}, ${agoSeconds(p.data.job.finished)}.`}
              </CardDescription>
            </CardHeader>
            {p.data.job.tail.length > 0 && (
              <CardContent>
                <CodeBlock code={p.data.job.tail.join('\n')} label="pack log" log />
              </CardContent>
            )}
          </Card>
        )}
      </div>
      <Dialog open={adding} onOpenChange={setAdding}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">{adding && <AddPack packs={p.data} onClose={() => setAdding(false)} />}</DialogContent>
      </Dialog>
    </>
  )
}

/** Pick a pack from the library, see what it holds, install it; or install one from a URL. */
function AddPack({ packs, onClose }: { packs: Packs; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [file, setFile] = useState('')
  const [source, setSource] = useState('')
  const [sha, setSha] = useState('')
  const [tab, setTab] = useState(packs.library_dir ? 'library' : 'url')
  const library = packs.library ?? []
  const available = library.filter((l) => !l.loaded)
  const chosen = library.find((l) => l.file === file)
  const replaces = chosen && packs.packs.some((k) => k.name === chosen.name)
  const shaBad = sha !== '' && !/^[0-9a-fA-F]{64}$/.test(sha)
  const install = useMutation({
    mutationFn: () =>
      tab === 'library' ? api('/api/admin/argus/packs/load', { body: { file } }) : api('/api/admin/argus/packs/install', { body: { source: source.trim(), sha256: sha || null } }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'packs'] })
      toast.success(tab === 'library' ? `${chosen?.name} installed` : 'Installing', {
        description: tab === 'library' ? 'Argus searches it from the next question.' : 'The page follows the download.',
      })
      onClose()
    },
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle>Add a pack</DialogTitle>
        <DialogDescription>A knowledge pack from the pack library, or from a URL. Only a pack built with Argus's embedding model can be searched.</DialogDescription>
      </DialogHeader>
      {install.error && <Alert variant="destructive">{errorMessage(install.error)}</Alert>}
      <Tabs value={tab} onValueChange={setTab} className="grid grid-cols-[minmax(0,1fr)]">
        <TabsList>
          <TabsTrigger value="library">From the library</TabsTrigger>
          <TabsTrigger value="url">From a URL</TabsTrigger>
        </TabsList>
        <TabsContent value="library" className="grid gap-4">
          {!packs.library_dir ? (
            <Alert>
              No pack library is set up: the platform mounts the repository's <code className="font-mono">packs/</code> folder, or <code className="font-mono">ARGUS_PACK_LIBRARY_DIR</code>.
            </Alert>
          ) : (
            <Field
              label="Pack"
              hint={
                library.length === 0
                  ? `The library is empty: build packs into it with tools/build-packs.sh (${packs.library_dir}).`
                  : available.length === 0
                    ? 'Every pack in the library is installed.'
                    : 'Packs in the library that Argus does not search yet.'
              }
            >
              <Select value={file} onValueChange={setFile}>
                <SelectTrigger className="min-w-0 [&>span]:truncate">
                  <SelectValue placeholder="Choose a pack" />
                </SelectTrigger>
                <SelectContent className="max-w-[calc(100vw-2rem)]">
                  <SelectGroup>
                    <SelectLabel>In the library</SelectLabel>
                    {available.map((l) => (
                      <SelectItem key={l.file} value={l.file} disabled={!l.compatible} className="[overflow-wrap:anywhere]">
                        {l.name} {l.version} · {formatValue(l.size_bytes, 'bytes')}
                        {!l.compatible ? ' · cannot be searched' : ''}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                </SelectContent>
              </Select>
            </Field>
          )}
          {chosen && <PackFacts pack={chosen} />}
          {replaces && <Alert>A pack named {chosen.name} is installed already: this one takes its place.</Alert>}
        </TabsContent>
        <TabsContent value="url" className="grid gap-4">
          <Field label="Pack URL or path" hint="A URL, or a path Argus can read. It is copied into Argus.">
            <Input value={source} onChange={(e) => setSource(e.target.value)} placeholder="https://…/win32.arguspack" />
          </Field>
          <Field label="SHA-256" hint="Recommended: a changed download is refused." error={shaBad ? '64 hexadecimal characters.' : undefined}>
            <Input value={sha} onChange={(e) => setSha(e.target.value.trim())} className="font-mono text-xs" />
          </Field>
        </TabsContent>
      </Tabs>
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button
          loading={install.isPending}
          disabled={tab === 'library' ? !chosen || !chosen.compatible : !source.trim() || shaBad}
          onClick={() => install.mutate()}
        >
          <Package /> Install
        </Button>
      </DialogFooter>
    </>
  )
}

/** What a pack holds and where it came from, from its own metadata. */
function PackFacts({ pack }: { pack: PackMeta & { file: string } }) {
  const d = pack.details
  const rows: [string, ReactNode][] = [
    ['File', <code key="file" className="font-mono text-xs [overflow-wrap:anywhere]">{pack.file}</code>],
    ['Contents', [count(d?.docs, 'pages'), count(d?.chunks, 'passages'), count(d?.symbols, 'API symbols')].filter(Boolean).join(' · ') || '—'],
    ['Size', formatValue(pack.size_bytes, 'bytes')],
    ['Embeddings', `${pack.model || 'unknown model'}, ${pack.dim} dimensions`],
    [
      'Source',
      d?.source_repo ? (
        <span key="source" className="[overflow-wrap:anywhere]">
          {d.source_repo}
          {d.source_branch ? ` (${d.source_branch})` : ''}
          {pack.commit ? ` at ${pack.commit.slice(0, 12)}` : ''}
        </span>
      ) : (
        '—'
      ),
    ],
    [
      'Licence',
      pack.license ? (
        d?.license_url ? (
          <a key="licence" href={d.license_url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 font-medium text-primary-ink underline-offset-2 hover:underline">
            {pack.license} <ExternalLink className="size-3.5" aria-hidden="true" />
          </a>
        ) : (
          pack.license
        )
      ) : (
        'none given'
      ),
    ],
  ]
  if (d?.attribution) rows.push(['Attribution', d.attribution])
  return (
    <section aria-label="About this pack" className="grid gap-3 rounded-lg border bg-muted/40 p-4 text-sm">
      <p className="flex flex-wrap items-center gap-2 font-medium">
        {pack.name} {pack.version && <Badge variant="secondary">{pack.version}</Badge>}
        {pack.compatible ? <Badge variant="success">Searchable</Badge> : <Badge variant="destructive">Cannot be searched</Badge>}
      </p>
      {!pack.compatible && pack.incompatible_reason && <Alert variant="warning">{pack.incompatible_reason}</Alert>}
      <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1.5">
        {rows.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-muted-foreground">{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}
