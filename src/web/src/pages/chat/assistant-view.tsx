import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, MessageSquarePlus, MessagesSquare, Plus, Trash2, Upload, X } from 'lucide-react'
import { useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { assistantQuery, configQuery, uploadFile } from './api'
import { AssistantIcon, AssistantLook, ReachLabel } from './assistant-icon'
import { AssistantSharing } from './assistant-sharing'
import type { Assistant, ChatConfig } from './types'

const DEFAULT = '__default__'
const maxStarters = 4

type Change = Partial<Pick<Assistant, 'name' | 'description' | 'instructions' | 'icon' | 'color' | 'tools' | 'starters'>> & { model?: string; thinking?: string }

/**
 * An assistant: what it is for and who made it, how much it is used, the instructions
 * and files every chat with it reads, what a new chat with it starts with, who it is
 * shared with, and the person's own chats with it. Its editors change it here.
 */
export function AssistantView({ assistantId, onOpenList, onNewChat }: { assistantId: string; onOpenList: () => void; onNewChat: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const assistant = useQuery(assistantQuery(assistantId))
  const config = useQuery(configQuery)
  const [instructions, setInstructions] = useState<string | null>(null)
  const [uploading, setUploading] = useState(0)
  const picker = useRef<HTMLInputElement>(null)
  const changed = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['assistants'] }), queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })])
  const save = useMutation({
    mutationFn: (body: Change) => api(`/api/assistants/${assistantId}`, { method: 'PATCH', body }),
    onSuccess: async (_, body) => {
      await changed()
      if (body.instructions !== undefined) {
        setInstructions(null)
        toast.success('Instructions saved', { description: 'Every answer with this assistant reads them from now on.' })
      } else toast.success('Assistant saved')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const removeFile = useMutation({
    mutationFn: (attachmentId: string) => api(`/api/assistants/${assistantId}/files/${attachmentId}`, { method: 'DELETE' }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/assistants/${assistantId}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await changed()
      toast.success('Assistant removed')
      void navigate('/assistants')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const add = async (files: FileList) => {
    for (const file of Array.from(files)) {
      setUploading((n) => n + 1)
      try {
        const a = await uploadFile(file, () => {})
        await api(`/api/assistants/${assistantId}/files`, { body: { attachmentId: a.id } })
      } catch (e) {
        toast.error(`${file.name}: ${errorMessage(e)}`)
      } finally {
        setUploading((n) => n - 1)
      }
    }
    await changed()
  }

  if (assistant.error) {
    return (
      <div className="p-6">
        {assistant.error instanceof ApiError && assistant.error.http === 404 ? (
          <p className="text-muted-foreground">This assistant does not exist, or it is not shared with you.</p>
        ) : (
          <QueryError error={assistant.error} retry={() => assistant.refetch()} />
        )}
      </div>
    )
  }
  const a = assistant.data
  return (
    <div className="min-h-0 overflow-y-auto">
      <div className="mx-auto grid max-w-3xl gap-4 px-4 py-6">
        <div className="flex items-start gap-3">
          <Button variant="ghost" size="icon-sm" className="lg:hidden" onClick={onOpenList} aria-label="Chats">
            <MessagesSquare />
          </Button>
          {a ? <AssistantIcon icon={a.icon} color={a.color} size="lg" /> : <Skeleton className="size-11" />}
          {!a ? (
            <Skeleton className="h-8 w-64" />
          ) : (
            <div className="grid min-w-0 flex-1 gap-0.5">
              <h1 dir="auto" className="text-xl font-semibold [overflow-wrap:anywhere]">
                {a.name}
              </h1>
              {a.description && (
                <p dir="auto" className="text-sm text-muted-foreground">
                  {a.description}
                </p>
              )}
              <p className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground">
                <span>{a.mine ? 'Yours' : `By ${a.owner?.name ?? 'someone'}`}</span>·<ReachLabel reach={a.reach} />·
                <span className="tabular-nums">
                  {a.usage.chats.toLocaleString()} {a.usage.chats === 1 ? 'chat' : 'chats'} started, {a.usage.people.toLocaleString()} {a.usage.people === 1 ? 'person' : 'people'} in the last 30 days
                </span>
              </p>
            </div>
          )}
        </div>

        <Button className="justify-self-start" onClick={onNewChat}>
          <MessageSquarePlus /> Start a chat
        </Button>

        {a && (a.canEdit ? <Settings key={a.updatedAt} a={a} config={config.data} saving={save.isPending} onSave={(body) => save.mutate(body)} /> : <About a={a} config={config.data} />)}

        <Card aria-label="Instructions">
          <CardHeader>
            <CardTitle className="text-base">Instructions</CardTitle>
            <CardDescription>Every answer with this assistant reads them: who it is for, how to answer, what to keep in mind.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-2">
            {!a ? (
              <Skeleton className="h-24" />
            ) : a.canEdit ? (
              <>
                <Textarea
                  dir="auto"
                  rows={5}
                  maxLength={20_000}
                  value={instructions ?? a.instructions ?? ''}
                  onChange={(e) => setInstructions(e.target.value)}
                  placeholder="e.g. Answer as a senior codec engineer. Our code is C11, built with CMake."
                  aria-label="Assistant instructions"
                />
                {instructions !== null && instructions !== (a.instructions ?? '') && (
                  <div className="flex gap-2">
                    <Button size="sm" loading={save.isPending} onClick={() => save.mutate({ instructions })}>
                      Save
                    </Button>
                    <Button size="sm" variant="outline" onClick={() => setInstructions(null)}>
                      Cancel
                    </Button>
                  </div>
                )}
              </>
            ) : a.instructions ? (
              <p dir="auto" className="rounded-lg bg-muted/40 px-3 py-2 text-sm whitespace-pre-wrap">
                {a.instructions}
              </p>
            ) : (
              <p className="text-sm text-muted-foreground">No instructions.</p>
            )}
          </CardContent>
        </Card>

        <Card aria-label="Assistant files">
          <CardHeader className="flex flex-row flex-wrap items-start gap-3">
            <div className="min-w-0 flex-1">
              <CardTitle className="text-base">Files</CardTitle>
              <CardDescription>What it knows: every chat with it has them; their text goes with each answer, and the tools can open them.</CardDescription>
            </div>
            {a?.canEdit && (
              <>
                <input ref={picker} type="file" multiple hidden onChange={(e) => e.target.files && void add(e.target.files).then(() => (e.target.value = ''))} aria-label="Add files to the assistant" />
                <Button variant="outline" size="sm" loading={uploading > 0} onClick={() => picker.current?.click()}>
                  <Upload /> Add files
                </Button>
              </>
            )}
          </CardHeader>
          <CardContent>
            {!a ? (
              <Skeleton className="h-12" />
            ) : a.files.length === 0 ? (
              <p className="text-sm text-muted-foreground">No files yet.</p>
            ) : (
              <ul className="grid gap-1.5" aria-label="Files">
                {a.files.map((f) => (
                  <li key={f.id} className="flex min-w-0 items-center gap-2 rounded-lg border px-3 py-2 text-sm">
                    <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="min-w-0 flex-1 truncate font-medium">{f.fileName}</span>
                    <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{formatValue(f.size, 'bytes')}</span>
                    {a.canEdit && (
                      <Button variant="ghost" size="icon-sm" className="size-7" disabled={removeFile.isPending} onClick={() => removeFile.mutate(f.id)} aria-label={`Remove ${f.fileName} from the assistant`}>
                        <X />
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        {a?.canShare && <AssistantSharing key={a.updatedAt} assistant={a} />}

        <Card aria-label="Your chats with it">
          <CardHeader>
            <CardTitle className="text-base">Your chats with it</CardTitle>
            <CardDescription>Only yours: other people's chats with it stay theirs.</CardDescription>
          </CardHeader>
          <CardContent>
            {!a ? (
              <Skeleton className="h-12" />
            ) : a.chats.length === 0 ? (
              <p className="text-sm text-muted-foreground">No chats yet: start one above, or move one here from its menu.</p>
            ) : (
              <ul className="grid gap-0.5" aria-label="Chats with the assistant">
                {a.chats.map((c) => (
                  <li key={c.id}>
                    <Link to={`/chat/${c.id}`} className="flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                      <bdi className="min-w-0 flex-1 truncate">{c.title}</bdi>
                      <span className="shrink-0 text-xs text-muted-foreground">{new Date(c.updatedAt).toLocaleDateString()}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        {a?.canShare && (
          <Button
            variant="ghost"
            className="justify-self-start text-destructive-ink"
            onClick={async () => {
              const description = a.reach === 'Private' ? 'Its instructions and file list go. Its chats stay, without it.' : 'It leaves everyone it is shared with. Chats with it stay, without it.'
              if (!(await confirm({ title: `Remove ${a.name}?`, description, confirm: 'Remove assistant', destructive: true }))) return
              remove.mutate()
            }}
          >
            <Trash2 /> Remove assistant
          </Button>
        )}
      </div>
    </div>
  )
}

/** The tools a new chat with it has on: its own list, or those on in new chats. */
const toolsOf = (a: Pick<Assistant, 'tools'>, config?: ChatConfig) => a.tools ?? config?.tools.filter((t) => t.onByDefault).map((t) => t.id) ?? []

/** What people see, and what a new chat with it starts with: for its editors. */
function Settings({ a, config, saving, onSave }: { a: Assistant; config?: ChatConfig; saving: boolean; onSave: (body: Change) => void }) {
  const start = { name: a.name, description: a.description ?? '', icon: a.icon, color: a.color, model: a.model ?? '', thinking: a.thinking ?? '', tools: a.tools, starters: a.starters }
  const [form, setForm] = useState(start)
  const dirty = JSON.stringify(form) !== JSON.stringify(start)
  const tools = toolsOf(form, config)
  return (
    <Card aria-label="Settings">
      <CardHeader>
        <CardTitle className="text-base">Settings</CardTitle>
        <CardDescription>What people see, and what a new chat with it starts with. A model or tool someone may not use is left out for them.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name">
            <Input required maxLength={100} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="What it is for">
            <Input maxLength={500} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </Field>
        </div>
        <div className="grid gap-2">
          <Label>Icon and colour</Label>
          <AssistantLook icon={form.icon} color={form.color} onChange={(look) => setForm({ ...form, ...look })} />
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Model">
            <Select value={form.model || DEFAULT} onValueChange={(v) => setForm({ ...form, model: v === DEFAULT ? '' : v })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={DEFAULT}>The default{config?.model ? ` (${config.model})` : ''}</SelectItem>
                {config?.models.map((m) => (
                  <SelectItem key={m.name} value={m.name}>
                    {m.name}
                  </SelectItem>
                ))}
                {form.model && !config?.models.some((m) => m.name === form.model) && <SelectItem value={form.model}>{form.model}</SelectItem>}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Thinking">
            <Select value={form.thinking || DEFAULT} onValueChange={(v) => setForm({ ...form, thinking: v === DEFAULT ? '' : v })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={DEFAULT}>The default</SelectItem>
                {config?.presets.map((p) => (
                  <SelectItem key={p.level} value={p.level}>
                    {p.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        </div>
        {config && config.tools.length > 0 && (
          <fieldset className="grid gap-2">
            <legend className="mb-2 text-sm font-medium">Tools</legend>
            <ul className="grid gap-1.5 sm:grid-cols-2">
              {config.tools.map((t) => (
                <li key={t.id} className="flex items-center gap-2">
                  <Checkbox
                    id={`assistant-tool-${t.id}`}
                    checked={tools.includes(t.id)}
                    onCheckedChange={(v) => setForm({ ...form, tools: v === true ? [...tools, t.id] : tools.filter((x) => x !== t.id) })}
                  />
                  <label htmlFor={`assistant-tool-${t.id}`} className="cursor-pointer text-sm">
                    {t.title}
                  </label>
                </li>
              ))}
            </ul>
          </fieldset>
        )}
        <div className="grid gap-2">
          <Label>Conversation starters</Label>
          <p className="text-xs text-muted-foreground">Up to {maxStarters} short prompts shown on a new chat with it.</p>
          <ul className="grid gap-1.5" aria-label="Conversation starters">
            {form.starters.map((s, i) => (
              <li key={i} className="flex items-center gap-2">
                <Input
                  dir="auto"
                  maxLength={200}
                  value={s}
                  onChange={(e) => setForm({ ...form, starters: form.starters.map((x, j) => (j === i ? e.target.value : x)) })}
                  aria-label={`Starter ${i + 1}`}
                />
                <Button variant="ghost" size="icon-sm" onClick={() => setForm({ ...form, starters: form.starters.filter((_, j) => j !== i) })} aria-label={`Remove starter ${i + 1}`}>
                  <X />
                </Button>
              </li>
            ))}
          </ul>
          {form.starters.length < maxStarters && (
            <Button variant="outline" size="sm" className="justify-self-start" onClick={() => setForm({ ...form, starters: [...form.starters, ''] })}>
              <Plus /> Add a starter
            </Button>
          )}
        </div>
        {dirty && (
          <div className="flex gap-2">
            <Button
              size="sm"
              loading={saving}
              disabled={!form.name.trim() || form.starters.some((s) => !s.trim())}
              onClick={() => onSave({ ...form, tools: form.tools ?? undefined })}
            >
              Save settings
            </Button>
            <Button size="sm" variant="outline" onClick={() => setForm(start)}>
              Cancel
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

/** What a new chat with it starts with, for those who use it. */
function About({ a, config }: { a: Assistant; config?: ChatConfig }) {
  const tools = toolsOf(a, config).map((id) => config?.tools.find((t) => t.id === id)?.title ?? id)
  const thinking = a.thinking ? (config?.presets.find((p) => p.level === a.thinking)?.label ?? a.thinking) : 'the default'
  return (
    <Card aria-label="About">
      <CardHeader>
        <CardTitle className="text-base">A chat with it starts with</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-3 text-sm">
        <p>
          Model: {a.model ?? 'the default'} · Thinking: {thinking} · Tools: {tools.length ? tools.join(', ') : 'none'}
        </p>
        {a.starters.length > 0 && (
          <ul className="grid gap-1" aria-label="Conversation starters">
            {a.starters.map((s) => (
              <li key={s} dir="auto" className="rounded-lg border px-3 py-2 text-muted-foreground">
                {s}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
