import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CheckCircle2, Cpu, Info, Sparkles, XCircle } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { attentionLabel, bytes, kindLabel, params, summary, tokens, type LibraryFile, type ModelProfile } from './model-profile'

interface Bounds {
  min: number
  max: number
}

interface Advice {
  profile: ModelProfile
  limits: {
    context: Bounds
    maxOutput: Bounds
    parallel: Bounds
    gpuLayers: Bounds
    cpuMoe: Bounds | null
    draftMax: Bounds
    cacheTypes: string[]
    ubatch: number[]
    mtp: boolean
    yarn: boolean
    projectors: string[]
    draftHeads: string[]
  } | null
  recommended: {
    context: number
    maxOutput: number
    parallel: number
    kvType: string
    ubatch: number | null
    mtp: boolean
    draftHead: string | null
    draftMax: number
    projector: string | null
    thinking: boolean
    tools: boolean
  } | null
  estimate: {
    gpuWeights: number
    gpuCache: number
    gpuCompute: number
    gpuTotal: number
    gpuBudget: number | null
    ramWeights: number
    ramCache: number
    ramTotal: number
    ramBudget: number | null
    gpuLayers: number
    layers: number
    expertLayersInRam: number
    moeLayers: number
    fit: 'gpu' | 'experts' | 'layers' | 'cpu' | 'over' | 'none' | 'unknown'
    approximate: boolean
  } | null
  problems: { field: string; message: string; error: boolean }[]
  hardware: { gpuName: string | null; gpus: number; gpuTotal: number; imageReserve: number; ramTotal: number; gpuForModels: number; ramForModels: number } | null
}

/** A local model as the API lists it: what the form edits. */
export interface SavedModel {
  name: string
  file?: string | null
  projector?: string | null
  context?: number | null
  maxOutput?: number | null
  placement?: 'auto' | 'manual'
  gpuLayers?: number
  cpuMoe?: number
  kvType?: string
  parallel?: number
  ubatch?: number | null
  mtp?: boolean
  draftHead?: string | null
  draftMax?: number
  yarn?: boolean
  temperature?: number | null
  topP?: number | null
  topK?: number | null
  minP?: number | null
  presencePenalty?: number | null
  extraPreset?: string | null
  thinking?: boolean
  tools?: boolean
  inputPerMtok?: number | null
  outputPerMtok?: number | null
}

/** The longest answer when none is set, as the API registers it at the gateway: half the context, at most 32,768. */
const defaultMaxOutput = (context: number) => Math.max(256, Math.min(32768, Math.floor(context / 2 / 1024) * 1024))

const baseName = (path: string) => (path.split('/').pop() ?? '').replace(/(-\d{5}-of-\d{5})?\.gguf$/i, '')

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return v
}

type FormState = ReturnType<typeof initial>

function initial(saved: SavedModel | null) {
  const s = (v: number | null | undefined) => (v === null || v === undefined ? '' : String(v))
  return {
    name: saved?.name ?? '',
    file: saved?.file ?? '',
    projector: saved?.projector ?? '',
    context: s(saved?.context ?? 32768),
    maxOutput: s(saved?.maxOutput),
    placement: saved?.placement ?? 'auto',
    gpuLayers: s(saved?.gpuLayers ?? 99),
    cpuMoe: s(saved?.cpuMoe ?? 0),
    kvType: saved?.kvType ?? 'q8_0',
    parallel: s(saved?.parallel ?? 1),
    ubatch: s(saved?.ubatch),
    mtp: saved?.mtp ?? false,
    draftHead: saved?.draftHead ?? '',
    draftMax: s(saved?.draftMax ?? 3),
    yarn: saved?.yarn ?? false,
    temperature: s(saved?.temperature),
    topP: s(saved?.topP),
    topK: s(saved?.topK),
    minP: s(saved?.minP),
    presencePenalty: s(saved?.presencePenalty),
    extraPreset: saved?.extraPreset ?? '',
    thinking: saved?.thinking ?? true,
    tools: saved?.tools ?? true,
    inputPerMtok: s(saved?.inputPerMtok),
    outputPerMtok: s(saved?.outputPerMtok),
  }
}

const numeric = ['context', 'maxOutput', 'gpuLayers', 'cpuMoe', 'parallel', 'draftMax', 'temperature', 'topP', 'topK', 'minP', 'presencePenalty', 'inputPerMtok', 'outputPerMtok'] as const
const clearable = ['maxOutput', 'ubatch', 'draftHead', 'temperature', 'topP', 'topK', 'minP', 'presencePenalty', 'inputPerMtok', 'outputPerMtok'] as const

/** What the API takes, from the form; an emptied field is named in "clear" (a missing one is left as it was). */
function body(form: FormState) {
  const n = (v: string) => (v.trim() === '' ? undefined : Number(v))
  return {
    file: form.file,
    projector: form.projector,
    context: n(form.context),
    maxOutput: n(form.maxOutput),
    placement: form.placement,
    gpuLayers: n(form.gpuLayers),
    cpuMoe: n(form.cpuMoe),
    kvType: form.kvType,
    parallel: n(form.parallel),
    ubatch: n(form.ubatch),
    mtp: form.mtp,
    draftHead: form.draftHead,
    draftMax: n(form.draftMax),
    yarn: form.yarn,
    temperature: n(form.temperature),
    topP: n(form.topP),
    topK: n(form.topK),
    minP: n(form.minP),
    presencePenalty: n(form.presencePenalty),
    extraPreset: form.extraPreset,
    thinking: form.thinking,
    tools: form.tools,
    inputPerMtok: n(form.inputPerMtok),
    outputPerMtok: n(form.outputPerMtok),
    clear: clearable.filter((k) => form[k].trim() === ''),
  }
}

/**
 * Adding or editing an engine model. The file says what it is (dense or a
 * mixture of experts, hybrid attention, what it was trained for); the form
 * asks only what that kind has, shows each limit, starts from settings that
 * fit this machine, and estimates the memory as it is filled in. The API runs
 * the same checks on save.
 */
export function ModelForm({ saved, onClose }: { saved: SavedModel | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const library = useQuery({ queryKey: ['admin', 'models', 'library'], queryFn: ({ signal }) => api<LibraryFile[]>('/api/admin/models/library', { signal }) })
  const [form, setForm] = useState(() => initial(saved))
  const [nameTouched, setNameTouched] = useState(!!saved)
  const [error, setError] = useState<string | null>(null)
  const files = library.data ?? []
  const file = files.find((f) => f.path === form.file)
  const language = file?.profile.kind === 'language'
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setForm((f) => ({ ...f, [k]: v }))

  const request = useMemo(() => body(form), [form])
  const debounced = useDebounced(request, 350)
  const advice = useQuery({
    queryKey: ['admin', 'models', 'advice', debounced],
    queryFn: ({ signal }) => api<Advice>('/api/admin/models/advice', { body: debounced, signal }),
    enabled: !!debounced.file && language,
    placeholderData: keepPreviousData,
    retry: false,
  })
  const a = language ? advice.data : undefined
  const current = !advice.isFetching && JSON.stringify(debounced) === JSON.stringify(request)
  const errors: Record<string, string> = {}
  for (const p of a?.problems ?? []) if (p.error && !errors[p.field]) errors[p.field] = p.message
  for (const k of numeric) if (form[k].trim() !== '' && !Number.isFinite(Number(form[k]))) errors[k] = 'Enter a number.'
  const limits = a?.limits
  const profile = file?.profile

  const recommend = (r: NonNullable<Advice['recommended']>) =>
    setForm((f) => ({
      ...f,
      context: String(r.context),
      maxOutput: String(r.maxOutput),
      parallel: String(r.parallel),
      kvType: r.kvType,
      ubatch: r.ubatch ? String(r.ubatch) : '',
      mtp: r.mtp,
      draftHead: r.draftHead ?? '',
      draftMax: String(r.draftMax),
      projector: r.projector ?? '',
      thinking: r.thinking,
      tools: r.tools,
      placement: 'auto',
      yarn: false,
    }))

  const chooseFile = async (path: string) => {
    const chosen = files.find((x) => x.path === path)
    // The name follows the file until it is typed by hand.
    setForm((cur) => ({ ...cur, file: path, name: nameTouched ? cur.name : baseName(path) }))
    if (saved || chosen?.profile.kind !== 'language') return
    try {
      const first = await api<Advice>('/api/admin/models/advice', { body: { file: path } })
      if (first.recommended) recommend(first.recommended)
    } catch {
      // The live check below says what is wrong.
    }
  }

  const save = useMutation({
    mutationFn: () =>
      saved
        ? api<{ warning: string | null }>(`/api/admin/models/${encodeURIComponent(saved.name)}`, { method: 'PATCH', body: request })
        : api<{ warning: string | null }>('/api/admin/models', { body: { ...request, name: form.name.trim() } }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r?.warning) toast.warning(r.warning)
      else toast.success(saved ? `${saved.name} saved` : `${form.name} added`, { description: 'The engine restarts to read it: the loaded model is back in a moment. Load it from its card.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })

  const blocked = !form.file || !form.name.trim() || !language || Object.keys(errors).length > 0 || (!current && !!a)
  const models = files.filter((f) => f.profile.kind === 'language')
  const others = files.filter((f) => f.profile.kind !== 'language')
  const describe = (f: LibraryFile) => [f.path, formatValue(f.size, 'bytes') + (f.parts > 1 ? ` in ${f.parts} parts` : ''), summary(f.profile)].filter(Boolean).join(' · ')
  const ctx = Number(form.context) || 0
  const outDefault = defaultMaxOutput(ctx || 32768)
  const moe = profile?.structure === 'moe'
  const samplingHint = profile?.sampling
    ? `Empty: its makers' ${[
        profile.sampling.temperature !== null && `temperature ${profile.sampling.temperature}`,
        profile.sampling.topP !== null && `top-p ${+profile.sampling.topP.toFixed(3)}`,
        profile.sampling.topK !== null && `top-k ${profile.sampling.topK}`,
        profile.sampling.minP !== null && `min-p ${profile.sampling.minP}`,
      ]
        .filter(Boolean)
        .join(', ')} (in its file; the engine applies it), else llama.cpp's defaults.`
    : "Empty: llama.cpp's defaults (temperature 0.8, top-p 0.95, top-k 40, min-p 0.05). Its file recommends none."

  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'Add a model'}</DialogTitle>
        <DialogDescription>A GGUF file from the model library, and how the engine runs it. Its file says what it is; the form asks only what that kind of model has.</DialogDescription>
      </DialogHeader>
      <form
        className="grid min-w-0 gap-5"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <Field label="Model file" error={errors.file} hint={library.isPending ? 'Reading the library…' : models.length ? 'Language models in the library; the other files are listed for what they are.' : 'No language models in the library (LLAMACPP_LIBRARY_DIR).'}>
          <Select value={form.file} onValueChange={chooseFile}>
            <SelectTrigger className="min-w-0 [&>span]:truncate">
              <SelectValue placeholder="Choose a file" />
            </SelectTrigger>
            <SelectContent className="max-w-[calc(100vw-2rem)]">
              <SelectGroup>
                <SelectLabel>Language models</SelectLabel>
                {models.map((f) => (
                  <SelectItem key={f.path} value={f.path} className="[overflow-wrap:anywhere]">
                    {describe(f)}
                  </SelectItem>
                ))}
              </SelectGroup>
              {others.length > 0 && (
                <SelectGroup>
                  <SelectLabel>Not served from here</SelectLabel>
                  {others.map((f) => (
                    <SelectItem key={f.path} value={f.path} disabled className="[overflow-wrap:anywhere]">
                      {f.path} · {kindLabel[f.profile.kind]}
                    </SelectItem>
                  ))}
                </SelectGroup>
              )}
            </SelectContent>
          </Select>
        </Field>

        {file && !language && <Alert variant="warning" title={kindLabel[file.profile.kind]}>{file.profile.why}</Alert>}
        {file && language && <Detected file={file} kvType={form.kvType} />}

        {language && (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Name" hint="Its name in the chat and at the gateway.">
                <Input
                  value={form.name}
                  onChange={(e) => {
                    setNameTouched(true)
                    set('name', e.target.value)
                  }}
                  disabled={!!saved}
                  required
                  maxLength={100}
                  autoComplete="off"
                />
              </Field>
              <Field
                label="Vision projector"
                error={errors.projector}
                hint={limits && limits.projectors.length === 0 ? 'None in the library fits this model: it reads text only.' : 'An mmproj file made for this model, for it to see images.'}
              >
                <Select value={form.projector || 'none'} onValueChange={(v) => set('projector', v === 'none' ? '' : v)} disabled={!limits || limits.projectors.length === 0}>
                  <SelectTrigger className="min-w-0 [&>span]:truncate">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="none">None</SelectItem>
                    {(limits?.projectors ?? []).map((p) => (
                      <SelectItem key={p} value={p}>
                        {p}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              <Field
                label="Context (tokens)"
                error={errors.context}
                hint={
                  limits
                    ? `${tokens(limits.context.min)} to ${tokens(limits.context.max)}${profile?.trainedContext ? ` (trained for ${tokens(profile.trainedContext)}${form.yarn ? ', stretched' : ''})` : ''}. One conversation can use all of it; answers at once share it.`
                    : 'Checking the limits…'
                }
              >
                <Input inputMode="numeric" value={form.context} onChange={(e) => set('context', e.target.value)} required />
              </Field>
              <Field
                label="Longest answer (tokens)"
                error={errors.maxOutput}
                hint={limits ? `${tokens(limits.maxOutput.min)} to ${tokens(limits.maxOutput.max)}, the context less room for a prompt. Empty: ${tokens(outDefault)}.` : undefined}
              >
                <Input inputMode="numeric" value={form.maxOutput} placeholder={String(outDefault)} onChange={(e) => set('maxOutput', e.target.value)} />
              </Field>
              <Field
                label="Answers at once"
                error={errors.parallel}
                hint={`1 to ${limits?.parallel.max ?? 32}. They share the context${profile?.recurrentBytesPerSlot ? `; each keeps ${bytes(profile.recurrentBytesPerSlot)} of recurrent state` : ''}.`}
              >
                <Input inputMode="numeric" value={form.parallel} onChange={(e) => set('parallel', e.target.value)} />
              </Field>
              <Field
                label="Cache type"
                error={errors.kvType}
                hint={
                  limits && !limits.cacheTypes.includes('q8_0')
                    ? `Its attention heads cannot take a quantized cache (no flash attention for them).`
                    : 'q8_0 halves the cache of f16 with no loss to speak of; q4_0 quarters it, with some.'
                }
              >
                <Select value={form.kvType} onValueChange={(v) => set('kvType', v)}>
                  <SelectTrigger className="min-w-0 [&>span]:truncate">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {(limits?.cacheTypes ?? [form.kvType]).map((k) => (
                      <SelectItem key={k} value={k}>
                        {k}
                        {profile?.kvBytesPerToken[k] ? ` · ${bytes(profile.kvBytesPerToken[k] * 1000)} / 1K tokens` : ''}
                        {a?.recommended?.kvType === k ? ' · recommended' : ''}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
            </div>

            <Section title="Placement" description="Where the layers go: the GPU is fastest; what does not fit there runs from RAM.">
              <Field label="Placement" error={errors.placement}>
                <Select value={form.placement} onValueChange={(v) => set('placement', v as 'auto' | 'manual')}>
                  <SelectTrigger className="min-w-0 [&>span]:truncate">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="auto">Automatic: llama.cpp fits it to the free GPU memory as it loads (recommended)</SelectItem>
                    <SelectItem value="manual">By hand</SelectItem>
                  </SelectContent>
                </Select>
              </Field>
              {form.placement === 'manual' && (
                <div className="grid gap-4 sm:grid-cols-2">
                  <Field label="Layers on the GPU" error={errors.gpuLayers} hint={limits ? `0 to ${limits.gpuLayers.max}: ${limits.gpuLayers.max - 1} layers and the output, counted from the last.` : undefined}>
                    <Input inputMode="numeric" value={form.gpuLayers} onChange={(e) => set('gpuLayers', e.target.value)} />
                  </Field>
                  {moe && (
                    <Field label="Layers with experts in RAM" error={errors.cpuMoe} hint={limits?.cpuMoe ? `0 to ${limits.cpuMoe.max}, from the first layer. The rest of each layer stays on the GPU.` : undefined}>
                      <Input inputMode="numeric" value={form.cpuMoe} onChange={(e) => set('cpuMoe', e.target.value)} />
                    </Field>
                  )}
                </div>
              )}
              <Field
                label="Prompt step (tokens)"
                error={errors.ubatch}
                hint={`Tokens of a prompt read at once. More reads long prompts faster for more GPU memory${moe ? '; 1024 or more helps a mixture of experts with experts in RAM' : ''}.`}
              >
                <Select value={form.ubatch || 'default'} onValueChange={(v) => set('ubatch', v === 'default' ? '' : v)}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="default">The engine's (512){a?.recommended && a.recommended.ubatch === null ? ' · recommended' : ''}</SelectItem>
                    {(limits?.ubatch ?? []).map((u) => (
                      <SelectItem key={u} value={String(u)}>
                        {tokens(u)}
                        {a?.recommended?.ubatch === u ? ' · recommended' : ''}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
            </Section>

            {(limits?.mtp || limits?.yarn) && (
              <Section title="Speed and reach">
                {limits.mtp && (
                  <div className="grid gap-3">
                    <Label className="font-normal">
                      <Switch checked={form.mtp} onCheckedChange={(v) => set('mtp', v)} />
                      Draft tokens with multi-token prediction
                      {profile?.mtpLayers ? ' (its own prediction layer)' : ''}
                    </Label>
                    {errors.mtp && <p className="text-xs font-medium text-destructive">{errors.mtp}</p>}
                    {form.mtp && (
                      <div className="grid gap-4 sm:grid-cols-2">
                        {limits.draftHeads.length > 0 && (
                          <Field label="Draft head" error={errors.draftHead} hint={profile?.mtpLayers ? 'Empty: its own layer.' : 'Made for this model.'}>
                            <Select value={form.draftHead || 'own'} onValueChange={(v) => set('draftHead', v === 'own' ? '' : v)}>
                              <SelectTrigger className="min-w-0 [&>span]:truncate">
                                <SelectValue />
                              </SelectTrigger>
                              <SelectContent>
                                {profile?.mtpLayers ? <SelectItem value="own">Its own layer</SelectItem> : <SelectItem value="own">Choose a head</SelectItem>}
                                {limits.draftHeads.map((h) => (
                                  <SelectItem key={h} value={h}>
                                    {h}
                                  </SelectItem>
                                ))}
                              </SelectContent>
                            </Select>
                          </Field>
                        )}
                        <Field label="Draft tokens" error={errors.draftMax} hint="1 to 8 guessed per step; 3 suits most.">
                          <Input inputMode="numeric" value={form.draftMax} onChange={(e) => set('draftMax', e.target.value)} />
                        </Field>
                      </div>
                    )}
                  </div>
                )}
                {limits.yarn && (
                  <div className="grid gap-1">
                    <Label className="font-normal">
                      <Switch checked={form.yarn} onCheckedChange={(v) => set('yarn', v)} />
                      Stretch the context past its training (YaRN, up to 4×)
                    </Label>
                    <p className="text-xs text-muted-foreground">For long documents; it can make answers to short prompts a little worse.</p>
                    {errors.yarn && <p className="text-xs font-medium text-destructive">{errors.yarn}</p>}
                  </div>
                )}
              </Section>
            )}

            <div className="flex flex-wrap gap-6">
              <Label className="font-normal">
                <Switch checked={form.thinking} onCheckedChange={(v) => set('thinking', v)} disabled={profile?.thinking === false && !form.thinking} /> Thinks before answering
                {profile?.thinking === false && <span className="text-muted-foreground">(its template has no thinking)</span>}
              </Label>
              <Label className="font-normal">
                <Switch checked={form.tools} onCheckedChange={(v) => set('tools', v)} disabled={profile?.tools === false && !form.tools} /> Can call tools
                {profile?.tools === false && <span className="text-muted-foreground">(its template has no tool calls)</span>}
              </Label>
            </div>
            {(errors.thinking || errors.tools) && <p className="text-xs font-medium text-destructive">{errors.thinking ?? errors.tools}</p>}

            <Section title="Sampling" description={samplingHint}>
              <div className="grid grid-cols-2 gap-4 sm:grid-cols-5">
                <Field label="Temperature">
                  <Input inputMode="decimal" value={form.temperature} placeholder={String(profile?.sampling?.temperature ?? 0.8)} onChange={(e) => set('temperature', e.target.value)} />
                </Field>
                <Field label="Top-p">
                  <Input inputMode="decimal" value={form.topP} placeholder={String(+(profile?.sampling?.topP ?? 0.95).toFixed(3))} onChange={(e) => set('topP', e.target.value)} />
                </Field>
                <Field label="Top-k">
                  <Input inputMode="numeric" value={form.topK} placeholder={String(profile?.sampling?.topK ?? 40)} onChange={(e) => set('topK', e.target.value)} />
                </Field>
                <Field label="Min-p">
                  <Input inputMode="decimal" value={form.minP} placeholder={String(profile?.sampling?.minP ?? 0.05)} onChange={(e) => set('minP', e.target.value)} />
                </Field>
                <Field label="Presence penalty">
                  <Input inputMode="decimal" value={form.presencePenalty} placeholder="0" onChange={(e) => set('presencePenalty', e.target.value)} />
                </Field>
              </div>
              {errors.sampling && <p className="text-xs font-medium text-destructive">{errors.sampling}</p>}
            </Section>

            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Price in, per 1M tokens" hint="Empty: the gateway's defaults.">
                <Input inputMode="decimal" value={form.inputPerMtok} onChange={(e) => set('inputPerMtok', e.target.value)} />
              </Field>
              <Field label="Price out, per 1M tokens">
                <Input inputMode="decimal" value={form.outputPerMtok} onChange={(e) => set('outputPerMtok', e.target.value)} />
              </Field>
            </div>
            <Field label="More engine options" error={errors.extra} hint="One per line, key = value, with llama-server's long option names, e.g. flash-attn = on. Names this engine does not know are refused: they would stop it.">
              <Textarea rows={3} className="font-mono text-xs" value={form.extraPreset} onChange={(e) => set('extraPreset', e.target.value)} />
            </Field>

            <Memory advice={a} pending={advice.isFetching} failed={advice.error ? errorMessage(advice.error) : null} />
          </>
        )}

        <DialogFooter className="gap-2 sm:justify-between">
          <Button type="button" variant="outline" disabled={!a?.recommended} onClick={() => a?.recommended && recommend(a.recommended)}>
            <Sparkles /> Use recommended
          </Button>
          <div className="flex flex-col-reverse gap-2 sm:flex-row">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending} disabled={blocked}>
              {saved ? 'Save' : 'Add model'}
            </Button>
          </div>
        </DialogFooter>
      </form>
    </>
  )
}

function Section({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <fieldset className="grid min-w-0 gap-4 rounded-lg border p-4">
      <legend className="px-1 text-sm font-medium">{title}</legend>
      {description && <p className="-mt-2 text-xs text-muted-foreground">{description}</p>}
      {children}
    </fieldset>
  )
}

/** What the file says it is. */
function Detected({ file, kvType }: { file: LibraryFile; kvType: string }) {
  const p = file.profile
  const size = params(p.parameters)
  const active = p.structure === 'moe' ? params(p.activeParameters) : null
  const perK = p.kvBytesPerToken[kvType] ?? p.kvBytesPerToken.f16
  const lines = [
    [p.name, size && `${size} parameters${active ? `, ${active} active per token` : ''}`, p.quant, p.bitsPerWeight && `${p.bitsPerWeight.toFixed(1)} bits per weight`, `${bytes(file.size)}${file.parts > 1 ? ` in ${file.parts} parts` : ''}`]
      .filter(Boolean)
      .join(' · '),
    p.experts && `${p.experts.count} experts, ${p.experts.used} per token${p.experts.shared ? `, ${p.experts.shared} shared` : ''}; ${bytes(p.experts.bytes)} of them in ${p.experts.layers} layers`,
    p.layers &&
      (p.attention === 'hybrid'
        ? `${p.attentionLayers} of ${p.layers} layers keep a cache (${bytes((perK ?? 0) * 1000)} per 1,000 tokens at ${kvType}); the rest keep a small state`
        : p.attention === 'sliding'
          ? `${p.layers} layers, most of them with a ${tokens(p.slidingWindow ?? 0)}-token window: the cache grows slowly with the context`
          : p.attention === 'recurrent'
            ? `${p.layers} recurrent layers: no cache that grows with the context`
            : `${p.layers} layers, each with a cache: ${bytes((perK ?? 0) * 1000)} per 1,000 tokens at ${kvType}`),
    p.trainedContext &&
      `Trained for ${tokens(p.trainedContext)} tokens of context${p.ropeScaling ? ` (stretched by its file: ${p.ropeScaling.type}${p.ropeScaling.factor ? ` ×${p.ropeScaling.factor}` : ''})` : ''}`,
  ].filter(Boolean)
  return (
    <section aria-label="What the file is" className="grid gap-2 rounded-lg border bg-muted/40 p-4 text-sm">
      <div className="flex flex-wrap gap-1.5">
        <Badge>{p.structure === 'moe' ? 'Mixture of experts' : 'Dense'}</Badge>
        {p.attention && attentionLabel[p.attention] && <Badge variant="secondary">{attentionLabel[p.attention]}</Badge>}
        {p.thinking && <Badge variant="outline">Thinks</Badge>}
        {p.tools && <Badge variant="outline">Calls tools</Badge>}
        {p.mtpLayers > 0 && <Badge variant="outline">Built-in multi-token prediction</Badge>}
        {p.architecture && <Badge variant="outline">{p.architecture}</Badge>}
      </div>
      {lines.map((l) => (
        <p key={l as string} className="[overflow-wrap:anywhere] text-muted-foreground">
          {l}
        </p>
      ))}
      {p.note && (
        <p className="flex gap-1.5 text-muted-foreground">
          <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          {p.note}
        </p>
      )}
    </section>
  )
}

const fitText: Record<NonNullable<Advice['estimate']>['fit'], string> = {
  gpu: 'All of it on the GPU: the fastest it runs.',
  experts: '',
  // Said by the problems below.
  layers: '',
  cpu: '',
  over: '',
  none: '',
  unknown: 'No memory figures for this machine: Prometheus and its GPU exporter (the smi profile) give them. The engine still fits the model when it loads.',
}

/** Where the model's bytes go, and whether that is fast, slow, or impossible. */
function Memory({ advice, pending, failed }: { advice: Advice | undefined; pending: boolean; failed: string | null }) {
  const e = advice?.estimate
  const hw = advice?.hardware
  const warnings = (advice?.problems ?? []).filter((p) => p.field === 'memory' || p.field === 'power')
  if (failed) return <Alert variant="warning">{failed}</Alert>
  if (!e) return null
  const good = e.fit === 'gpu' || e.fit === 'experts'
  const bad = e.fit === 'over' || e.fit === 'none'
  const text =
    e.fit === 'experts'
      ? `The experts of ${e.expertLayersInRam} of ${e.moeLayers} layers stay in RAM; the rest is on the GPU. Usual for a mixture of experts too big for the GPU: answers come a little slower, long prompts still read on the GPU.`
      : fitText[e.fit]
  const budget = e.gpuBudget ?? e.gpuTotal
  const segments = [
    { label: 'Weights', value: e.gpuWeights, className: 'bg-primary' },
    { label: 'Cache', value: e.gpuCache, className: 'bg-primary/40' },
    { label: 'Buffers', value: Math.max(0, e.gpuTotal - e.gpuWeights - e.gpuCache), className: 'bg-muted-foreground/50' },
  ]
  const scale = Math.max(budget, e.gpuTotal, 1)
  return (
    <section aria-label="Memory" className={cn('grid gap-3 rounded-lg border p-4 text-sm', bad && 'border-destructive/50', pending && 'opacity-70')}>
      <div className="flex items-start gap-2">
        {bad ? (
          <XCircle className="mt-0.5 size-4 shrink-0 text-destructive-ink" aria-hidden="true" />
        ) : good ? (
          <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-success-ink" aria-hidden="true" />
        ) : e.fit === 'unknown' ? (
          <Cpu className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        ) : (
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning-ink" aria-hidden="true" />
        )}
        <p>
          <span className="font-medium">
            {e.fit === 'unknown' ? `About ${bytes(e.gpuTotal)} of GPU memory with all of it on the GPU.` : `About ${bytes(e.gpuTotal)} of the ${bytes(budget)} of GPU memory there is for models${hw?.gpuName ? ` (${hw.gpuName})` : ''}.`}
          </span>{' '}
          {text}
          {e.approximate && ' Rough for this architecture: parts of its cache are not modelled.'}
        </p>
      </div>
      {/* The legend below says the same in words. */}
      <div aria-hidden="true" className="relative flex h-3 overflow-hidden rounded-full bg-muted">
        {segments.map((s) => (
          <div key={s.label} className={cn('h-full border-r-2 border-background last:border-r-0', s.className)} style={{ width: `${(100 * s.value) / scale}%` }} />
        ))}
        {e.gpuBudget !== null && e.gpuTotal > e.gpuBudget && <div className="absolute inset-y-0 w-0.5 bg-destructive" style={{ left: `${(100 * e.gpuBudget) / scale}%` }} />}
      </div>
      <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
        {segments.map((s) => (
          <li key={s.label} className="flex items-center gap-1.5">
            <span className={cn('size-2.5 rounded-sm', s.className)} aria-hidden="true" />
            {s.label} {bytes(s.value)}
          </li>
        ))}
        <li>
          RAM: {bytes(e.ramWeights)} of weights{e.ramCache > 0 ? `, ${bytes(e.ramCache)} of cache and buffers` : ''}
          {e.ramBudget !== null ? ` (${bytes(e.ramBudget)} kept for models)` : ''}
        </li>
      </ul>
      {warnings.map((w) => (
        <p key={w.message} className={cn('text-xs', w.error ? 'font-medium text-destructive' : 'text-warning-ink')}>
          {w.message}
        </p>
      ))}
    </section>
  )
}
