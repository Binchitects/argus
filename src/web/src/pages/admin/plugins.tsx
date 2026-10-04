import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Blocks, Download, Link2, RefreshCw, Settings2, Trash2, Upload } from 'lucide-react'
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
import { api, errorMessage } from '@/lib/api'

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
  installed: { id: string; toolId: string; plugin: string; title: string; version: string; url: string; personAuth: string | null; writes: string[]; settings: SettingView[] }[]
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
  const file = useRef<HTMLInputElement>(null)
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
                const bytes = new Uint8Array(await f.arrayBuffer())
                let binary = ''
                for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
                setInstalling({ zip: btoa(binary) })
              }}
            />
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
                <InstalledCard key={p.id} plugin={p} newer={plugins.data.catalog.find((c) => c.name === p.plugin && c.version !== p.version)?.version ?? null} onEdit={() => setEditing(p)} onChanged={refresh} />
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
    </>
  )
}

function InstalledCard({ plugin, newer, onEdit, onChanged }: { plugin: Plugins['installed'][number]; newer: string | null; onEdit: () => void; onChanged: () => void }) {
  const confirm = useConfirm()
  const update = useMutation({
    mutationFn: () => api<{ version: string }>(`/api/admin/plugins/${plugin.id}/update`, { body: {} }),
    onSuccess: (v) => {
      toast.success(`${plugin.title} is now ${v.version}`)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${plugin.id}`, { method: 'DELETE' }),
    onSuccess: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card aria-label={plugin.title}>
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          {plugin.title} <Badge variant="secondary">{plugin.version}</Badge>
          {plugin.personAuth && <Badge variant="outline">{signIn[plugin.personAuth]}</Badge>}
        </CardTitle>
        <CardDescription className="font-mono text-xs break-all">{plugin.url}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {plugin.writes.length > 0 && <p className="text-xs text-muted-foreground">Asks first before: {plugin.writes.join(', ')} (and any call that changes something).</p>}
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={onEdit}>
            <Settings2 /> Settings
          </Button>
          <Button variant="outline" size="sm" asChild>
            <Link to="/admin/tools">Who may use it</Link>
          </Button>
          {newer && (
            <Button variant="outline" size="sm" loading={update.isPending} onClick={() => update.mutate()}>
              <RefreshCw /> Update to {newer}
            </Button>
          )}
          <Button
            variant="outline"
            size="sm"
            className="text-destructive-ink"
            onClick={async () => {
              if (await confirm({ title: `Remove ${plugin.title}?`, description: 'Its tools leave every chat at once, and everyone’s connected accounts for it are forgotten.', confirm: 'Remove', destructive: true }))
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
  const [error, setError] = useState<string | null>(null)
  const install = useMutation({
    mutationFn: () => api('/api/admin/plugins/install', { body: { ...source, settings: values } }),
    onSuccess: () => {
      toast.success(`${preview.data?.title} installed`, { description: 'It is on for everyone: choose who may use it in Admin → Tools.' })
      setValues({})
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
            <SettingsFields settings={p.settings} values={values} onChange={(k, v) => setValues({ ...values, [k]: v })} />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" loading={install.isPending}>
                Install
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  )
}

function SettingsDialog({ plugin, onClose, onDone }: { plugin: Plugins['installed'][number] | null; onClose: () => void; onDone: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${plugin!.id}`, { method: 'PATCH', body: { settings: values } }),
    onSuccess: () => {
      toast.success('Saved')
      onDone()
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Dialog
      open={plugin !== null}
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-lg">
        {plugin && (
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
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" loading={save.isPending}>
                Save
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
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
