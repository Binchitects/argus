import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, BookOpen, Cloud, ExternalLink, Files, FileText, FolderOpen, FolderUp, GitBranch, GitFork, Globe, HardDrive, Library, NotebookText, Pencil, PlugZap, Plus, RefreshCw, Rss, Ticket, Trash2, Upload } from 'lucide-react'
import { useId, useRef, useState, type DragEvent } from 'react'
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
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, count, formatValue } from '@/lib/format'
import { AccessPicker, type Audience } from './access-picker'
import { groupsQuery } from './groups-api'

type Kind = 'gitlab' | 'folder' | 'website' | 'confluence' | 'sharepoint' | 'upload' | 'github' | 'jira' | 'notion' | 'feed' | 'webdav' | 's3'

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
  /** Confluence: the space keys read; Jira: the project keys; null: all the account may read. */
  spaces: string | null
  blogPosts: boolean
  sitePages: boolean
  /** Confluence and Jira Cloud: the account's email; SharePoint: the app's client ID; GitHub: the API's address; WebDAV: the user; S3: the access key ID. */
  account: string | null
  tenant: string | null
  /** A token or client secret is saved (it is never sent back). */
  secretSet: boolean
  /** Confluence and SharePoint: what the last sync could mirror of who may read it, a line each. */
  mirror: string | null
  state: 'new' | 'syncing' | 'synced' | 'failed'
  error: string | null
  syncStartedAt: string | null
  syncedAt: string | null
  documents: number
  passages: number
  /** A GitLab source's projects, with how many of their members may read them. */
  projects: { name: string | null; people: number; fetchedAt: string }[] | null
  /** A Confluence source's spaces, with how many people may view each. */
  viewers: { name: string | null; people: number; fetchedAt: string }[] | null
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
  confluence: { label: 'Confluence', icon: BookOpen },
  sharepoint: { label: 'SharePoint', icon: Cloud },
  upload: { label: 'Uploaded files', icon: Files },
  github: { label: 'GitHub or Gitea', icon: GitFork },
  jira: { label: 'Jira', icon: Ticket },
  notion: { label: 'Notion', icon: NotebookText },
  feed: { label: 'Feed', icon: Rss },
  webdav: { label: 'WebDAV', icon: HardDrive },
  s3: { label: 'S3 bucket', icon: Archive },
}

/** Confluence and SharePoint: who may read is their own permissions; the admin chooses only for what those cannot tell. */
const mirrors = (kind: Kind) => kind === 'confluence' || kind === 'sharepoint'

/** The kinds whose connection the form can test before saving. */
const testable = (kind: Kind) => mirrors(kind) || ['github', 'jira', 'notion', 'feed', 'webdav', 's3'].includes(kind)

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
  const [editing, setEditing] = useState<KnowledgeSource | null>(null)
  const [reading, setReading] = useState<KnowledgeSource | null>(null)
  const [filing, setFiling] = useState<KnowledgeSource | null>(null)
  if (knowledge.isPending) return <PageSkeleton />
  if (knowledge.error) return <QueryError error={knowledge.error} retry={() => knowledge.refetch()} />
  const k = knowledge.data
  return (
    <>
      <PageHeader
        title="Knowledge"
        description="The company's documents the chat searches (Company knowledge, a tool in Admin → Tools), each person only what they may read: GitLab, GitHub and Gitea, Confluence, Jira, Notion, SharePoint and OneDrive, WebDAV, S3 buckets, folders, websites, feeds, and files uploaded here (scans and pictures read by OCR)."
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
            Add a GitLab project or group (its wiki and issues, read by its members), Confluence spaces or SharePoint sites (read by whom they let read), GitHub or Gitea repositories, Jira projects, Notion pages, a WebDAV folder, an S3 bucket, a folder mounted under {k.folderRoot}, a website or feeds, or upload files. Each is read again every {every(k.syncEvery)}.
          </EmptyState>
        ) : (
          <div className="stagger grid gap-4 xl:grid-cols-2">
            {k.sources.map((s) => (
              <SourceCard key={s.id} source={s} onDocuments={() => setReading(s)} onEdit={() => setEditing(s)} onFiles={() => setFiling(s)} />
            ))}
          </div>
        )}
      </div>
      <SourceDialog open={adding} folderRoot={k.folderRoot} onClose={() => setAdding(false)} />
      <SourceDialog key={editing?.id ?? 'none'} open={editing !== null} source={editing ?? undefined} folderRoot={k.folderRoot} onClose={() => setEditing(null)} />
      <DocumentsDialog source={reading} onClose={() => setReading(null)} />
      <FilesDialog source={filing} onClose={() => setFiling(null)} />
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

function SourceCard({ source, onDocuments, onEdit, onFiles }: { source: KnowledgeSource; onDocuments: () => void; onEdit: () => void; onFiles: () => void }) {
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
          <CardDescription className="font-mono text-xs break-all whitespace-pre-line">
            {source.kind === 'notion' ? 'Pages shared with the integration' : source.location}
            {source.kind === 'github' && ` · documentation${source.issues ? ' and issues' : ''}${source.account ? ` · ${source.account}` : ''}`}
            {source.kind === 'jira' && ` · ${source.spaces ? `projects ${source.spaces}` : 'every project it may browse'} · ${source.account ? 'Cloud' : 'Data Center'}`}
            {source.kind === 's3' && ` · region ${source.tenant ?? 'us-east-1'}`}
            {source.kind === 'gitlab' && ` · ${[source.wiki && 'wiki', source.issues && 'issues'].filter(Boolean).join(' and ')}`}
            {source.kind === 'website' && ` · ${source.hosts ?? 'its own host'}, up to ${count(source.maxPages)} pages`}
            {source.kind === 'confluence' && ` · ${source.spaces ? `spaces ${source.spaces}` : 'every space it may read'}${source.blogPosts ? ', blog posts too' : ''} · ${source.account ? 'Cloud' : 'Data Center'}`}
            {source.kind === 'sharepoint' && `${source.sitePages ? '\nsite pages too' : ''} · tenant ${source.tenant ?? ''}`}
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
        ) : mirrors(source.kind) ? (
          <div className="grid gap-2 text-sm">
            <p className="text-muted-foreground">
              Who may read it: {source.kind === 'confluence' ? 'whoever may view each space and page in Confluence, by their email (Cloud) or username (Data Center).' : "each file's permissions in SharePoint, people by their email and Microsoft Entra groups by name."}
            </p>
            {(source.viewers ?? []).length > 0 && (
              <ul className="grid gap-0.5 text-xs" aria-label="Spaces">
                {source.viewers!.map((v) => (
                  <li key={v.name} className="flex items-center gap-2">
                    <span className="font-mono">{v.name}</span>
                    <span className="text-muted-foreground">
                      {v.people} {v.people === 1 ? 'person' : 'people'}, read {ago(v.fetchedAt)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
            {source.mirror && (
              <ul className="grid gap-0.5 text-xs text-muted-foreground" aria-label="What is mirrored">
                {source.mirror.split('\n').map((line) => (
                  <li key={line}>{line}</li>
                ))}
              </ul>
            )}
            <AccessPicker
              label={`Where ${kinds[source.kind].label} cannot tell`}
              value={{ audience: source.audience, groups: source.groups }}
              onChange={(audience, groups) => readers.mutate({ audience, groups })}
            />
          </div>
        ) : (
          <AccessPicker label="Who may read it" value={{ audience: source.audience, groups: source.groups }} onChange={(audience, groups) => readers.mutate({ audience, groups })} />
        )}
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" loading={sync.isPending} disabled={source.state === 'syncing'} onClick={() => sync.mutate()}>
            <RefreshCw /> Sync now
          </Button>
          {source.kind === 'upload' && (
            <Button variant="outline" size="sm" onClick={onFiles}>
              <Upload /> Files
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={onDocuments} disabled={source.documents === 0}>
            <FileText /> Documents
          </Button>
          <Button variant="outline" size="sm" onClick={onEdit}>
            <Pencil /> Settings
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
  spaces: string
  blogPosts: boolean
  sitePages: boolean
  account: string
  tenant: string
  secret: string
  audience: Audience
  groups: string[]
}

const empty: Draft = {
  kind: 'gitlab', name: '', location: '', wiki: true, issues: true, hosts: '', maxPages: '200', spaces: '', blogPosts: false, sitePages: true, account: '', tenant: '', secret: '',
  audience: 'Everyone', groups: [],
}

const draftOf = (s: KnowledgeSource): Draft => ({
  kind: s.kind, name: s.name, location: s.location, wiki: s.wiki, issues: s.issues, hosts: s.hosts ?? '', maxPages: String(s.maxPages), spaces: s.spaces ?? '',
  blogPosts: s.blogPosts, sitePages: s.sitePages, account: s.account ?? '', tenant: s.tenant ?? '', secret: '', audience: s.audience, groups: s.groups.map((g) => g.id),
})

/** What the form sends for its kind; a secret only when one was typed (empty keeps the saved one). */
function bodyOf(d: Draft): Record<string, unknown> {
  const readers = d.kind === 'gitlab' ? {} : { audience: d.audience, groups: d.groups }
  const secret = d.secret.trim() ? { secret: d.secret.trim() } : {}
  switch (d.kind) {
    case 'gitlab':
      return { name: d.name, kind: d.kind, location: d.location, wiki: d.wiki, issues: d.issues }
    case 'website':
      return { name: d.name, kind: d.kind, location: d.location, ...readers, hosts: d.hosts, maxPages: Number(d.maxPages) }
    case 'confluence':
      return { name: d.name, kind: d.kind, location: d.location, account: d.account, spaces: d.spaces, blogPosts: d.blogPosts, ...secret, ...readers }
    case 'sharepoint':
      return { name: d.name, kind: d.kind, location: d.location, tenant: d.tenant, account: d.account, sitePages: d.sitePages, ...secret, ...readers }
    case 'github':
      return { name: d.name, kind: d.kind, location: d.location, account: d.account, issues: d.issues, ...secret, ...readers }
    case 'jira':
      return { name: d.name, kind: d.kind, location: d.location, account: d.account, spaces: d.spaces, ...secret, ...readers }
    case 'notion':
      return { name: d.name, kind: d.kind, ...secret, ...readers }
    case 'webdav':
      return { name: d.name, kind: d.kind, location: d.location, account: d.account, ...secret, ...readers }
    case 's3':
      return { name: d.name, kind: d.kind, location: d.location, account: d.account, tenant: d.tenant, ...secret, ...readers }
    default:
      return { name: d.name, kind: d.kind, location: d.location, ...readers }
  }
}

/** Add a source, or change one (its kind stays). */
function SourceDialog({ open, source, folderRoot, onClose }: { open: boolean; source?: KnowledgeSource; folderRoot: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const groups = useQuery({ ...groupsQuery, enabled: open })
  const id = useId()
  const start = source ? draftOf(source) : empty
  const [draft, setDraft] = useState<Draft>(start)
  const [error, setError] = useState<string | null>(null)
  const [tested, setTested] = useState<{ ok: boolean; message: string } | null>(null)
  const set = (change: Partial<Draft>) => {
    setDraft({ ...draft, ...change })
    setTested(null)
  }
  const close = () => {
    setDraft(start)
    setError(null)
    setTested(null)
    onClose()
  }
  const save = useMutation({
    mutationFn: () => (source ? api(`/api/admin/knowledge/${source.id}`, { method: 'PATCH', body: { ...bodyOf(draft), kind: undefined } }) : api('/api/admin/knowledge', { body: bodyOf(draft) })),
    onSuccess: () => {
      if (source) toast.success(`${draft.name} saved`)
      else toast.success(`${draft.name} added`, { description: 'It is being read now: its documents show here as they are embedded.' })
      queryClient.invalidateQueries({ queryKey: ['admin', 'knowledge'] })
      close()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const test = useMutation({
    mutationFn: () => api<{ message: string }>(source ? `/api/admin/knowledge/${source.id}/test` : '/api/admin/knowledge/test', { body: bodyOf(draft) }),
    onSuccess: (r) => setTested({ ok: true, message: r.message }),
    onError: (e) => setTested({ ok: false, message: errorMessage(e) }),
  })
  const names = (groups.data ?? []).filter((g) => draft.groups.includes(g.id)).map((g) => ({ id: g.id, name: g.name }))
  const saved = source?.secretSet ? 'Saved: leave empty to keep it' : undefined
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent className="sm:max-w-lg">
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          <DialogHeader>
            <DialogTitle>{source ? `${source.name}: settings` : 'Add a knowledge source'}</DialogTitle>
            <DialogDescription>{source ? 'A change to what it reads syncs it again now.' : 'It is read now, then again on schedule; only what changed is embedded again.'}</DialogDescription>
          </DialogHeader>
          {error && <Alert variant="destructive">{error}</Alert>}
          {!source && (
            <Field label="Kind">
              <Select value={draft.kind} onValueChange={(kind: Kind) => set({ kind, location: '', audience: mirrors(kind) ? 'Admins' : 'Everyone', groups: [] })}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="gitlab">GitLab project or group</SelectItem>
                  <SelectItem value="confluence">Confluence</SelectItem>
                  <SelectItem value="sharepoint">SharePoint or OneDrive</SelectItem>
                  <SelectItem value="github">GitHub or Gitea</SelectItem>
                  <SelectItem value="jira">Jira</SelectItem>
                  <SelectItem value="notion">Notion</SelectItem>
                  <SelectItem value="webdav">WebDAV (Nextcloud, ownCloud, a NAS)</SelectItem>
                  <SelectItem value="s3">S3 bucket (Amazon S3, MinIO, Ceph)</SelectItem>
                  <SelectItem value="folder">Folder</SelectItem>
                  <SelectItem value="website">Website</SelectItem>
                  <SelectItem value="feed">RSS or Atom feeds</SelectItem>
                  <SelectItem value="upload">Files uploaded here</SelectItem>
                </SelectContent>
              </Select>
            </Field>
          )}
          <Field label="Name">
            <Input
              required
              maxLength={100}
              value={draft.name}
              onChange={(e) => set({ name: e.target.value })}
              placeholder={{ gitlab: 'Engineering wiki', folder: 'Handbook', website: 'Product docs', confluence: 'Company wiki', sharepoint: 'Engineering site', upload: 'Company files', github: 'Open-source docs', jira: 'Support tickets', notion: 'Team notes', feed: 'Engineering blog', webdav: 'Shared drive', s3: 'Archived reports' }[draft.kind]}
            />
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
          {draft.kind === 'confluence' && (
            <>
              <Field label="Site address" hint="Cloud: https://your-site.atlassian.net. Data Center or Server: its address, with its path if it has one.">
                <Input required type="url" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://your-site.atlassian.net" />
              </Field>
              <Field label="Account's email (Cloud)" hint="The email of the account that reads, with its API token. Empty for Data Center, which takes a personal access token.">
                <Input type="email" value={draft.account} onChange={(e) => set({ account: e.target.value })} placeholder="knowledge-bot@example.com" />
              </Field>
              <Field label="API token or personal access token" hint="An account that only reads is enough. Stored encrypted; never shown again.">
                <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
              </Field>
              <Field label="Spaces (optional)" hint="Their keys, comma separated. Empty: every space the account may read.">
                <Input className="font-mono" value={draft.spaces} onChange={(e) => set({ spaces: e.target.value })} placeholder="ENG, HR" />
              </Field>
              <span className="flex items-center gap-2 text-sm">
                <Checkbox id={`${id}-blog`} checked={draft.blogPosts} onCheckedChange={(v) => set({ blogPosts: v === true })} />
                <label htmlFor={`${id}-blog`}>Blog posts too</label>
              </span>
            </>
          )}
          {draft.kind === 'sharepoint' && (
            <>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Tenant ID" hint="Microsoft Entra ID → Overview, or the tenant's domain.">
                  <Input required className="font-mono" value={draft.tenant} onChange={(e) => set({ tenant: e.target.value })} placeholder="contoso.onmicrosoft.com" />
                </Field>
                <Field label="Client ID" hint="The app's Application (client) ID.">
                  <Input required className="font-mono" value={draft.account} onChange={(e) => set({ account: e.target.value })} />
                </Field>
              </div>
              <Field label="Client secret" hint="With Sites.Read.All (or Sites.Selected) as an application permission. Stored encrypted; never shown again.">
                <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
              </Field>
              <Field label="Sites or libraries" hint="Their addresses, one a line: a site reads all its libraries; a library's address reads that library only.">
                <Textarea required rows={3} className="font-mono text-xs" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://contoso.sharepoint.com/sites/engineering" />
              </Field>
              <span className="flex items-center gap-2 text-sm">
                <Checkbox id={`${id}-pages`} checked={draft.sitePages} onCheckedChange={(v) => set({ sitePages: v === true })} />
                <label htmlFor={`${id}-pages`}>The sites' pages too</label>
              </span>
            </>
          )}
          {draft.kind === 'github' && (
            <>
              <Field label="Repositories" hint="owner/repo, one a line; owner alone takes every repository of an organisation or a person. Their documentation is read (Markdown, text, and docs/), not their code.">
                <Textarea required rows={3} className="font-mono text-xs" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="acme/handbook" />
              </Field>
              <Field label="API address (optional)" hint="Empty for GitHub. Gitea or Forgejo: https://gitea.example.com/api/v1. GitHub Enterprise: https://github.example.com/api/v3.">
                <Input type="url" className="font-mono" value={draft.account} onChange={(e) => set({ account: e.target.value })} placeholder="https://api.github.com" />
              </Field>
              <Field label="Token (optional for public repositories)" hint="Read only: a fine-grained token with Contents and Issues read. Stored encrypted; never shown again.">
                <Input type="password" autoComplete="off" value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
              </Field>
              <span className="flex items-center gap-2 text-sm">
                <Checkbox id={`${id}-gh-issues`} checked={draft.issues} onCheckedChange={(v) => set({ issues: v === true })} />
                <label htmlFor={`${id}-gh-issues`}>Issues and their comments (not pull requests)</label>
              </span>
            </>
          )}
          {draft.kind === 'jira' && (
            <>
              <Field label="Site address" hint="Cloud: https://your-site.atlassian.net. Data Center: its address.">
                <Input required type="url" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://your-site.atlassian.net" />
              </Field>
              <Field label="Account's email (Cloud)" hint="With its API token. Empty for Data Center, which takes a personal access token.">
                <Input type="email" value={draft.account} onChange={(e) => set({ account: e.target.value })} placeholder="knowledge-bot@example.com" />
              </Field>
              <Field label="API token or personal access token" hint="An account that may only browse is enough. Stored encrypted; never shown again.">
                <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
              </Field>
              <Field label="Projects (optional)" hint="Their keys, comma separated. Empty: every project the account may browse.">
                <Input className="font-mono" value={draft.spaces} onChange={(e) => set({ spaces: e.target.value })} placeholder="OPS, SUP" />
              </Field>
            </>
          )}
          {draft.kind === 'notion' && (
            <Field label="Integration secret" hint="An internal integration (notion.so/profile/integrations) with Read content; then share pages with it (… → Connections). Stored encrypted; never shown again.">
              <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
            </Field>
          )}
          {draft.kind === 'feed' && (
            <Field label="Feeds" hint="Their addresses, one a line. Each item is a document.">
              <Textarea required rows={3} className="font-mono text-xs" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://blog.example.com/feed.xml" />
            </Field>
          )}
          {draft.kind === 'webdav' && (
            <>
              <Field label="Folder address" hint="Nextcloud: https://cloud.example.com/remote.php/dav/files/USER/Folder. Its files and subfolders are read as uploaded files are.">
                <Input required type="url" className="font-mono" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://cloud.example.com/remote.php/dav/files/bot/Handbook" />
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="User">
                  <Input required value={draft.account} onChange={(e) => set({ account: e.target.value })} />
                </Field>
                <Field label="Password or app password" hint="Stored encrypted; never shown again.">
                  <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
                </Field>
              </div>
            </>
          )}
          {draft.kind === 's3' && (
            <>
              <Field label="Endpoint, bucket and prefix" hint="Path-style. Amazon: https://s3.REGION.amazonaws.com/bucket/prefix/. MinIO: https://minio.example.com:9000/bucket/.">
                <Input required type="url" className="font-mono" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder="https://s3.eu-west-1.amazonaws.com/docs-bucket/handbook/" />
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Access key ID">
                  <Input required className="font-mono" value={draft.account} onChange={(e) => set({ account: e.target.value })} />
                </Field>
                <Field label="Region (optional)" hint="Empty: us-east-1.">
                  <Input className="font-mono" value={draft.tenant} onChange={(e) => set({ tenant: e.target.value })} placeholder="eu-west-1" />
                </Field>
              </div>
              <Field label="Secret access key" hint="A key that may only list and read the bucket (s3:ListBucket, s3:GetObject). Stored encrypted; never shown again.">
                <Input type="password" autoComplete="off" required={!source?.secretSet} value={draft.secret} onChange={(e) => set({ secret: e.target.value })} placeholder={saved} />
              </Field>
            </>
          )}
          {draft.kind === 'folder' && (
            <Field label="Folder" hint={`Mount it under ${folderRoot}, read-only, in docker-compose.override.yml. Text, Markdown, HTML, PDF and Office files are read.`}>
              <Input required className="font-mono" value={draft.location} onChange={(e) => set({ location: e.target.value })} placeholder={`${folderRoot}/handbook`} />
            </Field>
          )}
          {draft.kind === 'upload' && !source && (
            <p className="text-sm text-muted-foreground">
              Once it is added, drop files on it (Files): documents, spreadsheets and slides (Office, OpenDocument and older Office files), PDFs with their scanned
              pages, pictures, e-mails, EPUB books and zip archives (each file inside on its own). Pictures and scans are read by OCR in the sandbox.
            </p>
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
          {mirrors(draft.kind) && (
            <div className="grid gap-2">
              <AccessPicker label={`Where ${kinds[draft.kind].label} cannot tell`} value={{ audience: draft.audience, groups: names }} onChange={(audience, chosen) => set({ audience, groups: chosen })} />
              <p className="text-xs text-muted-foreground">
                {draft.kind === 'confluence'
                  ? 'Each page is read by whoever may view it in Confluence. Where Confluence does not tell (a space whose permissions the account cannot see, someone whose email is hidden), these people read it.'
                  : "Each file is read by whoever its permissions name. Where Graph cannot tell (SharePoint groups such as a site's Members, and site pages), these people read it."}
              </p>
            </div>
          )}
          {!mirrors(draft.kind) && draft.kind !== 'gitlab' && (
            <AccessPicker label="Who may read it" value={{ audience: draft.audience, groups: names }} onChange={(audience, chosen) => set({ audience, groups: chosen })} />
          )}
          {tested && <Alert variant={tested.ok ? 'success' : 'destructive'}>{tested.message}</Alert>}
          <DialogFooter>
            {testable(draft.kind) && (
              <Button type="button" variant="outline" className="sm:mr-auto" loading={test.isPending} onClick={() => test.mutate()}>
                <PlugZap /> Test connection
              </Button>
            )}
            <Button type="button" variant="outline" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              {source ? 'Save' : 'Add'}
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

interface KnowledgeFile {
  id: string
  path: string
  contentType: string | null
  size: number
  uploadedAt: string
  uploadedBy: string | null
  documents: number
  passages: number
}

/** Files at a time in one upload, and bytes: a large folder goes up in parts. */
const PartFiles = 50
const PartBytes = 512 * 1024 * 1024

/** An upload source's files: dropped or chosen (a whole folder keeps its folders' names), each with what it became; removed one by one. */
function FilesDialog({ source, onClose }: { source: KnowledgeSource | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const pick = useRef<HTMLInputElement>(null)
  const pickFolder = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const [sending, setSending] = useState<string | null>(null)
  const files = useQuery({
    queryKey: ['admin', 'knowledge', source?.id, 'files'],
    queryFn: ({ signal }) => api<KnowledgeFile[]>(`/api/admin/knowledge/${source!.id}/files`, { signal }),
    enabled: source !== null,
    // While it is being read, what each file became changes.
    refetchInterval: source?.state === 'syncing' ? 3000 : false,
  })
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'knowledge'] })
  }
  const upload = async (chosen: File[]) => {
    if (!source || chosen.length === 0) return
    const parts: File[][] = [[]]
    let bytes = 0
    for (const f of chosen) {
      const last = parts[parts.length - 1]!
      if (last.length >= PartFiles || (last.length > 0 && bytes + f.size > PartBytes)) {
        parts.push([])
        bytes = 0
      }
      parts[parts.length - 1]!.push(f)
      bytes += f.size
    }
    try {
      let done = 0
      for (const part of parts) {
        setSending(`Uploading ${done + 1}–${done + part.length} of ${chosen.length}…`)
        const form = new FormData()
        for (const f of part) form.append('files', f, (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name)
        await api(`/api/admin/knowledge/${source.id}/files`, { body: form })
        done += part.length
      }
      toast.success(`${chosen.length} ${chosen.length === 1 ? 'file' : 'files'} added`, { description: 'They are being read now: the documents show as they are embedded.' })
    } catch (e) {
      toast.error('The upload stopped', { description: errorMessage(e) })
    } finally {
      setSending(null)
      refresh()
    }
  }
  const remove = useMutation({
    mutationFn: (f: KnowledgeFile) => api(`/api/admin/knowledge/${source!.id}/files/${f.id}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const drop = (e: DragEvent) => {
    e.preventDefault()
    setOver(false)
    void upload([...e.dataTransfer.files])
  }
  return (
    <Dialog open={source !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{source?.name}: files</DialogTitle>
          <DialogDescription>
            Documents, spreadsheets, slides, PDFs and scans, pictures, e-mails, EPUB books and zip archives, up to 200 MB each. A file uploaded again by the same name
            replaces it.
          </DialogDescription>
        </DialogHeader>
        {/* Dropping is the mouse's way; the buttons are everyone's. */}
        <div
          onDragOver={(e) => {
            e.preventDefault()
            setOver(true)
          }}
          onDragLeave={() => setOver(false)}
          onDrop={drop}
          className={`grid place-items-center gap-3 rounded-xl border-2 border-dashed px-4 py-8 text-center text-sm ${over ? 'border-primary bg-primary/5' : 'border-border'}`}
        >
          <Upload className="size-6 text-muted-foreground" aria-hidden="true" />
          <p className="text-muted-foreground">{sending ?? 'Drop files or a folder here'}</p>
          <div className="flex flex-wrap justify-center gap-2">
            <Button variant="outline" size="sm" disabled={sending !== null} onClick={() => pick.current?.click()}>
              <Upload /> Choose files
            </Button>
            <Button variant="outline" size="sm" disabled={sending !== null} onClick={() => pickFolder.current?.click()}>
              <FolderUp /> Choose a folder
            </Button>
          </div>
          <input ref={pick} type="file" multiple hidden aria-label="Files to upload" onChange={(e) => void upload([...(e.target.files ?? [])])} />
          <input
            ref={pickFolder}
            type="file"
            multiple
            hidden
            aria-label="A folder to upload"
            {...({ webkitdirectory: '' } as Record<string, string>)}
            onChange={(e) => void upload([...(e.target.files ?? [])])}
          />
        </div>
        {files.error && <Alert variant="destructive">{errorMessage(files.error)}</Alert>}
        {files.data && files.data.length === 0 && <p className="text-sm text-muted-foreground">No files yet.</p>}
        <ul className="grid max-h-[45vh] gap-1 overflow-y-auto text-sm" aria-label="Files">
          {(files.data ?? []).map((f) => (
            <li key={f.id} className="flex min-w-0 items-center gap-3 rounded-md px-2 py-1.5 hover:bg-accent">
              <div className="grid min-w-0 flex-1">
                <span className="truncate font-mono text-xs" title={f.path}>
                  {f.path}
                </span>
                <span className="text-xs text-muted-foreground">
                  {formatValue(f.size, 'bytes')} · {f.uploadedBy ?? 'someone removed'}, {ago(f.uploadedAt)} ·{' '}
                  {f.documents === 0 ? 'not read yet' : `${f.documents} ${f.documents === 1 ? 'document' : 'documents'}, ${f.passages} passages`}
                </span>
              </div>
              <Button
                variant="ghost"
                size="icon-sm"
                className="text-destructive-ink"
                aria-label={`Remove ${f.path}`}
                onClick={async () => {
                  if (await confirm({ title: `Remove ${f.path}?`, description: 'The file and what was read from it are deleted.', confirm: 'Remove', destructive: true })) remove.mutate(f)
                }}
              >
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      </DialogContent>
    </Dialog>
  )
}

