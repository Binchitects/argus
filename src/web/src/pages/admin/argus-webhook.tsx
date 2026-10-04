import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw, Webhook } from 'lucide-react'
import { useState } from 'react'
import { CodeBlock } from '@/components/app/code-block'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds } from '@/lib/format'

export interface WebhookView {
  enabled: boolean
  fromEnv: boolean
  url: string
  header: string
  deliveries: { at: number; event: string; repo: string | null; outcome: string }[]
  /** Only in the answer that made it: the secret is shown once. */
  token?: string
}

const key = ['admin', 'argus', 'webhook']

/** GitLab tells Argus when a branch changes (a push, a merged merge request); that repository is updated at once. */
export function WebhookCard() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const view = useQuery({ queryKey: key, queryFn: ({ signal }) => api<WebhookView>('/api/admin/argus/webhook', { signal }), refetchInterval: 30_000 })
  const [secret, setSecret] = useState<string | null>(null)
  const done = (v: WebhookView) => {
    queryClient.setQueryData(key, v)
    void queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'status'] })
  }
  const rotate = useMutation({
    mutationFn: () => api<WebhookView>('/api/admin/argus/webhook', { body: {} }),
    onSuccess: (v) => {
      setSecret(v.token ?? null)
      done(v)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const off = useMutation({
    mutationFn: () => api<WebhookView>('/api/admin/argus/webhook', { method: 'DELETE' }),
    onSuccess: (v) => {
      setSecret(null)
      done(v)
      toast.success('Webhook turned off', { description: 'Only the schedule and Index now update the index.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const d = view.data
  if (!d) return view.error ? <Alert variant="destructive">{errorMessage(view.error)}</Alert> : null
  return (
    <Card aria-label="Push and merge webhook">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Push and merge webhook {d.enabled ? <Badge variant="success">On</Badge> : <Badge variant="secondary">Off</Badge>}
        </CardTitle>
        <CardDescription>GitLab tells Argus when a branch changes: a push, or a merge request merged. Argus updates that repository at once instead of waiting for the schedule.</CardDescription>
        {!d.fromEnv && (
          <CardAction className="flex gap-2">
            {d.enabled && (
              <Button
                variant="outline"
                size="sm"
                loading={off.isPending}
                onClick={async () => {
                  if (await confirm({ title: 'Turn the webhook off?', description: 'GitLab deliveries are refused from now on. Remove the webhook in GitLab too.', confirm: 'Turn off', destructive: true }))
                    off.mutate()
                }}
              >
                Turn off
              </Button>
            )}
            <Button
              variant={d.enabled ? 'outline' : 'default'}
              size="sm"
              loading={rotate.isPending}
              onClick={async () => {
                if (!d.enabled || (await confirm({ title: 'Make a new secret?', description: 'The current secret stops working at once. Paste the new one into GitLab.', confirm: 'Make a new secret', destructive: true })))
                  rotate.mutate()
              }}
            >
              {d.enabled ? (
                <>
                  <RefreshCw /> New secret
                </>
              ) : (
                <>
                  <Webhook /> Turn on
                </>
              )}
            </Button>
          </CardAction>
        )}
      </CardHeader>
      <CardContent className="grid gap-4">
        {d.fromEnv && <Alert>The secret comes from ARGUS_WEBHOOK_TOKEN in Argus's environment. Remove it there to manage the webhook here.</Alert>}
        {d.enabled && (
          <>
            {secret ? (
              <div className="grid gap-2">
                <Secret label="Secret token" value={secret} />
                <p className="text-xs text-muted-foreground">Shown this once. Argus keeps only its hash. Lost it? Make a new one.</p>
              </div>
            ) : (
              !d.fromEnv && <p className="text-sm text-muted-foreground">The secret was shown when it was made. Lost it? Make a new one and paste it into GitLab.</p>
            )}
            <div className="grid gap-1.5">
              <p className="text-sm font-medium">In GitLab</p>
              <ol className="ml-5 list-decimal space-y-1 text-sm text-muted-foreground">
                <li>Open the group (or a project) → Settings → Webhooks → Add new webhook. A group webhook covers every project in it.</li>
                <li>URL: the address below. Secret token: the secret.</li>
                <li>Trigger: Push events and Merge request events.</li>
                <li>Leave SSL verification on when this site has a trusted certificate (ACME_EMAIL set); otherwise turn it off.</li>
                <li>Save, then Test → Push events. The delivery shows up below.</li>
              </ol>
              <CodeBlock code={d.url} label="Webhook URL" />
              <p className="text-xs text-muted-foreground">
                A GitLab on this network refuses local addresses by default: Admin → Settings → Network → Outbound requests → Allow requests to the local network from webhooks.
              </p>
            </div>
            <div className="grid gap-1.5">
              <p className="text-sm font-medium">Last deliveries</p>
              {d.deliveries.length === 0 ? (
                <p className="text-sm text-muted-foreground">None since Argus started.</p>
              ) : (
                <ul className="grid gap-1 text-sm">
                  {d.deliveries.slice(0, 10).map((x) => (
                    <li key={`${x.at}-${x.event}-${x.repo}-${x.outcome}`} className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
                      <span className="w-20 shrink-0 text-xs text-muted-foreground tabular-nums">{agoSeconds(x.at)}</span>
                      <span className="font-medium">{x.event}</span>
                      {x.repo && <code className="font-mono text-[0.8125rem]">{x.repo}</code>}
                      <span className={x.outcome.startsWith('refused') ? 'text-destructive-ink' : 'text-muted-foreground'}>{x.outcome}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  )
}
