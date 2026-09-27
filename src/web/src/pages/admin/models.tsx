import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, CircleDot, Eye, HelpCircle, Image as ImageIcon, Loader2, Pencil, Pin, Plus, Power, PowerOff, Settings2, Trash2, XCircle } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { AccessPicker, type AccessRule } from './access-picker'
import { ModelForm, type SavedModel } from './model-form'
import { bytes, summary, type ModelProfile } from './model-profile'

interface ModelRow extends SavedModel {
  /** env: the .env model; local: added here; gateway: served by the gateway otherwise (cloud, pictures). */
  source: 'env' | 'local' | 'gateway'
  mode: string
  /** failed: its last load exited with an error (the engine's log says why); missing: the engine does not list it (yet). */
  status: 'loaded' | 'loading' | 'unloaded' | 'failed' | 'missing' | null
  vision: boolean
  atGateway?: boolean
  access: AccessRule
  /** What its file is, when it is in the library. */
  profile?: ModelProfile | null
  /** Kept loaded: loaded at start, and again whenever it is not. */
  kept?: boolean
}

interface Plan {
  problems: { field: string; message: string; error: boolean }[]
  gpus: { index: number; name: string; budget: number; need: number; models: string[] }[]
}

interface ModelsView {
  engine: {
    enabled: boolean
    error: string | null
    checkedAt: string | null
    /** The models kept loaded, and how many the engine holds at once. */
    kept: string[]
    max: number
    /** A place is left beside the kept models: the others load when asked for. */
    onRequest: boolean
    loaded: string[]
    loading: string[]
    gpus: { index: number; name: string; total: number }[]
    plan: Plan | null
  }
  models: ModelRow[]
}

export function ModelsPage() {
  const queryClient = useQueryClient()
  const models = useQuery({
    queryKey: ['admin', 'models'],
    queryFn: ({ signal }) => api<ModelsView>('/api/admin/models', { signal }),
    // Faster while a model loads, so the page shows it the moment it is ready.
    refetchInterval: (q) => (q.state.data?.engine.loading.length ? 3000 : 15000),
  })
  const [editing, setEditing] = useState<ModelRow | 'new' | null>(null)
  if (models.isPending) return <PageSkeleton />
  if (models.error) return <QueryError error={models.error} retry={() => models.refetch()} />
  const { engine } = models.data
  return (
    <>
      <PageHeader
        title="Models"
        description="Every model at the gateway, and who may use each. The engine holds several at once: the ones kept loaded stay, and the others load when asked for."
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/admin/model">
                <Settings2 /> Deployment
              </Link>
            </Button>
            {engine.enabled && (
              <Button onClick={() => setEditing('new')}>
                <Plus /> Add a model
              </Button>
            )}
          </>
        }
      />
      {!engine.enabled && <Alert className="mb-4">Switching and adding models needs the llama.cpp engine (the llamacpp profile). Who may use each model applies with any engine.</Alert>}
      {engine.enabled && engine.error && (
        <Alert variant="warning" className="mb-4" title="The engine did not answer">
          {engine.error}
        </Alert>
      )}
      {engine.enabled && <EngineSummary engine={engine} />}
      <div className="stagger grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
        {models.data.models.map((m) => (
          <ModelCard key={m.name} model={m} engine={engine} onEdit={() => setEditing(m)} onChanged={() => queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })} />
        ))}
      </div>
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">
          {editing !== null && <ModelForm key={editing === 'new' ? 'new' : editing.name} saved={editing === 'new' ? null : editing} gpus={engine.gpus} onClose={() => setEditing(null)} />}
        </DialogContent>
      </Dialog>
    </>
  )
}

/** How many models the engine holds, which are kept, and how full each GPU is with them. */
function EngineSummary({ engine }: { engine: ModelsView['engine'] }) {
  const plan = engine.plan
  return (
    <Card className="mb-4">
      <CardHeader>
        <CardTitle className="text-base">The engine</CardTitle>
        <CardDescription>
          Up to {engine.max} model{engine.max === 1 ? '' : 's'} loaded at once
          {engine.kept.length > 0 ? `, ${engine.kept.length} kept loaded (${engine.kept.join(', ')})` : ', none kept loaded'}.{' '}
          {engine.onRequest
            ? 'Any other loads when someone asks for it; at the limit, the one used least recently unloads, and a kept one comes back.'
            : 'Every place is kept, so no other model loads on request. Raise "Models loaded at once" under Settings for more.'}
        </CardDescription>
      </CardHeader>
      {plan && (plan.gpus.length > 0 || plan.problems.length > 0) && (
        <CardContent className="grid gap-3">
          {plan.gpus.map((g) => (
            <div key={g.index} className="grid gap-1.5">
              <div className="flex flex-wrap justify-between gap-2 text-sm">
                <span className="font-medium">
                  GPU {g.index} · {g.name}
                </span>
                <span className="text-muted-foreground">
                  {bytes(g.need)} of {bytes(g.budget)} for models{g.models.length ? ` · ${g.models.join(', ')}` : ''}
                </span>
              </div>
              {/* oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a styled bar; <meter> cannot be styled consistently */}
              <div role="meter" aria-label={`GPU ${g.index}: the models kept loaded`} aria-valuemin={0} aria-valuemax={g.budget} aria-valuenow={Math.min(g.need, g.budget)} className="h-2 overflow-hidden rounded-full bg-muted">
                <div className={cn('h-full rounded-full', g.need > g.budget ? 'bg-warning' : 'bg-primary')} style={{ width: `${Math.min(100, (100 * g.need) / Math.max(g.budget, 1))}%` }} />
              </div>
            </div>
          ))}
          {plan.problems.map((p) => (
            <Alert key={p.message} variant={p.error ? 'destructive' : 'warning'}>
              {p.message}
            </Alert>
          ))}
        </CardContent>
      )}
    </Card>
  )
}

function Status({ status }: { status: ModelRow['status'] }) {
  if (status === 'loaded')
    return (
      <span className="flex items-center gap-1.5 text-sm font-medium text-success-ink">
        <CircleDot className="size-4" aria-hidden="true" /> Loaded
      </span>
    )
  if (status === 'loading')
    return (
      <span className="flex items-center gap-1.5 text-sm text-warning-ink">
        <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Loading…
      </span>
    )
  if (status === 'failed')
    return (
      <span className="flex items-center gap-1.5 text-sm font-medium text-destructive-ink">
        <XCircle className="size-4" aria-hidden="true" /> Could not load
      </span>
    )
  if (status === 'unloaded') return <span className="text-sm text-muted-foreground">Not loaded</span>
  if (status === 'missing')
    return (
      <span className="flex items-center gap-1.5 text-sm text-warning-ink">
        <HelpCircle className="size-4" aria-hidden="true" /> Not in the engine
      </span>
    )
  return null
}

function ModelCard({ model: m, engine, onEdit, onChanged }: { model: ModelRow; engine: ModelsView['engine']; onEdit: () => void; onChanged: () => void }) {
  const confirm = useConfirm()
  const keep = useMutation({
    mutationFn: (on: boolean) => api<{ warning: string | null }>(`/api/admin/models/${encodeURIComponent(m.name)}/keep`, { method: 'PUT', body: { keep: on } }),
    onSuccess: (r, on) => {
      if (r.warning) toast.warning(on ? `${m.name} is kept loaded` : `${m.name} is no longer kept loaded`, { description: r.warning })
      else toast.success(on ? `${m.name} is kept loaded` : `${m.name} is no longer kept loaded`, on ? { description: 'It loads now if it is not loaded, and again whenever it is not.' } : undefined)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const act = useMutation({
    mutationFn: (what: 'load' | 'unload') => api(`/api/admin/models/${encodeURIComponent(m.name)}/${what}`, { body: {} }),
    onSuccess: (_, what) => {
      toast.success(what === 'load' ? `Loading ${m.name}` : `${m.name} unloaded`, what === 'load' ? { description: 'It answers once it is loaded: seconds for a small model, minutes for a large one.' } : undefined)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/models/${encodeURIComponent(m.name)}`, { method: 'DELETE' }),
    onSuccess: () => {
      toast.success(`${m.name} removed`, { description: 'The engine restarts to drop it; the loaded model comes back in a moment.' })
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const access = useMutation({
    mutationFn: (rule: { audience: string; groups: string[] }) => api(`/api/admin/models/${encodeURIComponent(m.name)}/access`, { method: 'PUT', body: rule }),
    onSettled: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const onEngine = m.status !== null && m.status !== 'missing'
  const image = m.mode === 'image_generation'
  // Loading one more needs a place beside the kept models, unless this is one of them.
  const canLoad = m.kept === true || engine.kept.length < engine.max
  return (
    <Card className={cn(m.status === 'loaded' && 'border-success/40')}>
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
          {image ? <ImageIcon className="size-4.5" aria-hidden="true" /> : <Boxes className="size-4.5" aria-hidden="true" />}
        </span>
        <div className="min-w-0 flex-1">
          <CardTitle className="flex flex-wrap items-center gap-2 [overflow-wrap:anywhere]">
            {m.name}
            <Badge variant={m.source === 'local' ? 'default' : 'secondary'}>{m.source === 'env' ? '.env' : m.source === 'local' ? 'Added here' : 'Gateway'}</Badge>
            {image && <Badge variant="outline">Pictures</Badge>}
            {m.kept && (
              <Badge variant="outline">
                <Pin /> Kept loaded
              </Badge>
            )}
            {m.vision && (
              <Badge variant="outline">
                <Eye /> Sees images
              </Badge>
            )}
          </CardTitle>
          <CardDescription className="[overflow-wrap:anywhere]">
            {[m.file, m.context ? `${m.context.toLocaleString('en-US')} tokens of context` : null].filter(Boolean).join(' · ') || (image ? 'An image model' : 'Served by the gateway')}
          </CardDescription>
          {m.profile && <p className="mt-1 text-xs text-muted-foreground [overflow-wrap:anywhere]">{summary(m.profile)}</p>}
        </div>
        <Status status={m.status} />
      </CardHeader>
      <CardContent className="grid gap-4">
        {m.source === 'local' && m.atGateway === false && <Alert variant="warning">Not at the gateway yet: it is added again within a minute.</Alert>}
        {m.status === 'missing' && (
          <Alert variant="warning" title="The engine does not list it">
            It restarts to read a new or changed model, which takes seconds. If this stays, the engine refused the model list and serves the .env model alone:{' '}
            <Link to="/admin/logs?container=llamacpp&level=warn" className="font-medium underline underline-offset-2">
              its warnings and errors
            </Link>{' '}
            say why.
          </Alert>
        )}
        {m.status === 'failed' && (
          <Alert variant="destructive" title="The engine could not load it">
            Its log says why:{' '}
            <Link to="/admin/logs?container=llamacpp&level=warn" className="font-medium underline underline-offset-2">
              the engine's warnings and errors
            </Link>
            , or <code className="text-xs">docker compose logs llamacpp</code> on the host. An incomplete download, a file this llama.cpp cannot read, or too
            little GPU memory are the usual causes. It is not tried again until you load it.
          </Alert>
        )}
        {onEngine && (
          <Label className="flex items-start gap-3 font-normal">
            <Switch checked={m.kept === true} disabled={keep.isPending} onCheckedChange={(on) => keep.mutate(on)} aria-describedby={`keep-${m.name}`} />
            <span className="grid gap-0.5">
              <span className="font-medium">Keep loaded</span>
              <span id={`keep-${m.name}`} className="text-xs text-muted-foreground">
                {m.kept ? 'Loaded at start, and again whenever it is not.' : engine.onRequest ? 'Otherwise it loads when someone asks for it.' : 'Otherwise it loads only when an admin loads it.'}
              </span>
            </span>
          </Label>
        )}
        <AccessPicker value={m.access} onChange={(audience, groups) => access.mutate({ audience, groups })} />
        <div className="flex flex-wrap gap-2">
          {onEngine && m.status !== 'loaded' && (
            <Button
              size="sm"
              loading={act.isPending}
              disabled={m.status === 'loading' || !canLoad}
              title={canLoad ? undefined : 'Every place in the engine keeps a model loaded'}
              onClick={async () => {
                const full = engine.loaded.length + engine.loading.length >= engine.max
                if (
                  await confirm({
                    title: `Load ${m.name}?`,
                    description: full
                      ? 'The engine is full: the model used least recently unloads to make room (a kept one comes back after). Answers wait until this one is loaded: seconds for a small model, minutes for a large one.'
                      : 'It loads beside the models loaded now. Answers wait until it is loaded: seconds for a small model, minutes for a large one.',
                    confirm: 'Load',
                  })
                )
                  act.mutate('load')
              }}
            >
              <Power /> Load
            </Button>
          )}
          {onEngine && m.status === 'loaded' && (
            <Button
              size="sm"
              variant="outline"
              loading={act.isPending}
              onClick={async () => {
                const description = m.kept
                  ? 'It stops being kept loaded, too. Chats that use it wait for it to load again when asked for.'
                  : 'Chats that use it wait for it to load again when asked for.'
                if (await confirm({ title: `Unload ${m.name}?`, description, confirm: 'Unload', destructive: true }))
                  act.mutate('unload')
              }}
            >
              <PowerOff /> Unload
            </Button>
          )}
          {m.source === 'local' && (
            <>
              <Button size="sm" variant="outline" onClick={onEdit}>
                <Pencil /> Edit
              </Button>
              <Button
                size="sm"
                variant="outline"
                className="text-destructive-ink"
                onClick={async () => {
                  const description = m.kept
                    ? 'It is kept loaded: it stops being kept, and chats that chose it fall back to a loaded model. The model file stays in the library.'
                    : 'Chats that chose it fall back to a loaded model. The model file stays in the library.'
                  if (await confirm({ title: `Remove ${m.name}?`, description, confirm: 'Remove', destructive: true })) remove.mutate()
                }}
              >
                <Trash2 /> Remove
              </Button>
            </>
          )}
        </div>
      </CardContent>
    </Card>
  )
}
