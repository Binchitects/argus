import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Monitor, Moon, RefreshCw, ShieldCheck, ShieldOff, Sun } from 'lucide-react'
import QRCode from 'qrcode'
import { RadioGroup } from 'radix-ui'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useOutletContext } from 'react-router'
import { z } from 'zod'
import { PageHeader } from '@/components/app/page-header'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, meQuery, type Me } from '@/lib/api'
import { ago, money, when } from '@/lib/format'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { cn } from '@/lib/utils'

interface Keys {
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  spend: number
  budget: number | null
}

export function AccountPage() {
  const me = useOutletContext<Me>()
  return (
    <>
      <PageHeader title="Your account" description="Your profile, API key, sign-in security and appearance." />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="grid content-start gap-6">
          <Profile me={me} />
          <ApiKey />
          <Appearance />
        </div>
        <div className="grid content-start gap-6">
          <TwoFactor enabled={me.twoFactorEnabled} />
          {me.source === 'local' ? (
            <Password />
          ) : (
            <Card>
              <CardHeader>
                <CardTitle>Password</CardTitle>
                <CardDescription>Your password is managed by the company directory. Change it there.</CardDescription>
              </CardHeader>
            </Card>
          )}
        </div>
      </div>
    </>
  )
}

function Profile({ me }: { me: Me }) {
  return (
    <Card>
      <CardContent className="flex items-center gap-4">
        <Avatar name={me.displayName || me.userName} className="size-12 text-base" />
        <div className="min-w-0">
          <p className="truncate text-base font-semibold">{me.displayName}</p>
          <p className="truncate text-muted-foreground">
            {me.userName} · {me.email}
          </p>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {me.isAdmin && <Badge>Admin</Badge>}
            <Badge variant="secondary">{me.source === 'ldap' ? 'Company directory' : 'Local account'}</Badge>
            <Badge variant="outline">Signed in {ago(me.signedInAt)}</Badge>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

function ApiKey() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const keys = useQuery({ queryKey: ['account', 'keys'], queryFn: () => api<Keys>('/api/account/keys') })
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>('/api/account/keys/rotate', { body: {} }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['account', 'keys'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const d = keys.data
  const used = d && d.budget ? Math.min(1, d.spend / d.budget) : null
  return (
    <Card>
      <CardHeader>
        <CardTitle>API key</CardTitle>
        <CardDescription>For your tools: Qwen Code, IDEs, scripts. It spends from your credit.</CardDescription>
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

function TwoFactor({ enabled }: { enabled: boolean }) {
  const queryClient = useQueryClient()
  const [setup, setSetup] = useState<{ sharedKey: string; uri: string } | null>(null)
  const [qr, setQr] = useState<string | null>(null)
  const [codes, setCodes] = useState<string[] | null>(null)
  const [code, setCode] = useState('')
  const refresh = () => queryClient.invalidateQueries({ queryKey: meQuery.queryKey })

  const begin = useMutation({ mutationFn: () => api<{ sharedKey: string; uri: string }>('/api/account/2fa/setup', { body: {} }), onSuccess: setSetup })
  const enable = useMutation({
    mutationFn: (c: string) => api<{ recoveryCodes: string[] }>('/api/account/2fa/enable', { body: { code: c } }),
    onSuccess: (r) => {
      setCodes(r.recoveryCodes)
      setSetup(null)
      setCode('')
      toast.success('Two-factor sign-in is on')
      refresh()
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
        {enabled && !codes && (
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

const passwordSchema = z
  .object({
    current: z.string().min(1, 'Enter your current password.'),
    next: z.string().min(10, 'At least 10 characters. A few unrelated words make a strong password.'),
    again: z.string(),
  })
  .refine((v) => v.next === v.again, { path: ['again'], message: 'The new passwords differ.' })

function Password() {
  const [error, setError] = useState<string | null>(null)
  const form = useForm({ resolver: zodResolver(passwordSchema), defaultValues: { current: '', next: '', again: '' } })
  const submit = form.handleSubmit(async ({ current, next }) => {
    setError(null)
    try {
      await api('/api/account/password', { body: { current, next } })
      form.reset()
      toast.success('Password changed. Your other devices are signed out.')
    } catch (e) {
      setError(errorMessage(e))
    }
  })
  const { errors, isSubmitting } = form.formState
  return (
    <Card>
      <CardHeader>
        <CardTitle>Password</CardTitle>
        <CardDescription>Changing it signs out your other devices.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} noValidate className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="Current password" error={errors.current?.message}>
            <Input type="password" autoComplete="current-password" {...form.register('current')} />
          </Field>
          <Field label="New password" hint="At least 10 characters. A few unrelated words work well." error={errors.next?.message}>
            <Input type="password" autoComplete="new-password" {...form.register('next')} />
          </Field>
          <Field label="New password again" error={errors.again?.message}>
            <Input type="password" autoComplete="new-password" {...form.register('again')} />
          </Field>
          <div>
            <Button type="submit" loading={isSubmitting}>
              Change password
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

const themes: { value: ThemePreference; label: string; icon: typeof Sun }[] = [
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
  { value: 'system', label: 'System', icon: Monitor },
]

function Appearance() {
  const { preference, setPreference } = useTheme()
  return (
    <Card>
      <CardHeader>
        <CardTitle>Appearance</CardTitle>
        <CardDescription>Remembered on this device.</CardDescription>
      </CardHeader>
      <CardContent>
        <RadioGroup.Root value={preference} onValueChange={(v) => setPreference(v as ThemePreference)} aria-label="Theme" className="grid grid-cols-3 gap-2">
          {themes.map((t) => (
            <RadioGroup.Item
              key={t.value}
              value={t.value}
              className="flex flex-col items-center gap-2 rounded-lg border p-3 text-sm font-medium transition-colors outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring data-[state=checked]:border-primary data-[state=checked]:bg-primary/5 data-[state=checked]:text-primary-ink"
            >
              <t.icon className="size-5" aria-hidden="true" />
              {t.label}
            </RadioGroup.Item>
          ))}
        </RadioGroup.Root>
      </CardContent>
    </Card>
  )
}
