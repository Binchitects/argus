import { useState, useSyncExternalStore } from 'react'
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { currentPick, finishPick, onPick, type Open, type PickItem } from './quick-pick'

/** The quick pick shown, for the page's root. */
export function QuickPickHost() {
  const current = useSyncExternalStore(onPick, currentPick)
  return current ? <Picker key={current.title + current.items.length} open={current} /> : null
}

/** The items a pick draws at most: a list of thousands (every file of the folder) is narrowed by typing. */
const shownMost = 200

function Picker({ open: o }: { open: Open }) {
  const [typed, setTyped] = useState(o.value ?? '')
  // The item chosen in the list: the caller's at first ("active"), then as the keys and the mouse move it.
  const [chosen, setChosen] = useState(() => (o.active !== undefined && o.items.some((i) => i.id === o.active) ? o.active : ''))
  const finish = (item: PickItem | undefined) => finishPick(o, item)
  const words = typed.toLowerCase().split(/\s+/).filter(Boolean)
  const matching = o.match ? o.match(typed) : o.filter ? o.items.filter((i) => words.every((w) => `${i.label} ${i.description ?? ''} ${i.detail ?? ''}`.toLowerCase().includes(w))) : o.items
  const shown = matching.slice(0, shownMost)
  // A secret is taken as typed, spaces too; nothing typed, when the caller takes that.
  const extra = o.free && (typed.trim() || o.empty) ? o.free(o.password ? typed : typed.trim()) : null
  const groups = [...new Set(shown.map((i) => i.group ?? ''))]
  const choose = (value: string) => {
    setChosen(value)
    o.onActive?.(o.items.find((i) => i.id === value))
  }
  return (
    <CommandDialog open onOpenChange={(v) => !v && finish(undefined)} title={o.title} description={o.placeholder} shouldFilter={false} value={chosen} onValueChange={choose}>
      {o.password ? (
        // A secret: typed as dots, never shown (cmdk's own box has no password kind; Enter still takes the choice).
        <div className="flex items-center gap-2 border-b px-3">
          <input
            autoFocus
            type="password"
            autoComplete="off"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== 'Enter' || !extra) return
              e.preventDefault()
              e.stopPropagation()
              finish(extra)
            }}
            placeholder={o.placeholder}
            aria-label={o.title}
            className="flex h-11 w-full bg-transparent py-3 text-sm outline-none placeholder:text-muted-foreground"
          />
        </div>
      ) : (
        <CommandInput value={typed} onValueChange={setTyped} onFocus={(e) => e.currentTarget.select()} placeholder={o.placeholder} aria-label={o.title} />
      )}
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
                  {i.detail && <span className="ml-auto max-w-[50%] min-w-0 shrink truncate text-xs text-muted-foreground">{i.detail}</span>}
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
        {matching.length > shown.length && <p className="px-2 py-1.5 text-xs text-muted-foreground">{matching.length - shown.length} more: type to narrow the list.</p>}
      </CommandList>
    </CommandDialog>
  )
}

