import { zodResolver } from '@hookform/resolvers/zod'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Building2, KeyRound, LifeBuoy, ShieldCheck } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
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
import { EyesBackdrop } from './eyes-backdrop'

type Answer = { status: 'ok'; redirect: string } | { status: '2fa' }

const passwordSchema = z.object({
  userName: z.string().trim().min(1, 'Enter your username or email.'),
  password: z.string().min(1, 'Enter your password.'),
  remember: z.boolean(),
})
const codeSchema = z.object({ code: z.string().trim().min(1, 'Enter the code.') })

/** Company sign-in (OIDC or SAML, one button that starts either): the button's label when an admin has set it up. */
const companyQuery = {
  queryKey: ['auth', 'company'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ label: string | null; protocol?: 'oidc' | 'saml' | null }>('/api/auth/company', { signal }),
  staleTime: 5 * 60_000,
}

/** What the identity provider's answer came to (login?error=company_...), for people. */
const companyErrors: Record<string, string> = {
  company_not_allowed: 'Your company account is not allowed to sign in here. Ask an admin.',
  company_disabled: 'This account is disabled. Ask an admin.',
  company_refused: 'Your company account cannot be used here. Ask an admin: the audit log says why.',
  company_failed: 'Company sign-in did not work. Try again; if it keeps failing, ask an admin (the audit log says why).',
  company_unavailable: "Your company's sign-in cannot be reached right now. Try again shortly.",
  company_cancelled: 'Company sign-in was cancelled.',
  company_off: 'Company sign-in is not set up here.',
}

/** Where the company button goes: the app starts the sign-in at the identity provider. */
function companyHref(redirect: string, remember: boolean): string {
  return `/api/auth/company/start?rd=${encodeURIComponent(redirect)}${remember ? '&remember=true' : ''}`
}

/** Sign-in for the app and for everything that trusts it (the chat, tools). */
export function LoginPage() {
  const [params] = useSearchParams()
  const redirect = params.get('rd') ?? '/'
  const companyError = companyErrors[params.get('error') ?? ''] ?? null
  const info = useQuery(infoQuery)
  const company = useQuery(companyQuery)
  const [step, setStep] = useState<'password' | '2fa'>('password')
  const [remember, setRemember] = useState(false)
  const name = info.data?.name ?? 'Argus Arena'
  const logo = useRef<HTMLImageElement>(null)
  useEffect(() => {
    document.title = `Sign in · ${name}`
  }, [name])

  // The eyes behind keep off everything marked data-keep-clear: the name, the words, the form.
  return (
    <div className="relative isolate flex min-h-dvh flex-col lg:grid lg:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
      <EyesBackdrop logo={logo} />
      <aside className="flex flex-col items-center px-6 pt-8 lg:p-10">
        <div className="flex flex-col items-center gap-3 lg:flex-1 lg:justify-center">
          <img ref={logo} src="/favicon.svg" alt="" className="eyes-logo size-24 drop-shadow-xl sm:size-28 lg:size-44" />
          <p data-keep-clear className="text-lg font-semibold lg:text-xl">
            {name}
          </p>
        </div>
        <div data-keep-clear className="hidden max-w-md self-start lg:block">
          {info.data?.signInHeadline && <p className="text-2xl leading-snug font-semibold">{info.data.signInHeadline}</p>}
          <p className="mt-3 text-muted-foreground">One sign-in for the chat, the dashboards and every tool that trusts it.</p>
        </div>
      </aside>
      <main className="flex flex-1 items-center justify-center px-4 pt-6 pb-10 sm:p-10">
        <div data-keep-clear className="w-full max-w-sm rounded-xl border bg-card p-6 text-card-foreground shadow-xl sm:p-8">
          {step === 'password' ? (
            <PasswordStep redirect={redirect} company={company.data?.label ?? null} companyError={companyError} onTwoFactor={(r) => { setRemember(r); setStep('2fa') }} />
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

function PasswordStep({ redirect, company, companyError, onTwoFactor }: { redirect: string; company: string | null; companyError: string | null; onTwoFactor: (remember: boolean) => void }) {
  const finish = useFinish()
  const [error, setError] = useState<string | null>(companyError)
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
      {company && (
        <>
          <Button variant="outline" size="lg" asChild>
            <a href={companyHref(redirect, form.watch('remember'))}>
              <Building2 /> Sign in with {company}
            </a>
          </Button>
          <div className="flex items-center gap-3 text-xs text-muted-foreground" aria-hidden="true">
            <span className="h-px flex-1 bg-border" /> or <span className="h-px flex-1 bg-border" />
          </div>
        </>
      )}
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
