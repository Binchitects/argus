import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Loader2, Moon } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
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
import { engineLabel, serverPerModel, useFallbacks, useSnapshots, type EngineGpu, type EngineInstance, type EngineModel, type ModelMove } from './engine-api'
import { bytes, defaultMaxOutput, tokens, type LibraryFile } from './model-profile'

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
  limits: { context: { min: number; max: number }; maxOutput: { min: number; max: number }; parallel: { min: number; max: number } } | null
  recommended: {
    context: number
    maxOutput: number
    parallel: number
    kvType: string
    devices: string | null
    /** The shares of each GPU that hold it there: the smallest leaves room for a whole context, the largest fits beside what is kept. */
    shareMin?: number | null
    shareMax?: number | null
  } | null
  estimate: { gpuWeights: number; gpuCache: number; gpuTotal: number; gpuBudget: number | null; ramTotal: number; fit: string; approximate: boolean } | null
  problems: { field: string; message: string; error: boolean }[]
}

interface VllmForm {
  name: string
  devices: string
  replicas: string
  gpuMemoryUtilization: string
  quantization: string
  kvType: string
  context: string
  /** The longest answer; empty: half the context. */
  maxOutput: string
  parallel: string
  specDecode: string
  draftHead: string
  kvCpuGb: string
  kvTier: string
  kvDiskGb: string
  sleep: boolean
  fallback: string
}

/** A file's model for its name: a snapshot's folder without its revision, a GGUF file without its split part or extension. */
const lastPart = (path: string) => (path.split('/').pop() ?? path).replace(/@.*$/, '').replace(/(-\d{5}-of-\d{5})?\.gguf$/i, '')

function start(saved: EngineModel | null, file: string, kind: VllmEngine['kind']): VllmForm {
  // A llama.cpp model moved back here takes up its own settings of this kind again (its answers at once, its GPUs, share,
  // longest answer...); one that never ran here takes this kind's defaults for what llama.cpp counts otherwise: 16 answers at
  // once, and the GPUs proposed for this machine (its draft head is a GGUF file, not a snapshot).
  const moving = !!saved && !serverPerModel(saved.engineKind)
  const back = moving && saved?.left?.engineKind === kind ? saved.left : null
  if (back) saved = { ...saved!, ...back, name: saved!.name, file: saved!.file, context: saved!.context, engineKind: kind }
  const fresh = moving && !back
  return {
    name: saved?.name ?? lastPart(file),
    devices: fresh ? '' : (saved?.devices ?? ''),
    replicas: String(saved?.replicas ?? 1),
    gpuMemoryUtilization: saved?.gpuMemoryUtilization ? String(saved.gpuMemoryUtilization) : '',
    quantization: saved?.quantization ?? '',
    kvType: saved?.kvType && ['fp8', 'auto', 'fp8_e4m3', 'fp8_e5m2'].includes(saved.kvType) ? saved.kvType : 'fp8',
    context: String(saved?.context ?? 32768),
    maxOutput: saved?.maxOutput ? String(saved.maxOutput) : '',
    parallel: String(fresh ? 16 : (saved?.parallel ?? 16)),
    specDecode: fresh ? '' : (saved?.specDecode ?? ''),
    draftHead: fresh ? '' : (saved?.draftHead ?? ''),
    kvCpuGb: saved?.kvCpuGb ? String(saved.kvCpuGb) : '',
    kvTier: saved?.kvTier ?? '',
    kvDiskGb: saved?.kvDiskGb ? String(saved.kvDiskGb) : '',
    sleep: saved?.sleep ?? true,
    fallback: saved?.fallback ?? '',
  }
}

/** The engine a vLLM or SGLang model runs on: its kind, and its instance by name ('' for the first of its kind, the API's default). */
export interface VllmEngine {
  kind: 'vllm' | 'sglang'
  name: string
}

/** What the form keeps that its engine allows: LMCache is vLLM's alone. */
const allowed = (form: VllmForm, kind: VllmEngine['kind']): VllmForm => (kind === 'sglang' && form.kvTier === 'lmcache' ? { ...form, kvTier: '' } : form)

/** What the API takes for a vLLM or SGLang model on its engine: what is empty is cleared (the default), what is set is sent. */
function request(typed: VllmForm, file: string, engine: VllmEngine) {
  const form = { ...allowed(typed, engine.kind), engine: engine.name }
  const n = (s: string) => (s.trim() === '' ? undefined : Number(s))
  const clear = (['devices', 'gpuMemoryUtilization', 'quantization', 'maxOutput', 'specDecode', 'draftHead', 'kvCpuGb', 'kvTier', 'kvDiskGb', 'fallback', 'engine'] as const).filter(
    (k) => String(form[k]).trim() === '',
  )
  return {
    engineKind: engine.kind,
    file,
    engine: form.engine || undefined,
    devices: form.devices || undefined,
    replicas: n(form.replicas),
    gpuMemoryUtilization: n(form.gpuMemoryUtilization),
    quantization: form.quantization || undefined,
    kvType: form.kvType,
    context: n(form.context),
    maxOutput: n(form.maxOutput),
    parallel: n(form.parallel),
    specDecode: form.specDecode || undefined,
    draftHead: form.draftHead || undefined,
    kvCpuGb: n(form.kvCpuGb),
    kvTier: form.kvTier || undefined,
    kvDiskGb: form.kvTier === 'lmcache' ? n(form.kvDiskGb) : undefined,
    sleep: form.sleep,
    fallback: form.fallback || undefined,
    clear,
  }
}

/**
 * A vLLM or SGLang model's part of the model dialog: a Hugging Face snapshot or a GGUF file from the library on the engine the
 * dialog's Engine field chose (vLLM's or SGLang's, and which machine), the GPUs (by vendor and size; empty: those proposed for this
 * machine), its copies, its share of each GPU, quantisation, cache type, context, answers at once, speculative decoding, a
 * CPU tier for its cache, sleep, and its fallback. The advisor's fit, cache and recommendation show as it is filled in; the
 * API runs the same checks on save.
 */
export function VllmModelSection({
  saved,
  file,
  gguf,
  engine,
  moves,
  gpus,
  onClose,
}: {
  saved: EngineModel | null
  /** Its snapshot's folder or its GGUF file, in the library. */
  file: string
  /** Its GGUF file as the library reads it (none: a snapshot). */
  gguf?: LibraryFile
  engine: VllmEngine
  /** A saved model put on another engine than its own moves there: that engine for people ("vLLM", "vLLM · name"); null: it stays. */
  moves: string | null
  gpus: EngineGpu[]
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const snapshots = useSnapshots()
  const { kind, name } = engine
  const label = engineLabel(kind)
  const [typed, setForm] = useState(() => start(saved, file, kind))
  const form = allowed(typed, kind)
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof VllmForm>(k: K, v: VllmForm[K]) => setForm((f) => ({ ...f, [k]: v }))
  const body = useMemo(() => request(typed, file, { kind, name }), [typed, file, kind, name])
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
  const save = useMutation({
    mutationFn: () =>
      saved
        ? api<{ warning: string | null }>(`/api/admin/models/${encodeURIComponent(saved.name)}`, { method: 'PATCH', body })
        : api<{ warning: string | null }>('/api/admin/models', { body: { ...body, name: form.name.trim() } }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r?.warning) toast.warning(r.warning)
      else if (saved && moves)
        toast.success(`${saved.name} moves to ${moves}`, { description: 'Its card says how far it is: its answers end where it was, then it loads here.' })
      else toast.success(saved ? `${saved.name} saved` : `${form.name} added`, { description: 'It starts when it is loaded or asked for.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const shot = snapshots.data?.find((s) => s.path === file)
  const e = a?.estimate
  const r = a?.recommended
  const shares = r?.shareMin != null && r.shareMax != null ? `${r.shareMin.toFixed(2)} to ${r.shareMax.toFixed(2)}` : null
  // A llama.cpp model moved here with no share of its own, where the default 0.90 does not fit: the smallest share that does.
  const shareFromAdvice = useRef(!!saved && !serverPerModel(saved.engineKind) && !typed.gpuMemoryUtilization)
  const smallest = (errors.gpuMemoryUtilization || errors.devices) && typed.gpuMemoryUtilization === '' ? r?.shareMin : undefined
  useEffect(() => {
    if (!shareFromAdvice.current || !smallest) return
    shareFromAdvice.current = false
    setForm((f) => ({ ...f, gpuMemoryUtilization: smallest.toFixed(2) }))
  }, [smallest])
  return (
    <form
      className="grid min-w-0 gap-5"
      aria-label={`The ${label} model`}
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
      {gguf && (
        <p className="text-sm text-muted-foreground">
          A GGUF file · {[gguf.profile.quant, bytes(gguf.size) + (gguf.parts > 1 ? ` in ${gguf.parts} parts` : ''), gguf.profile.architecture].filter(Boolean).join(' · ')}: {label}{' '}
          reads its settings and tokenizer from its header, and runs it more slowly than a snapshot's own safetensors.
        </p>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" hint="Its name in the chat and at the gateway.">
          <Input value={form.name} onChange={(ev) => set('name', ev.target.value)} disabled={!!saved} />
        </Field>
      </div>
      <GpuChooser
        gpus={gpus}
        value={form.devices}
        onChange={(v) => set('devices', v)}
        error={errors.devices ?? errors.replicas}
        hint={form.devices ? 'Only these, split among its copies in order.' : `None ticked: ${r?.devices ? `GPUs ${r.devices}, proposed for this machine (the least placed)` : 'those the advisor proposes, the least placed'}.`}
      />
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Copies" hint="Each on its share of the GPUs; a conversation goes to the least busy and stays there while it has room." error={errors.replicas}>
          <Input inputMode="numeric" value={form.replicas} onChange={(ev) => set('replicas', ev.target.value)} />
        </Field>
        <Field
          label="Share of each GPU"
          hint={`${shares ? `From ${shares} here: the smallest holds one person's whole context, a larger one more people at once.` : '0.01 to 0.98.'} Empty: 0.90 (${kind === 'sglang' ? '--mem-fraction-static' : '--gpu-memory-utilization'}).`}
          error={errors.gpuMemoryUtilization}
        >
          <Input inputMode="decimal" value={form.gpuMemoryUtilization} onChange={(ev) => set('gpuMemoryUtilization', ev.target.value)} placeholder="0.90" />
        </Field>
        <Field label="Quantisation" hint={gguf ? 'Empty: a GGUF file is quantised as it is.' : 'Empty: what its config says.'} error={errors.quantization}>
          <Input value={form.quantization} onChange={(ev) => set('quantization', ev.target.value)} placeholder="auto" />
        </Field>
        <Field
          label="Context per person"
          hint={`${a?.limits ? `${tokens(a.limits.context.min)} to ${tokens(a.limits.context.max)} tokens` : 'Tokens'}: what each person's conversation holds, its prompt and its answer together, however many are at once.`}
          error={errors.context}
        >
          <Input inputMode="numeric" value={form.context} onChange={(ev) => set('context', ev.target.value)} />
        </Field>
        <Field
          label="Longest answer"
          hint={
            // A move cuts one its window here does not hold, and says so.
            a?.problems.find((p) => !p.error && p.field === 'maxOutput')?.message ??
            `${a?.limits ? `${tokens(a.limits.maxOutput.min)} to ${tokens(a.limits.maxOutput.max)}: the context less room for a prompt. ` : ''}Empty: ${tokens(defaultMaxOutput(Number(form.context) || 32768))}.`
          }
          error={errors.maxOutput}
        >
          <Input inputMode="numeric" value={form.maxOutput} placeholder={String(defaultMaxOutput(Number(form.context) || 32768))} onChange={(ev) => set('maxOutput', ev.target.value)} />
        </Field>
        <Field label="People at once" hint={`Per copy, each with the whole context per person (${kind === 'sglang' ? '--max-running-requests' : '--max-num-seqs'}). More than its cache holds wait or are preempted: the advisor says how many fit.`} error={errors.parallel}>
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
                  .filter((s) => s.path !== file)
                  .map((s) => (
                    <SelectItem key={s.path} value={s.path}>
                      {s.path}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>
          </Field>
        )}
        <Field
          label="Cache beyond the GPU"
          hint={kind === 'sglang' ? "SGLang's hierarchical cache, in RAM." : "vLLM's own CPU tier, or LMCache (RAM, local disk that survives a restart, the stack's shared store)."}
          error={errors.kvTier}
        >
          <Select value={form.kvTier || 'cpu'} onValueChange={(v) => set('kvTier', v === 'cpu' ? '' : v)}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="cpu">{label}'s own (RAM)</SelectItem>
              {kind === 'vllm' && <SelectItem value="lmcache">LMCache (RAM and disk)</SelectItem>}
            </SelectContent>
          </Select>
        </Field>
        <Field label="CPU tier for its cache (GB)" hint="RAM a copy keeps conversations in; empty: none." error={errors.kvCpuGb}>
          <Input inputMode="numeric" value={form.kvCpuGb} onChange={(ev) => set('kvCpuGb', ev.target.value)} />
        </Field>
        {form.kvTier === 'lmcache' && (
          <Field label="Disk tier (GB)" hint="Under the engine's /kvcache (KVCACHE_DIR), a copy; empty: none." error={errors.kvDiskGb}>
            <Input inputMode="numeric" value={form.kvDiskGb} onChange={(ev) => set('kvDiskGb', ev.target.value)} />
          </Field>
        )}
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
          Weights {bytes(e.gpuWeights)}, token cache {bytes(e.gpuCache)} ({e.approximate ? `rough: ${label} says its own as it starts` : 'all copies'}), {bytes(e.gpuTotal)} of the GPUs
          {e.gpuBudget ? ` (${bytes(e.gpuBudget)} there for models)` : ''}
          {e.ramTotal ? `; ${bytes(e.ramTotal)} of RAM asleep and in its CPU tier` : ''}.
        </Alert>
      )}
      {(a?.problems ?? [])
        .filter((p) => !p.error && p.field !== 'maxOutput')
        .map((p) => (
          <Alert key={p.message} variant="warning">
            {p.message}
          </Alert>
        ))}
      {r && (
        <p className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          Fits this machine: {r.devices ? `GPUs ${r.devices}, ` : ''}
          {r.context.toLocaleString()} tokens a person, {r.parallel} at once.
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

/** What moving a model to another engine does, as the dialog says it before it is saved (the engines by their names for people). */
export function MovesAlert({ name, from: old, to: next }: { name: string; from: string; to: string }) {
  return (
    <Alert title={`Moves ${name} to ${next}`}>
      It keeps its name, who may use it, its prices and sampling. Its answers in flight end on {old}; new requests wait for it on {next}, where it loads
      at once on other GPUs, or once {old} has let it go on the same ones. Should it not start there, it stays on {old} and says why. What only {old} has is
      not carried over: it is kept, and a move back to {old} takes it up again.
    </Alert>
  )
}

/** A model's move to another engine on its card: how far it is, or why it failed (put away with Dismiss). */
export function MoveState({ name, move, onDismiss }: { name: string; move: ModelMove; onDismiss: () => void }) {
  const [from, to] = [engineLabel(move.from), engineLabel(move.to)]
  if (move.phase === 'failed') {
    return (
      <Alert variant="destructive" title={`${name} could not move to ${to}`}>
        {move.error} It stays on {from}, as it was. Edit it to try again.
        <div className="mt-2">
          <Button type="button" size="sm" variant="outline" onClick={onDismiss}>
            Dismiss
          </Button>
        </div>
      </Alert>
    )
  }
  const text =
    move.phase === 'draining'
      ? `Its answers on ${from} end first, on the same GPUs; then it loads on ${to}. Requests for it wait meanwhile.`
      : move.phase === 'loading'
        ? `Loading on ${to}: requests for it wait until it is ready there.${move.old !== 'gone' ? ` Its last answers on ${from} end beside it.` : ''}`
        : `Ready on ${to}, where its requests go. Its last answers on ${from} are ending.`
  return (
    <Alert title={`Moving to ${to}`}>
      <span className="inline-flex items-center gap-1.5">
        <Loader2 className="size-3.5 animate-spin" aria-hidden="true" />
        {text}
      </span>
    </Alert>
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
