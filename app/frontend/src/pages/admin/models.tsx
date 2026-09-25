import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, CircleDot, Eye, Image as ImageIcon, Loader2, Pencil, Plus, Power, PowerOff, Settings2, Trash2, XCircle } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { AccessPicker, type AccessRule } from './access-picker'

interface ModelRow {
  name: string
  /** env: the .env model; local: added here; gateway: served by the gateway otherwise (cloud, pictures). */
  source: 'env' | 'local' | 'gateway'
  mode: string
  /** failed: its last load exited with an error (the engine's log says why). */
  status: 'loaded' | 'loading' | 'unloaded' | 'failed' | null
  file?: string | null
  projector?: string | null
  context?: number | null
  maxOutput?: number | null
  gpuLayers?: number
  cpuMoe?: number
  kvType?: string
  parallel?: number
  extraPreset?: string | null
  thinking?: boolean
  tools?: boolean
  inputPerMtok?: number | null
  outputPerMtok?: number | null
  vision: boolean
  atGateway?: boolean
  access: AccessRule
}

interface ModelsView {
  engine: { enabled: boolean; error: string | null; checkedAt: string | null; active: string | null; loaded: string[]; loading: string[] }
  models: ModelRow[]
}

interface LibraryFile {
  path: string
  size: number
  parts: number
  role: 'model' | 'projector' | 'draft' | 'other'
  architecture: string | null
  name: string | null
  sizeLabel: string | null
  trainedContext: number | null
  usedBy: string[]
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
        <DialogContent className="sm:max-w-2xl">{editing !== null && <ModelForm key={editing === 'new' ? 'new' : editing.name} saved={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}</DialogContent>
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
  const onEngine = m.status !== null
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
        </div>
        <Status status={m.status} />
      </CardHeader>
      <CardContent className="grid gap-4">
        {m.source === 'local' && m.atGateway === false && <Alert variant="warning">Not at the gateway yet: it is added again within a minute.</Alert>}
        {m.status === 'failed' && (
          <Alert variant="destructive" title="The engine could not load it">
            Its log says why: <code className="text-xs">docker compose logs llamacpp</code> on the host, or Grafana's logs. An incomplete download, a file this
            llama.cpp cannot read, or too little GPU memory are the usual causes. It is not tried again until you load it.
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

const kvTypes = ['q8_0', 'f16', 'bf16', 'q5_1', 'q5_0', 'q4_1', 'q4_0', 'iq4_nl']

function ModelForm({ saved, onClose }: { saved: ModelRow | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const library = useQuery({ queryKey: ['admin', 'models', 'library'], queryFn: ({ signal }) => api<LibraryFile[]>('/api/admin/models/library', { signal }) })
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    file: saved?.file ?? '',
    projector: saved?.projector ?? '',
    context: String(saved?.context ?? 32768),
    maxOutput: saved?.maxOutput ? String(saved.maxOutput) : '',
    gpuLayers: String(saved?.gpuLayers ?? 99),
    cpuMoe: String(saved?.cpuMoe ?? 0),
    kvType: saved?.kvType ?? 'q8_0',
    parallel: String(saved?.parallel ?? 1),
    extraPreset: saved?.extraPreset ?? '',
    thinking: saved?.thinking ?? true,
    tools: saved?.tools ?? true,
    inputPerMtok: saved?.inputPerMtok != null ? String(saved.inputPerMtok) : '',
    outputPerMtok: saved?.outputPerMtok != null ? String(saved.outputPerMtok) : '',
  })
  const [error, setError] = useState<string | null>(null)
  const files = library.data ?? []
  const modelFiles = files.filter((f) => f.role === 'model')
  const projectors = files.filter((f) => f.role === 'projector')
  const set = (k: keyof typeof form, v: string | boolean) => setForm((f) => ({ ...f, [k]: v }))
  const chooseFile = (path: string) => {
    const f = files.find((x) => x.path === path)
    setForm((cur) => ({
      ...cur,
      file: path,
      // A name and a context to start from: the file's own, the context capped at 32K.
      name: cur.name || (path.split('/').pop() ?? '').replace(/(-\d{5}-of-\d{5})?\.gguf$/i, ''),
      context: saved ? cur.context : String(Math.min(f?.trainedContext ?? 32768, 32768)),
    }))
  }
  const number = (v: string) => (v.trim() === '' ? undefined : Number(v))
  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: saved ? undefined : form.name.trim(),
        file: form.file,
        projector: form.projector,
        context: number(form.context),
        maxOutput: number(form.maxOutput),
        gpuLayers: number(form.gpuLayers),
        cpuMoe: number(form.cpuMoe),
        kvType: form.kvType,
        parallel: number(form.parallel),
        extraPreset: form.extraPreset,
        thinking: form.thinking,
        tools: form.tools,
        inputPerMtok: number(form.inputPerMtok),
        outputPerMtok: number(form.outputPerMtok),
        // An emptied field is sent as one to clear: a missing one is left as it was.
        clear: (['maxOutput', 'inputPerMtok', 'outputPerMtok'] as const).filter((k) => form[k].trim() === ''),
      }
      return saved ? api<{ warning: string | null }>(`/api/admin/models/${encodeURIComponent(saved.name)}`, { method: 'PATCH', body }) : api<{ warning: string | null }>('/api/admin/models', { body })
    },
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r?.warning) toast.warning(r.warning)
      else toast.success(saved ? `${saved.name} saved` : `${form.name} added`, { description: 'The engine restarts to read it: the loaded model is back in a moment. Load it from its card.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const describe = (f: LibraryFile) =>
    `${f.path} · ${formatValue(f.size, 'bytes')}${f.parts > 1 ? ` in ${f.parts} parts` : ''}${f.sizeLabel ? ` · ${f.sizeLabel}` : ''}${f.architecture ? ` · ${f.architecture}` : ''}`
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add a model'}</DialogTitle>
        <DialogDescription>A GGUF file from the model library, and how the engine runs it. It can then be loaded, and given to whom you choose.</DialogDescription>
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
        <Field label="Model file" hint={library.isPending ? 'Reading the library…' : modelFiles.length ? 'Language models found in the library.' : 'No language models in the library (LLAMACPP_LIBRARY_DIR).'}>
          <Select value={form.file} onValueChange={chooseFile}>
            <SelectTrigger>
              <SelectValue placeholder="Choose a file" />
            </SelectTrigger>
            <SelectContent>
              {modelFiles.map((f) => (
                <SelectItem key={f.path} value={f.path}>
                  {describe(f)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" hint="Its name in the chat and at the gateway.">
            <Input value={form.name} onChange={(e) => set('name', e.target.value)} disabled={!!saved} required maxLength={100} autoComplete="off" />
          </Field>
          <Field label="Vision projector" hint="Only for a model that can see (an mmproj file).">
            <Select value={form.projector || 'none'} onValueChange={(v) => set('projector', v === 'none' ? '' : v)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="none">None</SelectItem>
                {projectors.map((f) => (
                  <SelectItem key={f.path} value={f.path}>
                    {f.path}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Context (tokens)" hint="More takes more GPU memory for the cache.">
            <Input inputMode="numeric" value={form.context} onChange={(e) => set('context', e.target.value)} required />
          </Field>
          <Field label="Longest answer (tokens)" hint="Empty: up to 32,768.">
            <Input inputMode="numeric" value={form.maxOutput} onChange={(e) => set('maxOutput', e.target.value)} />
          </Field>
          <Field label="Layers on the GPU" hint="99: all of them.">
            <Input inputMode="numeric" value={form.gpuLayers} onChange={(e) => set('gpuLayers', e.target.value)} />
          </Field>
          <Field label="MoE layers with experts in RAM" hint="For mixture-of-experts models too big for the GPU; 0 keeps them all on it.">
            <Input inputMode="numeric" value={form.cpuMoe} onChange={(e) => set('cpuMoe', e.target.value)} />
          </Field>
          <Field label="Cache type">
            <Select value={form.kvType} onValueChange={(v) => set('kvType', v)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {kvTypes.map((k) => (
                  <SelectItem key={k} value={k}>
                    {k}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="People served at once" hint="Parallel slots share the context.">
            <Input inputMode="numeric" value={form.parallel} onChange={(e) => set('parallel', e.target.value)} />
          </Field>
          <Field label="Price in, per 1M tokens" hint="Empty: the gateway's defaults.">
            <Input inputMode="decimal" value={form.inputPerMtok} onChange={(e) => set('inputPerMtok', e.target.value)} />
          </Field>
          <Field label="Price out, per 1M tokens">
            <Input inputMode="decimal" value={form.outputPerMtok} onChange={(e) => set('outputPerMtok', e.target.value)} />
          </Field>
        </div>
        <div className="flex flex-wrap gap-6">
          <Label className="font-normal">
            <Switch checked={form.thinking} onCheckedChange={(v) => set('thinking', v)} /> Thinks before answering
          </Label>
          <Label className="font-normal">
            <Switch checked={form.tools} onCheckedChange={(v) => set('tools', v)} /> Can call tools
          </Label>
        </div>
        <Field label="More engine options" hint="One per line, key = value, with llama-server's long option names, e.g. flash-attn = on.">
          <Textarea rows={3} className="font-mono text-xs" value={form.extraPreset} onChange={(e) => set('extraPreset', e.target.value)} />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending} disabled={!form.file || !form.name.trim()}>
            {saved ? 'Save' : 'Add model'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
