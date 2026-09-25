import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pencil, Plug, PlugZap, Plus, Trash2, Wrench } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { toolIcon } from '../chat/tools'
import { AccessPicker, type Audience } from './access-picker'


interface ToolRow {
  id: string
  title: string
  description: string
  icon: string
  unavailable: string | null
  setting: { enabled: boolean; audience: Audience; onByDefault: boolean; askFirst: boolean; groups: { id: string; name: string }[] }
  server: { id: string; name: string; description: string | null; url: string; headerName: string | null; headerSet: boolean; emailHeader: string | null; prefix: string } | null
}

interface Setting {
  enabled: boolean
  audience: Audience
  groups: string[]
  onByDefault: boolean
  askFirst: boolean
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
        description="What the chat's model may call, and for whom. Turn a tool off, give it to some groups only, have it ask before each call, or add an MCP server."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> Add MCP server
          </Button>
        }
      />
      <div className="grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
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
  return (
    <Card className={tool.setting.enabled ? '' : 'opacity-80'}>
      <CardHeader className="flex flex-row items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
          <Icon className="size-4.5" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <CardTitle className="flex flex-wrap items-center gap-2">
            {tool.title}
            <Badge variant={tool.server ? 'outline' : 'secondary'}>{tool.server ? 'MCP server' : 'Built in'}</Badge>
          </CardTitle>
          <CardDescription>{tool.description}</CardDescription>
        </div>
        <Switch checked={tool.setting.enabled} onCheckedChange={(enabled) => set({ enabled })} aria-label={`${tool.title} on`} />
      </CardHeader>
      <CardContent className="grid gap-4">
        {tool.unavailable && <Alert variant="warning">{tool.unavailable}</Alert>}
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
            <p className="text-muted-foreground">
              Its functions are named <span className="font-mono text-foreground">{tool.server.prefix}…</span>
            </p>
          </div>
        )}
        <AccessPicker value={tool.setting} onChange={(audience, groups) => set({ audience, groups })} />
        <div className="grid gap-2">
          <Label className="flex items-center justify-between gap-3 font-normal">
            <span>
              On in new chats
              <span className="block text-xs text-muted-foreground">People can still turn it on or off in each chat.</span>
            </span>
            <Switch checked={tool.setting.onByDefault} onCheckedChange={(onByDefault) => set({ onByDefault })} />
          </Label>
          <Label className="flex items-center justify-between gap-3 font-normal">
            <span>
              Ask before each call
              <span className="block text-xs text-muted-foreground">The chat shows what it wants to run and waits for Allow.</span>
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
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    description: saved?.description ?? '',
    url: saved?.url ?? '',
    headerName: saved?.headerName ?? '',
    headerValue: '',
    emailHeader: saved?.emailHeader ?? '',
  })
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<{ ok: boolean; error?: string; tools?: { name: string; description: string | null }[] } | null>(null)
  const body = { ...form, headerValue: form.headerValue || (saved ? null : '') }
  const check = useMutation({
    mutationFn: () => api<NonNullable<typeof test>>(`/api/admin/tools/servers/test${saved ? `?id=${saved.id}` : ''}`, { body }),
    onSuccess: setTest,
    onError: (e) => setTest({ ok: false, error: errorMessage(e) }),
  })
  const save = useMutation({
    mutationFn: () => (saved ? api(`/api/admin/tools/servers/${saved.id}`, { method: 'PATCH', body }) : api('/api/admin/tools/servers', { body })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] })
      toast.success(saved ? 'Server saved.' : 'Server added. Its tools are on for everyone: set who may use them on its card.')
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const field = (k: keyof typeof form) => ({ value: form[k], onChange: (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value }) })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add an MCP server'}</DialogTitle>
        <DialogDescription>A server that speaks MCP over HTTP (streamable HTTP). Its tools join the chat as one tool you can turn on for some people.</DialogDescription>
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
        <Field label="Name" hint="Shown to people in the chat's Tools menu.">
          <Input required maxLength={100} autoComplete="off" {...field('name')} />
        </Field>
        <Field label="Address">
          <Input required type="url" placeholder="https://tools.example.com/mcp" autoComplete="off" {...field('url')} />
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
        <Field label="Person's email header" hint="Optional, e.g. X-User-Email: for servers that answer as the person asking.">
          <Input autoComplete="off" {...field('emailHeader')} />
        </Field>
        {test &&
          (test.ok ? (
            <Alert variant="success" title={`Connected: ${test.tools?.length ?? 0} tools`}>
              <ul className="mt-1 grid gap-0.5 text-xs">
                {test.tools?.map((t) => (
                  <li key={t.name}>
                    <span className="font-mono">{t.name}</span>
                    {t.description && <span className="text-muted-foreground"> · {t.description}</span>}
                  </li>
                ))}
              </ul>
            </Alert>
          ) : (
            <Alert variant="destructive" title="Could not connect">
              {test.error}
            </Alert>
          ))}
        <DialogFooter className="sm:justify-between">
          <Button type="button" variant="outline" onClick={() => check.mutate()} loading={check.isPending} disabled={!form.url}>
            <PlugZap /> Test
          </Button>
          <span className="flex gap-2">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              {saved ? 'Save' : 'Add server'}
            </Button>
          </span>
        </DialogFooter>
      </form>
    </>
  )
}
