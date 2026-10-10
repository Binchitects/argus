import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AudioLines, Boxes, CircleDot, Clapperboard, Clock, Eye, Search, HelpCircle, Image as ImageIcon, Loader2, Mic, Pencil, Pin, Plus, Power, PowerOff, Sparkles, Trash2, XCircle } from 'lucide-react'
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
import { api, ApiError, errorMessage } from '@/lib/api'
import { money } from '@/lib/format'
import { cn } from '@/lib/utils'
import { AccessPicker, type AccessRule } from './access-picker'
import { ModelForm, type SavedModel } from './model-form'
import { WorkingHours } from './model-hours'
import { DefaultModelsCard } from './default-models'
import { DownloadsCard, HuggingFaceBrowser } from './huggingface'
import { bytes, cacheLine, summary, type ModelProfile, type TokenCache } from './model-profile'
import { ServersSection } from './servers'

interface ModelRow extends SavedModel {
  /** local: added here; remote: on another GPU server; media: a picture, video or speech model of this stack; gateway: served by the gateway otherwise. */
  source: 'local' | 'remote' | 'media' | 'gateway'
  /** For a remote model: its server, and its id there. */
  server?: string
  remote?: string
  mode: string
  /** unloading: told to unload, and not stopped yet; failed: its last load exited with an error (the engine's log says why);
   * missing: the engine does not list it (yet); waiting: a media model whose files are being fetched; off: its server does not run. */
  status: 'loaded' | 'loading' | 'unloading' | 'unloaded' | 'failed' | 'missing' | 'waiting' | 'off' | null
  /** Loading or unloading: why ("loaded by ada", "for a chat or an API request", "making room for X", "kept loaded"), and since when. */
  why?: string | null
  since?: string | null
  /** Loaded by an admin in place of a model that never makes room: it never makes room either, until unloaded. */
  instead?: boolean
  /** How often lately the engine unloaded it by its own choice, to load another. */
  evicted?: number
  /** A media model: on (at the gateway, in the chat's tools) or off. */
  enabled?: boolean
  vision: boolean
  atGateway?: boolean
  access: AccessRule
  /** What its file is, when it is in the library. */
  profile?: ModelProfile | null
  /** A model added here: what its token cache keeps, and the RAM it takes. */
  cache?: TokenCache | null
  /** Failed to load: when it is tried again by itself. */
  retryAt?: string | null
  /** Pinned to keep loaded: loaded at start, and again whenever it is not (outside working hours). */
  kept?: boolean
  /** Kept loaded now: pinned, or by the working hours in force. */
  keptNow?: boolean
  /** A picture, video or speech model's price (Settings → Prices). */
  unitPrice?: { unit: string; amount: number }
}

/** "$0.20 in · $0.02 cached · $0.80 out per 1M tokens (the defaults)", or a media model's "$0.01 per picture". */
function priceLine(m: Pick<ModelRow, 'price' | 'unitPrice'>): string | null {
  if (m.unitPrice) return `${money(m.unitPrice.amount)} per ${m.unitPrice.unit} (Settings → Prices)`
  const p = m.price
  if (!p) return null
  const own = p.own.input || p.own.cachedInput || p.own.output
  const all = p.own.input && p.own.cachedInput && p.own.output
  return `${money(p.input)} in · ${money(p.cachedInput)} cached · ${money(p.output)} out per 1M tokens${all ? '' : own ? ' (some from the defaults)' : ' (the defaults)'}`
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
    /** The models kept loaded now (the working hours', else the pinned), the pinned ones, and how many the engine holds at once. */
    kept: string[]
    pinned: string[]
    max: number
    /** Working hours in force now, and until when. */
    hours: { id: string; name: string; until: string | null } | null
    /** A place is left beside the kept models: the others load when asked for. */
    onRequest: boolean
    loaded: string[]
    loading: string[]
    unloading: string[]
    gpus: { index: number; name: string; total: number }[]
    plan: Plan | null
  }
  /** The model for sub-agents and small steps (Settings → Model), and what keeps it from doing them well. */
  small: { name: string; warning: string | null } | null
  models: ModelRow[]
}

export function ModelsPage() {
  const queryClient = useQueryClient()
  const models = useQuery({
    queryKey: ['admin', 'models'],
    queryFn: ({ signal }) => api<ModelsView>('/api/admin/models', { signal }),
    // Faster while a model loads or unloads (an engine model or a picture, video or speech one), so the page shows it the moment it is done.
    refetchInterval: (q) => (q.state.data?.models.some((m) => m.status === 'loading' || m.status === 'unloading') ? 2000 : 15000),
  })
  const [editing, setEditing] = useState<ModelRow | 'new' | { preset: string } | null>(null)
  const [hf, setHf] = useState(false)
  if (models.isPending) return <PageSkeleton />
  if (models.error) return <QueryError error={models.error} retry={() => models.refetch()} />
  const { engine, small } = models.data
  return (
    <>
      <PageHeader
        title="Models"
        description="Every model at the gateway, and who may use each. The engine holds several at once: the ones kept loaded stay, and the others load when asked for."
        actions={
          <>
            {engine.enabled && (
              <Button variant="outline" onClick={() => setHf(true)}>
                <Search /> Find on Hugging Face
              </Button>
            )}
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
      <DefaultModelsCard />
      {engine.enabled && <DownloadsCard onAdd={(preset) => setEditing({ preset })} />}
      {small?.warning && (
        <Alert variant="warning" className="mb-4" title="The model for small steps">
          {small.warning}
        </Alert>
      )}
      {engine.enabled && <EngineSummary engine={engine} />}
      {engine.enabled && (
        <WorkingHours
          engineModels={models.data.models.filter((m) => m.source === 'local').map((m) => m.name)}
          chatModels={[...new Set(models.data.models.filter((m) => (m.mode ?? 'chat') === 'chat').map((m) => m.name))]}
        />
      )}
      <ServersSection onChanged={() => queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })} />
      <div className="stagger grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
        {models.data.models.map((m) => (
          <ModelCard
            key={`${m.source}:${m.server ?? ''}:${m.name}`}
            model={m}
            engine={engine}
            small={m.name === small?.name && (m.mode ?? 'chat') === 'chat'}
            onEdit={() => setEditing(m)}
            onChanged={() => queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })}
          />
        ))}
      </div>
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">
          {editing !== null && (
            <ModelForm
              key={editing === 'new' ? 'new' : 'preset' in editing ? `preset:${editing.preset}` : editing.name}
              saved={editing === 'new' || 'preset' in editing ? null : editing}
              file={editing !== 'new' && 'preset' in editing ? editing.preset : undefined}
              gpus={engine.gpus}
              onClose={() => setEditing(null)}
            />
          )}
        </DialogContent>
      </Dialog>
      <Dialog open={hf} onOpenChange={setHf}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">{hf && <HuggingFaceBrowser onClose={() => setHf(false)} />}</DialogContent>
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
          {engine.kept.length > 0 ? `, ${engine.kept.length} kept loaded (${engine.kept.join(', ')})` : ', none kept loaded'}
          {engine.hours ? ` by the working hours "${engine.hours.name}"${engine.hours.until ? ` until ${new Date(engine.hours.until).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}, instead of the pinned ${engine.pinned.length ? engine.pinned.join(', ') : 'none'}` : ''}.{' '}
          {engine.onRequest
            ? 'Any other loads when someone asks for it. At the limit, an idle one makes room first; one kept loaded, the one new chats use, and the one for small steps while a place is left beside it, never do.'
            : 'Every place is kept loaded, or held by the model new chats use or the one for small steps, so no other model loads on request. Raise "Models loaded at once" under Settings for more.'}
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

/** "for 40 s", "for 3 min": how long it has been loading or unloading. */
function forHowLong(since: string | null | undefined): string | null {
  if (!since) return null
  const s = Math.max(0, Math.round((Date.now() - new Date(since).getTime()) / 1000))
  return s < 90 ? `for ${s} s` : `for ${Math.round(s / 60)} min`
}

function Status({ status, why, since }: { status: ModelRow['status']; why?: string | null; since?: string | null }) {
  const reason = [why, forHowLong(since)].filter(Boolean).join(', ')
  if (status === 'loaded')
    return (
      <span className="flex items-center gap-1.5 text-sm font-medium text-success-ink">
        <CircleDot className="size-4" aria-hidden="true" /> Loaded
      </span>
    )
  if (status === 'loading')
    return (
      <span className="flex flex-col items-end text-sm text-warning-ink">
        <span className="flex items-center gap-1.5">
          <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Loading…
        </span>
        {reason && <span className="text-xs text-muted-foreground">{reason}</span>}
      </span>
    )
  if (status === 'unloading')
    return (
      <span className="flex flex-col items-end text-sm text-muted-foreground">
        <span className="flex items-center gap-1.5">
          <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Unloading…
        </span>
        {reason && <span className="text-xs">{reason}</span>}
      </span>
    )
  if (status === 'failed')
    return (
      <span className="flex items-center gap-1.5 text-sm font-medium text-destructive-ink">
        <XCircle className="size-4" aria-hidden="true" /> Could not load
      </span>
    )
  if (status === 'unloaded') return <span className="text-sm text-muted-foreground">Not loaded</span>
  if (status === 'waiting')
    return (
      <span className="flex items-center gap-1.5 text-sm text-warning-ink">
        <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Fetching its files…
      </span>
    )
  if (status === 'off') return <span className="text-sm text-muted-foreground">Its server does not run</span>
  if (status === 'missing')
    return (
      <span className="flex items-center gap-1.5 text-sm text-warning-ink">
        <HelpCircle className="size-4" aria-hidden="true" /> Not in the engine
      </span>
    )
  return null
}

function ModelCard({ model: m, engine, small, onEdit, onChanged }: { model: ModelRow; engine: ModelsView['engine']; small: boolean; onEdit: () => void; onChanged: () => void }) {
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
  // Load and Unload apart, so only the button pressed shows that it works.
  const load = useMutation({
    mutationFn: (instead: boolean) => api(`/api/admin/models/${encodeURIComponent(m.name)}/load${instead ? '?instead=true' : ''}`, { body: {} }),
    onSuccess: () => {
      toast.success(`Loading ${m.name}`, { description: 'It answers once it is loaded: seconds for a small model, minutes for a large one.' })
      onChanged()
    },
    onError: async (e) => {
      // Every place is held by models that never make room: the admin may load it instead of one that is not kept.
      const holding = e instanceof ApiError && e.status === 'held' ? (e.data as { holding?: string[] } | undefined)?.holding : undefined
      if (holding?.length) {
        if (
          await confirm({
            title: `Load ${m.name} instead of ${holding[0]}?`,
            description: `${e.message} ${holding[0]} unloads, and ${m.name} keeps its place until you unload it (chats on ${holding[0]} are told it is not loaded meanwhile).`,
            confirm: `Load instead of ${holding[0]}`,
          })
        )
          load.mutate(true)
        return
      }
      toast.error(errorMessage(e))
    },
  })
  const unload = useMutation({
    mutationFn: () => api(`/api/admin/models/${encodeURIComponent(m.name)}/unload`, { body: {} }),
    onSuccess: () => {
      toast.success(`Unloading ${m.name}`, { description: 'It stops in a few seconds: one answering is stopped within ten.' })
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
  const media = m.source === 'media'
  const onEngine = media ? m.status !== 'off' : m.status !== null && m.status !== 'missing'
  const image = m.mode === 'image_generation'
  const kind = mediaKinds[m.mode]
  const Icon = kind?.icon ?? Boxes
  // Loading one more needs a place beside the kept models, unless this is one of them. A media model has its own server.
  const canLoad = media ? m.enabled !== false && m.status !== 'waiting' : m.keptNow === true || engine.kept.length < engine.max
  const enable = useMutation({
    mutationFn: (on: boolean) => api(`/api/admin/models/${encodeURIComponent(m.name)}/enabled`, { method: 'PUT', body: { enabled: on } }),
    onSuccess: (_, on) => {
      toast.success(on ? `${m.name} is on` : `${m.name} is off`, { description: on ? 'At the gateway and in the chat’s tools.' : 'Unloaded, and gone from the gateway and the chat’s tools.' })
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card className={cn(m.status === 'loaded' && 'border-success/40')}>
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary-ink">
          <Icon className="size-4.5" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <CardTitle className="flex flex-wrap items-center gap-2 [overflow-wrap:anywhere]">
            {m.name}
            <Badge variant={m.source === 'local' ? 'default' : 'secondary'}>
              {m.source === 'local' ? 'Added here' : m.source === 'remote' ? `On ${m.server}` : media ? 'This stack' : 'Gateway'}
            </Badge>
            {kind && <Badge variant="outline">{kind.label}</Badge>}
            {media && m.enabled === false && <Badge variant="secondary">Off</Badge>}
            {m.kept && (
              <Badge variant="outline">
                <Pin /> Kept loaded
              </Badge>
            )}
            {m.keptNow && !m.kept && (
              <Badge variant="outline">
                <Clock /> Kept by working hours
              </Badge>
            )}
            {m.instead && (
              <Badge variant="outline" title="Loaded by an admin in place of a model that never makes room: it keeps its place until unloaded">
                <Pin /> Holds its place
              </Badge>
            )}
            {m.vision && (
              <Badge variant="outline">
                <Eye /> Sees images
              </Badge>
            )}
            {small && (
              <Badge variant="outline" title="The model for sub-agents and small steps (Settings → Model)">
                <Sparkles /> Small steps
              </Badge>
            )}
          </CardTitle>
          <CardDescription className="[overflow-wrap:anywhere]">
            {media
              ? `On the ${m.server} server`
              : [m.source === 'remote' ? `${m.remote} on ${m.server}` : m.file, m.context ? `${m.context.toLocaleString('en-US')} tokens of context` : null].filter(Boolean).join(' · ') ||
                (image ? 'An image model' : 'Served by the gateway')}
          </CardDescription>
          {m.profile && <p className="mt-1 text-xs text-muted-foreground [overflow-wrap:anywhere]">{summary(m.profile)}</p>}
          {priceLine(m) && <p className="mt-1 text-xs text-muted-foreground tabular-nums">{priceLine(m)}</p>}
          {m.cache && <p className="mt-1 text-xs text-muted-foreground [overflow-wrap:anywhere]">{cacheLine(m.cache)}</p>}
        </div>
        <Status status={m.status} why={m.why} since={m.since} />
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
        {(m.evicted ?? 0) >= 2 && (
          <Alert variant="warning" title="The engine keeps unloading it">
            The engine unloaded it {m.evicted} times lately, by its own choice, to load another: it holds fewer models at once than Models loaded at once ({engine.max})
            says (its --models-max, or too little GPU memory for them together). It is loaded again after a wait, not at once, so two models do not push each
            other out for ever. Lower Models loaded at once under Settings, or keep fewer models loaded.
          </Alert>
        )}
        {m.status === 'failed' && media && (
          <Alert variant="destructive" title="Its server did not answer">
            It has not answered for ten minutes since it was turned on, and is still tried. Its server's log says why:{' '}
            <Link to={`/admin/logs?container=${m.server}&level=warn`} className="font-medium underline underline-offset-2">
              its warnings and errors
            </Link>
            ; too little GPU memory left beside the chat models is the usual cause.
          </Alert>
        )}
        {m.status === 'failed' && !media && (
          <Alert variant="destructive" title="The engine could not load it">
            Its log says why:{' '}
            <Link to="/admin/logs?container=llamacpp&level=warn" className="font-medium underline underline-offset-2">
              the engine's warnings and errors
            </Link>
            , or <code className="text-xs">docker compose logs llamacpp</code> on the host. An incomplete download, a file this llama.cpp cannot read, or too
            little GPU memory are the usual causes.
            It is tried again by itself after a minute, then after 2, 4, 8, 16 and at most 30 minutes (kept loaded, by the app; else at the next question for
            it){m.retryAt ? `: next from ${new Date(m.retryAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}. Loading it tries at once.
          </Alert>
        )}
        {media && (
          <Label className="flex items-start gap-3 font-normal">
            <Switch checked={m.enabled !== false} disabled={enable.isPending} onCheckedChange={(on) => enable.mutate(on)} aria-describedby={`on-${m.name}`} />
            <span className="grid gap-0.5">
              <span className="font-medium">On</span>
              <span id={`on-${m.name}`} className="text-xs text-muted-foreground">
                {m.enabled !== false ? 'At the gateway for API keys, and in the chat’s tools.' : 'Off: not at the gateway, not in the chat.'}
              </span>
            </span>
          </Label>
        )}
        {onEngine && (!media || m.enabled !== false) && (
          <Label className="flex items-start gap-3 font-normal">
            <Switch checked={m.kept === true} disabled={keep.isPending} onCheckedChange={(on) => keep.mutate(on)} aria-describedby={`keep-${m.name}`} />
            <span className="grid gap-0.5">
              <span className="font-medium">Keep loaded</span>
              <span id={`keep-${m.name}`} className="text-xs text-muted-foreground">
                {m.kept
                  ? 'Loaded at start, and again whenever it is not.'
                  : media
                    ? 'Otherwise it loads when someone asks for it, and unloads after ten minutes unused.'
                    : engine.onRequest
                      ? 'Otherwise it loads when someone asks for it.'
                      : 'Otherwise it loads only when an admin loads it.'}
              </span>
            </span>
          </Label>
        )}
        <AccessPicker value={m.access} onChange={(audience, groups) => access.mutate({ audience, groups })} />
        <div className="flex flex-wrap gap-2">
          {onEngine && m.status !== 'loaded' && (
            <Button
              size="sm"
              loading={load.isPending}
              disabled={m.status === 'loading' || m.status === 'unloading' || !canLoad}
              title={canLoad ? undefined : 'Every place in the engine keeps a model loaded'}
              onClick={async () => {
                const full = !media && engine.loaded.length + engine.loading.length >= engine.max
                if (
                  await confirm({
                    title: `Load ${m.name}?`,
                    description: media
                      ? 'It loads on its own server, beside the chat models. A model not kept loaded unloads again after ten minutes unused.'
                      : full
                      ? 'The engine is full: an idle model that may make room unloads first (never one kept loaded, nor the one new chats use, unless you say so). Answers wait until this one is loaded: seconds for a small model, minutes for a large one.'
                      : 'It loads beside the models loaded now. Answers wait until it is loaded: seconds for a small model, minutes for a large one.',
                    confirm: 'Load',
                  })
                )
                  load.mutate(false)
              }}
            >
              <Power /> Load
            </Button>
          )}
          {onEngine && m.status === 'loaded' && (
            <Button
              size="sm"
              variant="outline"
              loading={unload.isPending}
              onClick={async () => {
                const description = m.kept
                  ? 'It stops being kept loaded, too. Chats that use it wait for it to load again when asked for.'
                  : 'Chats that use it wait for it to load again when asked for.'
                if (await confirm({ title: `Unload ${m.name}?`, description, confirm: 'Unload', destructive: true }))
                  unload.mutate()
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

/** What a media model does, as a badge and an icon. */
const mediaKinds: Record<string, { label: string; icon: typeof Boxes }> = {
  image_generation: { label: 'Pictures', icon: ImageIcon },
  video_generation: { label: 'Video', icon: Clapperboard },
  audio_transcription: { label: 'Speech to text', icon: Mic },
  audio_speech: { label: 'Text to speech', icon: AudioLines },
}
