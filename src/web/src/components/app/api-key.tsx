import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, RefreshCw } from 'lucide-react'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { money, when } from '@/lib/format'
import { cn } from '@/lib/utils'

interface Keys {
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  spend: number
  budget: number | null
}

/** The person's API key: its spend against their credit, and a new one on demand (shown once, and handed to onNewKey). */
export function ApiKey({ onNewKey }: { onNewKey?: (key: string) => void } = {}) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const keys = useQuery({ queryKey: ['account', 'keys'], queryFn: () => api<Keys>('/api/account/keys') })
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>('/api/account/keys/rotate', { body: {} }),
    onSuccess: (made) => {
      onNewKey?.(made.apiKey)
      return queryClient.invalidateQueries({ queryKey: ['account', 'keys'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const d = keys.data
  const used = d && d.budget ? Math.min(1, d.spend / d.budget) : null
  return (
    <Card>
      <CardHeader>
        <CardTitle>API key</CardTitle>
        <CardDescription>For your tools: coding agents, editors, scripts. It spends from your credit.</CardDescription>
        <CardAction>
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
        </CardAction>
      </CardHeader>
      <CardContent className="grid gap-4">
        {keys.isPending && <Skeleton className="h-16" />}
        {keys.error && <Alert variant="destructive">{errorMessage(keys.error)}</Alert>}
        {d && (
          <div className="grid gap-2">
            <div className="flex items-baseline justify-between text-sm">
              <span className="text-muted-foreground">Credit used</span>
              <span className="font-medium tabular-nums">
                {money(d.spend)} <span className="text-muted-foreground">of {d.budget === null ? 'no limit' : money(d.budget)}</span>
              </span>
            </div>
            {used !== null && (
              // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a styled bar; <meter> cannot be styled consistently
              <div className="h-2 overflow-hidden rounded-full bg-muted" role="meter" aria-label="Credit used" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(used * 100)}>
                <div className={cn('h-full rounded-full', used >= 0.9 ? 'bg-destructive' : used >= 0.75 ? 'bg-warning' : 'bg-primary')} style={{ width: `${used * 100}%` }} />
              </div>
            )}
          </div>
        )}
        {d && (
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
