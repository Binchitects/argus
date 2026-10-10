import { Command as CommandIcon } from 'lucide-react'
import { useState } from 'react'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Kbd } from '@/components/ui/kbd'

/** A command of the workbench or the editor, as the palette lists it. */
export interface PaletteCommand {
  id: string
  label: string
  keys?: string
  run: () => void
}

/** Every command by its name (Ctrl+Shift+P): the workbench's, then the editor's own; typing narrows them. */
export function CommandPalette({ open, onOpenChange, commands }: { open: boolean; onOpenChange: (open: boolean) => void; commands: PaletteCommand[] }) {
  const [typed, setTyped] = useState('')
  const words = typed.toLowerCase().split(/\s+/).filter(Boolean)
  const shown = commands.filter((c) => words.every((w) => c.label.toLowerCase().includes(w)))
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
        {shown.map((c) => (
          <CommandItem
            key={c.id}
            value={c.id}
            onSelect={() => {
              onOpenChange(false)
              setTyped('')
              // After the dialog gives the focus back: an editor's command acts on the editor.
              requestAnimationFrame(() => c.run())
            }}
          >
            <CommandIcon aria-hidden="true" />
            <span className="truncate">{c.label}</span>
            {c.keys && <Kbd className="ml-auto">{c.keys}</Kbd>}
          </CommandItem>
        ))}
      </CommandList>
    </CommandDialog>
  )
}
