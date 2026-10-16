import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { api } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { MediaModelSection, type LibraryRow, type SavedMediaModel, type SpeechRow } from './media-model-form'
import { roleLabel, type MediaMode } from './media-modes'
import { ChatModelSection, type ChatModelSlot, type SavedModel } from './model-form'
import { kindLabel, summary, type LibraryFile } from './model-profile'

/** What a model is, as the dialog adds it: a chat model the engine runs, or a picture, video or speech model. */
export type ModelDialogKind = 'chat' | MediaMode

/** What a new model starts from: a file in the library, or a model the speech server has. */
export type ModelSource = { file: string } | { speech: string }

/** The dialog opened to edit a saved model, or to add one (from a file or speech model, or from nothing yet). */
export type ModelDialogOpen = { edit: { kind: 'chat'; model: SavedModel } | { kind: 'media'; model: SavedMediaModel } } | { add: ModelSource | null }

const kindName: Record<ModelDialogKind, string> = {
  chat: 'A chat model',
  image_generation: 'A picture model',
  video_generation: 'A video model',
  audio_transcription: 'A speech to text model',
  audio_speech: 'A text to speech model',
}

const kindDescription: Record<ModelDialogKind, string> = {
  chat: 'A GGUF file from the model library, and how the engine runs it. Its file says what it is; the form asks only what that kind of model has.',
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
  group: 'chat' | 'pictures' | 'parts' | 'speech' | 'other'
  /** The kind of model it is; null: no model is added from it (an embedding model, a projector…). */
  kind: ModelDialogKind | null
  /** What it is, said plainly, when it is not simply a model of its kind. */
  why: string | null
  /** A picture or video model's file: the role it reads as. */
  role: string
  title: string
}

const groupLabel: Record<Choice['group'], string> = {
  chat: 'Chat models',
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
      group: k === 'language' ? 'chat' : k === 'image' || k === 'video' ? 'pictures' : k === 'encoder' ? 'parts' : 'other',
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

/**
 * The one dialog that adds and edits every kind of model. A file from the library (or a model the speech server
 * has) is chosen first; what it is is read from it, and the dialog takes it as that kind of model, which can be
 * changed. That kind's own section follows: the chat model's, or the picture, video or speech model's.
 */
export function ModelDialog({
  open,
  chat,
  gpus = [],
  onClose,
  chatSlot,
}: {
  open: ModelDialogOpen
  /** Chat models can be added: the llama.cpp engine runs. */
  chat: boolean
  gpus?: { index: number; name: string; total: number }[]
  onClose: () => void
  /** Fields another component mounts in the chat model's section. */
  chatSlot?: ChatModelSlot
}) {
  const edit = 'edit' in open ? open.edit : null
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
  const all = choices(library.data ?? [], mediaFiles.data ?? [], speech.data ?? [])
  const value = sourceValue(source)
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

  const pending = library.isPending || mediaFiles.isPending
  // A saved model is offered what its kind reads: a chat model's files, a picture or video model's, or the speech server's models.
  const offered: Choice['group'][] = !edit ? ['chat', 'pictures', 'parts', 'speech', 'other'] : kind === 'chat' ? ['chat', 'other'] : kind === 'image_generation' || kind === 'video_generation' ? ['pictures', 'parts'] : ['speech']
  const groups = offered.map((g) => [g, all.filter((c) => c.group === g)] as const).filter(([, list]) => list.length > 0)
  const mismatch = choice?.kind && choice.kind !== kind ? choice.kind : null

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
      <div className="grid min-w-0 gap-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <Field
          label="File or model"
          hint={
            pending
              ? 'Reading the library…'
              : all.length
                ? 'The library (LLAMACPP_LIBRARY_DIR) by what each file is, and the models the speech server has downloaded.'
                : 'Nothing in the library (LLAMACPP_LIBRARY_DIR), and the speech server lists no models.'
          }
        >
          <Select
            value={value || undefined}
            onValueChange={(v) => setSource(v.startsWith('speech:') ? { speech: v.slice('speech:'.length) } : { file: v.slice('file:'.length) })}
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
        <Alert variant="warning" title={choice.title}>
          {choice.why} It is not added as a model of its own.
        </Alert>
      )}
      {choice && mismatch && (
        <Alert variant="warning" title={`It reads as ${kindName[mismatch].toLowerCase()}`}>
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

      {kind === 'chat' ? (
        <ChatModelSection
          saved={edit?.kind === 'chat' ? edit.model : null}
          file={source && 'file' in source ? source.file : ''}
          gpus={gpus}
          onClose={onClose}
          slot={chatSlot}
        />
      ) : (
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
      )}
    </>
  )
}
