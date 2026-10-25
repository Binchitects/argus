import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlignJustify, Columns2, Maximize2, Minus, Monitor, Moon, RectangleHorizontal, Sun, Text } from 'lucide-react'
import { RadioGroup } from 'radix-ui'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useOutletContext } from 'react-router'
import { z } from 'zod'
import { ApiKey } from '@/components/app/api-key'
import { Connections } from '@/components/app/connections'
import { MemoryManager } from '@/components/app/memory'
import { PageHeader } from '@/components/app/page-header'
import { PushDevices } from '@/components/app/push-devices'
import { TwoFactor } from '@/components/app/two-factor'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago } from '@/lib/format'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { setWidth, useWidth, type WidthPreference } from '@/lib/width'
import { YourData } from './account-data'
import { AskForAccess } from './account-access'
import { CodeHosts } from './account-code-hosts'
import { VoiceSection } from './account-voice'

export function AccountPage() {
  const me = useOutletContext<Me>()
  return (
    <>
      <PageHeader title="Your account" description="Your profile, API key, answers, voice, memory, sign-in security and appearance." />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="grid content-start gap-6">
          <Profile me={me} />
          <ApiKey />
          <Answers />
          <VoiceSection />
          <Memory />
          <Appearance />
          <YourData />
        </div>
        <div className="grid content-start gap-6">
          <Connections />
          <CodeHosts />
          <PushDevices />
          <AskForAccess />
          {me.source === 'oidc' ? (
            <Card>
              <CardHeader>
                <CardTitle>Two-factor sign-in</CardTitle>
                <CardDescription>Your company account's sign-in asks for it, when your company has set it up.</CardDescription>
              </CardHeader>
            </Card>
          ) : (
            <TwoFactor enabled={me.twoFactorEnabled} />
          )}
          {me.source === 'local' ? (
            <Password />
          ) : (
            <Card>
              <CardHeader>
                <CardTitle>Password</CardTitle>
                <CardDescription>
                  {me.source === 'oidc' ? 'You sign in with your company account: its password is changed there.' : 'Your password is managed by the company directory. Change it there.'}
                </CardDescription>
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
            <Badge variant="secondary">{me.source === 'ldap' ? 'Company directory' : me.source === 'oidc' ? 'Company sign-in' : 'Local account'}</Badge>
            <Badge variant="outline">Signed in {ago(me.signedInAt)}</Badge>
          </div>
        </div>
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

const widths: { value: WidthPreference; label: string; icon: typeof Sun }[] = [
  { value: 'comfortable', label: 'Comfortable', icon: Columns2 },
  { value: 'wide', label: 'Wide', icon: RectangleHorizontal },
  { value: 'full', label: 'Full width', icon: Maximize2 },
]

const choice =
  'flex flex-col items-center gap-2 rounded-lg border p-3 text-sm font-medium transition-colors outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring data-[state=checked]:border-primary data-[state=checked]:bg-primary/5 data-[state=checked]:text-primary-ink'

type AnswerLength = 'short' | 'normal' | 'thorough'

/** The person's answer preferences: compactAt null follows the company's (compactAtDefault); 0 compacts only when a message would not fit. */
interface Preferences {
  answerLength: AnswerLength
  compactAt: number | null
  compactAtDefault: number
}

const lengths: { value: AnswerLength; label: string; icon: typeof Sun }[] = [
  { value: 'short', label: 'Short', icon: Minus },
  { value: 'normal', label: 'Normal', icon: Text },
  { value: 'thorough', label: 'Thorough', icon: AlignJustify },
]

/** How long answers should be: said to the model on every question. */
function Answers() {
  const queryClient = useQueryClient()
  const prefs = useQuery({ queryKey: ['account', 'preferences'], queryFn: () => api<Preferences>('/api/account/preferences') })
  const save = useMutation({
    mutationFn: (answerLength: AnswerLength) => api<Preferences>('/api/account/preferences', { method: 'PUT', body: { answerLength } }),
    onSuccess: (v) => {
      queryClient.setQueryData(['account', 'preferences'], v)
      toast.success('Saved', { description: { short: 'Answers come short: the answer first, no preamble.', normal: 'The model judges how long.', thorough: 'Answers explain, cover the cases and give examples.' }[v.answerLength] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card id="answers">
      <CardHeader>
        <CardTitle>Answers</CardTitle>
        <CardDescription>How long answers are, in every chat. Shorter and Longer under an answer change one answer.</CardDescription>
      </CardHeader>
      <CardContent>
        {prefs.data ? (
          <RadioGroup.Root value={prefs.data.answerLength} onValueChange={(v) => save.mutate(v as AnswerLength)} aria-label="Answer length" className="grid grid-cols-3 gap-2">
            {lengths.map((t) => (
              <RadioGroup.Item key={t.value} value={t.value} className={choice} disabled={save.isPending}>
                <t.icon className="size-5" aria-hidden="true" />
                {t.label}
              </RadioGroup.Item>
            ))}
          </RadioGroup.Root>
        ) : prefs.error ? (
          <Alert variant="destructive">{errorMessage(prefs.error)}</Alert>
        ) : (
          <Skeleton className="h-20" />
        )}
        {prefs.data && <CompactChoice prefs={prefs.data} />}
      </CardContent>
    </Card>
  )
}

type Compacting = 'company' | 'share' | 'needed'

/** When long chats compact: the company's share of the model's context, one of the person's own (20% to 95%), or only when a message would not fit. */
function CompactChoice({ prefs }: { prefs: Preferences }) {
  const queryClient = useQueryClient()
  const kind: Compacting = prefs.compactAt == null ? 'company' : prefs.compactAt === 0 ? 'needed' : 'share'
  const [share, setShare] = useState(String(prefs.compactAt || prefs.compactAtDefault || 80))
  const [chosen, setChosen] = useState<Compacting>(kind)
  const [error, setError] = useState<string>()
  const save = useMutation({
    mutationFn: (compactAt: number | null) => api<Preferences>('/api/account/preferences', { method: 'PUT', body: { compactAt } }),
    onSuccess: (v) => {
      queryClient.setQueryData(['account', 'preferences'], v)
      void queryClient.invalidateQueries({ queryKey: ['chat', 'config'] })
      toast.success('Saved', {
        description: v.compactAt == null ? `Your chats compact at the company's ${v.compactAtDefault}%.` : v.compactAt === 0 ? 'Your chats compact only when a message would not fit.' : `Your chats compact at ${v.compactAt}% of the model's context.`,
      })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const choose = (v: Compacting) => {
    setChosen(v)
    setError(undefined)
    if (v === 'company') save.mutate(null)
    else if (v === 'needed') save.mutate(0)
  }
  const saveShare = () => {
    const n = Number(share)
    if (!Number.isInteger(n) || n < 20 || n > 95) return setError('From 20% to 95% of the model’s context.')
    setError(undefined)
    save.mutate(n)
  }
  return (
    <div className="mt-6 grid gap-3">
      <div>
        <p className="text-sm font-medium" id="compact-label">
          Compact long chats
        </p>
        <p className="text-xs text-muted-foreground">
          When a chat fills this much of the model’s context, its older messages become a summary the model reads instead (you still see them). A chat is always compacted before a message would not fit. Code Arena follows this too, unless chosen there.
        </p>
      </div>
      <RadioGroup.Root value={chosen} onValueChange={(v) => choose(v as Compacting)} aria-labelledby="compact-label" className="grid gap-2 sm:grid-cols-3">
        <RadioGroup.Item value="company" className={choice} disabled={save.isPending}>
          Company default ({prefs.compactAtDefault === 0 ? 'only when needed' : `${prefs.compactAtDefault}%`})
        </RadioGroup.Item>
        <RadioGroup.Item value="share" className={choice} disabled={save.isPending}>
          At a share of the context
        </RadioGroup.Item>
        <RadioGroup.Item value="needed" className={choice} disabled={save.isPending}>
          Only when a message would not fit
        </RadioGroup.Item>
      </RadioGroup.Root>
      {chosen === 'share' && (
        <form
          className="flex items-end gap-2"
          noValidate
          onSubmit={(e) => {
            e.preventDefault()
            saveShare()
          }}
        >
          <Field label="Compact at (% of the context)" error={error} className="w-48">
            <Input type="number" min={20} max={95} step={5} inputMode="numeric" value={share} onChange={(e) => setShare(e.target.value)} />
          </Field>
          <Button type="submit" variant="outline" disabled={save.isPending} className={cn(error && 'mb-6')}>
            Save
          </Button>
        </form>
      )}
    </div>
  )
}

/** What the chat remembers about the person, in every chat; only they see it. */
function Memory() {
  return (
    <Card id="memory">
      <CardHeader>
        <CardTitle>Memory</CardTitle>
        <CardDescription>What the chat remembers about you, for every answer. Nobody else sees it, admins included.</CardDescription>
      </CardHeader>
      <CardContent>
        <MemoryManager />
      </CardContent>
    </Card>
  )
}

function Appearance() {
  const { preference, setPreference } = useTheme()
  const width = useWidth()
  return (
    <Card>
      <CardHeader>
        <CardTitle>Appearance</CardTitle>
        <CardDescription>Remembered on this device.</CardDescription>
      </CardHeader>
      <CardContent>
        <p className="mb-2 text-sm font-medium">Theme</p>
        <RadioGroup.Root value={preference} onValueChange={(v) => setPreference(v as ThemePreference)} aria-label="Theme" className="grid grid-cols-3 gap-2">
          {themes.map((t) => (
            <RadioGroup.Item key={t.value} value={t.value} className={choice}>
              <t.icon className="size-5" aria-hidden="true" />
              {t.label}
            </RadioGroup.Item>
          ))}
        </RadioGroup.Root>
        <p className="mt-4 mb-2 text-sm font-medium">Width on wide screens</p>
        <RadioGroup.Root value={width} onValueChange={(v) => setWidth(v as WidthPreference)} aria-label="Width on wide screens" className="grid grid-cols-3 gap-2">
          {widths.map((t) => (
            <RadioGroup.Item key={t.value} value={t.value} className={choice}>
              <t.icon className="size-5" aria-hidden="true" />
              {t.label}
            </RadioGroup.Item>
          ))}
        </RadioGroup.Root>
      </CardContent>
    </Card>
  )
}
