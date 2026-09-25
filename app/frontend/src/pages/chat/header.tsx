import { Archive, ArchiveRestore, Brain, Check, ChevronDown, Eye, FolderOpen, GitFork, MessagesSquare, MoreHorizontal, SlidersHorizontal, Trash2, Wrench } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import type { ChatConfig, ChatSettings } from './types'
import { useChatActions } from './chat-actions'

const DEFAULT = '__default__'

export function ModelPicker({ config, value, onChange }: { config: ChatConfig; value: string | null; onChange: (model: string | null) => void }) {
  const current = config.models.find((m) => m.name === value) ?? config.models[0]
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="h-8 min-w-0 max-w-64 shrink gap-1.5 px-2 font-semibold" aria-label={`Model: ${current?.name ?? 'none'}`}>
          <span className="truncate">{current?.name ?? 'No model'}</span>
          <ChevronDown className="opacity-60" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-80">
        <DropdownMenuLabel>Model</DropdownMenuLabel>
        {config.models.map((m) => (
          <DropdownMenuItem key={m.name} onSelect={() => onChange(m.name === config.model ? null : m.name)} className="items-start">
            <span className="mt-0.5 w-4">{m.name === current?.name && <Check />}</span>
            <span className="grid gap-1">
              <span className="font-medium text-foreground">{m.name}</span>
              <span className="flex flex-wrap gap-1">
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
  const model = config.models.find((m) => m.name === settings.model) ?? config.models[0]
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
      <PopoverContent align="end" className="w-96">
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            apply()
          }}
        >
          <p className="text-sm font-semibold">This chat</p>
          <Field label="Instructions" hint="Sent with every question: a role, a style, what to assume.">
            <Textarea value={draft.systemPrompt} onChange={(e) => setDraft({ ...draft, systemPrompt: e.target.value })} placeholder="e.g. Answer briefly, in British English." className="min-h-24" />
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
  title,
  onRename,
  onChange,
  filesCount,
  filesOpen,
  onToggleFiles,
  onOpenList,
  chat,
}: {
  config: ChatConfig
  settings: ChatSettings
  title: string | null
  onRename?: (title: string) => void
  onChange: (change: ChatSettings) => void
  filesCount: number
  filesOpen: boolean
  onToggleFiles: () => void
  onOpenList: () => void
  /** The chat on screen, once it exists: fork, archive and delete it from here too. */
  chat?: { id: string; title: string; archived: boolean }
}) {
  const [name, setName] = useState<string | null>(null)
  return (
    <header className="flex h-12 shrink-0 items-center gap-1 border-b px-2 sm:px-3">
      <Button variant="ghost" size="icon-sm" className="shrink-0 lg:hidden" onClick={onOpenList} aria-label="Chats">
        <MessagesSquare />
      </Button>
      <div className="flex min-w-0 items-center gap-1">
        <ModelPicker config={config} value={settings.model ?? null} onChange={(model) => onChange({ model: model ?? '' })} />
        <ThinkingPicker config={config} value={settings.thinking ?? null} onChange={(thinking) => onChange({ thinking: thinking ?? '' })} />
      </div>
      {config.argus && (
        <Label className="ml-1 hidden h-8 items-center gap-2 rounded-md px-2 text-sm font-normal hover:bg-accent sm:flex">
          <Switch checked={settings.useArgus ?? true} onCheckedChange={(v) => onChange({ useArgus: v })} aria-label="Search our code (Argus)" />
          Argus
        </Label>
      )}
      <div className="mx-2 hidden min-w-0 flex-1 justify-center md:flex">
        {title !== null && onRename && (name === null ? (
          <button type="button" className="max-w-md truncate rounded-md px-2 py-1 text-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring" onClick={() => setName(title)} aria-label={`Chat title: ${title}. Rename`}>
            {title}
          </button>
        ) : (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (name.trim()) onRename(name.trim())
              setName(null)
            }}
          >
            <Input value={name} onChange={(e) => setName(e.target.value)} onBlur={() => setName(null)} aria-label="Chat title" autoFocus className="h-8 w-72" />
          </form>
        ))}
      </div>
      <span className="ml-auto flex shrink-0 items-center gap-1 md:ml-0">
        <ChatSettingsPopover config={config} settings={settings} onChange={onChange} />
        <Tooltip content={filesOpen ? 'Hide files' : 'Files in this chat'}>
          <Button variant={filesOpen ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" onClick={onToggleFiles} aria-pressed={filesOpen} aria-label={`Files (${filesCount})`}>
            <FolderOpen /> <span className="tabular-nums">{filesCount}</span>
          </Button>
        </Tooltip>
        {chat && <ChatMenu chat={chat} />}
      </span>
    </header>
  )
}

function ChatMenu({ chat }: { chat: { id: string; title: string; archived: boolean } }) {
  const { fork, archive, askDelete } = useChatActions(chat, true)
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
