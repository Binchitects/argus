import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Clock, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { dayNames, daysLabel, fullDays, timesLabel } from './hours'

export interface ModelWindow {
  id: string
  name: string
  enabled: boolean
  /** ISO: Monday 1 … Sunday 7. */
  days: number[]
  start: string
  end: string
  keep: string[]
  defaultModel: string | null
  order: number
}

interface HoursView {
  timeZone: string
  problem: string | null
  localNow: string
  active: { id: string; name: string; until: string | null } | null
  max: number
  windows: ModelWindow[]
}

/**
 * Working hours: by day and hour, which models the engine keeps loaded and which one
 * new chats start on. A small fast model in busy hours, the big one at night. Outside
 * every window, the pinned models are kept.
 */
export function WorkingHours({ engineModels, chatModels }: { engineModels: string[]; chatModels: string[] }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const hours = useQuery({ queryKey: ['admin', 'model-hours'], queryFn: ({ signal }) => api<HoursView>('/api/admin/model-hours', { signal }), refetchInterval: 60_000 })
  const [editing, setEditing] = useState<ModelWindow | 'new' | null>(null)
  const changed = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['admin', 'model-hours'] }), queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })])
  const toggle = useMutation({
    mutationFn: (w: ModelWindow) => api(`/api/admin/model-hours/${w.id}`, { method: 'PATCH', body: { enabled: !w.enabled } }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (w: ModelWindow) => api(`/api/admin/model-hours/${w.id}`, { method: 'DELETE' }),
    onSuccess: async (_, w) => {
      await changed()
      toast.success(`${w.name} removed`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const data = hours.data
  return (
    <Card className="mb-4" aria-label="Working hours">
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <CardTitle className="text-base">Working hours</CardTitle>
          <CardDescription>
            Which models are kept loaded, and which one new chats start on, by day and hour: a small fast model in busy hours, the big one at night. Outside them, the pinned models are kept.
            {data && (
              <>
                {' '}
                Times are in <span className="font-medium text-foreground">{data.timeZone}</span> (now {data.localNow.slice(11)};{' '}
                <Link to="/admin/settings" className="text-primary-ink underline underline-offset-2">
                  change it under Settings
                </Link>
                ).
              </>
            )}
          </CardDescription>
        </div>
        <Button variant="outline" size="sm" onClick={() => setEditing('new')}>
          <Plus /> Add working hours
        </Button>
      </CardHeader>
      <CardContent className="grid gap-3">
        {hours.error && <Alert variant="destructive">{errorMessage(hours.error)}</Alert>}
        {data?.problem && <Alert variant="warning">{data.problem}</Alert>}
        {data && data.windows.length === 0 && <p className="text-sm text-muted-foreground">None yet: the pinned models are kept at every hour.</p>}
        {data && data.windows.length > 0 && (
          <ul className="grid gap-2" aria-label="Working hours">
            {data.windows.map((w) => {
              const now = data.active?.id === w.id
              return (
                <li key={w.id} className={cn('flex flex-wrap items-center gap-3 rounded-lg border p-3', now && 'border-primary/50 bg-primary/5', !w.enabled && 'opacity-70')}>
                  <Clock className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <div className="grid min-w-0 flex-1 gap-0.5">
                    <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                      {w.name}
                      {now && <Badge>Now{data.active?.until ? `, until ${new Date(data.active.until).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', timeZone: data.timeZone })}` : ''}</Badge>}
                      {!w.enabled && <Badge variant="outline">Off</Badge>}
                    </p>
                    <p className="text-xs text-muted-foreground [overflow-wrap:anywhere]">
                      {daysLabel(w.days)}, {timesLabel(w.start, w.end)} · keeps {w.keep.length ? w.keep.join(', ') : 'none'} loaded
                      {w.defaultModel ? ` · new chats on ${w.defaultModel}` : ''}
                    </p>
                  </div>
                  <Switch checked={w.enabled} disabled={toggle.isPending} onCheckedChange={() => toggle.mutate(w)} aria-label={`${w.name} on`} />
                  <Button variant="ghost" size="icon-sm" onClick={() => setEditing(w)} aria-label={`Edit ${w.name}`}>
                    <Pencil />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label={`Remove ${w.name}`}
                    onClick={async () => {
                      if (await confirm({ title: `Remove ${w.name}?`, description: now ? 'They are in force now: the pinned models are kept again at once.' : undefined, confirm: 'Remove', destructive: true })) remove.mutate(w)
                    }}
                  >
                    <Trash2 />
                  </Button>
                </li>
              )
            })}
          </ul>
        )}
      </CardContent>
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="sm:max-w-xl">
          {editing !== null && (
            <WindowForm key={editing === 'new' ? 'new' : editing.id} saved={editing === 'new' ? null : editing} max={data?.max ?? 1} engineModels={engineModels} chatModels={chatModels} onClose={() => setEditing(null)} onSaved={changed} />
          )}
        </DialogContent>
      </Dialog>
    </Card>
  )
}

const USUAL = '(usual)'

function WindowForm({ saved, max, engineModels, chatModels, onClose, onSaved }: {
  saved: ModelWindow | null
  max: number
  engineModels: string[]
  chatModels: string[]
  onClose: () => void
  onSaved: () => Promise<unknown>
}) {
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    days: saved?.days ?? [1, 2, 3, 4, 5],
    start: saved?.start ?? '08:00',
    end: saved?.end ?? '18:00',
    keep: saved?.keep ?? [],
    defaultModel: saved?.defaultModel ?? '',
    enabled: saved?.enabled ?? true,
  })
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => (saved ? api(`/api/admin/model-hours/${saved.id}`, { method: 'PATCH', body: form }) : api('/api/admin/model-hours', { body: form })),
    onSuccess: async () => {
      await onSaved()
      toast.success(saved ? 'Working hours saved.' : 'Working hours added.', { description: 'In force at once when they cover now: their models load, and the others make room.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const toggle = <T,>(list: T[], v: T, on: boolean) => (on ? [...list, v] : list.filter((x) => x !== v))
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add working hours'}</DialogTitle>
        <DialogDescription>On these days, from and until these times, the engine keeps these models loaded instead of the pinned ones.</DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <Field label="Name" hint="e.g. Busy hours, Nights.">
          <Input required maxLength={100} autoComplete="off" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </Field>
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">Days</legend>
          <div className="flex flex-wrap gap-1.5">
            {dayNames.map((d, i) => (
              <label key={d} className="flex cursor-pointer items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary/10 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring">
                <input type="checkbox" className="size-3.5 accent-primary outline-none" checked={form.days.includes(i + 1)} onChange={(e) => setForm({ ...form, days: toggle(form.days, i + 1, e.target.checked).sort((a, b) => a - b) })} aria-label={fullDays[i]} />
                {d}
              </label>
            ))}
          </div>
        </fieldset>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="From">
            <Input type="time" required value={form.start} onChange={(e) => setForm({ ...form, start: e.target.value })} />
          </Field>
          <Field label="Until" hint="Before From: past midnight. The same: all day.">
            <Input type="time" required value={form.end} onChange={(e) => setForm({ ...form, end: e.target.value })} />
          </Field>
        </div>
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">Kept loaded</legend>
          <p className="text-xs text-muted-foreground">
            Up to {max} (the engine&apos;s places). Leave a place free for others to load when asked for.
          </p>
          <div className="grid gap-1.5 sm:grid-cols-2">
            {engineModels.map((m) => (
              <label key={m} className="flex cursor-pointer items-center gap-2 rounded-md border px-2.5 py-1.5 text-sm [overflow-wrap:anywhere] has-[:checked]:border-primary has-[:checked]:bg-primary/5 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring">
                <input type="checkbox" className="size-3.5 shrink-0 accent-primary outline-none" checked={form.keep.includes(m)} onChange={(e) => setForm({ ...form, keep: toggle(form.keep, m, e.target.checked) })} />
                {m}
              </label>
            ))}
          </div>
        </fieldset>
        <Field label="New chats start on">
          <Select value={form.defaultModel || USUAL} onValueChange={(v) => setForm({ ...form, defaultModel: v === USUAL ? '' : v })}>
            <SelectTrigger className="min-w-0 [&>span]:truncate">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={USUAL}>The usual: the first loaded model</SelectItem>
              {chatModels.map((m) => (
                <SelectItem key={m} value={m}>
                  {m}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <div className="flex items-center gap-2">
          <Switch id="window-on" checked={form.enabled} onCheckedChange={(enabled) => setForm({ ...form, enabled })} />
          <Label htmlFor="window-on">On</Label>
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending} disabled={form.days.length === 0}>
            {saved ? 'Save' : 'Add'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
