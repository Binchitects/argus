import { Archive, ArchiveRestore, Bot, Brain, Check, ChevronDown, Eye, FileDown, FilePen, FoldVertical, FolderOpen, GitFork, MessagesSquare, MoreHorizontal, Share2, SlidersHorizontal, Sparkles, Trash2, Wrench } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import { AUTO, assistantsQuery, chatModel, onAuto } from './api'
import { AssistantIcon } from './assistant-icon'
import type { ChatConfig, ChatSettings } from './types'
import { useChatActions } from './chat-actions'
import { MemoryButton } from './memory'

const DEFAULT = '__default__'

export function ModelPicker({ config, value, onChange }: { config: ChatConfig; value: string | null; onChange: (model: string | null) => void }) {
  const current = chatModel(config, value)
  // Auto: the small model answers easy questions, and hands the rest to the main model (the default).
  const auto = onAuto(config, value)
  const shown = auto ? 'Auto' : current?.name
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="h-8 min-w-0 max-w-64 shrink gap-1.5 px-2 font-semibold" aria-label={`Model: ${shown ?? 'none'}`}>
          {auto && <Sparkles className="text-primary-ink" aria-hidden="true" />}
          <span className="truncate">{shown ?? 'No model'}</span>
          {!auto && current && !current.loaded && (current.onRequest ? <Badge variant="secondary">Loads when asked</Badge> : <Badge variant="warning">Not loaded</Badge>)}
          <ChevronDown className="opacity-60" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-80">
        <DropdownMenuLabel>Model</DropdownMenuLabel>
        {config.auto && (
          <DropdownMenuItem onSelect={() => onChange(config.auto!.byDefault ? null : AUTO)} className="items-start">
            <span className="mt-0.5 w-4">{auto && <Check />}</span>
            <span className="grid gap-1">
              <span className="flex items-center gap-1.5 font-medium text-foreground">
                <Sparkles className="size-3.5 text-primary-ink" aria-hidden="true" /> Auto
              </span>
              <span className="text-xs text-muted-foreground">
                {config.auto.model} answers easy questions itself, and hands the rest to {chatModel(config, null)?.name ?? 'the main model'}, thinking as hard as each needs.
              </span>
            </span>
          </DropdownMenuItem>
        )}
        {config.models.map((m) => (
          <DropdownMenuItem key={m.name} disabled={!m.loaded && !m.onRequest} onSelect={() => onChange(m.name === config.model && !config.auto?.byDefault ? null : m.name)} className="items-start">
            <span className="mt-0.5 w-4">{!auto && m.name === current?.name && <Check />}</span>
            <span className="grid gap-1">
              <span className="font-medium text-foreground">{m.name}</span>
              <span className="flex flex-wrap gap-1">
                {!m.loaded && (m.onRequest ? <Badge variant="secondary">Loads when asked</Badge> : <Badge variant="warning">Not loaded now</Badge>)}
                {m.context && <Badge variant="secondary">{formatValue(m.context)} context</Badge>}
                {m.tools && (
                  <Badge variant="secondary">
                    <Wrench /> Tools
                  </Badge>
                )}
                {m.thinking && (
                  <Badge variant="secondary">
                    <Brain /> Thinks
                  </Badge>
                )}
                {m.vision && (
                  <Badge variant="secondary">
                    <Eye /> Sees images
                  </Badge>
                )}
              </span>
            </span>
          </DropdownMenuItem>
        ))}
        {config.models.length === 0 && <p className="px-2 py-1.5 text-sm text-muted-foreground">The gateway lists no model.</p>}
        {config.models.some((m) => !m.loaded) && (
          <p className="border-t px-2 pt-2 pb-1 text-xs text-muted-foreground">
            {config.models.some((m) => !m.loaded && m.onRequest)
              ? 'A model that loads when asked makes its first answer wait while it loads: seconds for a small one, minutes for a large one.'
              : 'A model that is not loaded answers once an admin loads it, in Administration → Models.'}
          </p>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

export function ThinkingPicker({ config, value, onChange }: { config: ChatConfig; value: string | null; onChange: (level: string | null) => void }) {
  if (!config.presets.length) return null
  const defaultLabel = config.presets.find((p) => p.level === config.defaultThinking)?.label
  return (
    <Select value={value ?? DEFAULT} onValueChange={(v) => onChange(v === DEFAULT ? null : v)}>
      <SelectTrigger size="sm" className="h-8 w-auto shrink-0 gap-1.5 border-transparent bg-transparent px-2 shadow-none hover:bg-accent [&>svg:last-child]:hidden sm:[&>svg:last-child]:block" aria-label="Thinking">
        <Brain className="size-4 text-muted-foreground" aria-hidden="true" />
        {/* On a phone the icon alone; the choice shows when the list opens. */}
        <span className="hidden sm:inline">
          <SelectValue />
        </span>
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={DEFAULT}>Default{defaultLabel ? ` (${defaultLabel})` : ''}</SelectItem>
        {config.presets.map((p) => (
          <SelectItem key={p.level} value={p.level}>
            {p.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/** A chat's own instructions and sampling parameters. Empty means the model's default. */
export function ChatSettingsPopover({ config, settings, onChange }: { config: ChatConfig; settings: ChatSettings; onChange: (change: ChatSettings) => void }) {
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState({ systemPrompt: '', temperature: '', topP: '', maxTokens: '' })
  const [error, setError] = useState<string | null>(null)
  const model = chatModel(config, settings.model)
  const custom = !!settings.systemPrompt || settings.temperature != null || settings.topP != null || settings.maxTokens != null
  const num = (v: string) => (v.trim() === '' ? null : Number(v))
  const apply = () => {
    const t = num(draft.temperature)
    const p = num(draft.topP)
    const m = num(draft.maxTokens)
    if (t !== null && !(t >= 0 && t <= 2)) return setError('Temperature is from 0 to 2.')
    if (p !== null && !(p >= 0 && p <= 1)) return setError('Top-p is from 0 to 1.')
    if (m !== null && !(Number.isInteger(m) && m >= 1 && m <= (model?.maxOutput ?? Infinity))) return setError(`The longest answer is from 1 to ${model?.maxOutput?.toLocaleString() ?? 'the model’s maximum'} tokens.`)
    onChange({ systemPrompt: draft.systemPrompt.trim(), temperature: t ?? -1, topP: p ?? -1, maxTokens: m ?? -1 })
    setOpen(false)
  }
  return (
    <Popover
      open={open}
      onOpenChange={(o) => {
        setOpen(o)
        setError(null)
        if (o)
          setDraft({
            systemPrompt: settings.systemPrompt ?? '',
            temperature: settings.temperature?.toString() ?? '',
            topP: settings.topP?.toString() ?? '',
            maxTokens: settings.maxTokens?.toString() ?? '',
          })
      }}
    >
      <Tooltip content="Instructions and parameters for this chat">
        <PopoverTrigger asChild>
          <Button variant="ghost" size="icon-sm" className="relative" aria-label="Chat settings">
            <SlidersHorizontal />
            {custom && <span className="absolute top-1 right-1 size-1.5 rounded-full bg-primary" aria-hidden="true" />}
          </Button>
        </PopoverTrigger>
      </Tooltip>
      <PopoverContent align="end" className="w-96" aria-label="Chat settings">
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            apply()
          }}
        >
          <p className="text-sm font-semibold">This chat</p>
          <Field label="Instructions" hint="Sent with every question: a role, a style, what to assume.">
            <Textarea dir="auto" value={draft.systemPrompt} onChange={(e) => setDraft({ ...draft, systemPrompt: e.target.value })} placeholder="e.g. Answer briefly, in British English." className="min-h-24" />
          </Field>
          <div className="grid grid-cols-3 gap-2">
            <Field label="Temperature">
              <Input inputMode="decimal" value={draft.temperature} onChange={(e) => setDraft({ ...draft, temperature: e.target.value })} placeholder="default" />
            </Field>
            <Field label="Top-p">
              <Input inputMode="decimal" value={draft.topP} onChange={(e) => setDraft({ ...draft, topP: e.target.value })} placeholder="default" />
            </Field>
            <Field label="Max tokens">
              <Input inputMode="numeric" value={draft.maxTokens} onChange={(e) => setDraft({ ...draft, maxTokens: e.target.value })} placeholder="default" />
            </Field>
          </div>
          {error && (
            <p className="text-xs font-medium text-destructive-ink" role="alert">
              {error}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => setDraft({ systemPrompt: '', temperature: '', topP: '', maxTokens: '' })}>
              Clear
            </Button>
            <Button type="submit" size="sm">
              Apply
            </Button>
          </div>
        </form>
      </PopoverContent>
    </Popover>
  )
}

export function ChatHeader({
  config,
  settings,
  onChange,
  filesCount,
  filesOpen,
  onToggleFiles,
  onOpenList,
  chat,
  onCompact,
  onExport,
  assistant,
  onMove,
  onShare,
  canvas,
}: {
  config: ChatConfig
  settings: ChatSettings
  onChange: (change: ChatSettings) => void
  /** The chat's canvases (once the chat exists): how many, and the panel that shows them. */
  canvas?: { count: number; open: boolean; onToggle: () => void }
  filesCount: number
  filesOpen: boolean
  onToggleFiles: () => void
  onOpenList: () => void
  /** The chat on screen, once it exists: fork, archive and delete it from here too. */
  chat?: { id: string; title: string; archived: boolean }
  /** Summarize the branch on screen (undefined: nothing to compact now). */
  onCompact?: () => void
  /** Export the branch on screen, or its summary. */
  onExport?: (kind: ExportKind) => void
  /** The assistant the chat is with (or a new chat will be with), and moving it to another. */
  assistant?: HeaderAssistant | null
  onMove?: (assistantId: string | null) => void
  /** Share the chat: a read-only link. */
  onShare?: () => void
}) {
  return (
    <header className="flex h-12 shrink-0 items-center gap-1 border-b px-2 sm:px-3">
      <Button variant="ghost" size="icon-sm" className="shrink-0 lg:hidden" onClick={onOpenList} aria-label="Chats">
        <MessagesSquare />
      </Button>
      <div className="flex min-w-0 items-center gap-1">
        <ModelPicker config={config} value={settings.model ?? null} onChange={(model) => onChange({ model: model ?? '' })} />
        <ThinkingPicker config={config} value={settings.thinking ?? null} onChange={(thinking) => onChange({ thinking: thinking ?? '' })} />
      </div>
      {assistant && (
        <Link
          to={`/chat/assistants/${assistant.id}`}
          className="ms-2 hidden min-w-0 max-w-56 items-center gap-1.5 truncate rounded-md px-2 py-1 text-xs text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring sm:flex"
          aria-label={`Assistant: ${assistant.name}`}
        >
          <AssistantIcon icon={assistant.icon} color={assistant.color} size="sm" />
          <bdi className="truncate">{assistant.name}</bdi>
        </Link>
      )}
      <span className="ml-auto flex shrink-0 items-center gap-1">
        <MemoryButton />
        <ChatSettingsPopover config={config} settings={settings} onChange={onChange} />
        {canvas && (
          <Tooltip content={canvas.open ? 'Hide the canvas' : 'Canvas: documents and code you and the model edit'}>
            <Button variant={canvas.open ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" onClick={canvas.onToggle} aria-pressed={canvas.open} aria-label={`Canvas (${canvas.count})`}>
              <FilePen /> <span className="tabular-nums">{canvas.count}</span>
            </Button>
          </Tooltip>
        )}
        <Tooltip content={filesOpen ? 'Hide files' : 'Files in this chat'}>
          <Button variant={filesOpen ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" onClick={onToggleFiles} aria-pressed={filesOpen} aria-label={`Files (${filesCount})`}>
            <FolderOpen /> <span className="tabular-nums">{filesCount}</span>
          </Button>
        </Tooltip>
        {chat && <ChatMenu chat={chat} onCompact={onCompact} onExport={onExport} assistant={assistant} onMove={onMove} onShare={onShare} />}
      </span>
    </header>
  )
}

export type ExportKind = 'md' | 'html' | 'pdf' | 'json' | 'summary'

/** The assistant in a chat's header: its name and look. */
export interface HeaderAssistant {
  id: string
  name: string
  icon: string
  color: string
}

function ChatMenu({
  chat,
  onCompact,
  onExport,
  assistant,
  onMove,
  onShare,
}: {
  chat: { id: string; title: string; archived: boolean }
  onCompact?: () => void
  onExport?: (kind: ExportKind) => void
  assistant?: HeaderAssistant | null
  onMove?: (assistantId: string | null) => void
  onShare?: () => void
}) {
  const { fork, archive, askDelete } = useChatActions(chat, true)
  const assistants = useQuery({ ...assistantsQuery, enabled: !!onMove })
  return (
    <DropdownMenu>
      <Tooltip content="More">
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-sm" aria-label="Chat actions">
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
      </Tooltip>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => fork.mutate()}>
          <GitFork /> Fork
        </DropdownMenuItem>
        <DropdownMenuItem disabled={!onCompact} onSelect={() => onCompact?.()}>
          <FoldVertical /> Compact
        </DropdownMenuItem>
        {onShare && (
          <DropdownMenuItem onSelect={() => onShare()}>
            <Share2 /> Share
          </DropdownMenuItem>
        )}
        {onMove && (
          <DropdownMenuSub>
            <DropdownMenuSubTrigger>
              <Bot /> Move to assistant
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent className="max-h-80 overflow-y-auto">
              {assistants.data?.map((a) => (
                <DropdownMenuItem key={a.id} disabled={a.id === assistant?.id} onSelect={() => onMove(a.id)}>
                  <AssistantIcon icon={a.icon} color={a.color} size="sm" /> <bdi className="max-w-56 truncate">{a.name}</bdi>
                </DropdownMenuItem>
              ))}
              {assistants.data?.length === 0 && <DropdownMenuItem disabled>No assistants yet</DropdownMenuItem>}
              {assistant && (
                <>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onSelect={() => onMove(null)}>Without {assistant.name}</DropdownMenuItem>
                </>
              )}
            </DropdownMenuSubContent>
          </DropdownMenuSub>
        )}
        {onExport && (
          <DropdownMenuSub>
            <DropdownMenuSubTrigger>
              <FileDown /> Export
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              <DropdownMenuItem onSelect={() => onExport('md')}>Markdown (.md)</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => onExport('html')}>Web page (.html)</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => onExport('pdf')}>PDF (print it)</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => onExport('json')}>Data (.json)</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => onExport('summary')}>Summary by the model (.md)</DropdownMenuItem>
            </DropdownMenuSubContent>
          </DropdownMenuSub>
        )}
        <DropdownMenuItem onSelect={() => archive.mutate(!chat.archived)}>
          {chat.archived ? <ArchiveRestore /> : <Archive />} {chat.archived ? 'Unarchive' : 'Archive'}
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem variant="destructive" onSelect={() => void askDelete()}>
          <Trash2 /> Delete
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
