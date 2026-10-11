import { Command as CommandIcon } from 'lucide-react'
import { useState } from 'react'
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Kbd } from '@/components/ui/kbd'
import { allCommands, runCommand, useCommandsVersion } from './commands'
import { keymap, useKeymap } from './keyboard'
import { keyFor } from './keys'

/** A command as the palette lists it: the workbench's (by its category) or the editor's own. */
export interface PaletteCommand {
  id: string
  label: string
  keys?: string
  run: () => void
}

/** The commands run last, newest first, kept with the page's preferences. */
export const recentLimit = 20

/**
 * Every command by its name (Ctrl+Shift+P, F1), as VS Code's: "Category: Title", the ones run lately first; typing a few
 * words of a title (or of its id) narrows them. The editor's own commands follow the workbench's.
 */
export function CommandPalette({
  open,
  onOpenChange,
  editor,
  recent,
  onRan,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The editor's own commands, while a file is shown. */
  editor: PaletteCommand[]
  recent: string[]
  onRan: (id: string) => void
}) {
  const [typed, setTyped] = useState('')
  useCommandsVersion()
  useKeymap()
  const workbench: PaletteCommand[] = allCommands()
    .filter((c) => !c.hidden && (!c.enabled || c.enabled()))
    .map((c) => ({ id: c.id, label: c.category ? `${c.category}: ${c.title}` : c.title, keys: keyFor(keymap.rules, c.id), run: () => void runCommand(c.id) }))
    .sort((a, b) => a.label.localeCompare(b.label))
  const editorOwn = editor.map((c) => ({ ...c, keys: c.keys ?? keyFor(keymap.rules, c.id) }))
  const all = [...workbench, ...editorOwn]
  const words = typed.toLowerCase().split(/\s+/).filter(Boolean)
  const matches = (c: PaletteCommand) => words.every((w) => c.label.toLowerCase().includes(w) || c.id.toLowerCase().includes(w))
  const byId = new Map(all.map((c) => [c.id, c]))
  const recentShown = recent.map((id) => byId.get(id)).filter((c): c is PaletteCommand => !!c && matches(c))
  const recentIds = new Set(recentShown.map((c) => c.id))
  const rest = all.filter((c) => !recentIds.has(c.id) && matches(c))
  const item = (c: PaletteCommand, group: string) => (
    <CommandItem
      key={`${group}:${c.id}`}
      value={`${group}:${c.id}`}
      onSelect={() => {
        onOpenChange(false)
        setTyped('')
        onRan(c.id)
        // After the dialog gives the focus back: an editor's command acts on the editor.
        requestAnimationFrame(() => c.run())
      }}
    >
      <CommandIcon aria-hidden="true" />
      <span className="truncate">{c.label}</span>
      {c.keys && <Kbd className="ml-auto">{c.keys}</Kbd>}
    </CommandItem>
  )
  return (
    <CommandDialog
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o)
        if (!o) setTyped('')
      }}
      title="Commands"
      description="Type a few words of a command"
      shouldFilter={false}
    >
      <CommandInput value={typed} onValueChange={setTyped} placeholder="Run a command: type a few words of it" aria-label="Command to run" />
      <CommandList>
        <CommandEmpty>No command matches.</CommandEmpty>
        {recentShown.length > 0 && <CommandGroup heading="Recently used">{recentShown.map((c) => item(c, 'recent'))}</CommandGroup>}
        <CommandGroup heading={recentShown.length > 0 ? 'Other commands' : undefined}>{rest.map((c) => item(c, 'all'))}</CommandGroup>
      </CommandList>
    </CommandDialog>
  )
}
