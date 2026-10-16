import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Moon } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DialogFooter } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { useDebounced } from '@/lib/use-debounced'
import { cn } from '@/lib/utils'
import { useFallbacks, useSnapshots, type EngineGpu, type EngineInstance, type EngineModel } from './engine-api'
import { bytes } from './model-profile'

export function FallbackField({ self, value, onChange, error }: { self: string | undefined; value: string; onChange: (v: string) => void; error?: string }) {
  const names = useFallbacks(self)
  return (
    <Field label="Fallback" hint="The model that answers in its place while it cannot (it restarts, failed, or has no room): the chat goes there at once, and the gateway on a failure mid-answer." error={error}>
      <Select value={value || 'none'} onValueChange={(v) => onChange(v === 'none' ? '' : v)}>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="none">None</SelectItem>
          {names.map((n) => (
            <SelectItem key={n} value={n}>
              {n}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}

/**
 * GPUs by vendor and size: each group's GPUs ticked one by one, or all of a group at once. Empty: the ones proposed for
 * this machine. A vLLM model's copies split them in order; each copy's count must split its heads (1, 2, 4, 8).
 */
export function GpuChooser({ gpus, value, onChange, error, hint }: { gpus: EngineGpu[]; value: string; onChange: (v: string) => void; error?: string; hint: string }) {
  const chosen = new Set(value.split(',').filter(Boolean).map(Number))
  const groups = useMemo(() => {
    const by = new Map<string, EngineGpu[]>()
    for (const g of gpus) {
      const key = `${g.vendor ?? 'nvidia'} · ${g.name} · ${bytes(g.total)}`
      by.set(key, [...(by.get(key) ?? []), g])
    }
    return [...by.entries()]
  }, [gpus])
  const put = (next: Set<number>) => onChange([...next].sort((a, b) => a - b).join(','))
  return (
    <fieldset className="grid gap-2">
      <legend className="mb-1 text-sm font-medium">GPUs</legend>
      {groups.map(([label, list]) => (
        <div key={label} className="grid gap-1.5">
          <Label className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
            <Checkbox
              checked={list.every((g) => chosen.has(g.index))}
              onCheckedChange={(on) => {
                const next = new Set(chosen)
                for (const g of list) {
                  if (on === true) next.add(g.index)
                  else next.delete(g.index)
                }
                put(next)
              }}
              aria-label={`Every ${label}`}
            />
            {label} ({list.length})
          </Label>
          <div className="flex flex-wrap gap-x-5 gap-y-2 pl-6">
            {list.map((g) => (
              <Label key={g.index} className="flex items-center gap-2 font-normal">
                <Checkbox
                  checked={chosen.has(g.index)}
                  onCheckedChange={(on) => {
                    const next = new Set(chosen)
                    if (on === true) next.add(g.index)
                    else next.delete(g.index)
                    put(next)
                  }}
                />
                GPU {g.index}
              </Label>
            ))}
          </div>
        </div>
      ))}
      {gpus.length === 0 && <p className="text-xs text-muted-foreground">This machine's GPUs are not known yet (no GPU exporter or hardware file).</p>}
      <p className={cn('text-xs', error ? 'text-destructive-ink' : 'text-muted-foreground')}>{error ?? hint}</p>
    </fieldset>
  )
}

interface VllmAdvice {
  limits: { context: { min: number; max: number }; parallel: { min: number; max: number } } | null
  recommended: { context: number; maxOutput: number; parallel: number; kvType: string; devices: string | null } | null
  estimate: { gpuWeights: number; gpuCache: number; gpuTotal: number; gpuBudget: number | null; ramTotal: number; fit: string; approximate: boolean } | null
  problems: { field: string; message: string; error: boolean }[]
}

interface VllmForm {
  name: string
  engine: string
  devices: string
  replicas: string
  gpuMemoryUtilization: string
  quantization: string
  kvType: string
  context: string
  parallel: string
  specDecode: string
  draftHead: string
  kvCpuGb: string
  sleep: boolean
  fallback: string
}

const lastPart = (path: string) => (path.split('/').pop() ?? path).replace(/@.*$/, '')

function start(saved: EngineModel | null, snapshot: string): VllmForm {
  return {
    name: saved?.name ?? lastPart(snapshot),
    engine: saved?.engine ?? '',
    devices: saved?.devices ?? '',
    replicas: String(saved?.replicas ?? 1),
    gpuMemoryUtilization: saved?.gpuMemoryUtilization ? String(saved.gpuMemoryUtilization) : '',
    quantization: saved?.quantization ?? '',
    kvType: saved?.kvType && ['fp8', 'auto', 'fp8_e4m3', 'fp8_e5m2'].includes(saved.kvType) ? saved.kvType : 'fp8',
    context: String(saved?.context ?? 32768),
    parallel: String(saved?.parallel ?? 16),
    specDecode: saved?.specDecode ?? '',
    draftHead: saved?.draftHead ?? '',
    kvCpuGb: saved?.kvCpuGb ? String(saved.kvCpuGb) : '',
    sleep: saved?.sleep ?? true,
    fallback: saved?.fallback ?? '',
  }
}

/** What the API takes for a vLLM model: what is empty is cleared (the default), what is set is sent. */
function request(form: VllmForm, file: string) {
  const n = (s: string) => (s.trim() === '' ? undefined : Number(s))
  const clear = (['devices', 'gpuMemoryUtilization', 'quantization', 'specDecode', 'draftHead', 'kvCpuGb', 'fallback', 'engine'] as const).filter((k) => String(form[k]).trim() === '')
  return {
    engineKind: 'vllm',
    file,
    engine: form.engine || undefined,
    devices: form.devices || undefined,
    replicas: n(form.replicas),
    gpuMemoryUtilization: n(form.gpuMemoryUtilization),
    quantization: form.quantization || undefined,
    kvType: form.kvType,
    context: n(form.context),
    parallel: n(form.parallel),
    specDecode: form.specDecode || undefined,
    draftHead: form.draftHead || undefined,
    kvCpuGb: n(form.kvCpuGb),
    sleep: form.sleep,
    fallback: form.fallback || undefined,
    clear,
  }
}

/**
 * A vLLM model's part of the model dialog: a Hugging Face snapshot from the library, the GPUs it runs on (by vendor and
 * size; empty: those proposed for this machine), its copies, its share of each GPU, quantisation, cache type, context,
 * answers at once, speculative decoding, a CPU tier for its cache, sleep, and its fallback. The advisor's fit, cache and
 * recommendation show as it is filled in; the API runs the same checks on save.
 */
export function VllmModelSection({
  saved,
  snapshot,
  gpus,
  instances,
  onClose,
}: {
  saved: EngineModel | null
  snapshot: string
  gpus: EngineGpu[]
  instances: EngineInstance[]
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const snapshots = useSnapshots()
  const [form, setForm] = useState(() => start(saved, snapshot))
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof VllmForm>(k: K, v: VllmForm[K]) => setForm((f) => ({ ...f, [k]: v }))
  const body = useMemo(() => request(form, snapshot), [form, snapshot])
  const debounced = useDebounced(body, 350)
  const advice = useQuery({
    queryKey: ['admin', 'models', 'advice', 'vllm', debounced],
    queryFn: ({ signal }) => api<VllmAdvice>('/api/admin/models/advice', { body: { ...debounced, name: saved?.name }, signal }),
    placeholderData: keepPreviousData,
    retry: false,
  })
  const a = advice.data
  const errors: Record<string, string> = {}
  for (const p of a?.problems ?? []) if (p.error && !errors[p.field]) errors[p.field] = p.message
  const vllmEngines = instances.filter((i) => i.kind === 'vllm')
  const save = useMutation({
    mutationFn: () =>
      saved
        ? api<{ warning: string | null }>(`/api/admin/models/${encodeURIComponent(saved.name)}`, { method: 'PATCH', body })
        : api<{ warning: string | null }>('/api/admin/models', { body: { ...body, name: form.name.trim() } }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r?.warning) toast.warning(r.warning)
      else toast.success(saved ? `${saved.name} saved` : `${form.name} added`, { description: 'It starts when it is loaded or asked for.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const shot = snapshots.data?.find((s) => s.path === snapshot)
  const e = a?.estimate
  const r = a?.recommended
  return (
    <form
      className="grid min-w-0 gap-5"
      aria-label="The vLLM model"
      onSubmit={(ev) => {
        ev.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      {error && <Alert variant="destructive">{error}</Alert>}
      {errors.file && <Alert variant="destructive">{errors.file}</Alert>}
      {shot && (
        <p className="text-sm text-muted-foreground">
          {shot.repo} at {shot.revision.slice(0, 12)} · {bytes(shot.size)} · {shot.architectures.join(', ') || 'architecture not said'}
          {!shot.complete && ` · ${shot.why}`}
        </p>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" hint="Its name in the chat and at the gateway.">
          <Input value={form.name} onChange={(ev) => set('name', ev.target.value)} disabled={!!saved} />
        </Field>
        {vllmEngines.length > 1 && (
          <Field label="vLLM engine" hint="The machine (engine) it runs on.">
            <Select value={form.engine || vllmEngines[0].name} onValueChange={(v) => set('engine', v === vllmEngines[0].name ? '' : v)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {vllmEngines.map((i) => (
                  <SelectItem key={i.name} value={i.name}>
                    {i.name}
                    {i.problem ? ' (not running)' : ''}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        )}
      </div>
      <GpuChooser
        gpus={gpus}
        value={form.devices}
        onChange={(v) => set('devices', v)}
        error={errors.devices ?? errors.replicas}
        hint={form.devices ? 'Only these, split among its copies in order.' : `None ticked: ${r?.devices ? `GPUs ${r.devices}, proposed for this machine` : 'those the advisor proposes'}.`}
      />
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Copies" hint="Each on its share of the GPUs; a conversation stays on one." error={errors.replicas}>
          <Input inputMode="numeric" value={form.replicas} onChange={(ev) => set('replicas', ev.target.value)} />
        </Field>
        <Field label="Share of each GPU" hint="0.10 to 0.98; empty: 0.90 (--gpu-memory-utilization)." error={errors.gpuMemoryUtilization}>
          <Input inputMode="decimal" value={form.gpuMemoryUtilization} onChange={(ev) => set('gpuMemoryUtilization', ev.target.value)} placeholder="0.90" />
        </Field>
        <Field label="Quantisation" hint="Empty: what its config says." error={errors.quantization}>
          <Input value={form.quantization} onChange={(ev) => set('quantization', ev.target.value)} placeholder="auto" />
        </Field>
        <Field label="Context" hint={a?.limits ? `${a.limits.context.min.toLocaleString()} to ${a.limits.context.max.toLocaleString()} tokens.` : 'Tokens.'} error={errors.context}>
          <Input inputMode="numeric" value={form.context} onChange={(ev) => set('context', ev.target.value)} />
        </Field>
        <Field label="Answers at once" hint="Per copy (--max-num-seqs)." error={errors.parallel}>
          <Input inputMode="numeric" value={form.parallel} onChange={(ev) => set('parallel', ev.target.value)} />
        </Field>
        <Field label="Token cache" error={errors.kvType}>
          <Select value={form.kvType} onValueChange={(v) => set('kvType', v)}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="fp8">fp8 (half the memory)</SelectItem>
              <SelectItem value="auto">The model's own type</SelectItem>
            </SelectContent>
          </Select>
        </Field>
        <Field label="Speculative decoding" error={errors.specDecode ?? errors.draftHead}>
          <Select value={form.specDecode || 'none'} onValueChange={(v) => set('specDecode', v === 'none' ? '' : v)}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="none">None</SelectItem>
              <SelectItem value="mtp">Its own prediction layers (MTP)</SelectItem>
              <SelectItem value="eagle3">EAGLE-3, with a draft model</SelectItem>
            </SelectContent>
          </Select>
        </Field>
        {form.specDecode === 'eagle3' && (
          <Field label="Draft model" hint="A snapshot in the library." error={errors.draftHead}>
            <Select value={form.draftHead || undefined} onValueChange={(v) => set('draftHead', v)}>
              <SelectTrigger>
                <SelectValue placeholder="Choose a snapshot" />
              </SelectTrigger>
              <SelectContent>
                {(snapshots.data ?? [])
                  .filter((s) => s.path !== snapshot)
                  .map((s) => (
                    <SelectItem key={s.path} value={s.path}>
                      {s.path}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>
          </Field>
        )}
        <Field label="CPU tier for its cache (GB)" hint="RAM a copy keeps conversations in (OffloadingConnector); empty: none." error={errors.kvCpuGb}>
          <Input inputMode="numeric" value={form.kvCpuGb} onChange={(ev) => set('kvCpuGb', ev.target.value)} />
        </Field>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Label className="flex items-center gap-3 font-normal">
          <Switch checked={form.sleep} onCheckedChange={(v) => set('sleep', v)} aria-label="May sleep" />
          <span>
            May sleep <Moon className="inline size-3.5" aria-hidden="true" />
            <span className="block text-xs text-muted-foreground">Its weights to RAM while a picture or video job has its GPUs: back in seconds. Off: it unloads.</span>
          </span>
        </Label>
        <FallbackField self={saved?.name ?? form.name} value={form.fallback} onChange={(v) => set('fallback', v)} error={errors.fallback} />
      </div>
      {e && (
        <Alert variant={e.fit === 'none' ? 'destructive' : 'info'} title={e.fit === 'none' ? 'It would not start' : 'On its GPUs'}>
          Weights {bytes(e.gpuWeights)}, token cache {bytes(e.gpuCache)} ({e.approximate ? 'rough: vLLM says its own as it starts' : 'all copies'}), {bytes(e.gpuTotal)} of the GPUs
          {e.gpuBudget ? ` (${bytes(e.gpuBudget)} there for models)` : ''}
          {e.ramTotal ? `; ${bytes(e.ramTotal)} of RAM asleep and in its CPU tier` : ''}.
        </Alert>
      )}
      {(a?.problems ?? [])
        .filter((p) => !p.error)
        .map((p) => (
          <Alert key={p.message} variant="warning">
            {p.message}
          </Alert>
        ))}
      {r && (
        <p className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          Fits this machine: {r.devices ? `GPUs ${r.devices}, ` : ''}
          {r.context.toLocaleString()} tokens of context, {r.parallel} answers at once.
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => setForm((f) => ({ ...f, context: String(r.context), parallel: String(r.parallel), devices: f.devices || (r.devices ?? '') }))}
          >
            Use these
          </Button>
        </p>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" disabled={save.isPending || !form.name.trim() || Object.keys(errors).length > 0}>
          {saved ? 'Save' : 'Add model'}
        </Button>
      </DialogFooter>
    </form>
  )
}

/** The engines that cannot list their models now (a vLLM engine not running), for Admin → Models. */
export function EngineProblems({ instances }: { instances: EngineInstance[] | undefined }) {
  const down = (instances ?? []).filter((i) => i.problem)
  return (
    <>
      {down.map((i) => (
        <Alert key={i.name} variant="warning" title={`The ${i.name} engine`}>
          {i.problem}
        </Alert>
      ))}
    </>
  )
}
