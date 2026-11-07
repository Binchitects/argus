import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plug, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { stateQuery } from './api'
import { api } from './http'

/** A provider of models beside Arena's, as Code Arena keeps it: never its key, only where the key comes from. */
export interface Provider {
  name: string
  kind: 'anthropic' | 'openai'
  url: string
  key: 'config' | 'env' | 'none'
  keyEnv: string | null
  models: string[]
}

export interface KnownProvider {
  name: string
  kind: 'anthropic' | 'openai'
  url: string
  keyEnv: string
  about: string
  keyInEnv: boolean
}

export interface ProvidersAnswer {
  allowed: boolean
  providers: Provider[]
  known: KnownProvider[]
  said?: string
}

const providersQuery = { queryKey: ['code', 'providers'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<ProvidersAnswer>('/api/providers', { signal }) }

/**
 * Other providers' models with the person's own key (Anthropic, OpenAI, DeepSeek, OpenRouter, Mistral, any
 * OpenAI-compatible API): added and removed here, as /providers does; their models join the model picker as
 * provider:model. Signed in to Arena, its admins decide whether they may be used.
 */
export function ProvidersButton() {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Tooltip content="Other providers' models, with your own key">
        <Button type="button" variant="ghost" size="sm" className="h-8 px-2" aria-label="Other providers" onClick={() => setOpen(true)}>
          <Plug />
        </Button>
      </Tooltip>
      {open && <ProvidersDialog onClose={() => setOpen(false)} />}
    </>
  )
}

export function ProvidersDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const query = useQuery(providersQuery)
  const [choice, setChoice] = useState('anthropic')
  const [own, setOwn] = useState('')
  const name = choice || own
  const [url, setUrl] = useState('')
  const [kind, setKind] = useState<'openai' | 'anthropic'>('openai')
  const [key, setKey] = useState('')
  const known = query.data?.known.find((k) => k.name === choice)
  const done = async (answer: ProvidersAnswer) => {
    queryClient.setQueryData(providersQuery.queryKey, answer)
    await queryClient.invalidateQueries({ queryKey: stateQuery.queryKey })
  }
  const add = useMutation({
    mutationFn: () =>
      api<ProvidersAnswer>('/api/providers', {
        body: { name, ...(known ? {} : { url, kind }), ...(key ? { key } : {}) },
      }),
    onSuccess: async (answer) => {
      setKey('')
      await done(answer)
      if (answer.said) toast.success(answer.said)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (gone: string) => api<ProvidersAnswer>('/api/providers/remove', { body: { name: gone } }),
    onSuccess: done,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const data = query.data
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Other providers</DialogTitle>
          <DialogDescription>
            Models of Anthropic, OpenAI, DeepSeek, OpenRouter, Mistral or any OpenAI-compatible API, with your own key, beside Arena&apos;s. They show in the model picker as
            provider:model. What a session sends them goes to them, not through Arena.
          </DialogDescription>
        </DialogHeader>
        {query.isError && (
          <p role="alert" className="text-sm text-destructive">
            {errorMessage(query.error)}
          </p>
        )}
        {data && !data.allowed && (
          <p role="status" className="rounded-md border border-border bg-muted px-3 py-2 text-sm">
            Arena&apos;s admins do not let Code Arena use other providers (Admin → Settings → Chat → Code Arena may use other providers).
          </p>
        )}
        {data && data.providers.length > 0 && (
          <ul className="divide-y rounded-md border" aria-label="Your providers">
            {data.providers.map((p) => (
              <li key={p.name} className="flex items-start gap-3 px-3 py-2">
                <div className="grid min-w-0 flex-1 gap-0.5">
                  <span className="text-sm font-medium">{p.name}</span>
                  <span className="truncate text-xs text-muted-foreground">
                    {p.kind === 'anthropic' ? 'Messages API' : 'Chat completions'} · {p.url} ·{' '}
                    {p.key === 'config' ? 'key kept here' : p.key === 'env' ? `key from ${p.keyEnv}` : <span className="text-destructive-ink">no key</span>}
                  </span>
                  <span className="text-xs text-muted-foreground">{p.models.length === 0 ? 'No models listed' : `${p.models.length} model${p.models.length === 1 ? '' : 's'}: ${p.models.slice(0, 4).join(', ')}${p.models.length > 4 ? ', …' : ''}`}</span>
                </div>
                <Button type="button" variant="ghost" size="sm" className="h-8 px-2" aria-label={`Remove ${p.name}`} disabled={remove.isPending} onClick={() => remove.mutate(p.name)}>
                  <Trash2 />
                </Button>
              </li>
            ))}
          </ul>
        )}
        {data?.allowed && (
          <form
            className="grid gap-3"
            aria-label="Add a provider"
            onSubmit={(e) => {
              e.preventDefault()
              add.mutate()
            }}
          >
            <label className="grid gap-1 text-sm">
              Provider
              <select className="h-9 rounded-md border border-input bg-background px-2 text-sm" value={choice} onChange={(e) => setChoice(e.target.value)}>
                {data.known.map((k) => (
                  <option key={k.name} value={k.name}>
                    {k.about}
                  </option>
                ))}
                <option value="">Another API (OpenAI-compatible, or Anthropic&apos;s), by its address</option>
              </select>
            </label>
            {!known && (
              <>
                <label className="grid gap-1 text-sm">
                  Name (its models are name:model)
                  <Input value={own} onChange={(e) => setOwn(e.target.value.toLowerCase())} placeholder="local" required pattern="[a-z0-9][a-z0-9-]*" />
                </label>
                <label className="grid gap-1 text-sm">
                  Address
                  <Input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="http://gpu-server:8000" required type="url" />
                </label>
                <label className="grid gap-1 text-sm">
                  It speaks
                  <select className="h-9 rounded-md border border-input bg-background px-2 text-sm" value={kind} onChange={(e) => setKind(e.target.value as 'openai' | 'anthropic')}>
                    <option value="openai">OpenAI&apos;s chat completions</option>
                    <option value="anthropic">Anthropic&apos;s Messages API</option>
                  </select>
                </label>
              </>
            )}
            <label className="grid gap-1 text-sm">
              API key
              <Input
                type="password"
                autoComplete="off"
                value={key}
                onChange={(e) => setKey(e.target.value)}
                placeholder={known?.keyInEnv ? `From ${known.keyEnv} (leave empty)` : 'Kept in config.json, readable by you only'}
              />
            </label>
            <div className="flex justify-end">
              <Button type="submit" disabled={add.isPending || (!known && (!name || !url))}>
                {add.isPending ? 'Adding…' : 'Add and list its models'}
              </Button>
            </div>
          </form>
        )}
      </DialogContent>
    </Dialog>
  )
}
