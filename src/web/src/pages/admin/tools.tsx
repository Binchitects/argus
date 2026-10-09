import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pencil, Plug, PlugZap, Plus, Trash2, Wrench } from 'lucide-react'
import { useRef, useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { toolIcon } from '../chat/tools'
import { AccessPicker, type Audience } from './access-picker'
import { tlsBody, type CertificateProblem, type Tls, type TlsCheck } from './tls'
import { CertificateRefused, TlsCaLine, TlsChoice, TlsWarning } from './tls-choice'


interface ToolRow {
  id: string
  title: string
  description: string
  icon: string
  unavailable: string | null
  setting: { enabled: boolean; audience: Audience; onByDefault: boolean; askFirst: boolean; groups: { id: string; name: string }[] }
  server: {
    id: string
    name: string
    description: string | null
    url: string
    headerName: string | null
    headerSet: boolean
    emailHeader: string | null
    callTimeoutMinutes: number | null
    prefix: string
    kind: 'mcp' | 'openapi'
    spec: string | null
    tls: TlsCheck
    tlsCa: string | null
    tlsCaNames: string[]
  } | null
}

interface Setting {
  enabled: boolean
  audience: Audience
  groups: string[]
  onByDefault: boolean
  askFirst: boolean
}

/** A tool's two switches in words: the label, and what it means. */
interface SwitchWords {
  onByDefault: [string, string]
  askFirst: [string, string]
}

const defaultWords: SwitchWords = {
  onByDefault: ['On in new chats', 'People can still turn it on or off in each chat.'],
  askFirst: ['Ask before each call', 'The chat shows what it wants to run and waits for Allow.'],
}

/** For a tool where "a call" says it badly. */
const switchWords: Record<string, SwitchWords> = {
  research: {
    onByDefault: ['The model may start it in new chats', 'People can still turn that on or off in each chat. Deep research in the message box is there either way.'],
    askFirst: ['Ask before each run', 'When the model starts one itself, the chat asks the person first. Pressing Deep research is the person asking.'],
  },
}

const toolsQuery = {
  queryKey: ['admin', 'tools'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ToolRow[]>('/api/admin/tools', { signal }),
}

export function ToolsPage() {
  const tools = useQuery(toolsQuery)
  const [editing, setEditing] = useState<ToolRow['server'] | 'new' | null>(null)
  if (tools.isPending) return <PageSkeleton />
  if (tools.error) return <QueryError error={tools.error} retry={() => tools.refetch()} />
  return (
    <>
      <PageHeader
        title="Tools"
        description="What the chat's model may call, and for whom. Turn a tool off, give it to some groups only, have it ask before each call, or add an MCP server or an API."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> Add a server or API
          </Button>
        }
      />
      <div className="stagger grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
        {tools.data.map((t) => (
          <ToolCard key={t.id} tool={t} onEdit={() => setEditing(t.server)} />
        ))}
      </div>
      <ServerDialog server={editing} onClose={() => setEditing(null)} />
    </>
  )
}

function ToolCard({ tool, onEdit }: { tool: ToolRow; onEdit: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const Icon = toolIcon[tool.icon] ?? Wrench
  const current: Setting = { ...tool.setting, groups: tool.setting.groups.map((g) => g.id) }
  const save = useMutation({
    mutationFn: (s: Setting) => api(`/api/admin/tools/${encodeURIComponent(tool.id)}`, { method: 'PUT', body: s }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/tools/servers/${tool.server!.id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const set = (change: Partial<Setting>) => save.mutate({ ...current, ...change })
  const words = switchWords[tool.id] ?? defaultWords
  return (
    // Off: a quieter card, not faded text (faded grey text fails contrast).
    <Card className={tool.setting.enabled ? '' : 'border-dashed bg-muted/40 shadow-none'}>
      <CardHeader className="flex flex-row items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
          <Icon className="size-4.5" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <CardTitle className="flex flex-wrap items-center gap-2">
            {tool.title}
            <Badge variant={tool.server ? 'outline' : 'secondary'}>{tool.server ? (tool.server.kind === 'openapi' ? 'API' : 'MCP server') : 'Built in'}</Badge>
          </CardTitle>
          <CardDescription>{tool.description}</CardDescription>
        </div>
        <Switch checked={tool.setting.enabled} onCheckedChange={(enabled) => set({ enabled })} aria-label={`${tool.title} on`} />
      </CardHeader>
      <CardContent className="grid gap-4">
        {tool.unavailable && <Alert variant="warning">{tool.unavailable}</Alert>}
        {tool.server?.tls === 'Off' && <TlsWarning name={tool.title} />}
        {tool.server && (
          <div className="grid gap-1 rounded-lg border bg-muted/30 p-3 text-xs">
            <p className="flex min-w-0 items-center gap-1.5 font-mono break-all">
              <Plug className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" /> {tool.server.url}
            </p>
            {tool.server.headerName && (
              <p className="text-muted-foreground">
                Sends <span className="font-mono text-foreground">{tool.server.headerName}</span> {tool.server.headerSet ? '(value saved, never shown)' : '(no value)'}
              </p>
            )}
            {tool.server.emailHeader && (
              <p className="text-muted-foreground">
                The person's email goes in <span className="font-mono text-foreground">{tool.server.emailHeader}</span>
              </p>
            )}
            {tool.server.tls === 'OwnCa' && <TlsCaLine names={tool.server.tlsCaNames} />}
            {tool.server.callTimeoutMinutes && (
              <p className="text-muted-foreground">
                A call may run for up to <span className="text-foreground">{tool.server.callTimeoutMinutes} minutes</span>
              </p>
            )}
            <p className="text-muted-foreground">
              Its functions are named <span className="font-mono text-foreground">{tool.server.prefix}…</span>
              {tool.server.kind === 'openapi' && '; calls that change something (not GET) always ask first'}
            </p>
          </div>
        )}
        <AccessPicker value={tool.setting} onChange={(audience, groups) => set({ audience, groups })} />
        <div className="grid gap-2">
          <Label className="flex items-center justify-between gap-3 font-normal">
            <span>
              {words.onByDefault[0]}
              <span className="block text-xs text-muted-foreground">{words.onByDefault[1]}</span>
            </span>
            <Switch checked={tool.setting.onByDefault} onCheckedChange={(onByDefault) => set({ onByDefault })} />
          </Label>
          <Label className="flex items-center justify-between gap-3 font-normal">
            <span>
              {words.askFirst[0]}
              <span className="block text-xs text-muted-foreground">{words.askFirst[1]}</span>
            </span>
            <Switch checked={tool.setting.askFirst} onCheckedChange={(askFirst) => set({ askFirst })} />
          </Label>
        </div>
        {tool.server && (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" size="sm" onClick={onEdit}>
              <Pencil /> Edit
            </Button>
            <Button
              variant="outline"
              size="sm"
              className="text-destructive-ink"
              onClick={async () => {
                if (await confirm({ title: `Remove ${tool.title}?`, description: 'Its tools leave every chat at once.', confirm: 'Remove', destructive: true })) remove.mutate()
              }}
            >
              <Trash2 /> Remove
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function ServerDialog({ server, onClose }: { server: ToolRow['server'] | 'new' | null; onClose: () => void }) {
  const open = server !== null
  const saved = server === 'new' ? null : server
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-xl">{open && <ServerForm key={saved?.id ?? 'new'} saved={saved} onClose={onClose} />}</DialogContent>
    </Dialog>
  )
}

function ServerForm({ saved, onClose }: { saved: ToolRow['server']; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<'mcp' | 'openapi'>(saved?.kind ?? 'mcp')
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    description: saved?.description ?? '',
    url: saved?.url ?? '',
    headerName: saved?.headerName ?? '',
    headerValue: '',
    emailHeader: saved?.emailHeader ?? '',
    callTimeoutMinutes: saved?.callTimeoutMinutes ? String(saved.callTimeoutMinutes) : '',
    spec: saved?.spec ?? '',
    specUrl: '',
  })
  const [tls, setTls] = useState<Tls>({ tls: saved?.tls ?? 'System', tlsCa: saved?.tlsCa ?? '' })
  const caField = useRef<HTMLTextAreaElement>(null)
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<{
    ok: boolean
    error?: string
    url?: string
    tools?: { name: string; description: string | null; asksFirst?: boolean }[]
    /** Why its certificate was refused, when that was why. */
    certificate?: CertificateProblem | null
  } | null>(null)
  const api_ = kind === 'openapi'
  // An MCP server sends "" for the document (it is not an API); an API sends it, or where to fetch it.
  const body = {
    ...form,
    headerValue: form.headerValue || (saved ? null : ''),
    callTimeoutMinutes: Number(form.callTimeoutMinutes) || 0,
    spec: api_ ? (form.specUrl ? null : form.spec || null) : saved?.kind === 'openapi' ? '' : null,
    specUrl: api_ ? form.specUrl || null : null,
    ...tlsBody(tls),
  }
  const check = useMutation({
    mutationFn: () => api<NonNullable<typeof test>>(`/api/admin/tools/servers/test${saved ? `?id=${saved.id}` : ''}`, { body }),
    onSuccess: setTest,
    onError: (e) => setTest({ ok: false, error: errorMessage(e) }),
  })
  const save = useMutation({
    mutationFn: () => (saved ? api(`/api/admin/tools/servers/${saved.id}`, { method: 'PATCH', body }) : api('/api/admin/tools/servers', { body })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] })
      toast.success(saved ? 'Saved.' : `${api_ ? 'API' : 'Server'} added. Its tools are on for everyone: set who may use them on its card.`)
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const field = (k: keyof typeof form) => ({ value: form[k], onChange: (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value }) })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add a server or API'}</DialogTitle>
        <DialogDescription>
          {api_
            ? 'A REST API, by its OpenAPI document: each operation becomes a function. Calls that change something (POST, PUT, PATCH, DELETE) always ask the person first.'
            : 'A server that speaks MCP over HTTP (streamable HTTP). Its tools join the chat as one tool you can turn on for some people.'}
        </DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <Tabs
          value={kind}
          onValueChange={(v) => {
            setKind(v as 'mcp' | 'openapi')
            setTest(null)
          }}
        >
          <TabsList aria-label="Kind">
            <TabsTrigger value="mcp">MCP server</TabsTrigger>
            <TabsTrigger value="openapi">API (OpenAPI)</TabsTrigger>
          </TabsList>
        </Tabs>
        <Field label="Name" hint="Shown to people in the chat's Tools menu.">
          <Input required maxLength={100} autoComplete="off" {...field('name')} />
        </Field>
        {api_ && (
          <>
            <Field label="OpenAPI document" hint="JSON or YAML, OpenAPI 3. Or give the address to fetch it from below.">
              <Textarea className="min-h-32 font-mono text-xs" spellCheck={false} placeholder={'openapi: 3.0.3\npaths:\n  /pets:\n    get: ...'} {...field('spec')} />
            </Field>
            <Field label="Or the document's address" hint="Fetched once, now; save again to fetch a newer one.">
              <Input type="url" placeholder="https://api.example.com/openapi.json" autoComplete="off" {...field('specUrl')} />
            </Field>
          </>
        )}
        <Field label="Address" hint={api_ ? "The API's base address. Empty: the document's first server." : undefined}>
          <Input required={!api_} type="url" placeholder={api_ ? 'https://api.example.com/v1' : 'https://tools.example.com/mcp'} autoComplete="off" {...field('url')} />
        </Field>
        <Field label="What it does">
          <Input maxLength={500} autoComplete="off" {...field('description')} />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Header name" hint="For a key, e.g. Authorization or X-Api-Key.">
            <Input autoComplete="off" {...field('headerName')} />
          </Field>
          <Field label="Header value" hint={saved?.headerSet ? 'Saved. Leave empty to keep it.' : 'Stored encrypted, never shown again.'}>
            <Input type="password" autoComplete="new-password" {...field('headerValue')} />
          </Field>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Person's email header" hint="Optional, e.g. X-User-Email: for servers that answer as the person asking.">
            <Input autoComplete="off" {...field('emailHeader')} />
          </Field>
          <Field label="Longest call (minutes)" hint="For tools that run long, up to 1440. Empty: the chat's limit (Settings → Chat).">
            <Input type="number" min={1} max={1440} inputMode="numeric" autoComplete="off" {...field('callTimeoutMinutes')} />
          </Field>
        </div>
        <TlsChoice name="tls" value={tls} onChange={setTls} caRef={caField} />
        {test &&
          (test.ok ? (
            <Alert variant="success" title={api_ ? `${test.tools?.length ?? 0} operations${test.url ? ` at ${test.url}` : ''}` : `Connected: ${test.tools?.length ?? 0} tools`}>
              <ul className="mt-1 grid max-h-48 gap-0.5 overflow-y-auto text-xs">
                {test.tools?.map((t) => (
                  <li key={t.name}>
                    <span className="font-mono">{t.name}</span>
                    {t.asksFirst && <Badge variant="warning" className="ml-1.5">asks first</Badge>}
                    {t.description && <span className="text-muted-foreground"> · {t.description}</span>}
                  </li>
                ))}
              </ul>
            </Alert>
          ) : test.certificate ? (
            <CertificateRefused
              problem={test.certificate}
              onTrust={() => {
                setTls({ ...tls, tls: 'OwnCa' })
                requestAnimationFrame(() => caField.current?.focus())
              }}
              onSkip={() => setTls({ ...tls, tls: 'Off' })}
            />
          ) : (
            <Alert variant="destructive" title={api_ ? 'The document does not work' : 'Could not connect'}>
              {test.error}
            </Alert>
          ))}
        <DialogFooter className="sm:justify-between">
          <Button type="button" variant="outline" onClick={() => check.mutate()} loading={check.isPending} disabled={api_ ? !form.spec && !form.specUrl && !saved?.spec : !form.url}>
            <PlugZap /> {api_ ? 'Read it' : 'Test'}
          </Button>
          <span className="flex gap-2">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              {saved ? 'Save' : api_ ? 'Add API' : 'Add server'}
            </Button>
          </span>
        </DialogFooter>
      </form>
    </>
  )
}
