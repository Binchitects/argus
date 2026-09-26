import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleDot, FolderOpen, Package, Power, PowerOff, RefreshCw, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NotConfigured } from './argus'

interface PackMeta {
  name: string
  version: string
  model: string
  dim: string | number
  size_bytes: number
  license: string | null
  compatible: boolean
  incompatible_reason: string | null
}

interface Packs {
  configured?: boolean
  /** Loaded packs: from the library (a link) or installed from a URL (a copy). */
  packs: (PackMeta & { source?: 'library' | 'installed' })[]
  /** The pack library's files, and which are loaded. */
  library?: (PackMeta & { file: string; loaded: boolean })[]
  library_dir?: string | null
  job: { state: string; action: string | null; target: string | null; returncode: number | null; tail: string[]; finished: number | null }
  index_url: string | null
  error?: string
}

const describe = (k: PackMeta) => [k.model && `${k.model}, ${k.dim} dimensions`, formatValue(k.size_bytes, 'bytes'), k.license || 'no licence given'].filter(Boolean).join(' · ')

/**
 * Knowledge packs, like the models: the pack library (built packs on the host)
 * lists every pack with Load and Unload; loading links it into Argus, so it is
 * instant and nothing is copied. A pack can still be installed from a URL.
 */
export function PacksPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const p = useQuery({
    queryKey: ['admin', 'argus', 'packs'],
    queryFn: ({ signal }) => api<Packs>('/api/admin/argus/packs', { signal }),
    refetchInterval: (q) => (q.state.data?.job?.state === 'running' ? 3000 : false),
  })
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: object }) => api(`/api/admin/argus/packs/${action}`, { body }),
    onSuccess: (_, { action, body }) => {
      if (action === 'load') toast.success(`${(body as { name: string }).name} loaded`, { description: 'Argus searches it from the next question.' })
      if (action === 'remove') toast.success(`${(body as { name: string }).name} ${(body as { unload?: boolean }).unload ? 'unloaded' : 'removed'}`)
      void queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'packs'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const [source, setSource] = useState('')
  const [sha, setSha] = useState('')
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
  const shaBad = sha !== '' && !/^[0-9a-fA-F]{64}$/.test(sha)
  const library = p.data.library ?? []
  const libraryNames = new Set(library.filter((l) => l.loaded).map((l) => l.name))
  // Loaded but not from a library file listed: installed from a URL, or a library file that went away.
  const others = p.data.packs.filter((k) => !libraryNames.has(k.name))
  const unload = async (name: string, copy: boolean) => {
    if (
      await confirm({
        title: copy ? `Remove ${name}?` : `Unload ${name}?`,
        description: copy ? 'Argus stops searching it and its file is deleted: installed from a URL, it is not in the library.' : 'Argus stops searching it. It stays in the library, to load again.',
        confirm: copy ? 'Remove' : 'Unload',
        destructive: copy,
      })
    )
      act.mutate({ action: 'remove', body: { name, unload: !copy } })
  }
  return (
    <>
      <PageHeader
        title="Knowledge packs"
        description="Prebuilt indexes of documentation (SDKs, standards) that Argus searches next to your code. Load one from the pack library and it is searched from the next question."
        actions={
          p.data.index_url && (
            <Button variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: {} })}>
              <RefreshCw /> Update all
            </Button>
          )
        }
      />
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
        {p.data.error && <Alert variant="warning">{p.data.error}</Alert>}

        <section aria-label="Pack library" className="grid gap-3">
          <h2 className="flex items-center gap-2 text-base font-semibold">
            <FolderOpen className="size-4 text-muted-foreground" aria-hidden="true" /> Pack library
          </h2>
          {!p.data.library_dir ? (
            <Alert>
              No pack library is set up: set <code className="font-mono">ARGUS_PACK_LIBRARY</code> on the argus service (the platform mounts the repository's{' '}
              <code className="font-mono">packs/</code> folder, or <code className="font-mono">ARGUS_PACK_LIBRARY_DIR</code>).
            </Alert>
          ) : library.length === 0 ? (
            <EmptyState icon={Package} title="The pack library is empty">
              Build packs into it with <code className="font-mono">tools/build-packs.sh</code>, or copy <code className="font-mono">.arguspack</code> files into the
              host folder mounted as <code className="font-mono">{p.data.library_dir}</code>.
            </EmptyState>
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              {library.map((k) => (
                <Card key={k.file} className={cn(k.loaded && 'border-success/40')}>
                  <CardHeader className="flex flex-row flex-wrap items-start gap-3">
                    <div className="min-w-0 flex-1">
                      <CardTitle className="flex flex-wrap items-center gap-2 [overflow-wrap:anywhere]">
                        {k.name} {k.version && <Badge variant="secondary">{k.version}</Badge>}
                        {!k.compatible && <Badge variant="destructive">Cannot load</Badge>}
                      </CardTitle>
                      <CardDescription className="[overflow-wrap:anywhere]">{describe(k)}</CardDescription>
                      <p className="mt-1 font-mono text-xs text-muted-foreground [overflow-wrap:anywhere]">{k.file}</p>
                    </div>
                    {k.loaded ? (
                      <span className="flex items-center gap-1.5 text-sm font-medium text-success-ink">
                        <CircleDot className="size-4" aria-hidden="true" /> Loaded
                      </span>
                    ) : (
                      <span className="text-sm text-muted-foreground">Not loaded</span>
                    )}
                  </CardHeader>
                  <CardContent className="grid gap-3">
                    {!k.compatible && k.incompatible_reason && <Alert variant="warning">{k.incompatible_reason}</Alert>}
                    {!k.loaded && p.data.packs.some((i) => i.name === k.name) && (
                      <Alert>A pack named {k.name} is loaded from elsewhere: loading this one takes its place.</Alert>
                    )}
                    <div className="flex flex-wrap gap-2">
                      {k.loaded ? (
                        <Button size="sm" variant="outline" disabled={running || act.isPending} onClick={() => unload(k.name, false)}>
                          <PowerOff /> Unload
                        </Button>
                      ) : (
                        <Button size="sm" disabled={running || !k.compatible} loading={act.isPending && act.variables?.action === 'load' && (act.variables.body as { file?: string }).file === k.file} onClick={() => act.mutate({ action: 'load', body: { file: k.file, name: k.name } })}>
                          <Power /> Load
                        </Button>
                      )}
                    </div>
                  </CardContent>
                </Card>
              ))}
            </div>
          )}
        </section>

        {others.length > 0 && (
          <section aria-label="Loaded from elsewhere" className="grid gap-3">
            <h2 className="text-base font-semibold">Loaded from elsewhere</h2>
            <div className="grid gap-3 md:grid-cols-2">
              {others.map((k) => {
                const copy = k.source !== 'library'
                return (
                  <Card key={k.name}>
                    <CardHeader>
                      <CardTitle className="flex flex-wrap items-center gap-2">
                        {k.name} {k.version && <Badge variant="secondary">{k.version}</Badge>}
                        {!k.compatible && <Badge variant="destructive">Incompatible</Badge>}
                        <Badge variant="outline">{copy ? 'Installed from a URL' : 'Library file gone'}</Badge>
                      </CardTitle>
                      <CardDescription>{describe(k)}</CardDescription>
                    </CardHeader>
                    <CardContent className="grid gap-3">
                      {!k.compatible && k.incompatible_reason && <Alert variant="warning">{k.incompatible_reason}</Alert>}
                      <div className="flex gap-2">
                        {p.data.index_url && copy && (
                          <Button size="sm" variant="outline" disabled={running} onClick={() => act.mutate({ action: 'update', body: { name: k.name } })}>
                            <RefreshCw /> Update
                          </Button>
                        )}
                        <Button size="sm" variant="outline" className={cn(copy && 'text-destructive-ink')} disabled={running} onClick={() => unload(k.name, copy)}>
                          {copy ? <Trash2 /> : <PowerOff />} {copy ? 'Remove' : 'Unload'}
                        </Button>
                      </div>
                    </CardContent>
                  </Card>
                )
              })}
            </div>
          </section>
        )}

        <Card>
          <CardHeader>
            <CardTitle>Install from a URL</CardTitle>
            <CardDescription>For a pack not in the library: a URL or a path Argus can read, copied into Argus. Give the SHA-256 so a changed download is refused.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <form
              className="grid gap-4"
              onSubmit={(e) => {
                e.preventDefault()
                if (!shaBad) act.mutate({ action: 'install', body: { source, sha256: sha || null } })
              }}
            >
              <Field label="Pack URL or path">
                <Input value={source} onChange={(e) => setSource(e.target.value)} required />
              </Field>
              <Field label="SHA-256" hint="Recommended." error={shaBad ? '64 hexadecimal characters.' : undefined}>
                <Input value={sha} onChange={(e) => setSha(e.target.value.trim())} className="font-mono text-xs" />
              </Field>
              <div>
                <Button type="submit" disabled={running || !source} loading={act.isPending && act.variables?.action === 'install'}>
                  <Package /> Install
                </Button>
              </div>
            </form>
            <output className="block text-sm text-muted-foreground">
              {running ? `${p.data.job.action} of ${p.data.job.target ?? 'packs'} running…` : p.data.job.finished ? `Last ${p.data.job.action}: exit ${p.data.job.returncode}, ${agoSeconds(p.data.job.finished)}.` : ''}
            </output>
            {p.data.job.tail.length > 0 && <CodeBlock code={p.data.job.tail.join('\n')} label="pack log" log />}
          </CardContent>
        </Card>
      </div>
    </>
  )
}
