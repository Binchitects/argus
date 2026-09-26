import { zodResolver } from '@hookform/resolvers/zod'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, LifeBuoy, ShieldCheck } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useSearchParams } from 'react-router'
import { z } from 'zod'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { api, errorMessage, infoQuery, meQuery, supportHref } from '@/lib/api'

type Answer = { status: 'ok'; redirect: string } | { status: '2fa' }

const passwordSchema = z.object({
  userName: z.string().trim().min(1, 'Enter your username or email.'),
  password: z.string().min(1, 'Enter your password.'),
  remember: z.boolean(),
})
const codeSchema = z.object({ code: z.string().trim().min(1, 'Enter the code.') })

/** Sign-in for the app and for everything that trusts it (the chat, tools). */
export function LoginPage() {
  const [params] = useSearchParams()
  const redirect = params.get('rd') ?? '/'
  const info = useQuery(infoQuery)
  const [step, setStep] = useState<'password' | '2fa'>('password')
  const [remember, setRemember] = useState(false)
  const name = info.data?.name ?? 'LLM Service'
  useEffect(() => {
    document.title = `Sign in · ${name}`
  }, [name])

  return (
    <div className="grid min-h-dvh lg:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
      <aside className="relative hidden overflow-hidden bg-[oklch(0.25_0.06_260)] p-10 text-white lg:flex lg:flex-col">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_20%_20%,oklch(0.56_0.16_255/0.55),transparent_55%),radial-gradient(circle_at_80%_90%,oklch(0.55_0.13_200/0.35),transparent_50%)]" aria-hidden="true" />
        <div className="relative flex items-center gap-2.5 text-lg font-semibold">
          <img src="/favicon.svg" alt="" className="size-8 rounded-md" /> {name}
        </div>
        <div className="relative mt-auto max-w-md">
          {info.data?.signInHeadline && <p className="text-2xl leading-snug font-semibold">{info.data.signInHeadline}</p>}
          <p className="mt-3 text-white/70">One sign-in for the chat, the dashboards and every tool that trusts it.</p>
        </div>
      </aside>
      <main className="flex items-center justify-center p-6 sm:p-10">
        <div className="w-full max-w-sm">
          <div className="mb-8 flex items-center gap-2.5 text-lg font-semibold lg:hidden">
            <img src="/favicon.svg" alt="" className="size-8 rounded-md" /> {name}
          </div>
          {step === 'password' ? (
            <PasswordStep redirect={redirect} onTwoFactor={(r) => { setRemember(r); setStep('2fa') }} />
          ) : (
            <CodeStep redirect={redirect} remember={remember} onBack={() => setStep('password')} />
          )}
          {info.data?.supportContact && <Support contact={info.data.supportContact} />}
        </div>
      </main>
    </div>
  )
}

function useFinish() {
  const queryClient = useQueryClient()
  return async (answer: Answer & { status: 'ok' }) => {
    await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    // A full navigation: the target may be another service or the OIDC endpoint.
    window.location.assign(answer.redirect)
  }
}

function PasswordStep({ redirect, onTwoFactor }: { redirect: string; onTwoFactor: (remember: boolean) => void }) {
  const finish = useFinish()
  const [error, setError] = useState<string | null>(null)
  const form = useForm({ resolver: zodResolver(passwordSchema), defaultValues: { userName: '', password: '', remember: false } })
  const submit = form.handleSubmit(async (v) => {
    setError(null)
    try {
      const answer = await api<Answer>('/api/auth/login', { body: { ...v, redirect } })
      if (answer.status === '2fa') onTwoFactor(v.remember)
      else await finish(answer)
    } catch (e) {
      setError(errorMessage(e, 'Sign-in failed. Try again.'))
    }
  })
  const { errors, isSubmitting } = form.formState
  return (
    <form onSubmit={submit} noValidate className="grid gap-5" aria-labelledby="login-title">
      <div>
        <h1 id="login-title" className="text-2xl font-semibold tracking-tight">
          Sign in
        </h1>
        <p className="mt-1 text-muted-foreground">With your account or your company directory login.</p>
      </div>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Username or email" error={errors.userName?.message}>
        <Input autoComplete="username" autoFocus {...form.register('userName')} />
      </Field>
      <Field label="Password" error={errors.password?.message}>
        <Input type="password" autoComplete="current-password" {...form.register('password')} />
      </Field>
      <Label className="font-normal">
        <Checkbox checked={form.watch('remember')} onCheckedChange={(v) => form.setValue('remember', !!v)} />
        Keep me signed in on this device
      </Label>
      <Button type="submit" size="lg" loading={isSubmitting}>
        <KeyRound /> Sign in
      </Button>
    </form>
  )
}

function CodeStep({ redirect, remember, onBack }: { redirect: string; remember: boolean; onBack: () => void }) {
  const finish = useFinish()
  const [recovery, setRecovery] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const form = useForm({ resolver: zodResolver(codeSchema), defaultValues: { code: '' } })
  const submit = form.handleSubmit(async ({ code }) => {
    setError(null)
    try {
      const answer = await api<Answer>('/api/auth/login/2fa', { body: { code, recovery, remember, redirect } })
      if (answer.status === 'ok') await finish(answer)
    } catch (e) {
      setError(errorMessage(e, 'That code did not work. Try again.'))
    }
  })
  return (
    <form onSubmit={submit} noValidate className="grid gap-5" aria-labelledby="code-title">
      <div>
        <div className="mb-3 flex size-10 items-center justify-center rounded-full bg-primary/10">
          <ShieldCheck className="size-5 text-primary" aria-hidden="true" />
        </div>
        <h1 id="code-title" className="text-2xl font-semibold tracking-tight">
          Two-factor sign-in
        </h1>
        <p className="mt-1 text-muted-foreground">{recovery ? 'Enter one of your recovery codes.' : 'Enter the 6-digit code from your authenticator app.'}</p>
      </div>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field key={recovery ? 'recovery' : 'totp'} label={recovery ? 'Recovery code' : 'Code'} error={form.formState.errors.code?.message}>
        <Input autoComplete="one-time-code" inputMode={recovery ? 'text' : 'numeric'} autoFocus className={recovery ? '' : 'text-center font-mono text-lg tracking-[0.4em]'} {...form.register('code')} />
      </Field>
      <Button type="submit" size="lg" loading={form.formState.isSubmitting}>
        Verify
      </Button>
      <div className="flex flex-wrap justify-between gap-2">
        <Button type="button" variant="link" className="h-auto px-0" onClick={() => { setRecovery(!recovery); form.reset() }}>
          {recovery ? 'Use the authenticator app' : 'Lost your phone? Use a recovery code'}
        </Button>
        <Button type="button" variant="link" className="h-auto px-0 text-muted-foreground" onClick={onBack}>
          Back
        </Button>
      </div>
    </form>
  )
}

function Support({ contact }: { contact: string }) {
  const href = supportHref(contact)
  return (
    <p className="mt-8 flex items-center gap-1.5 text-sm text-muted-foreground">
      <LifeBuoy className="size-4" aria-hidden="true" /> Trouble signing in?{' '}
      {href ? (
        <a href={href} className="font-medium text-primary-ink underline-offset-4 hover:underline">
          {contact}
        </a>
      ) : (
        <span className="font-medium text-foreground">{contact}</span>
      )}
    </p>
  )
}
