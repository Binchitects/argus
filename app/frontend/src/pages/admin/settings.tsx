import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Clock, PlugZap, RotateCcw, RotateCw, Save, Search, Server, Terminal, Undo2, Zap } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, ApiError, errorMessage, infoQuery } from '@/lib/api'
import { cn } from '@/lib/utils'
import { bytesHint, initialValue, slug, wireValue, type SettingsData, type SettingView } from './settings-model'

const settingsQuery = {
  queryKey: ['admin', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SettingsData>('/api/admin/config', { signal }),
}

const NONE = '__none__'

type Change = { key: string; value?: string; reset?: boolean }

/** Save changes; field errors come back per key. */
function useSave() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (changes: Change[]) => api<SettingsData>('/api/admin/config', { method: 'PUT', body: { changes } }),
    onSuccess: (data) => {
      queryClient.setQueryData(settingsQuery.queryKey, data)
      void queryClient.invalidateQueries({ queryKey: infoQuery.queryKey })
    },
  })
}

export function SettingsPage() {
  const settings = useQuery(settingsQuery)
  const [search, setSearch] = useState('')
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const save = useSave()
  const confirm = useConfirm()

  const all = useMemo(() => settings.data?.groups.flatMap((g) => g.settings) ?? [], [settings.data])
  const byKey = useMemo(() => new Map(all.map((s) => [s.key, s])), [all])
  const dirty = Object.entries(draft).filter(([k, v]) => byKey.has(k) && v !== initialValue(byKey.get(k)!))

  // One group at a time (#company-directory-ldap links straight to one); a search spans them all.
  const { hash } = useLocation()
  const navigate = useNavigate()
  const allGroups = settings.data?.groups ?? []
  const active = allGroups.find((g) => slug(g.title) === hash.slice(1)) ?? allGroups[0]
  const q = search.trim().toLowerCase()
  const groups = q
    ? allGroups
        .map((g) => ({ ...g, settings: g.settings.filter((s) => `${s.label} ${s.key} ${s.help} ${g.title}`.toLowerCase().includes(q)) }))
        .filter((g) => g.settings.length)
    : active
      ? [active]
      : []
  const unsavedIn = (title: string) => dirty.filter(([k]) => byKey.get(k)!.group === title).length

  const submit = async () => {
    const risky = dirty.map(([k]) => byKey.get(k)!).filter((s) => s.dangerous)
    if (
      risky.length &&
      !(await confirm({
        title: 'Save changes that can stop a service?',
        description: `A wrong value in ${risky.map((s) => s.label).join(', ')} can keep a service from starting. Check the value; stack changes still wait for you to apply them on the host.`,
        confirm: 'Save anyway',
        destructive: true,
      }))
    )
      return
    setErrors({})
    save.mutate(
      dirty.map(([key, text]) => ({ key, value: wireValue(byKey.get(key)!, text) })),
      {
        onSuccess: () => {
          const kinds = dirty.map(([k]) => byKey.get(k)!.scope)
          const n = (s: string) => kinds.filter((k) => k === s).length
          toast.success('Saved', {
            description: [n('live') && `${n('live')} in effect now`, n('apprestart') && `${n('apprestart')} after a restart`, n('stack') && `${n('stack')} waiting for apply-settings.sh`]
              .filter(Boolean)
              .join(' · '),
          })
          setDraft({})
        },
        onError: (e) => {
          const fields = e instanceof ApiError ? e.fieldErrors : undefined
          if (fields) setErrors(fields)
          toast.error(errorMessage(e))
        },
      },
    )
  }

  if (settings.isPending) return <PageSkeleton />
  if (settings.error) return <QueryError error={settings.error} retry={() => settings.refetch()} />
  const data = settings.data

  return (
    <>
      <PageHeader
        title="Settings"
        description="Everything about this deployment. Each setting says when it applies: at once, after the app restarts, or when you apply .env changes on the host."
      />
      <div className="mb-6 grid gap-3">
        {!data.pendingFileWritable && (
          <Alert variant="warning" title="Stack settings cannot be saved yet">
            The app has no settings folder to write to. Run <code className="font-mono">docker compose up -d</code> in the stack folder once, so it gets one.
          </Alert>
        )}
        {data.pendingStack > 0 && <PendingBanner count={data.pendingStack} />}
        {data.restartNeeded && <RestartBanner />}
      </div>
      <div className="grid gap-6 lg:grid-cols-[13rem_minmax(0,1fr)]">
        <nav aria-label="Setting groups" className="hidden lg:block">
          <div className="sticky top-20 grid gap-0.5">
            {data.groups.map((g) => {
              const changed = g.settings.filter((s) => s.source === 'saved' || s.pendingSet).length
              const current = !q && g === active
              return (
                <Link
                  key={g.title}
                  to={{ hash: slug(g.title) }}
                  replace
                  aria-current={current ? 'page' : undefined}
                  className={cn(
                    'flex items-center justify-between gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent hover:text-foreground',
                    current ? 'bg-accent font-medium text-foreground' : 'text-muted-foreground',
                  )}
                >
                  <span className="truncate">{g.title}</span>
                  <span className="flex items-center gap-1.5 text-xs tabular-nums">
                    {unsavedIn(g.title) > 0 && <span className="size-1.5 rounded-full bg-primary" aria-label="unsaved changes" />}
                    {changed > 0 && <span title="changed from .env or the default">{changed}</span>}
                  </span>
                </Link>
              )
            })}
          </div>
        </nav>
        <div className="grid min-w-0 content-start gap-6">
          <div className="flex flex-wrap gap-3">
            <div className="relative min-w-56 flex-1">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
              <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search all settings" aria-label="Search settings" className="pl-8" />
            </div>
            {!q && active && (
              <div className="w-full lg:hidden">
                <Select value={slug(active.title)} onValueChange={(v) => navigate({ hash: v }, { replace: true })}>
                  <SelectTrigger aria-label="Setting group">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {data.groups.map((g) => (
                      <SelectItem key={g.title} value={slug(g.title)}>
                        {g.title}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            )}
          </div>
          {groups.length === 0 && <EmptyState icon={Search} title="No setting matches" />}
          {groups.map((g) => (
            <Card key={g.title} id={slug(g.title)} className="scroll-mt-20" aria-labelledby={`${slug(g.title)}-title`}>
              <CardHeader>
                <CardTitle id={`${slug(g.title)}-title`}>{g.title}</CardTitle>
                <CardDescription>{groupNote(g.settings)}</CardDescription>
              </CardHeader>
              <CardContent className="grid gap-0 divide-y">
                {g.settings.map((s) => (
                  <SettingRow
                    key={s.key}
                    s={s}
                    value={draft[s.key] ?? initialValue(s)}
                    error={errors[s.key]}
                    onChange={(v) => {
                      setDraft((d) => ({ ...d, [s.key]: v }))
                      setErrors((prev) => {
                        const next = { ...prev }
                        delete next[s.key]
                        return next
                      })
                    }}
                  />
                ))}
                {g.title.startsWith('Company directory') && <DirectoryTest draft={draft} />}
              </CardContent>
            </Card>
          ))}
        </div>
      </div>
      {dirty.length > 0 && (
        <section className="sticky bottom-4 z-20 mt-6 flex flex-wrap items-center gap-3 rounded-xl border bg-popover/95 p-3 shadow-lg backdrop-blur" aria-label="Unsaved changes">
          <span className="text-sm font-medium">
            {dirty.length} unsaved change{dirty.length === 1 ? '' : 's'}
          </span>
          <span className="hidden truncate text-sm text-muted-foreground sm:inline">{dirty.map(([k]) => byKey.get(k)!.label).join(', ')}</span>
          <div className="ml-auto flex gap-2">
            <Button variant="ghost" onClick={() => { setDraft({}); setErrors({}) }}>
              <Undo2 /> Discard
            </Button>
            <Button onClick={submit} loading={save.isPending}>
              <Save /> Save changes
            </Button>
          </div>
        </section>
      )}
    </>
  )
}

function groupNote(settings: SettingView[]): string {
  const scopes = new Set(settings.map((s) => s.scope))
  if (scopes.size === 1 && scopes.has('stack')) return 'In .env: saved here, applied on the host with scripts/apply-settings.sh.'
  if (scopes.size === 1 && scopes.has('live')) return 'Applies as soon as you save.'
  return 'Each setting says when it applies.'
}

const scopeBadge: Record<SettingView['scope'], { icon: typeof Zap; text: string; tip: string }> = {
  live: { icon: Zap, text: 'At once', tip: 'Applies as soon as it is saved.' },
  apprestart: { icon: RotateCw, text: 'Restart', tip: 'Read when the app starts: restart it to apply (a few seconds).' },
  stack: { icon: Server, text: '.env', tip: 'Read by other services from .env: saved here, applied on the host with scripts/apply-settings.sh.' },
}

function SettingRow({ s, value, error, onChange }: { s: SettingView; value: string; error?: string; onChange: (v: string) => void }) {
  const id = `setting-${s.key.replace(/[^A-Za-z0-9]/g, '-')}`
  const scope = scopeBadge[s.scope]
  const save = useSave()
  const reset = (key: string, what: string) =>
    save.mutate([{ key, reset: true }], { onSuccess: () => toast.success(what), onError: (e) => toast.error(errorMessage(e)) })
  const describedBy = `${id}-help${error ? ` ${id}-error` : ''}`
  return (
    <div className="grid gap-3 py-4 first:pt-0 last:pb-0 md:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] md:gap-6">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-1.5">
          <Label htmlFor={id} className="text-sm font-medium">
            {s.label}
          </Label>
          <Tooltip content={scope.tip}>
            <button type="button" className="inline-flex rounded outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`${scope.text}: ${scope.tip}`}>
              <Badge variant="outline" className="text-muted-foreground">
                <scope.icon /> {scope.text}
              </Badge>
            </button>
          </Tooltip>
          {s.source === 'saved' && <Badge>Changed</Badge>}
          {s.pendingSet && (
            <Badge variant="warning">
              <Clock /> Pending
            </Badge>
          )}
          {s.restartPending && (
            <Badge variant="warning">
              <RotateCw /> Needs restart
            </Badge>
          )}
          {s.dangerous && (
            <Tooltip content="A wrong value can stop a service from starting.">
              <button type="button" className="inline-flex rounded outline-none focus-visible:ring-[3px] focus-visible:ring-ring" aria-label="Careful: a wrong value can stop a service from starting">
                <AlertTriangle className="size-3.5 text-warning" aria-hidden="true" />
              </button>
            </Tooltip>
          )}
        </div>
        <p id={`${id}-help`} className="mt-1 text-sm text-muted-foreground">
          {s.help}
          {s.impact && <span className="block text-xs">{s.impact}</span>}
        </p>
        <code className="mt-1 block font-mono text-[0.6875rem] text-muted-foreground">{s.key}</code>
      </div>
      <div className="grid content-start gap-1.5">
        <Editor s={s} id={id} value={value} onChange={onChange} describedBy={describedBy} invalid={!!error} />
        {error && (
          <p id={`${id}-error`} className="text-xs font-medium text-destructive-ink">
            {error}
          </p>
        )}
        <Provenance s={s} />
        {(s.source === 'saved' || s.pendingSet) && (
          <div>
            <Button
              variant="link"
              size="sm"
              className="h-auto px-0 text-xs"
              loading={save.isPending}
              onClick={() => (s.pendingSet ? reset(s.key, 'Pending change discarded') : reset(s.key, `${s.label}: back to ${s.environmentValue !== null ? 'the .env value' : 'the default'}`))}
            >
              {s.pendingSet ? <Undo2 /> : <RotateCcw />} {s.pendingSet ? 'Discard pending change' : s.environmentValue !== null ? 'Back to the .env value' : 'Back to the default'}
            </Button>
          </div>
        )}
      </div>
    </div>
  )
}

/** Where the value comes from, in words. */
function Provenance({ s }: { s: SettingView }) {
  let text: ReactNode = null
  if (s.scope === 'stack') {
    if (s.pendingSet) text = s.type === 'secret' ? 'A new value is waiting to be applied.' : <>Now: <code className="font-mono">{s.value || '(empty)'}</code>. The new value is waiting to be applied.</>
    else if (s.type === 'secret') text = s.isSet ? 'Set in .env. Type a new value to replace it.' : 'Not set.'
  } else if (s.source === 'saved' && s.environmentValue !== null && s.type !== 'secret') {
    text = <>Overrides <code className="font-mono">{s.environmentValue || '(empty)'}</code> from .env.</>
  } else if (s.type === 'secret') {
    text = s.isSet ? 'Set. Type a new value to replace it.' : 'Not set.'
  } else if (s.source === 'default' && s.default) {
    text = 'The default.'
  }
  return text ? <p className="text-xs text-muted-foreground">{text}</p> : null
}

function Editor({ s, id, value, onChange, describedBy, invalid }: { s: SettingView; id: string; value: string; onChange: (v: string) => void; describedBy: string; invalid: boolean }) {
  const common = { id, 'aria-describedby': describedBy, 'aria-invalid': invalid || undefined }
  switch (s.type) {
    case 'boolean':
      return <Switch {...common} checked={value === 'true'} onCheckedChange={(v) => onChange(v ? 'true' : 'false')} />
    case 'choice':
      return (
        <Select value={value || NONE} onValueChange={(v) => onChange(v === NONE ? '' : v)}>
          <SelectTrigger {...common}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {s.optional && <SelectItem value={NONE}>Not set (the default)</SelectItem>}
            {s.options!.map((o) => (
              <SelectItem key={o} value={o}>
                {o}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )
    case 'choices': {
      const chosen = new Set(value.split(',').map((v) => v.trim()).filter(Boolean))
      return (
        <fieldset id={id} aria-describedby={describedBy} className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          <legend className="sr-only">{s.label}</legend>
          {s.options!.map((o) => (
            <Label key={o} className="font-normal">
              <Checkbox
                checked={chosen.has(o)}
                onCheckedChange={(c) => {
                  const next = new Set(chosen)
                  if (c) next.add(o)
                  else next.delete(o)
                  onChange(s.options!.filter((x) => next.has(x)).join(','))
                }}
              />
              <span className="font-mono text-xs">{o}</span>
            </Label>
          ))}
        </fieldset>
      )
    }
    case 'duration':
      return (
        <div className="flex items-center gap-2">
          <Input {...common} inputMode="decimal" value={value} onChange={(e) => onChange(e.target.value)} className="w-32" />
          <span className="text-sm text-muted-foreground">{s.unit}</span>
        </div>
      )
    case 'wholenumber':
    case 'number': {
      const hint = s.unit === 'bytes' ? bytesHint(value) : null
      return (
        <div className="flex items-center gap-2">
          <Input {...common} inputMode={s.type === 'number' ? 'decimal' : 'numeric'} value={value} onChange={(e) => onChange(e.target.value)} placeholder={s.optional ? 'not set' : undefined} className="w-40" />
          {hint && <span className="text-sm text-muted-foreground">= {hint}</span>}
          {s.min !== null && s.max !== null && s.unit !== 'bytes' && (
            <span className="text-xs text-muted-foreground">
              {s.min}–{s.max}
            </span>
          )}
        </div>
      )
    }
    case 'secret':
      return <Input {...common} type="password" autoComplete="new-password" value={value} onChange={(e) => onChange(e.target.value)} placeholder={s.isSet || s.pendingSet ? '•••••••• (unchanged)' : 'not set'} />
    default:
      return <Input {...common} value={value} onChange={(e) => onChange(e.target.value)} placeholder={s.patternHelp ?? (s.optional ? 'not set' : undefined)} className={cn(s.key.includes('ARGS') || s.key.includes('FILES') ? 'font-mono text-xs' : undefined)} />
  }
}

function DirectoryTest({ draft }: { draft: Record<string, string> }) {
  const test = useMutation({
    mutationFn: () => api<{ ok: boolean; message: string }>('/api/admin/config/ldap-test', { body: Object.fromEntries(Object.entries(draft).filter(([k]) => k.startsWith('Ldap:'))) }),
  })
  return (
    <div className="grid gap-3 pt-4">
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" onClick={() => test.mutate()} loading={test.isPending}>
          <PlugZap /> Test connection
        </Button>
        <span className="text-sm text-muted-foreground">Tries the values above, saved or not.</span>
      </div>
      {test.data && <Alert variant={test.data.ok ? 'success' : 'destructive'}>{test.data.message}</Alert>}
      {test.error && <Alert variant="destructive">{errorMessage(test.error)}</Alert>}
    </div>
  )
}

function PendingBanner({ count }: { count: number }) {
  return (
    <Alert variant="warning" title={`${count} .env change${count === 1 ? ' is' : 's are'} waiting to be applied`}>
      <p className="mb-2">
        Other services read these from <code className="font-mono">.env</code>. On the host, in the stack folder, run the command below. It shows each change and asks before
        writing <code className="font-mono">.env</code>, then restarts only what changed.
      </p>
      <CodeBlock code="./scripts/apply-settings.sh" label="apply command" />
      <p className="mt-2 flex items-center gap-1.5 text-xs">
        <Terminal className="size-3.5" aria-hidden="true" /> Settings you change here never reach Docker directly: this web app has no Docker access.
      </p>
    </Alert>
  )
}

function RestartBanner() {
  const queryClient = useQueryClient()
  const [phase, setPhase] = useState<'idle' | 'restarting' | 'failed'>('idle')
  const restart = async () => {
    setPhase('restarting')
    try {
      await api('/api/admin/config/restart', { body: {} })
      // Wait for it to go away and come back (the container's restart policy).
      await new Promise((r) => setTimeout(r, 3000))
      for (let i = 0; i < 60; i++) {
        try {
          await api('/api/info')
          await queryClient.invalidateQueries({ queryKey: ['admin', 'config'] })
          toast.success('The app restarted with the new settings')
          setPhase('idle')
          return
        } catch {
          await new Promise((r) => setTimeout(r, 1000))
        }
      }
      setPhase('failed')
    } catch (e) {
      toast.error(errorMessage(e))
      setPhase('failed')
    }
  }
  return (
    <Alert
      variant="info"
      title="Some changes apply when the app restarts"
      action={
        <Button size="sm" onClick={restart} loading={phase === 'restarting'}>
          <RotateCw /> Restart the app now
        </Button>
      }
    >
      It takes a few seconds; people see a short pause, and answers being written are cut off.
      {phase === 'failed' && <span className="block text-destructive-ink">The app has not come back yet. Check it with docker compose ps.</span>}
    </Alert>
  )
}
