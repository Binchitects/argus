import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ShieldCheck, ShieldOff } from 'lucide-react'
import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, meQuery } from '@/lib/api'

/**
 * Two-factor sign-in: set up with an authenticator app (its recovery codes shown once), or turned off with a code. When the
 * company requires it (the account's `required` says why), it cannot be turned off here. `onDone`: for the sign-in that must
 * set it up first, called once the recovery codes are kept.
 */
export function TwoFactor({ enabled, onDone }: { enabled: boolean; onDone?: () => void }) {
  const queryClient = useQueryClient()
  const [setup, setSetup] = useState<{ sharedKey: string; uri: string } | null>(null)
  const [qr, setQr] = useState<string | null>(null)
  const [codes, setCodes] = useState<string[] | null>(null)
  const [code, setCode] = useState('')
  const refresh = () => queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
  const status = useQuery({ queryKey: ['account', '2fa'], queryFn: ({ signal }) => api<{ enabled: boolean; recoveryCodesLeft: number; required: string | null }>('/api/account/2fa', { signal }) })
  const required = status.data?.required ?? null

  const begin = useMutation({ mutationFn: () => api<{ sharedKey: string; uri: string }>('/api/account/2fa/setup', { body: {} }), onSuccess: setSetup })
  const enable = useMutation({
    mutationFn: (c: string) => api<{ recoveryCodes: string[] }>('/api/account/2fa/enable', { body: { code: c } }),
    onSuccess: (r) => {
      setCodes(r.recoveryCodes)
      setSetup(null)
      setCode('')
      toast.success('Two-factor sign-in is on')
      // The sign-in that had to set it up goes on only when the codes are kept.
      if (!onDone) refresh()
    },
  })
  const disable = useMutation({
    mutationFn: (c: string) => api('/api/account/2fa/disable', { body: { code: c } }),
    onSuccess: () => {
      setCode('')
      toast.success('Two-factor sign-in is off')
      refresh()
    },
  })

  useEffect(() => {
    if (setup) QRCode.toDataURL(setup.uri, { margin: 1, width: 200 }).then(setQr, () => setQr(null))
  }, [setup])

  const error = begin.error ?? enable.error ?? disable.error
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Two-factor sign-in
          {enabled ? (
            <Badge variant="success">
              <ShieldCheck /> On
            </Badge>
          ) : (
            <Badge variant="secondary">
              <ShieldOff /> Off
            </Badge>
          )}
        </CardTitle>
        <CardDescription>A code from your phone at every sign-in, so a stolen password is not enough.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {error && <Alert variant="destructive">{errorMessage(error)}</Alert>}
        {codes && (
          <Alert variant="warning" title="Recovery codes — shown only this once">
            <p className="mt-1">Each works once, if you lose your phone. Keep them somewhere safe.</p>
            <ul className="mt-3 grid grid-cols-2 gap-1.5 font-mono text-[0.8125rem] text-foreground">
              {codes.map((c) => (
                <li key={c} className="rounded bg-muted px-2 py-1">
                  {c}
                </li>
              ))}
            </ul>
          </Alert>
        )}
        {codes && onDone && (
          <div>
            <Button onClick={onDone}>I have kept them: continue</Button>
          </div>
        )}
        {!enabled && !setup && (
          <div>
            <Button onClick={() => begin.mutate()} loading={begin.isPending}>
              <ShieldCheck /> Set up
            </Button>
          </div>
        )}
        {setup && (
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              enable.mutate(code)
            }}
          >
            <ol className="grid list-decimal gap-1 pl-5 text-sm">
              <li>Scan this with an authenticator app (Google Authenticator, Aegis, 1Password…).</li>
              <li>Enter the code it shows.</li>
            </ol>
            <div className="flex flex-wrap items-center gap-4">
              {qr ? <img src={qr} alt="QR code for your authenticator app" width={160} height={160} className="rounded-lg border bg-white p-1" /> : <Skeleton className="size-40" />}
              <p className="min-w-0 flex-1 text-sm text-muted-foreground">
                Or enter this key by hand:
                <code className="mt-1 block font-mono text-[0.8125rem] break-all text-foreground">{setup.sharedKey}</code>
              </p>
            </div>
            <Field label="Code">
              <Input value={code} onChange={(e) => setCode(e.target.value)} autoComplete="one-time-code" inputMode="numeric" autoFocus required className="max-w-40 font-mono tracking-widest" />
            </Field>
            <div className="flex gap-2">
              <Button type="submit" loading={enable.isPending}>
                Turn on
              </Button>
              <Button type="button" variant="ghost" onClick={() => setSetup(null)}>
                Cancel
              </Button>
            </div>
          </form>
        )}
        {enabled && !codes && required && <p className="text-sm text-muted-foreground">It stays on: {required}. To move it to a new phone, ask an admin to reset it.</p>}
        {enabled && !codes && !required && (
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              disable.mutate(code)
            }}
          >
            <Field label="Current code, to turn it off" className="min-w-40 flex-1">
              <Input value={code} onChange={(e) => setCode(e.target.value)} autoComplete="one-time-code" required className="font-mono tracking-widest" />
            </Field>
            <Button type="submit" variant="outline" loading={disable.isPending}>
              Turn off
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  )
}
