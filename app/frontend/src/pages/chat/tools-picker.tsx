import { ShieldQuestion, Wrench } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Switch } from '@/components/ui/switch'
import { cn } from '@/lib/utils'
import { toolIcon } from './tools'
import type { ChatTool } from './types'


/** Which tools this chat may call, among those the person may use (Admin → Tools decides those). */
export function ToolsPicker({ tools, value, onChange }: { tools: ChatTool[]; value: string[]; onChange: (tools: string[]) => void }) {
  if (tools.length === 0) return null
  const on = new Set(value)
  const count = tools.filter((t) => on.has(t.id)).length
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className={cn('h-8 gap-1.5 px-2', count > 0 && 'text-primary-ink')} aria-label={`Tools: ${count} of ${tools.length} on`}>
          <Wrench /> <span className="hidden sm:inline">Tools</span>
          <span className="rounded-full bg-muted px-1.5 text-xs tabular-nums text-foreground">{count}</span>
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-80 p-0">
        <div className="border-b px-3 py-2">
          <p className="text-sm font-medium">Tools in this chat</p>
          <p className="text-xs text-muted-foreground">The model calls them when a question needs them.</p>
        </div>
        <ul className="max-h-80 divide-y overflow-y-auto">
          {tools.map((t) => {
            const Icon = toolIcon[t.icon] ?? Wrench
            const id = `tool-${t.id}`
            return (
              <li key={t.id} className="flex items-start gap-3 px-3 py-2.5">
                <Icon className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <label htmlFor={id} className="grid min-w-0 flex-1 cursor-pointer gap-0.5">
                  <span className="text-sm font-medium">{t.title}</span>
                  <span className="text-xs text-muted-foreground">{t.description}</span>
                  {t.askFirst && (
                    <span className="flex items-center gap-1 text-xs text-muted-foreground">
                      <ShieldQuestion className="size-3" aria-hidden="true" /> Asks you before each call
                    </span>
                  )}
                </label>
                <Switch id={id} checked={on.has(t.id)} onCheckedChange={(v) => onChange(v ? [...value, t.id] : value.filter((x) => x !== t.id))} />
              </li>
            )
          })}
        </ul>
      </PopoverContent>
    </Popover>
  )
}
