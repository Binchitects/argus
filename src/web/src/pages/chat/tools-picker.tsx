import { useQuery } from '@tanstack/react-query'
import { Info, Settings2, ShieldQuestion, Wrench } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Switch } from '@/components/ui/switch'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import { toolIcon } from './tools'
import type { ChatTool } from './types'


/**
 * Which tools this chat may call, among those the person may use (Admin → Tools decides those): a short list of names
 * and switches. A tool's description and functions are behind its info button; onCustomize (Code Arena's Configure
 * Tools) is the way to more.
 */
export function ToolsPicker({ tools, value, onChange, onCustomize }: { tools: ChatTool[]; value: string[]; onChange: (tools: string[]) => void; onCustomize?: () => void }) {
  const [open, setOpen] = useState<string | null>(null)
  if (tools.length === 0) return null
  const on = new Set(value)
  const count = tools.filter((t) => on.has(t.id) || t.locked).length
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className={cn('h-8 gap-1.5 px-2', count > 0 && 'text-primary-ink')} aria-label={`Tools: ${count} of ${tools.length} on`}>
          <Wrench /> <span className="hidden sm:inline">Tools</span>
          <span className="rounded-full bg-muted px-1.5 text-xs tabular-nums text-foreground">{count}</span>
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-0" aria-label="Tools in this chat">
        <Toolsets tools={tools} value={value} onChange={onChange} />
        <ul className="max-h-80 overflow-y-auto py-1">
          {tools.map((t) => {
            const Icon = toolIcon[t.icon] ?? Wrench
            const id = `tool-${t.id}`
            const shown = open === t.id
            return (
              <li key={t.id} className="px-2">
                <div className="flex items-center gap-2 rounded-md px-1 py-1.5 hover:bg-accent/50">
                  <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <label htmlFor={id} className="min-w-0 flex-1 cursor-pointer truncate text-sm">
                    {t.title}
                  </label>
                  {t.askFirst && <ShieldQuestion className="size-3.5 shrink-0 text-muted-foreground" aria-label="Asks you before each call" />}
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    className="size-6 shrink-0 text-muted-foreground"
                    aria-label={`About ${t.title}`}
                    aria-expanded={shown}
                    onClick={() => setOpen(shown ? null : t.id)}
                  >
                    <Info className="size-3.5" />
                  </Button>
                  {t.locked ? (
                    <span className="w-9 shrink-0 text-right text-[11px] text-muted-foreground">On</span>
                  ) : (
                    <Switch id={id} checked={on.has(t.id)} onCheckedChange={(v) => onChange(v ? [...value, t.id] : value.filter((x) => x !== t.id))} />
                  )}
                </div>
                {shown && (
                  <div className="mb-1 ml-7 grid gap-1 rounded-md bg-muted/50 px-2 py-1.5 text-xs text-muted-foreground">
                    <span>{t.description}</span>
                    {t.askFirst && <span>Asks you before each call.</span>}
                    {t.locked && <span>Always on.</span>}
                    {t.names && t.names.length > 0 && <span className="break-words font-mono text-[11px]">{t.names.join(', ')}</span>}
                  </div>
                )}
              </li>
            )
          })}
        </ul>
        {onCustomize && (
          <div className="border-t p-1">
            <Button type="button" variant="ghost" size="sm" className="h-8 w-full justify-start gap-2 text-sm" onClick={onCustomize}>
              <Settings2 className="size-4" /> Customize tools…
            </Button>
          </div>
        )}
      </PopoverContent>
    </Popover>
  )
}

interface Toolset {
  id: string
  name: string
  description: string | null
  tools: string[]
  isDefault: boolean
}

/** The toolsets meant for the person: one press switches the chat to that set of tools. */
function Toolsets({ tools, value, onChange }: { tools: ChatTool[]; value: string[]; onChange: (tools: string[]) => void }) {
  const sets = useQuery({ queryKey: ['chat', 'toolsets'], queryFn: ({ signal }) => api<Toolset[]>('/api/chat/toolsets', { signal }), staleTime: 60_000 })
  if (!sets.data?.length) return null
  const same = (s: Toolset) => s.tools.length === value.length && s.tools.every((t) => value.includes(t))
  return (
    <fieldset className="flex flex-wrap gap-1.5 border-b px-3 py-2">
      <legend className="sr-only">Toolsets</legend>
      {sets.data.map((s) => (
        <Button
          key={s.id}
          type="button"
          size="sm"
          variant={same(s) ? 'secondary' : 'outline'}
          className="h-7 px-2 text-xs"
          aria-pressed={same(s)}
          title={s.description ?? s.tools.map((id) => tools.find((t) => t.id === id)?.title ?? id).join(', ')}
          onClick={() => onChange(s.tools.filter((id) => tools.some((t) => t.id === id)))}
        >
          {s.name}
        </Button>
      ))}
    </fieldset>
  )
}
