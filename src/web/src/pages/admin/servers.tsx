import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleDot, Pencil, Plus, Search, Server, Trash2, XCircle } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'

export interface RemoteModel {
  remote: string
  name: string
  context: number | null
  maxOutput: number | null
  vision: boolean
  tools: boolean
  thinking: boolean
  /** Requests it serves at once (its parallel slots); null: not said. */
  parallel?: number | null
  /** Its prices in dollars per million tokens; null: the default (Settings → Prices). */
  inputPerMtok?: number | null
  cachedInputPerMtok?: number | null
  outputPerMtok?: number | null
  /** The server lists it (false: it no longer has it, and it cannot answer). */
  listed?: boolean
}

export interface RemoteServer {
  id: string
  name: string
  baseUrl: string
  keySet: boolean
  verifyTls: boolean
  status: { up: boolean; error: string | null; checkedAt: string; offers: { id: string; context: number | null }[] }
  models: RemoteModel[]
}

const serversQuery = { queryKey: ['admin', 'servers'], queryFn: ({ signal }: { signal: AbortSignal }) => api<RemoteServer[]>('/api/admin/servers', { signal }) }

/**
 * Other machines' OpenAI-compatible engines (llama.cpp, vLLM, SGLang, another gateway):
 * the gateway serves the models chosen from them beside this machine's. A model named
 * like one here is a second copy of it, and the gateway spreads requests between them.
 */
export function ServersSection({ onChanged }: { onChanged: () => void }) {
  const servers = useQuery({ ...serversQuery, refetchInterval: 30_000 })
  const [editing, setEditing] = useState<RemoteServer | 'new' | null>(null)
  return (
    <Card className="mb-4">
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <CardTitle className="text-base">Other GPU servers</CardTitle>
          <CardDescription>
            Another machine's OpenAI-compatible engine: the gateway serves the models you choose from it beside this machine's, to the chat, API keys and agents. A model named like one here is a
            second copy of it: each request goes to the least busy copy, never past the requests a copy serves at once.
          </CardDescription>
        </div>
        <Button size="sm" onClick={() => setEditing('new')}>
          <Plus /> Add a server
        </Button>
      </CardHeader>
      {servers.error && (
        <CardContent>
          <Alert variant="destructive">{errorMessage(servers.error)}</Alert>
        </CardContent>
      )}
      {(servers.data?.length ?? 0) > 0 && (
        <CardContent className="grid gap-3">
          {servers.data!.map((s) => (
            <ServerRow key={s.id} server={s} onEdit={() => setEditing(s)} onChanged={onChanged} />
          ))}
        </CardContent>
      )}
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">
          {editing !== null && (
            <ServerForm
              key={editing === 'new' ? 'new' : editing.id}
              saved={editing === 'new' ? null : editing}
              onDone={() => {
                setEditing(null)
                onChanged()
              }}
            />
          )}
        </DialogContent>
      </Dialog>
    </Card>
  )
}

function ServerRow({ server: s, onEdit, onChanged }: { server: RemoteServer; onEdit: () => void; onChanged: () => void }) {
  const confirm = useConfirm()
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/servers/${s.id}`, { method: 'DELETE' }),
    onSuccess: () => {
      toast.success(`${s.name} removed`, { description: 'Its models left the gateway.' })
      queryClient.invalidateQueries({ queryKey: ['admin', 'servers'] })
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <section aria-label={s.name} className="grid gap-2 rounded-lg border p-3">
      <div className="flex flex-wrap items-start gap-2">
        <Server className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <div className="grid min-w-0 flex-1">
          <span className="font-medium [overflow-wrap:anywhere]">{s.name}</span>
          <span className="font-mono text-xs text-muted-foreground [overflow-wrap:anywhere]">{s.baseUrl}</span>
        </div>
        {s.status.up ? (
          <span className="flex items-center gap-1.5 text-sm font-medium text-success-ink">
            <CircleDot className="size-4" aria-hidden="true" /> Answers
          </span>
        ) : (
          <span className="flex items-center gap-1.5 text-sm font-medium text-destructive-ink">
            <XCircle className="size-4" aria-hidden="true" /> Does not answer
          </span>
        )}
      </div>
      {!s.status.up && s.status.error && <Alert variant="destructive">{s.status.error}</Alert>}
      <ul className="flex flex-wrap gap-1.5">
        {s.models.map((m) => (
          <li key={m.name}>
            <Badge variant={m.listed === false ? 'warning' : 'secondary'} title={m.listed === false ? 'The server no longer lists it' : undefined}>
              {m.name}
              {m.name !== m.remote ? ` ← ${m.remote}` : ''}
              {m.listed === false ? ' · not listed' : ''}
            </Badge>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" onClick={onEdit}>
          <Pencil /> Edit
        </Button>
        <Button
          size="sm"
          variant="outline"
          className="text-destructive-ink"
          loading={remove.isPending}
          onClick={async () => {
            if (await confirm({ title: `Remove ${s.name}?`, description: 'Its models leave the gateway: chats and keys that use them fall back or fail.', confirm: 'Remove', destructive: true }))
              remove.mutate()
          }}
        >
          <Trash2 /> Remove
        </Button>
      </div>
    </section>
  )
}

const priceKeys = ['inputPerMtok', 'cachedInputPerMtok', 'outputPerMtok'] as const

interface Pick extends RemoteModel {
  on: boolean
  /** The prices as typed ("" for the default). */
  prices: Record<(typeof priceKeys)[number], string>
}

const typed = (m: RemoteModel): Pick['prices'] => ({
  inputPerMtok: m.inputPerMtok?.toString() ?? '',
  cachedInputPerMtok: m.cachedInputPerMtok?.toString() ?? '',
  outputPerMtok: m.outputPerMtok?.toString() ?? '',
})

/** What is wrong with a typed price, if anything (empty is the default). The API checks the same, with the default input too. */
function priceError(prices: Pick['prices'], k: (typeof priceKeys)[number]): string | undefined {
  const text = prices[k].trim()
  if (text === '') return undefined
  const n = Number(text)
  if (!Number.isFinite(n)) return 'Enter a number.'
  if (n < 0) return 'Prices cannot be negative.'
  const input = Number(prices.inputPerMtok)
  if (k === 'cachedInputPerMtok' && prices.inputPerMtok.trim() !== '' && Number.isFinite(input) && n > input) return 'Never above input.'
  return undefined
}

/** A typed price: empty for the default, else the number (one with an error blocks the save). */
const price = (text: string) => (text.trim() === '' ? null : Number(text))

function ServerForm({ saved, onDone }: { saved: RemoteServer | null; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(saved?.name ?? '')
  const [baseUrl, setBaseUrl] = useState(saved?.baseUrl ?? '')
  const [apiKey, setApiKey] = useState('')
  const [verifyTls, setVerifyTls] = useState(saved?.verifyTls ?? true)
  const [picks, setPicks] = useState<Pick[]>(() => (saved?.models ?? []).map((m) => ({ ...m, on: true, prices: typed(m) })))
  const probe = useMutation({
    mutationFn: () =>
      api<{ models: { id: string; context: number | null }[] }>('/api/admin/servers/probe', { body: { baseUrl, apiKey: apiKey || null, verifyTls, id: saved?.id ?? null } }),
    onSuccess: (r) =>
      setPicks((now) => [
        ...now,
        ...r.models
          .filter((o) => !now.some((p) => p.remote === o.id))
          .map((o) => ({
            remote: o.id, name: o.id.split('/').pop()!.replace(/[^A-Za-z0-9._:-]/g, '-'), context: o.context, maxOutput: null, vision: false, tools: true, thinking: false, parallel: null,
            on: false, prices: { inputPerMtok: '', cachedInputPerMtok: '', outputPerMtok: '' },
          })),
      ]),
  })
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name,
        baseUrl,
        verifyTls,
        ...(apiKey || !saved ? { apiKey } : {}),
        models: picks
          .filter((p) => p.on)
          .map((p) => ({
            remote: p.remote, name: p.name, context: p.context, maxOutput: p.maxOutput, vision: p.vision, tools: p.tools, thinking: p.thinking, parallel: p.parallel ?? null,
            inputPerMtok: price(p.prices.inputPerMtok), cachedInputPerMtok: price(p.prices.cachedInputPerMtok), outputPerMtok: price(p.prices.outputPerMtok),
          })),
      }
      return saved ? api<{ warning: string | null }>(`/api/admin/servers/${saved.id}`, { method: 'PATCH', body }) : api<{ warning: string | null }>('/api/admin/servers', { body })
    },
    onSuccess: (r) => {
      if (r?.warning) toast.warning(`${name} saved`, { description: r.warning })
      else toast.success(saved ? `${name} saved` : `${name} added`, { description: 'Its models are at the gateway now.' })
      queryClient.invalidateQueries({ queryKey: ['admin', 'servers'] })
      onDone()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const set = (i: number, change: Partial<Pick>) => setPicks((now) => now.map((p, j) => (j === i ? { ...p, ...change } : p)))
  const chosen = picks.filter((p) => p.on).length
  const invalid = picks.some((p) => p.on && priceKeys.some((k) => priceError(p.prices, k)))
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add a server'}</DialogTitle>
        <DialogDescription>Its OpenAI-compatible API: llama.cpp, vLLM, SGLang, or another gateway. Find its models, then choose which the gateway serves, and under which names.</DialogDescription>
      </DialogHeader>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="GPU box 2" required />
        </Field>
        <Field label="Address" hint="Usually ending in /v1, e.g. http://10.0.0.5:8000/v1">
          <Input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="http://10.0.0.5:8000/v1" inputMode="url" required />
        </Field>
        <Field label="Its API key" hint={saved?.keySet ? 'Kept encrypted. Leave empty to keep it.' : 'If it takes one. Kept encrypted, never shown again.'}>
          <Input type="password" autoComplete="off" value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
        </Field>
        <Label className="flex items-start gap-3 self-end pb-2 font-normal">
          <Switch checked={verifyTls} onCheckedChange={setVerifyTls} />
          <span className="grid gap-0.5">
            <span className="font-medium">Check its certificate</span>
            <span className="text-xs text-muted-foreground">For https. A private CA goes in config/ca. Off only for a self-signed server with no CA to trust.</span>
          </span>
        </Label>
      </div>
      <Button type="button" variant="outline" className="w-fit" loading={probe.isPending} disabled={!baseUrl} onClick={() => probe.mutate()}>
        <Search /> Find its models
      </Button>
      {probe.error && <Alert variant="destructive">{errorMessage(probe.error)}</Alert>}
      {picks.length > 0 && (
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">Its models ({chosen} chosen)</legend>
          {picks.map((p, i) => (
            <div key={p.remote} className={cn('grid gap-2 rounded-lg border p-3', !p.on && 'border-dashed')}>
              <Label className="flex items-center gap-2 font-normal">
                <Checkbox checked={p.on} onCheckedChange={(on) => set(i, { on: on === true })} aria-label={`Serve ${p.remote}`} />
                <span className="font-mono text-sm [overflow-wrap:anywhere]">{p.remote}</span>
              </Label>
              {p.on && (
                <div className="grid gap-3 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)]">
                  <Field label="Name at the gateway">
                    <Input value={p.name} onChange={(e) => set(i, { name: e.target.value })} aria-label={`Name at the gateway for ${p.remote}`} />
                  </Field>
                  <Field label="Context (tokens)">
                    <Input inputMode="numeric" value={p.context ?? ''} onChange={(e) => set(i, { context: e.target.value ? Number(e.target.value) : null })} aria-label={`Context for ${p.remote}`} />
                  </Field>
                  <Field label="Longest answer">
                    <Input inputMode="numeric" value={p.maxOutput ?? ''} onChange={(e) => set(i, { maxOutput: e.target.value ? Number(e.target.value) : null })} aria-label={`Longest answer for ${p.remote}`} />
                  </Field>
                  <Field label="At once" hint="Its parallel slots">
                    <Input inputMode="numeric" value={p.parallel ?? ''} onChange={(e) => set(i, { parallel: e.target.value ? Number(e.target.value) : null })} aria-label={`Requests at once for ${p.remote}`} />
                  </Field>
                  {priceKeys.map((k) => {
                    const label = k === 'inputPerMtok' ? 'Input, $ per 1M tokens' : k === 'cachedInputPerMtok' ? 'Cached input, $ per 1M' : 'Output, $ per 1M tokens'
                    return (
                      <Field key={k} label={label} error={priceError(p.prices, k)} hint={k === 'inputPerMtok' ? 'Empty: the default (Settings → Prices)' : undefined}>
                        <Input
                          inputMode="decimal"
                          value={p.prices[k]}
                          placeholder="the default"
                          onChange={(e) => set(i, { prices: { ...p.prices, [k]: e.target.value } })}
                          aria-label={`${label} for ${p.remote}`}
                        />
                      </Field>
                    )
                  })}
                  <div className="flex flex-wrap gap-4 sm:col-span-4">
                    {(['tools', 'thinking', 'vision'] as const).map((k) => (
                      <Label key={k} className="flex items-center gap-2 font-normal">
                        <Switch checked={p[k]} onCheckedChange={(v) => set(i, { [k]: v })} aria-label={`${p.remote}: ${k}`} />
                        {k === 'tools' ? 'Uses tools' : k === 'thinking' ? 'Thinks' : 'Sees images'}
                      </Label>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ))}
        </fieldset>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={chosen === 0 || !name || !baseUrl || invalid}>
          {saved ? 'Save' : 'Add server'}
        </Button>
      </DialogFooter>
    </form>
  )
}
