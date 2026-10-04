import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, FileText, FolderOpen, Globe, GitBranch, Library, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { useId, useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, count } from '@/lib/format'
import { AccessPicker, type Audience } from './access-picker'
import { groupsQuery } from './groups-api'

type Kind = 'gitlab' | 'folder' | 'website'

export interface KnowledgeSource {
  id: string
  name: string
  kind: Kind
  location: string
  wiki: boolean
  issues: boolean
  hosts: string | null
  maxPages: number
  audience: Audience
  groups: { id: string; name: string }[]
  state: 'new' | 'syncing' | 'synced' | 'failed'
  error: string | null
  syncStartedAt: string | null
  syncedAt: string | null
  documents: number
  passages: number
  /** A GitLab source's projects, with how many of their members may read them. */
  projects: { name: string | null; people: number; fetchedAt: string }[] | null
}

interface Knowledge {
  embedder: boolean
  problem: string | null
  gitlabBot: boolean
  folderRoot: string
  syncEvery: string
  sources: KnowledgeSource[]
}

interface KnowledgeDocument {
  id: string
  title: string
  url: string | null
  key: string
  syncedAt: string
  passages: number
}

const kinds: Record<Kind, { label: string; icon: typeof Globe }> = {
  gitlab: { label: 'GitLab', icon: GitBranch },
  folder: { label: 'Folder', icon: FolderOpen },
  website: { label: 'Website', icon: Globe },
}

const knowledgeQuery = {
  queryKey: ['admin', 'knowledge'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Knowledge>('/api/admin/knowledge', { signal }),
}

export function KnowledgePage() {
  const knowledge = useQuery({
    ...knowledgeQuery,
    // While a source syncs, its state and counts move.
    refetchInterval: (q) => (q.state.data?.sources.some((s) => s.state === 'syncing' || s.state === 'new') ? 2000 : false),
  })
  const [adding, setAdding] = useState(false)
  const [reading, setReading] = useState<KnowledgeSource | null>(null)
  if (knowledge.isPending) return <PageSkeleton />
  if (knowledge.error) return <QueryError error={knowledge.error} retry={() => knowledge.refetch()} />
  const k = knowledge.data
  return (
    <>
      <PageHeader
        title="Knowledge"
        description="The company's documents the chat searches (Company knowledge, a tool in Admin → Tools), each person only what they may read: GitLab wikis and issues, folders and websites. Confluence and SharePoint come next."
        actions={
          <Button onClick={() => setAdding(true)}>
            <Plus /> Add a source
          </Button>
        }
      />
      <div className="grid gap-4">
        {k.problem && <Alert variant="warning">{k.problem}</Alert>}
        {!k.gitlabBot && k.sources.some((s) => s.kind === 'gitlab') && (
          <Alert variant="warning">
            GitLab sources are read with the GitLab bot: set its address and token under <Link to="/admin/settings" className="underline underline-offset-2">Settings → Scheduled tasks</Link>.
          </Alert>
        )}
        {k.sources.length === 0 ? (
          <EmptyState icon={Library} title="No sources yet" action={<Button onClick={() => setAdding(true)}><Plus /> Add a source</Button>}>
            Add a GitLab project or group (its wiki and issues, read by its members), a folder mounted under {k.folderRoot}, or a website. Each is read again every {every(k.syncEvery)}.
          </EmptyState>
        ) : (
          <div className="stagger grid gap-4 xl:grid-cols-2">
            {k.sources.map((s) => (
              <SourceCard key={s.id} source={s} onDocuments={() => setReading(s)} />
            ))}
          </div>
        )}
      </div>
      <AddDialog open={adding} folderRoot={k.folderRoot} onClose={() => setAdding(false)} />
      <DocumentsDialog source={reading} onClose={() => setReading(null)} />
    </>
  )
}

/** "01:00:00" -> "hour", "00:30:00" -> "30 minutes". */
function every(span: string): string {
  const [h, m] = span.split(':').map(Number)
  const minutes = (h || 0) * 60 + (m || 0)
  if (minutes === 60) return 'hour'
  return minutes % 60 === 0 ? `${minutes / 60} hours` : `${minutes} minutes`
}

function StateBadge({ source }: { source: KnowledgeSource }) {
  switch (source.state) {
    case 'syncing':
      return <Badge variant="default">Syncing…</Badge>
    case 'failed':
      return <Badge variant="destructive">Failed</Badge>
    case 'synced':
      return <Badge variant={source.error ? 'warning' : 'success'}>{source.error ? 'Synced, with problems' : 'Synced'}</Badge>
    default:
      return <Badge variant="secondary">Waiting to sync</Badge>
  }
}

function SourceCard({ source, onDocuments }: { source: KnowledgeSource; onDocuments: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin', 'knowledge'] })
  const sync = useMutation({
    mutationFn: () => api(`/api/admin/knowledge/${source.id}/sync`, { body: {} }),
    onSuccess: () => {
      toast.success(`${source.name} is syncing`)
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const readers = useMutation({
    mutationFn: (r: { audience: Audience; groups: string[] }) => api(`/api/admin/knowledge/${source.id}`, { method: 'PATCH', body: r }),
    onSettled: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/knowledge/${source.id}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const Icon = kinds[source.kind].icon
  return (
    <Card aria-label={source.name}>
      <CardHeader className="flex flex-row items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
          <Icon className="size-4.5" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <CardTitle className="flex flex-wrap items-center gap-2">
            {source.name} <Badge variant="outline">{kinds[source.kind].label}</Badge> <StateBadge source={source} />
          </CardTitle>
          <CardDescription className="font-mono text-xs break-all">
            {source.location}
            {source.kind === 'gitlab' && ` · ${[source.wiki && 'wiki', source.issues && 'issues'].filter(Boolean).join(' and ')}`}
            {source.kind === 'website' && ` · ${source.hosts ?? 'its own host'}, up to ${count(source.maxPages)} pages`}
          </CardDescription>
        </div>
      </CardHeader>
      <CardContent className="grid gap-4">
        {source.error && <Alert variant={source.state === 'failed' ? 'destructive' : 'warning'}>{source.error}</Alert>}
        <p className="text-sm text-muted-foreground">
          <span className="text-foreground">{count(source.documents)}</span> documents, <span className="text-foreground">{count(source.passages)}</span> passages
          {source.state === 'syncing' && source.syncStartedAt ? ` · started ${ago(source.syncStartedAt)}` : source.syncedAt ? ` · last synced ${ago(source.syncedAt)}` : ''}
        </p>
        {source.kind === 'gitlab' ? (
          <div className="grid gap-1 text-sm">
            <p className="text-muted-foreground">Who may read it: the members of each project in GitLab (Guest and up), by their username.</p>
            {(source.projects ?? []).length > 0 && (
              <ul className="grid gap-0.5 text-xs" aria-label="Projects">
                {source.projects!.map((p) => (
                  <li key={p.name} className="flex items-center gap-2">
                    <span className="font-mono">{p.name}</span>
                    <span className="text-muted-foreground">
                      {p.people} {p.people === 1 ? 'person' : 'people'}, read {ago(p.fetchedAt)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        ) : (
          <AccessPicker label="Who may read it" value={{ audience: source.audience, groups: source.groups }} onChange={(audience, groups) => readers.mutate({ audience, groups })} />
        )}
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" loading={sync.isPending} disabled={source.state === 'syncing'} onClick={() => sync.mutate()}>
            <RefreshCw /> Sync now
          </Button>
          <Button variant="outline" size="sm" onClick={onDocuments} disabled={source.documents === 0}>
            <FileText /> Documents
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="text-destructive-ink"
            onClick={async () => {
              if (await confirm({ title: `Remove ${source.name}?`, description: 'Its documents and passages are deleted, and the chat no longer finds them. The source itself is untouched.', confirm: 'Remove', destructive: true }))
                remove.mutate()
            }}
          >
            <Trash2 /> Remove
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

interface Draft {
  kind: Kind
  name: string
  location: string
  wiki: boolean
  issues: boolean
  hosts: string
  maxPages: string
  audience: Audience
  groups: string[]
}

const empty: Draft = { kind: 'gitlab', name: '', location: '', wiki: true, issues: true, hosts: '', maxPages: '200', audience: 'Everyone', groups: [] }

function AddDialog({ open, folderRoot, onClose }: { open: boolean; folderRoot: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const groups = useQuery({ ...groupsQuery, enabled: open })
  const id = useId()
  const [draft, setDraft] = useState<Draft>(empty)
  const [error, setError] = useState<string | null>(null)
  const set = (change: Partial<Draft>) => setDraft({ ...draft, ...change })
  const close = () => {
    setDraft(empty)
    setError(null)
    onClose()
  }
  const add = useMutation({
    mutationFn: () =>
      api('/api/admin/knowledge', {
        body: {
          name: draft.name,
          kind: draft.kind,
          location: draft.location,
          ...(draft.kind === 'gitlab' ? { wiki: draft.wiki, issues: draft.issues } : { audience: draft.audience, groups: draft.groups }),
          ...(draft.kind === 'website' ? { hosts: draft.hosts, maxPages: Number(draft.maxPages) } : {}),
        },
      }),
    onSuccess: () => {
      toast.success(`${draft.name} added`, { description: 'It is being read now: its documents show here as they are embedded.' })
      queryClient.invalidateQueries({ queryKey: ['admin', 'knowledge'] })
      close()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const names = (groups.data ?? []).filter((g) => draft.groups.includes(g.id)).map((g) => ({ id: g.id, name: g.name }))
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent className="sm:max-w-lg">
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            add.mutate()
          }}
        >
          <DialogHeader>
            <DialogTitle>Add a knowledge source</DialogTitle>
            <DialogDescription>It is read now, then again on schedule; only what changed is embedded again.</DialogDescription>
          </DialogHeader>
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="Kind">
            <Select value={draft.kind} onValueChange={(kind: Kind) => set({ kind, location: '' })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="gitlab">GitLab project or group</SelectItem>
                <SelectItem value="folder">Folder</SelectItem>
                <SelectItem value="website">Website</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <Field label="Name">
            <Input required maxLength={100} value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder={draft.kind === 'gitlab' ? 'Engineering wiki' : draft.kind === 'folder' ? 'Handbook' : 'Product docs'} />
          </Field>
          {draft.kind === 'gitlab' && (
            <>
              <Field label="Project or group" hint="Its path in GitLab. A group takes every project in it and its subgroups. Each project's members read its documents.">
                <Input required className="font-mono" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="group/project" />
              </Field>
              <div className="flex flex-wrap gap-4 text-sm">
                <span className="flex items-center gap-2">
                  <Checkbox id={`${id}-wiki`} checked={draft.wiki} onCheckedChange={(v) => set({ wiki: v === true })} />
                  <label htmlFor={`${id}-wiki`}>Wiki pages</label>
                </span>
                <span className="flex items-center gap-2">
                  <Checkbox id={`${id}-issues`} checked={draft.issues} onCheckedChange={(v) => set({ issues: v === true })} />
                  <label htmlFor={`${id}-issues`}>Issues and their comments (not confidential ones)</label>
                </span>
              </div>
            </>
          )}
          {draft.kind === 'folder' && (
            <Field label="Folder" hint={`Mount it under ${folderRoot}, read-only, in docker-compose.override.yml. Text, Markdown, HTML, PDF and Office files are read.`}>
              <Input required className="font-mono" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder={`${folderRoot}/handbook`} />
            </Field>
          )}
          {draft.kind === 'website' && (
            <>
              <Field label="First page" hint="Its pages are read through their links, on public addresses only, minding robots.txt.">
                <Input required type="url" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://docs.example.com/" />
              </Field>
              <div className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_8rem]">
                <Field label="Hosts (optional)" hint="Where its pages may be, comma separated; *.example.com for a domain. Empty: the first page's host.">
                  <Input value={draft.hosts} onChange={(e) => set({ hosts: e.target.value })} placeholder="docs.example.com" />
                </Field>
                <Field label="Most pages">
                  <Input required type="number" min={1} max={2000} value={draft.maxPages} onChange={(e) => set({ maxPages: e.target.value })} />
                </Field>
              </div>
            </>
          )}
          {draft.kind !== 'gitlab' && (
            <AccessPicker label="Who may read it" value={{ audience: draft.audience, groups: names }} onChange={(audience, chosen) => set({ audience, groups: chosen })} />
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={add.isPending}>
              Add
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function DocumentsDialog({ source, onClose }: { source: KnowledgeSource | null; onClose: () => void }) {
  const documents = useQuery({
    queryKey: ['admin', 'knowledge', source?.id, 'documents'],
    queryFn: ({ signal }) => api<KnowledgeDocument[]>(`/api/admin/knowledge/${source!.id}/documents`, { signal }),
    enabled: source !== null,
  })
  return (
    <Dialog open={source !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{source?.name}: documents</DialogTitle>
          <DialogDescription>{source && source.documents > 500 ? `The first 500 of ${count(source.documents)}, by title.` : 'By title, with their passages.'}</DialogDescription>
        </DialogHeader>
        {documents.error && <Alert variant="destructive">{errorMessage(documents.error)}</Alert>}
        <ul className="grid max-h-[60vh] gap-1 overflow-y-auto text-sm" aria-label="Documents">
          {(documents.data ?? []).map((d) => (
            <li key={d.id} className="flex min-w-0 items-center gap-2 rounded-md px-2 py-1 hover:bg-accent">
              {d.url ? (
                <a href={d.url} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate underline-offset-2 hover:underline">
                  {d.title} <ExternalLink className="inline size-3" aria-hidden="true" />
                </a>
              ) : (
                <span className="min-w-0 flex-1 truncate font-mono text-xs">{d.title}</span>
              )}
              <span className="shrink-0 text-xs text-muted-foreground">
                {d.passages} {d.passages === 1 ? 'passage' : 'passages'}
              </span>
            </li>
          ))}
        </ul>
      </DialogContent>
    </Dialog>
  )
}
