import { useQuery } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { api } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { MediaModelSection, type LibraryRow, type SavedMediaModel, type SpeechRow } from './media-model-form'
import { roleLabel, type MediaMode } from './media-modes'
import { engineLabel, serverPerModel, useSnapshots, type EngineInstance, type EngineModel, type ModelMove } from './engine-api'
import { engineChoices, engineName, homeOf, matchGguf, matchSnapshot, type EngineChoice, type FormMatch } from './engine-choice'
import { MovesAlert, VllmModelSection } from './engine-fields'
import { ChatModelSection, type ChatModelSlot, type SavedModel } from './model-form'
import { kindLabel, summary, type LibraryFile } from './model-profile'

/** What a model is, as the dialog adds it: a chat model the engine runs, or a picture, video or speech model. */
export type ModelDialogKind = 'chat' | MediaMode

/** What a new model starts from: a file in the library, or a model the speech server has. */
export type ModelSource = { file: string } | { speech: string }

/** The dialog opened to edit a saved model (its Engine field focused: Change engine… on its card), or to add one (from a file or speech model, or from nothing yet). */
export type ModelDialogOpen = { edit: { kind: 'chat'; model: SavedModel } | { kind: 'media'; model: SavedMediaModel }; focus?: 'engine' } | { add: ModelSource | null }

const kindName: Record<ModelDialogKind, string> = {
  chat: 'A chat model',
  image_generation: 'A picture model',
  video_generation: 'A video model',
  audio_transcription: 'A speech to text model',
  audio_speech: 'A text to speech model',
}

const kindDescription: Record<ModelDialogKind, string> = {
  chat: 'Its engine, its file from the model library (a GGUF file for llama.cpp, a Hugging Face snapshot for vLLM and SGLang), and how the engine runs it. Its file says what it is; the form asks only what that kind of model has.',
  image_generation:
    'Its files from the model library, how stable-diffusion.cpp runs it, and what it makes unless asked otherwise. Its server runs one model at a time: the one kept loaded, else the one asked for last.',
  video_generation:
    'Its files from the model library, how stable-diffusion.cpp runs it, and what it makes unless asked otherwise. Its server runs one model at a time: the one kept loaded, else the one asked for last.',
  audio_transcription: 'A model the speech server has (it lists those it has downloaded), and how it is used.',
  audio_speech: 'A model the speech server has (it lists those it has downloaded), and how it is used.',
}

/** One thing the dialog offers to add: a library file or a speech server's model, with what it reads as. */
interface Choice {
  value: string
  label: string
  /** A chat model's file by its form: a GGUF file (llama.cpp's) or a Hugging Face snapshot (vLLM's and SGLang's). */
  group: 'gguf' | 'snapshots' | 'pictures' | 'parts' | 'speech' | 'other'
  /** The kind of model it is; null: no model is added from it (an embedding model, a projector…). */
  kind: ModelDialogKind | null
  /** What it is, said plainly, when it is not simply a model of its kind. */
  why: string | null
  /** A picture or video model's file: the role it reads as. */
  role: string
  title: string
}

const groupLabel: Record<Choice['group'], string> = {
  gguf: 'Chat models: GGUF files, for llama.cpp',
  snapshots: 'Chat models: Hugging Face snapshots, for vLLM and SGLang',
  pictures: 'Picture and video models',
  parts: "A picture or video model's other files",
  speech: 'Models the speech server has',
  other: 'Not models to add: choose one to see what it is',
}

/** A diffusion model's name that says it makes clips (as the API reads a GGUF's name). */
const videoName = /(^|[^a-z])(wan2?|ltx|hunyuan.?video|cosmos)/i

const fileValue = (path: string) => `file:${path}`
const speechValue = (id: string) => `speech:${id}`
const sourceValue = (s: ModelSource | null) => (!s ? '' : 'file' in s ? fileValue(s.file) : speechValue(s.speech))

/** Everything the dialog offers: the library's GGUF files by what they are, its other files by their role, and the speech server's models. */
function choices(library: LibraryFile[], mediaFiles: LibraryRow[], speech: SpeechRow[]): Choice[] {
  const size = (bytes: number, parts = 1) => formatValue(bytes, 'bytes') + (parts > 1 ? ` in ${parts} parts` : '')
  const used = (by: string[]) => (by.length ? ` · used by ${by.join(', ')}` : '')
  const roles = new Map(mediaFiles.map((f) => [f.path, f.role]))
  const out: Choice[] = library.map((f) => {
    const k = f.profile.kind
    const kind: ModelDialogKind | null = k === 'language' ? 'chat' : k === 'image' || k === 'encoder' ? 'image_generation' : k === 'video' ? 'video_generation' : null
    const label = k === 'language' ? [f.path, size(f.size, f.parts), summary(f.profile)].filter(Boolean).join(' · ') : `${f.path} · ${kindLabel[k]}`
    const role = k === 'image' || k === 'video' ? 'diffusion' : (roles.get(f.path) ?? (k === 'encoder' ? 't5xxl' : 'llm'))
    return {
      value: fileValue(f.path),
      label: label + used(f.usedBy),
      group: k === 'language' ? 'gguf' : k === 'image' || k === 'video' ? 'pictures' : k === 'encoder' ? 'parts' : 'other',
      kind,
      why: k === 'language' ? null : f.profile.why,
      role,
      title: kindLabel[k],
    }
  })
  // stable-diffusion.cpp's other formats (safetensors and the like) are not read for what they are: their names say their role.
  const read = new Set(library.map((f) => f.path))
  for (const f of mediaFiles) {
    if (read.has(f.path)) continue
    const own = f.role === 'diffusion' || f.role === 'model'
    const video = own && videoName.test(f.path.split('/').pop() ?? '')
    out.push({
      value: fileValue(f.path),
      label: `${f.path} · ${size(f.size)} · ${roleLabel[f.role] ?? f.role}${used(f.usedBy)}`,
      group: own ? 'pictures' : 'parts',
      kind: video ? 'video_generation' : 'image_generation',
      why: own ? null : `${roleLabel[f.role] ?? f.role}, by its name: one of a picture or video model's files.`,
      role: f.role,
      title: own ? (video ? 'Video model' : 'Picture model') : (roleLabel[f.role] ?? f.role),
    })
  }
  for (const m of speech) {
    const tts = m.task === 'text-to-speech'
    out.push({
      value: speechValue(m.id),
      label: `${m.id} · ${tts ? 'Text to speech' : 'Speech to text'}${m.language?.length ? ` · ${m.language.join(', ')}` : ''}`,
      group: 'speech',
      kind: tts ? 'audio_speech' : 'audio_transcription',
      why: null,
      role: '',
      title: tts ? 'Text to speech model' : 'Speech to text model',
    })
  }
  return out
}

/** A chat model's file by its form: a GGUF file for llama.cpp, a Hugging Face snapshot for vLLM and SGLang. */
type FileForm = 'gguf' | 'snapshot'

const formName: Record<FileForm, string> = { gguf: 'GGUF file', snapshot: 'snapshot' }

/** The form of file an engine of this kind runs. */
const formFor = (kind: string | undefined): FileForm => (serverPerModel(kind) ? 'snapshot' : 'gguf')

/** A file's model for people: its name without its folder, split part, extension or revision. */
const fileName = (path: string) => (path.split('/').pop() ?? path).replace(/(-\d{5}-of-\d{5})?\.gguf$/i, '').replace(/@.*$/, '')

/**
 * A chat model's Engine field: llama.cpp, vLLM and SGLang (each engine of a kind that has several), the model's own
 * selected. One that cannot be chosen is listed all the same, disabled, its option and the hint saying why.
 */
function EngineField({
  choices,
  value,
  onChange,
  held,
  hint,
  focus,
}: {
  choices: EngineChoice[]
  value: string
  onChange: (value: string) => void
  /** Why it cannot be changed now (the library is being read, the model moves or moved meanwhile, or it is not a chat model); null: it can. */
  held: string | null
  hint: string
  /** Focused as the dialog opens (Change engine… on the card), once it can be changed. */
  focus: boolean
}) {
  const trigger = useRef<HTMLButtonElement>(null)
  const focused = useRef(false)
  useEffect(() => {
    if (!focus || held || focused.current) return
    focused.current = true
    trigger.current?.focus()
  }, [focus, held])
  return (
    <Field
      label="Engine"
      hint={
        <>
          {held ?? hint}
          {!held &&
            choices
              .filter((c) => c.detail)
              .map((c) => (
                <span key={c.value} className="mt-0.5 block">
                  {c.detail}
                </span>
              ))}
        </>
      }
    >
      <Select value={value} onValueChange={onChange} disabled={!!held}>
        <SelectTrigger ref={trigger} className="sm:max-w-sm">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {choices.map((c) => (
            <SelectItem key={c.value} value={c.value} disabled={!!c.why}>
              {c.why ? `${c.label} (${c.why})` : c.down ? `${c.label} (not running)` : c.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}

/** In place of the engine's form when it has nothing to show: why, said plainly, and Save held with that reason. */
function Held({ title, children, saved, onClose, variant = 'warning' }: { title: string; children: ReactNode; saved: boolean; onClose: () => void; variant?: 'info' | 'warning' }) {
  const id = useId()
  return (
    <div className="grid min-w-0 gap-5">
      <Alert id={id} variant={variant} title={title}>
        {children}
      </Alert>
      <HeldSave why={id} saved={saved} onClose={onClose} />
    </div>
  )
}

/** Save (or Add model) held, described by the element that says why. */
function HeldSave({ why, saved, onClose }: { why: string; saved: boolean; onClose: () => void }) {
  return (
    <DialogFooter>
      <Button type="button" variant="outline" onClick={onClose}>
        Cancel
      </Button>
      <Button type="button" disabled aria-describedby={why}>
        {saved ? 'Save' : 'Add model'}
      </Button>
    </DialogFooter>
  )
}

/** A command to type, on a line of its own. */
const command = (text: string) => <code className="my-1 block text-xs [overflow-wrap:anywhere]">{text}</code>

/**
 * How to get a model's file of the form its engine runs into the library, as docs/admin.md says: Find on Hugging Face, or on
 * a host with no network, hf download on a machine that has one, into the folder the library reads, carried over.
 */
function HowToGet({ form, others }: { form: FileForm; others: boolean }) {
  return form === 'snapshot' ? (
    <div>
      <p>
        vLLM and SGLang run a Hugging Face snapshot (safetensors), not a GGUF file. Download it with <strong>Find on Hugging Face</strong> (on this page, beside Add a
        model): its <strong>Snapshots</strong> tab finds the repositories that have one. On a host with no network, download it on a machine that has one (the
        revision is a commit of the repository; the library is LLAMACPP_LIBRARY_DIR):
      </p>
      {command('hf download <owner>/<repository> --revision <revision> --local-dir <library>/hf/<owner>/<repository>@<revision>')}
      <p>
        then carry that folder to the same place in this host's library. File or model lists it once it is whole.
        {others ? ' The other snapshots it lists may be this model under another name.' : ''}
      </p>
    </div>
  ) : (
    <div>
      <p>
        llama.cpp runs a GGUF file, not a Hugging Face snapshot. Download one with <strong>Find on Hugging Face</strong> (on this page, beside Add a model): its{' '}
        <strong>Chat</strong> tab shows the quantisations that fit this machine. On a host with no network, download it on a machine that has one (the library is
        LLAMACPP_LIBRARY_DIR):
      </p>
      {command('hf download <owner>/<repository> <file>.gguf --local-dir <library>/<owner>/<repository>')}
      <p>
        then carry the file to the same place in this host's library. File or model lists it once it is there.
        {others ? ' The other GGUF files it lists may be this model under another name.' : ''}
      </p>
    </div>
  )
}

/**
 * The one dialog that adds and edits every kind of model. A file from the library (or a model the speech server
 * has) is chosen first; what it is is read from it, and the dialog takes it as that kind of model, which can be
 * changed. That kind's own section follows: the chat model's, or the picture, video or speech model's. A chat model's
 * Engine field (llama.cpp, vLLM or SGLang) and its file go together: another engine takes the same model in its form
 * from the library, and a file of the other form takes its engine; a saved model put on another engine moves there.
 */
export function ModelDialog({
  open,
  chat,
  gpus = [],
  onClose,
  chatSlot,
  instances = [],
  live,
}: {
  open: ModelDialogOpen
  /** Chat models can be added: the llama.cpp engine runs. */
  chat: boolean
  gpus?: { index: number; name: string; total: number }[]
  onClose: () => void
  /** Fields another component mounts in the chat model's section. */
  chatSlot?: ChatModelSlot
  /** The engine instances: llama.cpp's takes a GGUF file as a chat model, vLLM's and SGLang's a snapshot. */
  instances?: EngineInstance[]
  /**
   * The edited chat model as the page has it now: its engine, and its move to another one (its engine does not change while it
   * moves). Another admin may have moved it while this was open. Undefined: as it was opened.
   */
  live?: { engineKind?: string; engine?: string | null; move?: ModelMove | null }
}) {
  const edit = 'edit' in open ? open.edit : null
  const saved = edit?.kind === 'chat' ? (edit.model as EngineModel) : null
  const [kind, setKind] = useState<ModelDialogKind>(() =>
    edit
      ? edit.kind === 'chat'
        ? 'chat'
        : (edit.model.mode as MediaMode)
      : 'add' in open && open.add && 'speech' in open.add
        ? 'audio_speech'
        : chat
          ? 'chat'
          : 'image_generation',
  )
  const [source, setSource] = useState<ModelSource | null>(() => {
    if (!edit) return 'add' in open ? open.add : null
    if (edit.kind === 'chat') return edit.model.file ? { file: edit.model.file } : null
    const own = edit.model.files?.find((f) => f.role === 'diffusion' || f.role === 'model')
    return own ? { file: own.path } : edit.model.speechId ? { speech: edit.model.speechId } : null
  })
  const library = useQuery({ queryKey: ['admin', 'models', 'library'], queryFn: ({ signal }) => api<LibraryFile[]>('/api/admin/models/library', { signal }) })
  const mediaFiles = useQuery({ queryKey: ['admin', 'models', 'media', 'files'], queryFn: ({ signal }) => api<LibraryRow[]>('/api/admin/models/media/files', { signal }) })
  const speech = useQuery({ queryKey: ['admin', 'models', 'media', 'speech'], queryFn: ({ signal }) => api<SpeechRow[]>('/api/admin/models/media/speech', { signal }), retry: false })
  const snapshots = useSnapshots()
  const libraryFiles = library.data ?? []
  const snapshotList = snapshots.data ?? []

  // Every engine a chat model may run on, the saved model's own selected; one that cannot take it is listed disabled, with why.
  const own = saved ? homeOf(saved, instances) : null
  const engines = engineChoices(instances, { llama: chat && instances.length === 0, current: own, moving: !!saved })
  const runs = (c: EngineChoice) => !c.why
  const [engine, setEngine] = useState(() => own?.name ?? (engines.find((c) => c.kind === 'llamacpp' && runs(c)) ?? engines.find(runs) ?? engines[0]).value)
  const chosen = engines.find((c) => c.value === engine) ?? engines[0]
  const ownChoice = own ? engines.find((c) => c.value === own.name) : undefined
  const engineForm = formFor(chosen.kind)
  // The first engine for a form: the saved model's own, else one that runs.
  const firstFor = (form: FileForm) => {
    const of = engines.filter((c) => formFor(c.kind) === form)
    return of.find((c) => c.value === own?.name) ?? of.find((c) => runs(c) && !c.down) ?? of.find(runs) ?? of[0]
  }

  // Snapshots are chat models while vLLM or SGLang runs here (or the model is on one).
  const servers = engines.filter((c) => serverPerModel(c.kind) && runs(c))
  const serverNames = [...new Set(servers.map((c) => engineLabel(c.kind)))].join(' or ') || engineLabel(own?.kind)
  const all = [
    ...choices(libraryFiles, mediaFiles.data ?? [], speech.data ?? []),
    ...(servers.length > 0 || serverPerModel(own?.kind) ? snapshotList : []).map((s) => ({
      value: fileValue(s.path), label: `${s.path} · ${formatValue(s.size, 'bytes')} · ${serverNames}${s.complete ? '' : ` · ${s.why}`}`, group: 'snapshots' as const, kind: 'chat' as const,
      why: null, role: '', title: `${serverNames} snapshot`,
    })),
  ]
  const value = sourceValue(source)
  const file = source && 'file' in source ? source.file : ''
  // A file's form as the library lists it; the saved model's own is its engine's, listed or not (and before the library is read).
  const formOf = (path: string | undefined): FileForm | null =>
    !path
      ? null
      : snapshotList.some((s) => s.path === path)
        ? 'snapshot'
        : libraryFiles.some((f) => f.path === path && f.profile.kind === 'language')
          ? 'gguf'
          : saved && path === saved.file
            ? formFor(saved.engineKind)
            : null
  const fileForm = formOf(file)
  const found = all.find((c) => c.value === value)
  // A picture or video model's other file (a text encoder, a decoder) belongs to either: it reads as the one being added.
  const choice = found?.group === 'parts' && (kind === 'image_generation' || kind === 'video_generation') ? { ...found, kind } : found
  const can = (k: ModelDialogKind) => k !== 'chat' || chat || edit?.kind === 'chat'
  const kinds = (Object.keys(kindName) as ModelDialogKind[]).filter(can)

  // A new model is taken as what its file or speech model is, once that is known (a file just downloaded waits for the library to list it).
  // A saved model keeps its kind.
  const [detected, setDetected] = useState(edit ? value : '')
  if (!edit && value && detected !== value && choice) {
    setDetected(value)
    if (choice.kind && can(choice.kind)) setKind(choice.kind)
  }

  // Its engine does not change while it moves (the API refuses it then); a failed move may be tried again. Moved elsewhere
  // while this was open (by another admin), its form here is for the engine it left: saved, it would move back.
  const underway = (live !== undefined ? live.move : saved?.move) ?? null
  const moving = underway && underway.phase !== 'failed' ? underway : null
  const now = saved && live ? homeOf(live, instances) : null
  const moved = !!own && !!now && (now.kind !== own.kind || now.name !== own.name) ? engineName(live!, instances) : null
  const held = moving
    ? `It is moving from ${engineLabel(moving.from)} to ${engineLabel(moving.to)}: its engine changes again once it is there, or once its move has failed.`
    : moved
      ? `It moved to ${moved} while this was open: close and edit it again.`
      : kind !== 'chat'
        ? 'Chat models only: picture, video and speech models have servers of their own.'
        : null
  // Another engine's form is taken from the library: it is not chosen before the library and its snapshots are read.
  const reading = library.isPending || snapshots.isPending
  // A file of the other form takes its engine with it: a GGUF file llama.cpp, a snapshot vLLM or SGLang (the model's own first),
  // when that engine runs here; else the field stays, and File or model says why.
  const needs = kind === 'chat' && !held && fileForm && fileForm !== engineForm ? firstFor(fileForm) : null
  if (needs && runs(needs) && needs.value !== engine) setEngine(needs.value)
  const stranded = needs && !runs(needs) ? needs : null
  // The file last chosen in each form, brought back when the engine goes to that form again; the library's matches, shown.
  const [kept, setKept] = useState<Partial<Record<FileForm, string>>>({})
  const [matched, setMatched] = useState<Partial<Record<FileForm, FormMatch>>>({})
  const otherForm = (form: FileForm): FileForm => (form === 'snapshot' ? 'gguf' : 'snapshot')
  // The file being left (for another one, or for nothing): kept for its form.
  const keep = () => {
    const last = file && fileForm ? { ...kept, [fileForm]: file } : kept
    setKept(last)
    return last
  }
  // Another engine of the other form takes the same model in its form: the file last chosen in it, else the saved model's
  // own file, else the library's match.
  const chooseEngine = (name: string) => {
    const next = engines.find((c) => c.value === name)
    if (!next) return
    setEngine(name)
    const form = formFor(next.kind)
    // The same form (vLLM to SGLang on the same snapshot): its file stays.
    if (fileForm === form) return
    const last = keep()
    const back = last[form] ?? (saved?.file && formFor(saved.engineKind) === form ? saved.file : undefined)
    if (back) {
      setSource({ file: back })
      return
    }
    const base = file || last[otherForm(form)] || saved?.file || ''
    const match =
      form === 'snapshot'
        ? matchSnapshot(base, libraryFiles.find((f) => f.path === base)?.profile, snapshotList)
        : matchGguf(base, snapshotList.find((s) => s.path === base), libraryFiles)
    if (match) setMatched((m) => ({ ...m, [form]: match }))
    setSource(match ? { file: match.path } : null)
  }

  const pending = library.isPending || mediaFiles.isPending
  // A chat model's files of its engine's form first, the other form's after (choosing one moves the engine with it).
  const chatGroups: Choice['group'][] = engineForm === 'snapshot' ? ['snapshots', 'gguf'] : ['gguf', 'snapshots']
  // A saved model is offered what its kind reads: a chat model's files, a picture or video model's, or the speech server's models.
  const offered: Choice['group'][] = !edit
    ? [...chatGroups, 'pictures', 'parts', 'speech', 'other']
    : kind === 'chat'
      ? [...chatGroups, 'other']
      : kind === 'image_generation' || kind === 'video_generation'
        ? ['pictures', 'parts']
        : ['speech']
  const groups = offered.map((g) => [g, all.filter((c) => c.group === g)] as const).filter(([, list]) => list.length > 0)
  const mismatch = choice?.kind && choice.kind !== kind ? choice.kind : null
  const shown = matched[engineForm]
  const note = shown && shown.path === file ? `Its ${formName[engineForm]} in the library, by its ${shown.how}: choose another if it is not the same model.` : null
  // Why a file chosen is not added as a chat model (it is not one, or reads as another kind): said above the form, and Save held by it.
  const whyId = useId()

  // The chosen engine's form of the model: a file of that form (the saved model's own is its engine's).
  const ready = fileForm === engineForm
  const moves = !!saved && kind === 'chat' && !held && !!ownChoice && chosen.value !== ownChoice.value && runs(chosen)
  const others = all.some((c) => c.group === (engineForm === 'snapshot' ? 'snapshots' : 'gguf'))
  // The model by its name: the saved one's, else the file chosen (in either form, last).
  const lastFile = file || kept[otherForm(engineForm)]
  const named = saved?.name ?? (lastFile ? fileName(lastFile) : null)
  const readingNote = <p className="text-sm text-muted-foreground">Reading the library…</p>
  // Nothing chosen yet, and the library has files of this form: choose one. Else none of this model is there: how to get it.
  const noFile = (form: FileForm) =>
    !saved && !lastFile && others ? (
      <Held variant="info" title={`Choose its ${formName[form]} under File or model`} saved={false} onClose={onClose}>
        {form === 'snapshot' ? 'vLLM and SGLang run a Hugging Face snapshot (safetensors), not a GGUF file.' : 'llama.cpp runs a GGUF file, not a Hugging Face snapshot.'}
      </Held>
    ) : (
      <Held title={`No ${form === 'snapshot' ? 'Hugging Face snapshot' : 'GGUF file'}${named ? ` of ${named}` : ''} in the library`} saved={!!saved} onClose={onClose}>
        <HowToGet form={form} others={others} />
      </Held>
    )
  const firstOfKind = instances.find((i) => i.kind === chosen.kind)?.name

  return (
    <>
      <DialogHeader>
        <DialogTitle>{edit ? `Edit ${edit.model.name}` : 'Add a model'}</DialogTitle>
        <DialogDescription>
          {edit
            ? kindDescription[kind]
            : 'Choose a file from the model library, or a model the speech server has: the dialog reads what it is, and asks what that kind of model has.'}
        </DialogDescription>
      </DialogHeader>
      {edit?.kind !== 'media' && (
        <EngineField
          choices={engines}
          value={chosen.value}
          onChange={chooseEngine}
          held={held ?? (reading ? 'Reading the library…' : null)}
          hint={`llama.cpp runs a GGUF file, vLLM and SGLang a Hugging Face snapshot: File or model lists the engine's own form first.${saved ? ' Another engine moves the model there.' : ''}`}
          focus={!!edit && 'focus' in open && open.focus === 'engine'}
        />
      )}
      <div className="grid min-w-0 gap-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <Field
          label="File or model"
          hint={
            pending
              ? 'Reading the library…'
              : stranded
                ? `A ${formName[fileForm!]} needs ${stranded.label}: choose ${engineForm === 'snapshot' ? 'a snapshot' : 'a GGUF file'} for ${chosen.label}. ${stranded.detail ?? ''}`.trim()
                : note && kind === 'chat'
                  ? note
                  : all.length
                    ? 'The library (LLAMACPP_LIBRARY_DIR) by what each file is, and the models the speech server has downloaded.'
                    : 'Nothing in the library (LLAMACPP_LIBRARY_DIR), and the speech server lists no models.'
          }
        >
          {/* Always controlled: none chosen ('') shows the placeholder, and choosing the same file again takes it again. */}
          <Select
            value={value}
            onValueChange={(v) => {
              keep()
              setSource(v.startsWith('speech:') ? { speech: v.slice('speech:'.length) } : { file: v.slice('file:'.length) })
            }}
          >
            <SelectTrigger className="min-w-0 [&>span]:truncate">
              <SelectValue placeholder="Choose a file or model" />
            </SelectTrigger>
            <SelectContent className="max-w-[calc(100vw-2rem)]">
              {value && !choice && (
                <SelectItem value={value} className="[overflow-wrap:anywhere]">
                  {value.slice(value.indexOf(':') + 1)} ({source && 'speech' in source ? 'not listed by the speech server' : 'not in the library yet'})
                </SelectItem>
              )}
              {groups.map(([g, list]) => (
                <SelectGroup key={g}>
                  <SelectLabel>{groupLabel[g]}</SelectLabel>
                  {list.map((c) => (
                    <SelectItem key={c.value} value={c.value} className="[overflow-wrap:anywhere]">
                      {c.label}
                    </SelectItem>
                  ))}
                </SelectGroup>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <Field label="Add it as" hint={edit ? 'A saved model keeps its kind.' : choice?.kind === kind ? 'What it reads as: it can be changed.' : undefined}>
          <Select value={kind} onValueChange={(v) => setKind(v as ModelDialogKind)} disabled={!!edit}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {kinds.map((k) => (
                <SelectItem key={k} value={k}>
                  {kindName[k]}
                  {choice?.kind === k ? ' (what it reads as)' : ''}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      </div>

      {choice && choice.kind === null && (
        <Alert id={whyId} variant="warning" title={choice.title}>
          {choice.why} It is not added as a model of its own.
        </Alert>
      )}
      {choice && mismatch && (
        <Alert id={whyId} variant="warning" title={`It reads as ${kindName[mismatch].toLowerCase()}`}>
          {choice.why ?? `${choice.title}, not ${kindName[kind].toLowerCase()}.`}
          {!edit && can(mismatch) && (
            <div className="mt-2">
              <Button type="button" size="sm" variant="outline" onClick={() => setKind(mismatch)}>
                {`Add it as ${kindName[mismatch].toLowerCase()}`}
              </Button>
            </div>
          )}
          {!can(mismatch) && <p className="mt-1">Chat models need the llama.cpp engine (the llamacpp profile).</p>}
        </Alert>
      )}
      {choice && !mismatch && choice.group === 'parts' && <Alert title={choice.title}>{choice.why} It is put in among the files below, by its role.</Alert>}
      {!edit && <p className="-mt-2 text-xs text-muted-foreground">{kindDescription[kind]}</p>}

      {moves && <MovesAlert name={saved.name} from={ownChoice.label} to={chosen.label} />}
      {kind !== 'chat' ? (
        <MediaModelSection
          saved={edit?.kind === 'media' ? edit.model : null}
          mode={kind}
          // Its role is known once the library is read (a file not in it yet is taken as the model itself).
          file={source && 'file' in source && (choice || !pending) ? { path: source.file, role: choice?.role || 'diffusion' } : undefined}
          speechId={source && 'speech' in source ? source.speech : undefined}
          onFile={(path) => setSource({ file: path })}
          gpus={gpus}
          onClose={onClose}
        />
      ) : moved ? (
        <Held title={`It moved to ${moved} while this was open`} saved={!!saved} onClose={onClose}>
          This form is for {ownChoice?.label ?? engineLabel(own?.kind)}, where it was: saved, it would move back. Close this and edit it again.
        </Held>
      ) : moving ? (
        <Held variant="info" title={`It is moving to ${engineLabel(moving.to)}`} saved={!!saved} onClose={onClose}>
          It is not changed while it moves: edit it once it is there, or once its move has failed.
        </Held>
      ) : chosen.why && chosen.value !== own?.name ? (
        <Held title={`${chosen.label} does not run here`} saved={!!saved} onClose={onClose}>
          {chosen.detail}
        </Held>
      ) : stranded ? (
        <Held title={`${stranded.label} does not run here`} saved={!!saved} onClose={onClose}>
          {stranded.detail}
        </Held>
      ) : engineForm === 'snapshot' ? (
        ready ? (
          <VllmModelSection
            saved={saved}
            snapshot={file}
            // Its own engine as it names it (none: the first of its kind); another is named unless it is the first of its kind, the API's default.
            engine={{ kind: chosen.kind === 'sglang' ? 'sglang' : 'vllm', name: chosen.value === own?.name ? (saved?.engine ?? '') : chosen.value === firstOfKind ? '' : chosen.value }}
            moves={moves ? chosen.label : null}
            gpus={gpus}
            onClose={onClose}
          />
        ) : snapshots.isPending ? (
          readingNote
        ) : (
          noFile('snapshot')
        )
      ) : ready || (!saved && !!file && (!choice || choice.kind === 'chat')) ? (
        <ChatModelSection saved={saved} file={file} gpus={gpus} onClose={onClose} slot={chatSlot} moving={!!saved && serverPerModel(saved.engineKind)} />
      ) : file && choice && choice.kind !== 'chat' ? (
        <HeldSave why={whyId} saved={!!saved} onClose={onClose} />
      ) : library.isPending ? (
        readingNote
      ) : (
        noFile('gguf')
      )}
    </>
  )
}
