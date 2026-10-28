import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Plus, SlidersHorizontal, Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { money, when } from '@/lib/format'

/** A key's limits: requests and tokens a minute, requests at once, dollars in 30 days; null: none. */
export interface KeyLimits {
  requestsPerMinute: number | null
  tokensPerMinute: number | null
  parallel: number | null
  monthlyBudget: number | null
}

export interface OwnKey {
  id: string
  name: string
  /** own: made here; main: the one New key replaces; studio: a Code Studio workspace's; other. */
  kind: 'own' | 'main' | 'studio' | 'other'
  preview: string | null
  spend: number
  blocked: boolean
  createdAt: string | null
  /** The key's own limits (null for one not made here and never given any). */
  limits: KeyLimits | null
  /** What the gateway holds it to: the tighter of yours and its own. */
  effective: KeyLimits
}

const ownKeysKey = ['account', 'keys', 'own'] as const

/** "60 a minute · 5,000 tokens a minute · 2 at once · $5.00 in 30 days", or "no limits". */
function limitsText(l: KeyLimits): string {
  const parts = [
    l.requestsPerMinute !== null && `${l.requestsPerMinute.toLocaleString()} requests a minute`,
    l.tokensPerMinute !== null && `${l.tokensPerMinute.toLocaleString()} tokens a minute`,
    l.parallel !== null && `${l.parallel} at once`,
    l.monthlyBudget !== null && `${money(l.monthlyBudget)} in 30 days`,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : 'no limits'
}

const number = (s: string) => (s.trim() === '' ? null : Number(s))

/** The form for a key's name and limits (each empty: as many as you may). */
function KeyDialog({ title, start, onClose, onSave, saving }: { title: string; start?: OwnKey; onClose: () => void; onSave: (body: { name: string } & KeyLimits) => void; saving: boolean }) {
  const own = start?.limits
  const [form, setForm] = useState({
    name: start?.name ?? '',
    requestsPerMinute: own?.requestsPerMinute?.toString() ?? '',
    tokensPerMinute: own?.tokensPerMinute?.toString() ?? '',
    parallel: own?.parallel?.toString() ?? '',
    monthlyBudget: own?.monthlyBudget?.toString() ?? '',
  })
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    onSave({
      name: form.name.trim(),
      requestsPerMinute: number(form.requestsPerMinute),
      tokensPerMinute: number(form.tokensPerMinute),
      parallel: number(form.parallel),
      monthlyBudget: number(form.monthlyBudget),
    })
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <form onSubmit={submit} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
            <DialogDescription>Its limits hold under yours: the gateway holds it to the tighter of the two. Leave a limit empty for none of its own.</DialogDescription>
          </DialogHeader>
          <Field label="Name" hint="What uses it: CI, laptop, a script.">
            <Input value={form.name} onChange={set('name')} maxLength={80} required autoFocus />
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Requests a minute">
              <Input type="number" min={1} inputMode="numeric" value={form.requestsPerMinute} onChange={set('requestsPerMinute')} />
            </Field>
            <Field label="Tokens a minute">
              <Input type="number" min={1} inputMode="numeric" value={form.tokensPerMinute} onChange={set('tokensPerMinute')} />
            </Field>
            <Field label="Requests at once" hint="More wait their turn.">
              <Input type="number" min={1} max={1000} inputMode="numeric" value={form.parallel} onChange={set('parallel')} />
            </Field>
            <Field label="Budget in 30 days ($)" hint="Past it the key is refused until the 30 days end.">
              <Input type="number" min={0.01} step={0.01} inputMode="decimal" value={form.monthlyBudget} onChange={set('monthlyBudget')} />
            </Field>
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={saving}>
              Save
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Account → API keys: keys of your own, each named and limited, each revoked on its own. */
export function OwnKeys() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery({ queryKey: ownKeysKey, queryFn: () => api<{ keys: OwnKey[]; most: number }>('/api/account/keys/own') })
  const [editing, setEditing] = useState<OwnKey | 'new' | null>(null)
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: ownKeysKey }), queryClient.invalidateQueries({ queryKey: ['account', 'keys'] })])
  const make = useMutation({
    mutationFn: (body: { name: string } & KeyLimits) => api<{ apiKey: string; key: OwnKey }>('/api/account/keys/own', { body }),
    onSuccess: async () => {
      setEditing(null)
      await refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const change = useMutation({
    mutationFn: ({ id, body }: { id: string; body: { name: string } & KeyLimits }) => api<OwnKey>(`/api/account/keys/own/${id}`, { method: 'PUT', body }),
    onSuccess: async (k) => {
      setEditing(null)
      toast.success(`${k.name}: ${limitsText(k.effective)}`)
      await refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const revoke = useMutation({
    mutationFn: (k: OwnKey) => api(`/api/account/keys/own/${k.id}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const keys = list.data?.keys ?? []
  return (
    <Card>
      <CardHeader>
        <CardTitle>API keys</CardTitle>
        <CardDescription>A key for each tool or machine, named, with limits of its own under yours. Revoke one and the others keep working.</CardDescription>
        <CardAction>
          <Button variant="outline" size="sm" onClick={() => setEditing('new')} disabled={!!list.data && keys.filter((k) => k.kind === 'own').length >= list.data.most}>
            <Plus /> Add key
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent className="grid gap-3">
        {list.isPending && <Skeleton className="h-16" />}
        {list.error && <Alert variant="destructive">{errorMessage(list.error)}</Alert>}
        {list.data && keys.length === 0 && <p className="text-sm text-muted-foreground">No key yet.</p>}
        <ul className="grid gap-2" aria-label="Your API keys">
          {keys.map((k) => (
            <li key={k.id} className="grid gap-1 rounded-lg border px-3 py-2">
              <div className="flex min-w-0 items-center gap-2">
                <KeyRound className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span className="truncate text-sm font-medium">{k.name}</span>
                {k.kind === 'main' && <Badge variant="secondary">main</Badge>}
                {k.kind === 'studio' && <Badge variant="secondary">Code Studio</Badge>}
                {k.blocked && <Badge variant="destructive">Blocked</Badge>}
                <span className="ml-auto flex shrink-0 items-center gap-1">
                  {k.kind !== 'studio' && (
                    <Tooltip content="Name and limits">
                      <Button variant="ghost" size="icon-sm" aria-label={`${k.name}: name and limits`} onClick={() => setEditing(k)}>
                        <SlidersHorizontal />
                      </Button>
                    </Tooltip>
                  )}
                  {k.kind !== 'studio' && (
                    <Tooltip content="Revoke">
                      <Button
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Revoke ${k.name}`}
                        onClick={async () => {
                          if (await confirm({ title: `Revoke ${k.name}?`, description: 'It stops working at once; your other keys keep working.', confirm: 'Revoke', destructive: true })) revoke.mutate(k)
                        }}
                      >
                        <Trash2 />
                      </Button>
                    </Tooltip>
                  )}
                </span>
              </div>
              <p className="text-xs text-muted-foreground">
                {k.preview && <code className="mr-2 font-mono">{k.preview}</code>}
                {money(k.spend)} spent · {limitsText(k.effective)} · made {when(k.createdAt)}
              </p>
            </li>
          ))}
        </ul>
        {make.data && (
          <Alert variant="success" title={`${make.data.key.name}: the key, shown only this once`}>
            <div className="mt-2 grid gap-2">
              <Secret label="API key" value={make.data.apiKey} />
              <p>Store it in the tool's settings now.</p>
            </div>
          </Alert>
        )}
      </CardContent>
      {editing === 'new' && <KeyDialog title="Add an API key" onClose={() => setEditing(null)} onSave={(body) => make.mutate(body)} saving={make.isPending} />}
      {editing && editing !== 'new' && (
        <KeyDialog title={`${editing.name}: name and limits`} start={editing} onClose={() => setEditing(null)} onSave={(body) => change.mutate({ id: editing.id, body })} saving={change.isPending} />
      )}
    </Card>
  )
}
