import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, RefreshCw } from 'lucide-react'
import { LimitRows } from '@/components/app/rate-limits'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { money, when } from '@/lib/format'
import type { KeyLimits } from '@/lib/rate-limits'
import { cn } from '@/lib/utils'

interface Keys {
  /** An admin took their API access away: no key is shown or made. */
  apiOff: boolean
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  /** This month: their API keys' text requests, and the credit for them (null: no limit of their own). */
  apiSpend: number
  apiCredit: number | null
  /** Kind by kind, with the tightest of their groups' credits; null when the request log cannot be read. */
  standing: { kind: string; group: string | null; groupLeft: number | null }[] | null
}

/** The answer cache for the key: off, each person's choice (opt-in), or every key (all); and how long answers are kept. */
interface AnswerCache {
  mode: 'off' | 'opt-in' | 'all'
  chosen: boolean
  on: boolean
  ttlHours: number
}

const hours = (h: number) => (h % 24 === 0 ? `${h / 24} day${h === 24 ? '' : 's'}` : `${h} hour${h === 1 ? '' : 's'}`)

/** Repeated identical requests with the key answered from the cache: the person's switch when an admin lets them choose. */
function CacheChoice() {
  const queryClient = useQueryClient()
  const cache = useQuery({ queryKey: ['account', 'answer-cache'], queryFn: () => api<AnswerCache>('/api/account/answer-cache'), retry: false })
  const choose = useMutation({
    mutationFn: (on: boolean) => api<AnswerCache>('/api/account/answer-cache', { method: 'PUT', body: { on } }),
    onSuccess: (v) => {
      queryClient.setQueryData(['account', 'answer-cache'], v)
      toast.success(v.on ? 'Repeated requests come from the cache' : 'Every request goes to the model', {
        description: v.on ? `For ${hours(v.ttlHours)}, at no cost.` : 'What was kept for your key is gone.',
      })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const c = cache.data
  if (!c || c.mode === 'off') return null
  const explain = `The same request again with your key, within ${hours(c.ttlHours)}, is answered from the cache: no cost, and the response says x-arena-cache: hit. For scripts and FAQ bots that ask the same thing.`
  if (c.mode === 'all') return <p className="rounded-lg border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">{explain} An admin turned it on for every key.</p>
  return (
    <div className="flex items-start justify-between gap-3 rounded-lg border px-3 py-2">
      <div className="grid gap-0.5">
        <span id="answer-cache-label" className="text-sm font-medium">
          Answer repeated requests from the cache
        </span>
        <span className="text-xs text-muted-foreground">{explain} Leave it off for tools that must always ask the model.</span>
      </div>
      <Switch checked={c.chosen} disabled={choose.isPending} onCheckedChange={(on) => choose.mutate(on)} aria-labelledby="answer-cache-label" />
    </div>
  )
}

/** The person's API key: its spend against their credit, and a new one on demand (shown once, and handed to onNewKey). */
export function ApiKey({ onNewKey }: { onNewKey?: (key: string) => void } = {}) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const keys = useQuery({ queryKey: ['account', 'keys'], queryFn: () => api<Keys>('/api/account/keys') })
  // Its own request: it reads the gateway's request log, which the home page's credit card does not need.
  const limits = useQuery({ queryKey: ['account', 'keys', 'limits'], queryFn: () => api<KeyLimits>('/api/account/keys/limits') })
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>('/api/account/keys/rotate', { body: {} }),
    onSuccess: (made) => {
      onNewKey?.(made.apiKey)
      return queryClient.invalidateQueries({ queryKey: ['account', 'keys'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const d = keys.data
  const used = d && d.apiCredit ? Math.min(1, d.apiSpend / d.apiCredit) : null
  return (
    <Card>
      <CardHeader>
        <CardTitle>API key</CardTitle>
        <CardDescription>For your tools: coding agents, editors, scripts. Their text requests spend from your API credit; pictures, video and speech from theirs.</CardDescription>
        {!d?.apiOff && <CardAction>
          <Button
            variant="outline"
            size="sm"
            loading={rotate.isPending}
            onClick={async () => {
              if (await confirm({ title: 'Make a new API key?', description: 'The current key stops working at once. Tools that use it need the new one.', confirm: 'Make a new key', destructive: true }))
                rotate.mutate()
            }}
          >
            <RefreshCw /> New key
          </Button>
        </CardAction>}
      </CardHeader>
      <CardContent className="grid gap-4">
        {keys.isPending && <Skeleton className="h-16" />}
        {keys.error && <Alert variant="destructive">{errorMessage(keys.error)}</Alert>}
        {d?.apiOff && <Alert variant="warning" title="Your API access is off">An admin turned it off: your keys do not work, and you cannot make one. Ask them to turn it on.</Alert>}
        {d && !d.apiOff && (
          <div className="grid gap-2">
            <div className="flex items-baseline justify-between text-sm">
              <span className="text-muted-foreground">API credit used this month</span>
              <span className="font-medium tabular-nums">
                {money(d.apiSpend)} <span className="text-muted-foreground">of {d.apiCredit === null ? 'no limit of your own' : money(d.apiCredit)}</span>
              </span>
            </div>
            {d.standing?.find((x) => x.kind === 'api' && x.group) && (
              <p className="text-xs text-muted-foreground">
                {(() => {
                  const g = d.standing!.find((x) => x.kind === 'api' && x.group)!
                  return `${g.group}'s API credit: ${money(g.groupLeft ?? 0)} left this month.`
                })()}
              </p>
            )}
            {used !== null && (
              // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a styled bar; <meter> cannot be styled consistently
              <div className="h-2 overflow-hidden rounded-full bg-muted" role="meter" aria-label="Credit used" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(used * 100)}>
                <div className={cn('h-full rounded-full', used >= 0.9 ? 'bg-destructive' : used >= 0.75 ? 'bg-warning' : 'bg-primary')} style={{ width: `${used * 100}%` }} />
              </div>
            )}
          </div>
        )}
        {d && !d.apiOff && (
          <ul className="grid gap-2">
            {d.keys.length === 0 && <li className="text-sm text-muted-foreground">No key yet. Make one to use the API.</li>}
            {d.keys.map((k) => (
              <li key={k.alias + k.preview} className="flex items-center gap-3 rounded-lg border px-3 py-2">
                <KeyRound className="size-4 text-muted-foreground" aria-hidden="true" />
                <code className="font-mono text-[0.8125rem]">{k.preview ?? k.alias}</code>
                {k.blocked && <Badge variant="destructive">Blocked</Badge>}
                <span className="ml-auto text-xs text-muted-foreground">created {when(k.createdAt)}</span>
              </li>
            ))}
          </ul>
        )}
        {limits.data && d && d.keys.length > 0 && (
          <section aria-labelledby="key-limits" className="grid gap-2 border-t pt-4">
            <h3 id="key-limits" className="text-sm font-medium">
              Rate limits
            </h3>
            <LimitRows limits={limits.data} you />
            <p className="text-xs text-muted-foreground">
              For each key, counted by the gateway; used is the last minute. Past a limit a request is refused with HTTP 429 and Retry-After: wait that long and send it again. The chat is not limited by these.
            </p>
          </section>
        )}
        <CacheChoice />
        {rotate.data && (
          <Alert variant="success" title="Your new key — shown only this once">
            <div className="mt-2 grid gap-2">
              <Secret label="API key" value={rotate.data.apiKey} />
              <p>Store it in your tool's settings now. The old key has stopped working.</p>
            </div>
          </Alert>
        )}
      </CardContent>
    </Card>
  )
}
