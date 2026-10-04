import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BellOff, BellRing, Download, Send, Smartphone, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago } from '@/lib/format'
import { currentSubscription, pushSupport, subscribe, unsubscribe, useInstall } from '@/lib/pwa'

export interface PushDevice {
  id: string
  device: string | null
  endpoint: string
  createdAt: string
  lastSentAt: string | null
  lastError: string | null
}

interface PushView {
  available: boolean
  publicKey: string | null
  devices: PushDevice[]
}

const key = ['account', 'push']

/**
 * This device and the others that get the bell's news by push, even with the app closed:
 * an answer that finished while you were away, a task that ran, your credit. Also the
 * offer to install the app, when the browser makes it.
 */
export function PushDevices() {
  const queryClient = useQueryClient()
  const push = useQuery({ queryKey: key, queryFn: () => api<PushView>('/api/push') })
  const { canInstall, install, installed } = useInstall()
  const support = pushSupport()
  const [here, setHere] = useState<string | null>(null)
  useEffect(() => {
    currentSubscription()
      .then((s) => setHere(s?.endpoint ?? null))
      .catch(() => setHere(null))
  }, [])
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })

  const turnOn = useMutation({
    mutationFn: async () => {
      const sub = await subscribe(push.data!.publicKey!)
      await api('/api/push/subscriptions', { body: sub })
      return sub.endpoint ?? null
    },
    onSuccess: (endpoint) => {
      setHere(endpoint)
      toast.success('Push notifications are on for this device')
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: async (device: PushDevice) => {
      if (device.endpoint === here) await unsubscribe().catch(() => null)
      await api(`/api/push/subscriptions/${device.id}`, { method: 'DELETE' })
      return device
    },
    onSuccess: (device) => {
      if (device.endpoint === here) setHere(null)
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const test = useMutation({
    mutationFn: () => api<{ sent: number; failed: number }>('/api/push/test', { body: {} }),
    onSuccess: (r) =>
      r.sent ? toast.success(`Sent to ${r.sent} ${r.sent === 1 ? 'device' : 'devices'}`, { description: r.failed ? `${r.failed} did not take it.` : undefined }) : toast.error('No device took it.'),
    onError: (e) => toast.error(errorMessage(e)),
  })

  const devices = push.data?.devices ?? []
  const onHere = !!here && devices.some((d) => d.endpoint === here)
  return (
    <Card>
      <CardHeader>
        <CardTitle>Push notifications</CardTitle>
        <CardDescription>The bell&apos;s news on your devices, even with the app closed: answers that finished while you were away, scheduled tasks, your credit.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {push.isPending ? (
          <Skeleton className="h-16" />
        ) : push.error ? (
          <Alert variant="destructive">{errorMessage(push.error)}</Alert>
        ) : !push.data.available ? (
          <Alert variant="warning">Push notifications are not available here: APP_KEY is not set in the stack’s .env.</Alert>
        ) : (
          <>
            {support === 'unsupported' ? (
              <p className="text-sm text-muted-foreground">This browser cannot get push notifications. On an iPhone or iPad, add the app to the home screen first, then turn them on there.</p>
            ) : support === 'blocked' ? (
              <Alert variant="warning">Notifications are blocked for this site in the browser&apos;s settings. Allow them there to turn push on.</Alert>
            ) : (
              <div className="flex flex-wrap items-center gap-2">
                {onHere ? (
                  <Badge variant="success">
                    <BellRing /> On for this device
                  </Badge>
                ) : (
                  <Button onClick={() => turnOn.mutate()} loading={turnOn.isPending}>
                    <BellRing /> Turn on for this device
                  </Button>
                )}
              </div>
            )}
            {devices.length > 0 && (
              <ul className="grid gap-2" aria-label="Devices with push notifications">
                {devices.map((d) => (
                  <li key={d.id} className="flex items-center gap-3 rounded-lg border p-3" aria-label={d.device ?? 'A device'}>
                    <Smartphone className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <div className="grid min-w-0 flex-1 gap-0.5">
                      <span className="flex flex-wrap items-center gap-2 text-sm font-medium">
                        {d.device ?? 'A device'}
                        {d.endpoint === here && <Badge variant="secondary">This device</Badge>}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        Added {ago(d.createdAt)}
                        {d.lastSentAt && ` · last push ${ago(d.lastSentAt)}`}
                      </span>
                      {d.lastError && <span className="text-xs text-destructive-ink">{d.lastError}</span>}
                    </div>
                    <Button variant="ghost" size="icon-sm" aria-label={`Remove ${d.device ?? 'this device'}`} onClick={() => remove.mutate(d)} disabled={remove.isPending}>
                      {d.endpoint === here ? <BellOff /> : <Trash2 />}
                    </Button>
                  </li>
                ))}
              </ul>
            )}
            {devices.length > 0 && (
              <div>
                <Button variant="outline" size="sm" onClick={() => test.mutate()} loading={test.isPending}>
                  <Send /> Send a test
                </Button>
              </div>
            )}
          </>
        )}
        {(canInstall || installed) && (
          <div className="flex flex-wrap items-center gap-2 border-t pt-4">
            <p className="min-w-0 flex-1 text-sm">
              <span className="font-medium">The app on this device</span>
              <span className="block text-muted-foreground">{installed ? 'Installed: it opens in its own window.' : 'Install it: its own window and icon, like any app.'}</span>
            </p>
            {installed ? (
              <Badge variant="success">Installed</Badge>
            ) : (
              <Button variant="outline" onClick={() => void install()}>
                <Download /> Install
              </Button>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
