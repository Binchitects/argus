import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, Blocks, ClipboardCheck, Download, FileCode2, Link2, Pin, PinOff, RefreshCw, RotateCcw, Settings2, Trash2, Upload, Users } from 'lucide-react'
import { useRef, useState } from 'react'
import { Link } from 'react-router'
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
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage } from '@/lib/api'
import { PluginGroupsDialog } from './plugin-groups'
import { tlsBody, type Tls, type TlsCheck } from './tls'
import { TlsCaLine, TlsChoice, TlsWarning } from './tls-choice'

interface SettingView {
  key: string
  title: string
  type: 'text' | 'url' | 'secret'
  required: boolean
  help: string | null
  value?: string | null
  set?: boolean
}

interface Plugins {
  problem: string | null
  catalog: { name: string; version: string; title: string; description: string; source: 'app' | 'catalog'; personAuth: string | null; installed: { id: string; version: string } | null }[]
  installed: {
    id: string
    toolId: string
    plugin: string
    title: string
    version: string
    url: string
    personAuth: string | null
    writes: string[]
    settings: SettingView[]
    /** How its server's certificate is checked (as for any server in Admin → Tools). */
    tls: TlsCheck
    tlsCa: string | null
    tlsCaNames: string[]
    /** What the admin granted it. */
    permissions?: string[]
    pinned?: boolean
    /** A newer version in a catalog (none while pinned). */
    update?: string | null
    releases?: { version: string; installedAt: string }[]
  }[]
}

interface Permission {
  key: string
  text: string
}

interface Preview {
  name: string
  version: string
  title: string
  description: string
  personAuth: string | null
  help: string | null
  writes: string[]
  operations: number | null
  mcp: string | null
  settings: SettingView[]
  /** The prompts it adds to the library (Workspace → Prompts). */
  prompts?: { name: string; title: string }[]
  /** What installing it grants it, each in a sentence; needsYes: more than adding tools. */
  permissions?: Permission[]
  needsYes?: boolean
  author?: string | null
}

/** A plugin's zip checked as installing would: what stops it, what looks wrong, what it brings. */
interface CheckReport {
  ok: boolean
  problems: string[]
  warnings: string[]
  name?: string
  version?: string
  title?: string
  permissions?: Permission[]
}

/** The zip as base64, for the API. */
async function base64(f: File) {
  const bytes = new Uint8Array(await f.arrayBuffer())
  let binary = ''
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(binary)
}

/** Where a plugin comes from: its name in a catalog, its zip's address (and SHA-256), or the zip itself. */
type Source = { name: string } | { url: string; sha256?: string } | { zip: string }

const signIn: Record<string, string> = { oauth2: 'Each person signs in', api_key: 'Each person’s own key' }

export function PluginsPage() {
  const queryClient = useQueryClient()
  const plugins = useQuery({ queryKey: ['admin', 'plugins'], queryFn: ({ signal }) => api<Plugins>('/api/admin/plugins', { signal }) })
  const [installing, setInstalling] = useState<Source | null>(null)
  const [editing, setEditing] = useState<Plugins['installed'][number] | null>(null)
  const [fromUrl, setFromUrl] = useState(false)
  const [report, setReport] = useState<CheckReport | null>(null)
  const file = useRef<HTMLInputElement>(null)
  const checkFile = useRef<HTMLInputElement>(null)
  if (plugins.isPending) return <PageSkeleton />
  if (plugins.error) return <QueryError error={plugins.error} retry={() => plugins.refetch()} />
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin', 'plugins'] })
  return (
    <>
      <PageHeader
        title="Plugins"
        description="Ready-made tools for the chat: an API or an MCP server, with its sign-in and the calls that ask first. Installed, each is a tool in Admin → Tools, where you choose who may use it."
        actions={
          <>
            <input
              ref={file}
              type="file"
              accept=".zip,application/zip"
              className="hidden"
              aria-label="Plugin zip"
              onChange={async (e) => {
                const f = e.target.files?.[0]
                e.target.value = ''
                if (!f) return
                setInstalling({ zip: await base64(f) })
              }}
            />
            <input
              ref={checkFile}
              type="file"
              accept=".zip,application/zip"
              className="hidden"
              aria-label="Plugin zip to check"
              onChange={async (e) => {
                const f = e.target.files?.[0]
                e.target.value = ''
                if (!f) return
                try {
                  setReport(await api<CheckReport>('/api/admin/plugins/check', { body: { zip: await base64(f) } }))
                } catch (err) {
                  toast.error(errorMessage(err))
                }
              }}
            />
            <Button variant="outline" asChild>
              <a href="/api/admin/plugins/template" download="plugin-template.zip">
                <FileCode2 /> Make a plugin
              </a>
            </Button>
            <Button variant="outline" onClick={() => checkFile.current?.click()}>
              <ClipboardCheck /> Check a plugin
            </Button>
            <Button variant="outline" onClick={() => file.current?.click()}>
              <Upload /> Upload a zip
            </Button>
            <Button variant="outline" onClick={() => setFromUrl(true)}>
              <Link2 /> From an address
            </Button>
          </>
        }
      />
      <div className="grid gap-6">
        {plugins.data.problem && <Alert variant="warning">{plugins.data.problem}</Alert>}
        <section aria-labelledby="installed-heading" className="grid gap-3">
          <h2 id="installed-heading" className="text-sm font-medium text-muted-foreground">
            Installed
          </h2>
          {plugins.data.installed.length === 0 ? (
            <p className="text-sm text-muted-foreground">None yet. Install one from the catalog below, a zip, or its address.</p>
          ) : (
            <div className="grid gap-4 xl:grid-cols-2">
              {plugins.data.installed.map((p) => (
                <InstalledCard key={p.id} plugin={p} onEdit={() => setEditing(p)} onChanged={refresh} />
              ))}
            </div>
          )}
        </section>
        <section aria-labelledby="catalog-heading" className="grid gap-3">
          <h2 id="catalog-heading" className="text-sm font-medium text-muted-foreground">
            Catalog
          </h2>
          {plugins.data.catalog.length === 0 ? (
            <EmptyState icon={Blocks} title="No catalog">
              Plugins come with the app (its plugins folder), or from a catalog set under Settings → Plugins.
            </EmptyState>
          ) : (
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {plugins.data.catalog.map((c) => (
                <Card key={c.name} aria-label={c.title}>
                  <CardHeader>
                    <CardTitle className="flex flex-wrap items-center gap-2">
                      {c.title} <Badge variant="secondary">{c.version}</Badge>
                      {c.source === 'app' && <Badge variant="outline">Comes with the app</Badge>}
                    </CardTitle>
                    <CardDescription>{c.description}</CardDescription>
                  </CardHeader>
                  <CardContent className="flex flex-wrap items-center gap-2">
                    {c.personAuth && <span className="text-xs text-muted-foreground">{signIn[c.personAuth]}</span>}
                    <span className="flex-1" />
                    {c.installed ? (
                      <Badge variant="success">Installed {c.installed.version}</Badge>
                    ) : (
                      <Button size="sm" onClick={() => setInstalling({ name: c.name })}>
                        <Download /> Install
                      </Button>
                    )}
                  </CardContent>
                </Card>
              ))}
            </div>
          )}
        </section>
      </div>
      <InstallDialog source={installing} onClose={() => setInstalling(null)} onDone={refresh} />
      <SettingsDialog plugin={editing} onClose={() => setEditing(null)} onDone={refresh} />
      <UrlDialog open={fromUrl} onClose={() => setFromUrl(false)} onChosen={(s) => setInstalling(s)} />
      <ReportDialog report={report} onClose={() => setReport(null)} />
    </>
  )
}

/** What a plugin asks for beyond what was granted: the admin's yes, asked here. */
function useGrant() {
  const confirm = useConfirm()
  return async (e: unknown, title: string) => {
    const asks = e instanceof ApiError && e.status === 'accept' ? ((e.data as { permissions?: Permission[] } | undefined)?.permissions ?? []) : null
    if (!asks) return false
    return confirm({
      title: `${title} asks for more`,
      description: (
        <ul className="grid list-disc gap-1 pl-4">
          {asks.map((p) => (
            <li key={p.key}>{p.text}</li>
          ))}
        </ul>
      ),
      confirm: 'Grant it',
    })
  }
}

interface Health {
  on: boolean
  calls: number
  failed: number
  averageMs: number
  failuresInARow: number
  lastError: { error: string | null; at: string; function: string } | null
  lastCall: string | null
}

function InstalledCard({ plugin, onEdit, onChanged }: { plugin: Plugins['installed'][number]; onEdit: () => void; onChanged: () => void }) {
  const confirm = useConfirm()
  const grant = useGrant()
  const [showHealth, setShowHealth] = useState(false)
  const [forGroups, setForGroups] = useState(false)
  const health = useQuery({ queryKey: ['admin', 'plugins', plugin.id, 'health'], queryFn: ({ signal }) => api<Health>(`/api/admin/plugins/${plugin.id}/health`, { signal }), enabled: showHealth })
  // Asked for more than was granted: the admin's yes, then the same update with it.
  const update = useMutation({
    mutationFn: async (accept: boolean): Promise<{ version: string } | null> => {
      try {
        return await api<{ version: string }>(`/api/admin/plugins/${plugin.id}/update`, { body: { accept } })
      } catch (e) {
        if (!accept && (await grant(e, plugin.title))) return api<{ version: string }>(`/api/admin/plugins/${plugin.id}/update`, { body: { accept: true } })
        if (e instanceof ApiError && e.status === 'accept') return null
        throw e
      }
    },
    onSuccess: (v) => {
      if (!v) return
      toast.success(`${plugin.title} is now ${v.version}`)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const rollback = useMutation({
    mutationFn: (version: string) => api<{ version: string }>(`/api/admin/plugins/${plugin.id}/rollback`, { body: { version } }),
    onSuccess: (v) => {
      toast.success(`${plugin.title} is back at ${v.version}`)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const pin = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${plugin.id}/pin`, { body: { pinned: !plugin.pinned } }),
    onSuccess: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const check = useMutation({
    mutationFn: () => api<{ ok: boolean; functions?: number; error?: string; ms: number }>(`/api/admin/plugins/${plugin.id}/check`, { body: {} }),
    onSuccess: (r) => (r.ok ? toast.success(`${plugin.title} answers`, { description: `${r.functions} functions, in ${r.ms} ms.` }) : toast.error(`${plugin.title} does not answer`, { description: r.error })),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${plugin.id}`, { method: 'DELETE' }),
    onSuccess: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const earlier = (plugin.releases ?? []).filter((r) => r.version !== plugin.version)
  return (
    <Card aria-label={plugin.title}>
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          {plugin.title} <Badge variant="secondary">{plugin.version}</Badge>
          {plugin.personAuth && <Badge variant="outline">{signIn[plugin.personAuth]}</Badge>}
          {plugin.pinned && <Badge variant="outline">Pinned</Badge>}
        </CardTitle>
        <CardDescription className="font-mono text-xs break-all">{plugin.url}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {plugin.tls === 'Off' && <TlsWarning name={plugin.title} />}
        {plugin.tls === 'OwnCa' && (
          <div className="text-xs">
            <TlsCaLine names={plugin.tlsCaNames} />
          </div>
        )}
        {plugin.writes.length > 0 && <p className="text-xs text-muted-foreground">Asks first before: {plugin.writes.join(', ')} (and any call that changes something).</p>}
        {!!plugin.permissions?.length && <p className="text-xs text-muted-foreground">Granted: {plugin.permissions.join(', ')}.</p>}
        {showHealth && (
          <div className="grid gap-1 rounded-lg border bg-muted/30 p-3 text-xs" aria-live="polite">
            {!health.data ? (
              'Reading its health…'
            ) : (
              <>
                <p>
                  {health.data.on ? 'On' : <span className="text-destructive-ink">Off</span>} · {health.data.calls} calls this week · {health.data.failed} failed · {(health.data.averageMs / 1000).toFixed(1)} s on average
                  {health.data.failuresInARow > 0 && <span className="text-destructive-ink"> · {health.data.failuresInARow} failed in a row</span>}
                </p>
                {health.data.lastError && (
                  <p className="text-muted-foreground">
                    Last error ({health.data.lastError.function}, {new Date(health.data.lastError.at).toLocaleString()}): {health.data.lastError.error}
                  </p>
                )}
              </>
            )}
          </div>
        )}
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={onEdit}>
            <Settings2 /> Settings
          </Button>
          {plugin.settings.length > 0 && (
            <Button variant="outline" size="sm" onClick={() => setForGroups(true)}>
              <Users /> For groups
            </Button>
          )}
          <Button variant="outline" size="sm" asChild>
            <Link to="/admin/tools">Who may use it</Link>
          </Button>
          <Button variant="outline" size="sm" aria-pressed={showHealth} onClick={() => setShowHealth((v) => !v)}>
            <Activity /> Health
          </Button>
          <Button variant="outline" size="sm" loading={check.isPending} onClick={() => check.mutate()}>
            Check now
          </Button>
          {plugin.update && (
            <Button variant="outline" size="sm" loading={update.isPending} onClick={() => update.mutate(false)}>
              <RefreshCw /> Update to {plugin.update}
            </Button>
          )}
          {earlier.length > 0 && (
            <Button variant="outline" size="sm" loading={rollback.isPending} onClick={async () => {
              if (await confirm({ title: `Back to ${earlier[0]!.version}?`, description: `${plugin.title} goes back to the version installed ${new Date(earlier[0]!.installedAt).toLocaleDateString()}, its settings kept.`, confirm: 'Go back' }))
                rollback.mutate(earlier[0]!.version)
            }}>
              <RotateCcw /> Back to {earlier[0]!.version}
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={() => pin.mutate()} aria-pressed={plugin.pinned}>
            {plugin.pinned ? <PinOff /> : <Pin />} {plugin.pinned ? 'Unpin' : 'Pin this version'}
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="text-destructive-ink"
            onClick={async () => {
              if (await confirm({ title: `Remove ${plugin.title}?`, description: 'Its tools leave every chat at once, everyone’s connected accounts for it are forgotten, and its assistants, rules and toolsets go too.', confirm: 'Remove', destructive: true }))
                remove.mutate()
            }}
          >
            <Trash2 /> Remove
          </Button>
        </div>
      </CardContent>
      <PluginGroupsDialog plugin={plugin} open={forGroups} onClose={() => setForGroups(false)} />
    </Card>
  )
}

/** A plugin's zip checked: what would stop it installing, what looks wrong, and what it would ask for. */
function ReportDialog({ report, onClose }: { report: CheckReport | null; onClose: () => void }) {
  return (
    <Dialog open={report !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{report?.ok ? `${report.title ?? 'The plugin'} ${report.version ?? ''} would install` : 'The plugin would not install'}</DialogTitle>
          <DialogDescription>Checked as installing would, without installing anything.</DialogDescription>
        </DialogHeader>
        {report && (
          <div className="grid gap-3 text-sm">
            {report.problems.length > 0 && (
              <Alert variant="destructive">
                <ul className="list-disc pl-4">
                  {report.problems.map((p) => (
                    <li key={p}>{p}</li>
                  ))}
                </ul>
              </Alert>
            )}
            {report.warnings.length > 0 && (
              <Alert variant="warning">
                <ul className="list-disc pl-4">
                  {report.warnings.map((w) => (
                    <li key={w}>{w}</li>
                  ))}
                </ul>
              </Alert>
            )}
            {!!report.permissions?.length && (
              <div>
                <p className="font-medium">It would ask for</p>
                <ul className="list-disc pl-4 text-muted-foreground">
                  {report.permissions.map((p) => (
                    <li key={p.key}>{p.text}</li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

function SettingsFields({ settings, values, onChange }: { settings: SettingView[]; values: Record<string, string>; onChange: (key: string, value: string) => void }) {
  return (
    <>
      {settings.map((s) => (
        <Field key={s.key} label={s.title + (s.required ? '' : ' (optional)')} hint={s.type === 'secret' && s.set ? 'Saved, never shown. Leave empty to keep it.' : (s.help ?? undefined)}>
          <Input
            required={s.required && !(s.type === 'secret' && s.set)}
            type={s.type === 'secret' ? 'password' : s.type === 'url' ? 'url' : 'text'}
            autoComplete={s.type === 'secret' ? 'new-password' : 'off'}
            value={values[s.key] ?? ''}
            onChange={(e) => onChange(s.key, e.target.value)}
          />
        </Field>
      ))}
    </>
  )
}

function InstallDialog({ source, onClose, onDone }: { source: Source | null; onClose: () => void; onDone: () => void }) {
  const preview = useQuery({
    queryKey: ['admin', 'plugins', 'preview', source],
    queryFn: () => api<Preview>('/api/admin/plugins/preview', { body: source }),
    enabled: source !== null,
    retry: false,
  })
  const [values, setValues] = useState<Record<string, string>>({})
  const [tls, setTls] = useState<Tls>({ tls: 'System', tlsCa: '' })
  const [error, setError] = useState<string | null>(null)
  const install = useMutation({
    mutationFn: () => api('/api/admin/plugins/install', { body: { ...source, settings: values, ...tlsBody(tls), ...(preview.data?.needsYes ? { accept: true } : {}) } }),
    onSuccess: () => {
      toast.success(`${preview.data?.title} installed`, { description: 'It is on for everyone: choose who may use it in Admin → Tools.' })
      setValues({})
      setTls({ tls: 'System', tlsCa: '' })
      onDone()
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const p = preview.data
  return (
    <Dialog
      open={source !== null}
      onOpenChange={(o) => {
        if (!o) {
          setValues({})
          setTls({ tls: 'System', tlsCa: '' })
          setError(null)
          onClose()
        }
      }}
    >
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{p ? `Install ${p.title} ${p.version}` : 'Install a plugin'}</DialogTitle>
          <DialogDescription>{p?.description ?? 'Reading the plugin…'}</DialogDescription>
        </DialogHeader>
        {preview.error && <Alert variant="destructive">{errorMessage(preview.error)}</Alert>}
        {p && (
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              setError(null)
              install.mutate()
            }}
          >
            {error && <Alert variant="destructive">{error}</Alert>}
            <ul className="grid gap-1 text-sm text-muted-foreground">
              <li>{p.mcp ? `Tools from the MCP server at ${p.mcp}.` : `${p.operations ?? 0} operations of its API.`}</li>
              <li>{p.personAuth ? `${signIn[p.personAuth]}: each person connects their own account in Your account → Connections. ${p.help ?? ''}` : 'One sign-in for everyone, from the settings.'}</li>
              {p.writes.length > 0 && <li>Asks the person first before: {p.writes.join(', ')}.</li>}
              {!!p.prompts?.length && <li>Adds prompts for whoever may use it: {p.prompts.map((x) => `/${x.name} (${x.title})`).join(', ')}.</li>}
            </ul>
            {!!p.permissions?.length && (
              <div className="grid gap-1 rounded-lg border p-3 text-sm">
                <p className="font-medium">Installing it grants it</p>
                <ul className="list-disc pl-4 text-muted-foreground">
                  {p.permissions.map((x) => (
                    <li key={x.key}>{x.text}</li>
                  ))}
                </ul>
              </div>
            )}
            <SettingsFields settings={p.settings} values={values} onChange={(k, v) => setValues({ ...values, [k]: v })} />
            <TlsChoice name="install-tls" value={tls} onChange={setTls} />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" loading={install.isPending}>
                {p.needsYes ? 'Install and grant' : 'Install'}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  )
}

function SettingsDialog({ plugin, onClose, onDone }: { plugin: Plugins['installed'][number] | null; onClose: () => void; onDone: () => void }) {
  return (
    <Dialog
      open={plugin !== null}
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-lg">{plugin && <SettingsForm key={plugin.id} plugin={plugin} onClose={onClose} onDone={onDone} />}</DialogContent>
    </Dialog>
  )
}

function SettingsForm({ plugin, onClose, onDone }: { plugin: Plugins['installed'][number]; onClose: () => void; onDone: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({})
  const [tls, setTls] = useState<Tls>({ tls: plugin.tls, tlsCa: plugin.tlsCa ?? '' })
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${plugin.id}`, { method: 'PATCH', body: { settings: values, ...tlsBody(tls) } }),
    onSuccess: () => {
      toast.success('Saved')
      onDone()
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{plugin.title}: settings</DialogTitle>
        <DialogDescription>What is left empty stays as it is.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <SettingsFields
        settings={plugin.settings}
        values={Object.fromEntries(plugin.settings.map((s) => [s.key, values[s.key] ?? (s.type === 'secret' ? '' : (s.value ?? ''))]))}
        onChange={(k, v) => setValues({ ...values, [k]: v })}
      />
      <TlsChoice name="plugin-tls" value={tls} onChange={setTls} />
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

function UrlDialog({ open, onClose, onChosen }: { open: boolean; onClose: () => void; onChosen: (s: Source) => void }) {
  const [url, setUrl] = useState('')
  const [sha256, setSha256] = useState('')
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            onChosen({ url, sha256: sha256 || undefined })
            onClose()
          }}
        >
          <DialogHeader>
            <DialogTitle>Install from an address</DialogTitle>
            <DialogDescription>The plugin's zip. With its SHA-256, only that exact file is installed.</DialogDescription>
          </DialogHeader>
          <Field label="Address of the zip">
            <Input required type="url" placeholder="https://plugins.example.com/jira-1.2.0.zip" value={url} onChange={(e) => setUrl(e.target.value)} />
          </Field>
          <Field label="SHA-256 (recommended)">
            <Input className="font-mono" autoComplete="off" value={sha256} onChange={(e) => setSha256(e.target.value)} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit">Read it</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
