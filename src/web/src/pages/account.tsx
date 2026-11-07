import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlignJustify, Columns2, Maximize2, Minus, Monitor, Moon, RectangleHorizontal, Sun, Text } from 'lucide-react'
import { RadioGroup } from 'radix-ui'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useOutletContext } from 'react-router'
import { z } from 'zod'
import { useChooseColorTheme } from '@/app/color-theme'
import { ApiKey } from '@/components/app/api-key'
import { OwnKeys } from '@/components/app/own-keys'
import { YourFeedback } from '@/components/app/your-feedback'
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
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { colorThemes, type ColorThemeId, type Palette } from '@/lib/color-themes'
import { ago } from '@/lib/format'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { setWidth, useWidth, type WidthPreference } from '@/lib/width'
import { setSeeing, sizes, useSeeing, type Seeing } from '@/lib/seeing'
import { DataRights, YourData } from './account-data'
import { AskForAccess } from './account-access'
import { CodeHosts } from './account-code-hosts'
import { PoliciesToRead } from './account-policies'
import { VoiceSection } from './account-voice'

export function AccountPage() {
  const me = useOutletContext<Me>()
  return (
    <>
      <PageHeader title="Your account" description="Your profile, API key, answers, voice, memory, sign-in security and appearance." />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="grid content-start gap-6">
          <PoliciesToRead />
          <Profile me={me} />
          <ApiKey />
          <OwnKeys />
          <Answers />
          <VoiceSection />
          <Memory />
          <Appearance />
          <EasierToSee />
          <YourData />
          <DataRights />
        </div>
        <div className="grid content-start gap-6">
          <Connections />
          <CodeHosts />
          <PushDevices />
          <AskForAccess />
          <YourFeedback />
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
  colorTheme: ColorThemeId
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

/** A colour theme as a little page of its own: its background and border, its accent, and a line of text and one of muted text. */
function Swatch({ palette }: { palette: Palette }) {
  return (
    <span aria-hidden="true" className="flex h-10 w-full items-center gap-2 rounded-md border px-2.5" style={{ background: palette.background, borderColor: palette.border }}>
      <span className="size-4 shrink-0 rounded-full" style={{ background: palette.primary }} />
      <span className="grid flex-1 gap-1">
        <span className="h-1.5 w-4/5 rounded-full" style={{ background: palette.foreground }} />
        <span className="h-1.5 w-1/2 rounded-full" style={{ background: palette.lineNumber }} />
      </span>
    </span>
  )
}

const sizeChoice =
  'grid place-items-center rounded-md border px-2 py-1.5 text-sm font-medium tabular-nums transition-colors outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring data-[state=checked]:border-primary data-[state=checked]:bg-primary/5 data-[state=checked]:text-primary-ink'

/** A choice of the text's or the page's size, in percent. */
function SizeChoice({ label, help, value, onChange }: { label: string; help: string; value: number; onChange: (v: Seeing['text']) => void }) {
  return (
    <div className="grid gap-1.5">
      <p className="text-sm font-medium">{label}</p>
      <p className="text-xs text-muted-foreground">{help}</p>
      <RadioGroup.Root value={String(value)} onValueChange={(v) => onChange(Number(v) as Seeing['text'])} aria-label={label} className="grid grid-cols-4 gap-2">
        {sizes.map((n) => (
          <RadioGroup.Item key={n} value={String(n)} className={sizeChoice}>
            {n}%
          </RadioGroup.Item>
        ))}
      </RadioGroup.Root>
    </div>
  )
}

/** One of the on-or-off choices, with what it does under its name. */
function SeeingSwitch({ id, label, help, on, onChange }: { id: string; label: string; help: string; on: boolean; onChange: (on: boolean) => void }) {
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="grid gap-0.5">
        <label htmlFor={id} className="text-sm font-medium">
          {label}
        </label>
        <p id={`${id}-help`} className="text-xs text-muted-foreground">
          {help}
        </p>
      </div>
      <Switch id={id} checked={on} onCheckedChange={onChange} aria-describedby={`${id}-help`} className="mt-0.5" />
    </div>
  )
}

/**
 * For people who see less well or tell colours apart less well: more contrast, larger text or a larger page, stronger
 * focus rings, no motion, colours without red and green. Remembered on this device, as light or dark is.
 */
function EasierToSee() {
  const s = useSeeing()
  return (
    <Card id="easier-to-see">
      <CardHeader>
        <CardTitle>Easier to see</CardTitle>
        <CardDescription>For low vision and colour blindness, over any colour theme. Remembered on this device; Code Arena has the same in its settings.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        <SeeingSwitch id="seeing-contrast" label="More contrast" help="Secondary text as dark (or light) as the text, borders and fields that stand out. VS Code's High Contrast colour theme goes further." on={s.contrast === 'more'} onChange={(on) => setSeeing({ contrast: on ? 'more' : 'standard' })} />
        <SizeChoice label="Text size" help="The text larger, the rest of the page as it is." value={s.text} onChange={(text) => setSeeing({ text })} />
        <SizeChoice label="Page size" help="Everything larger: the text, the buttons, the spacing, as the browser's zoom." value={s.scale} onChange={(scale) => setSeeing({ scale })} />
        <SeeingSwitch id="seeing-focus" label="Strong focus rings" help="A solid ring in the text's colour round what the keyboard is on." on={s.focus === 'strong'} onChange={(on) => setSeeing({ focus: on ? 'strong' : 'standard' })} />
        <SeeingSwitch id="seeing-motion" label="Reduce motion" help="No animation or moving effects, whatever the system says (it is off already when the system asks for less motion)." on={s.motion === 'reduce'} onChange={(on) => setSeeing({ motion: on ? 'reduce' : 'system' })} />
        <SeeingSwitch id="seeing-colors" label="Colour-blind safe colours" help="Blue and orange where red and green say good and bad: a change added or removed, a check passed or failed." on={s.colors === 'safe'} onChange={(on) => setSeeing({ colors: on ? 'safe' : 'standard' })} />
      </CardContent>
    </Card>
  )
}

function Appearance() {
  const { preference, setPreference, resolved, colorTheme } = useTheme()
  const chooseColorTheme = useChooseColorTheme()
  const width = useWidth()
  return (
    <Card id="appearance">
      <CardHeader>
        <CardTitle>Appearance</CardTitle>
        <CardDescription>Your colour theme follows you to every device; light or dark and the width are remembered on this one.</CardDescription>
      </CardHeader>
      <CardContent>
        <p className="mb-2 text-sm font-medium">Colour theme</p>
        {/* Arena's own, calm; then VS Code's most used, as sharp as VS Code has them (each light and dark too). */}
        {([
          ['Colour theme', colorThemes.filter((t) => !t.vscode)],
          ['VS Code\'s colour themes', colorThemes.filter((t) => t.vscode)],
        ] as const).map(([label, list], i) => (
          <div key={label}>
            {i > 0 && <p className="mb-2 text-xs text-muted-foreground">As VS Code has them</p>}
            <RadioGroup.Root value={colorTheme} onValueChange={(v) => chooseColorTheme(v as ColorThemeId)} aria-label={label} className="mb-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
              {list.map((t) => (
                <RadioGroup.Item key={t.id} value={t.id} className={choice} title={t.description}>
                  <Swatch palette={t[resolved]} />
                  {t.vscode ? t.vscode.names[resolved] : t.label}
                </RadioGroup.Item>
              ))}
            </RadioGroup.Root>
          </div>
        ))}
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
