import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DialogFooter } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { roleLabel, type MediaMode } from './media-modes'
import { Section } from './model-form'

/** A picture, video or speech model as Admin -> Models has it (the fields this form edits). */
export interface SavedMediaModel {
  name: string
  mode: string
  files?: { role: string; path: string; present: boolean }[]
  speechId?: string | null
  settings?: Partial<Record<(typeof numberFields)[number] | (typeof textFields)[number], string | number | null>> & {
    voices?: string[]
    offloadToCpu?: boolean
    vaeTiling?: boolean
    flashAttention?: boolean
    vae?: string
  }
  kept?: boolean
  idleMinutes?: number | null
  unitPrice?: { unit: string; amount: number; own?: boolean }
}

export interface LibraryRow {
  path: string
  size: number
  role: string
  usedBy: string[]
}

export interface SpeechRow {
  id: string
  task: string
  language?: string[] | null
  voices?: string[] | null
}

const roles = ['diffusion', 'model', 'llm', 'llm_vision', 't5xxl', 'clip_l', 'clip_g', 'clip_vision', 'vae', 'taesd', 'high_noise'] as const
const samplers = ['euler', 'euler_a', 'heun', 'dpm2', 'dpm++2s_a', 'dpm++2m', 'dpm++2mv2', 'ipndm', 'ipndm_v', 'lcm', 'ddim_trailing', 'tcd']
const schedulers = ['discrete', 'karras', 'exponential', 'ays', 'gits', 'smoothstep', 'sgm_uniform', 'simple']

const numberFields = ['steps', 'cfgScale', 'guidance', 'flowShift', 'width', 'height', 'fps', 'maxSeconds', 'gpu', 'maxVramGb', 'vaeGpuGb', 'speed', 'idleMinutes', 'unitPrice'] as const
const textFields = ['sampler', 'scheduler', 'negativePrompt', 'extraArgs', 'language', 'defaultVoice'] as const

type Form = Record<(typeof numberFields)[number] | (typeof textFields)[number], string> & {
  name: string
  mode: MediaMode
  files: { role: string; path: string }[]
  speechId: string
  voices: string[]
  vae: string
  offloadToCpu: boolean
  vaeTiling: boolean
  flashAttention: boolean
  kept: boolean
}

const str = (v: unknown) => (v === null || v === undefined ? '' : String(v))

function initial(saved: SavedMediaModel | null, mode: MediaMode): Form {
  const s = saved?.settings ?? {}
  return {
    name: saved?.name ?? '',
    mode: (saved?.mode as MediaMode) ?? mode,
    files: saved?.files?.map((f) => ({ role: f.role, path: f.path })) ?? [{ role: 'diffusion', path: '' }],
    speechId: saved?.speechId ?? '',
    voices: s.voices ?? [],
    vae: s.vae ?? 'gpu',
    offloadToCpu: s.offloadToCpu ?? false,
    vaeTiling: s.vaeTiling ?? false,
    flashAttention: s.flashAttention ?? true,
    kept: saved?.kept ?? false,
    ...Object.fromEntries(numberFields.map((k) => [k, str(s[k])])),
    ...Object.fromEntries(textFields.map((k) => [k, str(s[k])])),
    idleMinutes: str(saved?.idleMinutes),
    unitPrice: saved?.unitPrice?.own ? str(saved.unitPrice.amount) : '',
  } as Form
}

/** The request: numbers as numbers, empty fields cleared on a change (left out on an add). */
function body(form: Form, editing: boolean) {
  const out: Record<string, unknown> = { kept: form.kept }
  const clear: string[] = []
  const pictures = form.mode === 'image_generation' || form.mode === 'video_generation'
  const numbers = pictures ? numberFields.filter((k) => k !== 'speed') : (['speed', 'idleMinutes', 'unitPrice'] as const)
  const texts = pictures ? (['sampler', 'scheduler', 'negativePrompt', 'extraArgs'] as const) : (['language', 'defaultVoice'] as const)
  for (const k of numbers) {
    const v = form[k].trim()
    if (v === '') clear.push(k)
    else out[k] = Number(v)
  }
  for (const k of texts) {
    const v = form[k].trim()
    if (v === '') clear.push(k)
    else out[k] = v
  }
  if (pictures) {
    out.files = form.files.filter((f) => f.path)
    Object.assign(out, { vae: form.vae, offloadToCpu: form.offloadToCpu, vaeTiling: form.vaeTiling, flashAttention: form.flashAttention })
  } else {
    out.speechId = form.speechId.trim()
    if (form.mode === 'audio_speech') out.voices = form.voices
  }
  if (editing) out.clear = clear
  return out
}

/** A name from a file: its own name without the extension. */
const fileName = (path: string) => path.split('/').pop()!.replace(/\.(gguf|safetensors|ckpt|pth|pt|bin)$/i, '')

/** The file that is the model itself: its diffusion model, or the whole model in one file. */
const mainFile = (files: { role: string; path: string }[]) => files.find((f) => (f.role === 'diffusion' || f.role === 'model') && f.path)?.path ?? ''

/**
 * The picture, video and speech models' part of the model dialog: a picture or video model's files from the
 * library (or the speech server's model), how it runs, its GPU, price and idle time.
 */
export function MediaModelSection({
  saved,
  mode,
  file,
  speechId,
  onFile,
  gpus = [],
  onClose,
}: {
  saved: SavedMediaModel | null
  /** What it does, as the dialog says. */
  mode: MediaMode
  /** A file chosen in the dialog (its "File or model"), with the role it reads as: the model itself goes first, another file in its role. */
  file?: { path: string; role: string }
  /** A model the speech server has, chosen in the dialog. */
  speechId?: string
  /** The model's own file changed here (in its Files): the dialog's choice follows it. */
  onFile?: (path: string) => void
  gpus?: { index: number; name: string; total: number }[]
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [state, setState] = useState(() => initial(saved, mode))
  // The dialog says what it does; the rest is this form's.
  const form = { ...state, mode }
  const [nameTouched, setNameTouched] = useState(!!saved)
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setState((f) => ({ ...f, [k]: v }))
  const pictures = mode === 'image_generation' || mode === 'video_generation'
  const video = mode === 'video_generation'
  const library = useQuery({
    queryKey: ['admin', 'models', 'media', 'files'],
    queryFn: ({ signal }) => api<LibraryRow[]>('/api/admin/models/media/files', { signal }),
    enabled: pictures,
  })
  const speech = useQuery({
    queryKey: ['admin', 'models', 'media', 'speech'],
    queryFn: ({ signal }) => api<SpeechRow[]>('/api/admin/models/media/speech', { signal }),
    enabled: !pictures,
    retry: false,
  })
  const task = mode === 'audio_speech' ? 'text-to-speech' : 'automatic-speech-recognition'
  const speechModels = (speech.data ?? []).filter((m) => m.task === task)
  const chosenSpeech = speechModels.find((m) => m.id === form.speechId)
  const files = library.data ?? []

  // The files changed: the name follows the model's own file until it is typed, and the dialog's choice follows it too.
  const shown = useRef(mainFile(state.files))
  const setFiles = (next: { role: string; path: string }[]) => {
    const main = mainFile(next)
    setState((f) => ({ ...f, files: next, name: nameTouched || !main ? f.name : fileName(main) }))
    if (main && main !== shown.current) {
      shown.current = main
      onFile?.(main)
    }
  }

  // A file chosen in the dialog: the model itself in place of the one there, another file in its role (in place of the one with that role, else added).
  const placed = useRef(mainFile(state.files))
  useEffect(() => {
    if (!pictures || !file?.path || file.path === placed.current || state.files.some((f) => f.path === file.path)) return
    placed.current = file.path
    const own = file.role === 'diffusion' || file.role === 'model'
    const at = own ? state.files.findIndex((f) => f.role === 'diffusion' || f.role === 'model') : state.files.findIndex((f) => f.role === file.role)
    const empty = state.files.findIndex((f) => !f.path && (own || (f.role !== 'diffusion' && f.role !== 'model')))
    const i = at >= 0 ? at : empty
    setFiles(i >= 0 ? state.files.map((f, j) => (j === i ? { role: file.role, path: file.path } : f)) : own ? [{ role: file.role, path: file.path }, ...state.files] : [...state.files, { role: file.role, path: file.path }])
  })

  // A model the speech server has, chosen in the dialog: the name follows it until it is typed.
  const [heard, setHeard] = useState(saved?.speechId ?? '')
  if (speechId && speechId !== heard) {
    setHeard(speechId)
    setState((f) => ({ ...f, speechId, name: nameTouched ? f.name : speechId.split('/').pop()! }))
  }

  const save = useMutation({
    mutationFn: () =>
      saved
        ? api<{ warning: string | null }>(`/api/admin/models/media/${encodeURIComponent(saved.name)}`, { method: 'PATCH', body: body(form, true) })
        : api<{ warning: string | null }>('/api/admin/models/media', { body: { ...body(form, false), name: form.name.trim(), mode } }),
    onSuccess: async (r) => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r?.warning) toast.warning(r.warning)
      else toast.success(saved ? `${saved.name} saved` : `${form.name.trim()} added`, { description: 'It loads when asked for, or now from its card.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const blocked = !form.name.trim() || (pictures ? !form.files.some((f) => f.path) : !form.speechId.trim())

  return (
    <>
      <form
        className="grid min-w-0 gap-5"
        aria-label={pictures ? (video ? 'The video model' : 'The picture model') : 'The speech model'}
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
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

        {pictures && (
          <Section title="Files" description="Each file by what it is: the diffusion model, its text encoders and its decoder (VAE), from the library.">
            {form.files.map((f, i) => (
              <div key={i} className="grid gap-2 sm:grid-cols-[12rem_minmax(0,1fr)_auto]">
                <Select value={f.role} onValueChange={(v) => v && setFiles(form.files.map((x, j) => (j === i ? { ...x, role: v } : x)))}>
                  <SelectTrigger aria-label={`File ${i + 1}: its role`}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {roles.map((r) => (
                      <SelectItem key={r} value={r}>
                        {roleLabel[r]}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Select
                  value={f.path || undefined}
                  onValueChange={(v) => {
                    // Emptied by the select itself when the library's list changes under it: the file stays.
                    if (!v) return
                    const hint = files.find((x) => x.path === v)?.role
                    setFiles(form.files.map((x, j) => (j === i ? { role: x.path || !hint ? x.role : hint, path: v } : x)))
                  }}
                >
                  <SelectTrigger className="min-w-0 [&>span]:truncate" aria-label={`File ${i + 1}`}>
                    <SelectValue placeholder={library.isPending ? 'Reading the library…' : 'Choose a file'} />
                  </SelectTrigger>
                  <SelectContent className="max-w-[calc(100vw-2rem)]">
                    {f.path && !files.some((x) => x.path === f.path) && <SelectItem value={f.path}>{f.path} (not in the library yet)</SelectItem>}
                    {files.map((x) => (
                      <SelectItem key={x.path} value={x.path} className="[overflow-wrap:anywhere]">
                        {x.path} · {formatValue(x.size, 'bytes')}
                        {x.usedBy.length ? ` · used by ${x.usedBy.join(', ')}` : ''}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button type="button" size="icon" variant="ghost" aria-label={`Remove file ${i + 1}`} onClick={() => setFiles(form.files.filter((_, j) => j !== i))}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            <div>
              <Button type="button" size="sm" variant="outline" onClick={() => setFiles([...form.files, { role: 'vae', path: '' }])}>
                <Plus /> Add a file
              </Button>
            </div>
          </Section>
        )}

        {pictures && (
          <Section title={video ? 'What a clip is made with' : 'What a picture is made with'} description="Unless a request says otherwise. Empty: stable-diffusion.cpp's own default.">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Steps">
                <Input inputMode="numeric" value={form.steps} onChange={(e) => set('steps', e.target.value)} />
              </Field>
              <Field label="CFG scale">
                <Input inputMode="decimal" value={form.cfgScale} onChange={(e) => set('cfgScale', e.target.value)} />
              </Field>
              <Field label="Guidance">
                <Input inputMode="decimal" value={form.guidance} onChange={(e) => set('guidance', e.target.value)} />
              </Field>
              <Field label="Flow shift">
                <Input inputMode="decimal" value={form.flowShift} onChange={(e) => set('flowShift', e.target.value)} />
              </Field>
              <Field label="Sampler">
                <Select value={form.sampler || 'default'} onValueChange={(v) => set('sampler', v === 'default' ? '' : v)}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="default">Its default</SelectItem>
                    {samplers.map((x) => (
                      <SelectItem key={x} value={x}>
                        {x}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              <Field label="Scheduler">
                <Select value={form.scheduler || 'default'} onValueChange={(v) => set('scheduler', v === 'default' ? '' : v)}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="default">Its default</SelectItem>
                    {schedulers.map((x) => (
                      <SelectItem key={x} value={x}>
                        {x}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              <Field label="Width" hint="A multiple of 16.">
                <Input inputMode="numeric" value={form.width} onChange={(e) => set('width', e.target.value)} />
              </Field>
              <Field label="Height">
                <Input inputMode="numeric" value={form.height} onChange={(e) => set('height', e.target.value)} />
              </Field>
              {video && (
                <>
                  <Field label="Frames a second">
                    <Input inputMode="numeric" value={form.fps} onChange={(e) => set('fps', e.target.value)} />
                  </Field>
                  <Field label="Longest clip (seconds)">
                    <Input inputMode="numeric" value={form.maxSeconds} onChange={(e) => set('maxSeconds', e.target.value)} />
                  </Field>
                </>
              )}
            </div>
            <Field label="Negative prompt" hint="What it should not draw.">
              <Input value={form.negativePrompt} onChange={(e) => set('negativePrompt', e.target.value)} maxLength={2000} />
            </Field>
          </Section>
        )}

        {pictures && (
          <Section title="The GPU" description="Its server shares the GPU with the chat models: a budget keeps its weights within it, the rest in RAM.">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="GPU">
                <Select value={form.gpu || 'any'} onValueChange={(v) => set('gpu', v === 'any' ? '' : v)}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="any">Any it sees</SelectItem>
                    {gpus.map((g) => (
                      <SelectItem key={g.index} value={String(g.index)}>
                        GPU {g.index} · {g.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              <Field label="GPU budget (GB)" hint="Empty: what is free.">
                <Input inputMode="decimal" value={form.maxVramGb} onChange={(e) => set('maxVramGb', e.target.value)} />
              </Field>
              <Field label="Decode on">
                <Select value={form.vae} onValueChange={(v) => set('vae', v)}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="gpu">The GPU</SelectItem>
                    <SelectItem value="cpu">The CPU (slower, less GPU memory)</SelectItem>
                    <SelectItem value="auto">The GPU when it has room as it loads</SelectItem>
                  </SelectContent>
                </Select>
              </Field>
              {form.vae === 'auto' && (
                <Field label="Decoding on the GPU takes (GB)" hint="It decodes there when this much, and 1 GB more, is free.">
                  <Input inputMode="decimal" value={form.vaeGpuGb} onChange={(e) => set('vaeGpuGb', e.target.value)} required />
                </Field>
              )}
            </div>
            <div className="flex flex-wrap gap-x-6 gap-y-2">
              <Label className="flex items-center gap-2 font-normal">
                <Checkbox checked={form.flashAttention} onCheckedChange={(on) => set('flashAttention', on === true)} /> Flash attention
              </Label>
              <Label className="flex items-center gap-2 font-normal">
                <Checkbox checked={form.vaeTiling} onCheckedChange={(on) => set('vaeTiling', on === true)} /> Decode in tiles
              </Label>
              <Label className="flex items-center gap-2 font-normal">
                <Checkbox checked={form.offloadToCpu} onCheckedChange={(on) => set('offloadToCpu', on === true)} /> Weights in RAM until needed
              </Label>
            </div>
            <Field label="More options" hint="sd-server's own, one a line (--seed 42). Its files, where it listens and what it reaches are the app's.">
              <Textarea value={form.extraArgs} onChange={(e) => set('extraArgs', e.target.value)} rows={3} className="font-mono text-xs" />
            </Field>
          </Section>
        )}

        {!pictures && (
          <Section title="The speech server's model">
            {/* Chosen above, from those the speech server lists; typed here when it lists none of this kind. */}
            {!speech.isPending && speechModels.length === 0 && (
              <Field
                label="Its id at the speech server"
                hint={speech.isError ? 'The speech server did not answer: type its id (owner/name).' : 'The speech server lists none of this kind: type its id (owner/name).'}
              >
                <Input value={form.speechId} onChange={(e) => set('speechId', e.target.value)} placeholder="owner/name" />
              </Field>
            )}
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Language" hint="Empty: what it is asked for.">
                <Input value={form.language} onChange={(e) => set('language', e.target.value)} maxLength={20} />
              </Field>
              {form.mode === 'audio_speech' && (
                <>
                  <Field label="Default voice">
                    <Input value={form.defaultVoice} onChange={(e) => set('defaultVoice', e.target.value)} maxLength={100} />
                  </Field>
                  <Field label="Speed" hint="0.5 to 2.">
                    <Input inputMode="decimal" value={form.speed} onChange={(e) => set('speed', e.target.value)} />
                  </Field>
                </>
              )}
            </div>
            {form.mode === 'audio_speech' && chosenSpeech?.voices?.length ? (
              <fieldset className="grid gap-2">
                <legend className="mb-1 text-sm font-medium">Voices offered</legend>
                <div className="flex flex-wrap gap-x-5 gap-y-2">
                  {chosenSpeech.voices.map((v) => (
                    <Label key={v} className="flex items-center gap-2 font-normal">
                      <Checkbox
                        checked={form.voices.length === 0 || form.voices.includes(v)}
                        onCheckedChange={(on) => {
                          const all = form.voices.length === 0 ? chosenSpeech.voices! : form.voices
                          const next = on ? [...new Set([...all, v])] : all.filter((x) => x !== v)
                          set('voices', next.length === chosenSpeech.voices!.length ? [] : next)
                        }}
                      />
                      {v}
                    </Label>
                  ))}
                </div>
                <p className="text-xs text-muted-foreground">All of them unless some are left out.</p>
              </fieldset>
            ) : null}
          </Section>
        )}

        <Section title="Loading and price">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Unload after (minutes unused)" hint="Empty: 10. Not when it is kept loaded.">
              <Input inputMode="numeric" value={form.idleMinutes} onChange={(e) => set('idleMinutes', e.target.value)} />
            </Field>
            <Field
              label={`Price per ${saved?.unitPrice?.unit ?? (form.mode === 'image_generation' ? 'picture' : video ? 'second of video' : form.mode === 'audio_transcription' ? 'minute of sound' : '1,000 characters')}`}
              hint={saved?.unitPrice && !saved.unitPrice.own ? `Empty: ${saved.unitPrice.amount} (Settings → Prices).` : 'Empty: Settings → Prices.'}
            >
              <Input inputMode="decimal" value={form.unitPrice} onChange={(e) => set('unitPrice', e.target.value)} />
            </Field>
          </div>
          <Label className="flex items-center gap-2 font-normal">
            <Checkbox checked={form.kept} onCheckedChange={(on) => set('kept', on === true)} /> Keep loaded
          </Label>
        </Section>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending} disabled={blocked}>
            {saved ? 'Save' : 'Add model'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
