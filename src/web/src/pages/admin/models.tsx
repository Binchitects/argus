import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, CircleDot, Eye, HelpCircle, Image as ImageIcon, Loader2, Pencil, Plus, Power, PowerOff, Settings2, Trash2, XCircle } from 'lucide-react'
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
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { AccessPicker, type AccessRule } from './access-picker'
import { ModelForm, type SavedModel } from './model-form'
import { summary, type ModelProfile } from './model-profile'

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
}

interface ModelsView {
  engine: { enabled: boolean; error: string | null; checkedAt: string | null; active: string | null; loaded: string[]; loading: string[] }
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
        description="Every model at the gateway, and who may use each. The engine's models switch live: load one and the one before unloads."
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
      <div className="stagger grid gap-4 xl:grid-cols-2 min-[2200px]:grid-cols-3">
        {models.data.models.map((m) => (
          <ModelCard key={m.name} model={m} active={engine.active} onEdit={() => setEditing(m)} onChanged={() => queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })} />
        ))}
      </div>
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">{editing !== null && <ModelForm key={editing === 'new' ? 'new' : editing.name} saved={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}</DialogContent>
      </Dialog>
    </>
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

function ModelCard({ model: m, active, onEdit, onChanged }: { model: ModelRow; active: string | null; onEdit: () => void; onChanged: () => void }) {
  const confirm = useConfirm()
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
        <AccessPicker value={m.access} onChange={(audience, groups) => access.mutate({ audience, groups })} />
        <div className="flex flex-wrap gap-2">
          {onEngine && m.status !== 'loaded' && (
            <Button
              size="sm"
              loading={act.isPending}
              disabled={m.status === 'loading'}
              onClick={async () => {
                if (
                  await confirm({
                    title: `Load ${m.name}?`,
                    description: 'The model loaded now unloads, for everyone. Answers wait until this one is loaded: seconds for a small model, minutes for a large one.',
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
                if (await confirm({ title: `Unload ${m.name}?`, description: 'Nothing answers from the engine until a model is loaded again.', confirm: 'Unload', destructive: true }))
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
                  const description =
                    m.name === active
                      ? 'It is the model the engine keeps loaded: the .env model takes its place.'
                      : 'Chats that chose it fall back to the loaded model. The model file stays in the library.'
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
