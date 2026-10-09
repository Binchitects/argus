import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Eye, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'
import { cleanupKinds, plural, size, type CleanupDone, type CleanupPlan, type CleanupType, type StorageReport } from './storage-api'

/** Every clean-up, each shown before it runs. */
export function Cleanups({ settings }: { settings: StorageReport['settings'] | undefined }) {
  return (
    <div className="grid gap-4 xl:grid-cols-2">
      {cleanupKinds.map((k) => (
        <CleanupCard key={k.kind} k={k} start={k.param === 'keep' ? settings?.backupsKept : k.kind === 'old-media' ? settings?.mediaDays : k.param === 'days' ? 7 : undefined} />
      ))}
    </div>
  )
}

/** One clean-up: its number, what it would take now and the room that frees, and the run after a confirmation. */
function CleanupCard({ k, start }: { k: CleanupType; start: number | undefined }) {
  const client = useQueryClient()
  const confirm = useConfirm()
  const [value, setValue] = useState<string | null>(null)
  const [asked, setAsked] = useState(false)
  const [chosen, setChosen] = useState<Set<string>>(new Set())
  const [running, setRunning] = useState(false)
  const number = value ?? (start !== undefined ? String(start) : '')
  const valid = !k.param || (/^\d+$/.test(number) && Number(number) >= 1)
  const query = k.param && number ? `?${k.param}=${Number(number)}` : ''
  const plan = useQuery({
    queryKey: ['admin', 'storage', 'cleanup', k.kind, query],
    queryFn: ({ signal }) => api<CleanupPlan>(`/api/admin/storage/cleanups/${k.kind}${query}`, { signal }),
    enabled: asked && valid,
  })
  const p = plan.data
  const models = k.kind === 'unused-models'
  const takes = models ? (p?.items.filter((i) => chosen.has(i.id)) ?? []) : null
  const count = takes ? takes.length : (p?.count ?? 0)
  const bytes = takes ? takes.reduce((a, i) => a + i.bytes, 0) : (p?.bytes ?? 0)

  const run = async () => {
    if (!p) return
    const ok = await confirm({
      title: `${k.title}: remove ${plural(count, ...k.noun)} (${size(bytes)})?`,
      description: [
        'They are gone for good; this cannot be undone.',
        p.warning,
        p.held.count ? `${plural(p.held.count, 'file')} of people on legal hold stay.` : null,
      ].filter(Boolean).join(' '),
      confirm: 'Clean up',
      destructive: true,
    })
    if (!ok) return
    setRunning(true)
    try {
      const done = await api<CleanupDone>(`/api/admin/storage/cleanups/${k.kind}`, {
        body: { ...(k.param ? { [k.param]: Number(number) } : {}), ...(models ? { only: [...chosen] } : {}) },
      })
      if (done.failed.length) toast.error(`${plural(done.count, ...k.noun)} removed; ${done.failed.length} could not be`, { description: done.failed.slice(0, 3).join(' ') })
      else toast.success(`${plural(done.count, ...k.noun)} removed, ${size(done.bytes)} freed`)
      setChosen(new Set())
      await client.invalidateQueries({ queryKey: ['admin', 'storage'] })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setRunning(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{k.title}</CardTitle>
        <CardDescription>{k.about}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex flex-wrap items-end gap-2">
          {k.param && (
            <Label className="grid gap-1 text-xs font-normal">
              {k.param === 'days' ? 'Older than (days)' : 'Keep the newest'}
              <Input
                inputMode="numeric"
                className="h-9 w-28"
                value={number}
                onChange={(e) => setValue(e.target.value)}
                aria-invalid={!valid}
                aria-label={`${k.title}: ${k.param === 'days' ? 'older than, in days' : 'backups to keep'}`}
              />
            </Label>
          )}
          <Button variant="outline" size="sm" className="h-9" disabled={!valid} loading={plan.isFetching} onClick={() => (asked ? void plan.refetch() : setAsked(true))}>
            <Eye /> {asked ? 'Look again' : 'Preview'}
          </Button>
          {p && !p.problem && (
            <Button variant="destructive" size="sm" className="h-9" disabled={count === 0 || plan.isFetching} loading={running} onClick={() => void run()}>
              <Trash2 /> Clean up
            </Button>
          )}
        </div>
        {!valid && <p className="text-sm text-destructive-ink">A whole number, 1 or more.</p>}
        {plan.error && <QueryError error={plan.error} retry={() => plan.refetch()} />}
        {p && (
          <div className="grid gap-2 text-sm" aria-live="polite">
            {p.problem && <Alert variant="warning">{p.problem}</Alert>}
            <p>
              {p.count === 0 ? (
                `Nothing to remove now.`
              ) : (
                <>
                  <span className="font-medium">{plural(p.count, ...k.noun)}</span> would go, freeing <span className="font-medium">{size(p.bytes)}</span>.
                </>
              )}
              {p.held.count > 0 && ` Legal hold keeps ${plural(p.held.count, 'file')} (${size(p.held.bytes)}).`}
            </p>
            {p.warning && <Alert variant="warning">{p.warning}</Alert>}
            {p.items.length > 0 && (
              <ul className="grid max-h-60 gap-1 overflow-y-auto rounded-lg border p-2" aria-label={`${k.title}: what would go`}>
                {p.items.map((i) => (
                  <li key={i.id} className="flex items-start justify-between gap-3">
                    <span className="flex min-w-0 items-start gap-2">
                      {models && (
                        <Checkbox
                          className="mt-0.5"
                          checked={chosen.has(i.id)}
                          onCheckedChange={(v) => setChosen((s) => { const n = new Set(s); if (v) n.add(i.id); else n.delete(i.id); return n })}
                          aria-label={`Delete ${i.name}`}
                        />
                      )}
                      <span className="min-w-0">
                        <span className="break-all">{i.name}</span>
                        <span className="block text-xs text-muted-foreground">{[i.person, i.at ? when(i.at) : null, i.note].filter(Boolean).join(' · ')}</span>
                      </span>
                    </span>
                    <span className="tabular-nums whitespace-nowrap">{size(i.bytes)}</span>
                  </li>
                ))}
                {p.count > p.items.length && <li className="text-xs text-muted-foreground">and {plural(p.count - p.items.length, 'more', 'more')}</li>}
              </ul>
            )}
            {models && p.items.length > 0 && <p className="text-xs text-muted-foreground">{chosen.size ? `${plural(chosen.size, 'model')} chosen, ${size(bytes)}.` : 'Choose the models to delete.'}</p>}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
