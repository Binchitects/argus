import { useState, useSyncExternalStore } from 'react'
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { currentPick, finishPick, onPick, type Open, type PickItem } from './quick-pick'

/** The quick pick shown, for the page's root. */
export function QuickPickHost() {
  const current = useSyncExternalStore(onPick, currentPick)
  return current ? <Picker key={current.title + current.items.length} open={current} /> : null
}

function Picker({ open: o }: { open: Open }) {
  const [typed, setTyped] = useState('')
  const finish = (item: PickItem | undefined) => finishPick(o, item)
  const words = typed.toLowerCase().split(/\s+/).filter(Boolean)
  const shown = o.filter ? o.items.filter((i) => words.every((w) => `${i.label} ${i.description ?? ''} ${i.detail ?? ''}`.toLowerCase().includes(w))) : o.items
  const extra = typed.trim() && o.free ? o.free(typed.trim()) : null
  const groups = [...new Set(shown.map((i) => i.group ?? ''))]
  return (
    <CommandDialog open onOpenChange={(v) => !v && finish(undefined)} title={o.title} description={o.placeholder} shouldFilter={false}>
      <CommandInput value={typed} onValueChange={setTyped} placeholder={o.placeholder} aria-label={o.title} />
      <CommandList>
        <CommandEmpty>{extra ? null : 'Nothing matches.'}</CommandEmpty>
        {groups.map((g) => (
          <CommandGroup key={g} heading={g || undefined}>
            {shown
              .filter((i) => (i.group ?? '') === g)
              .map((i) => (
                <CommandItem key={i.id} value={i.id} onSelect={() => finish(i)}>
                  {i.icon}
                  <span className="truncate">{i.label}</span>
                  {i.description && <span className="min-w-0 truncate text-xs text-muted-foreground">{i.description}</span>}
                  {i.detail && <span className="ml-auto shrink-0 text-xs text-muted-foreground">{i.detail}</span>}
                </CommandItem>
              ))}
          </CommandGroup>
        ))}
        {extra && (
          <CommandItem value={`free:${typed}`} onSelect={() => finish(extra)}>
            <span className="truncate">{extra.label}</span>
            {extra.description && <span className="text-xs text-muted-foreground">{extra.description}</span>}
          </CommandItem>
        )}
      </CommandList>
    </CommandDialog>
  )
}

