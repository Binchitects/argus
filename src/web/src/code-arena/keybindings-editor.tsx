import { useQueryClient } from '@tanstack/react-query'
import { Braces, Keyboard, MoreHorizontal, Search } from 'lucide-react'
import { useState, type KeyboardEvent } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Kbd } from '@/components/ui/kbd'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { allCommands, useCommandsVersion } from './commands'
import { userFile } from './editor-options'
import { useEditor } from './editor-state'
import { userFileQuery, writeUserFile } from './ide-api'
import { appendElement, parse, removeElement, setProperty } from './jsonc'
import { keymap, useKeymap } from './keyboard'
import { chordOf, chordText, isRemoval, keysLabel, parseWhen, sameChord, type Chord, type Rule } from './keys'
import { inputBox } from './quick-pick'

/** A row of the table: a command with one of its keys (or none). */
interface Row {
  command: string
  title: string
  rule: Rule | null
}

/** Keys the browser keeps for itself in a tab: a rule of one of them works only in an app window. */
const browserKeeps = (chords: Chord[]) => {
  const c = chords[0]!
  const mod = c.ctrl || c.meta
  return chords.length === 1 && mod && !c.alt && (['w', 't', 'n', 'tab'].includes(c.key) || (c.shift && ['t', 'n'].includes(c.key)) || ['pageup', 'pagedown'].includes(c.key))
}

/**
 * The Keyboard Shortcuts editor (Ctrl+K Ctrl+S), as VS Code's: every command with its keys, when they apply and whose
 * rule each is; searched by words or by pressing the keys (Record Keys). A key changed, removed or reset is written into
 * the person's keybindings.json as VS Code writes it: a default taken away is a "-command" rule.
 */
export default function KeybindingsEditor() {
  const ed = useEditor()
  const queryClient = useQueryClient()
  useCommandsVersion()
  const state = useKeymap()
  const [query, setQuery] = useState(ed.pageQuery)
  const [recording, setRecording] = useState(false)
  const [recorded, setRecorded] = useState<Chord[]>([])
  const [defining, setDefining] = useState<Row | null>(null)

  const titles = new Map<string, string>()
  for (const c of allCommands()) titles.set(c.id, c.category ? `${c.category}: ${c.title}` : c.title)
  for (const [id, label] of keymap.editorCommands) if (!titles.has(id)) titles.set(id, label)
  const rows: Row[] = []
  const withKey = new Set<string>()
  for (const r of [...state.rules].reverse()) {
    if (isRemoval(r)) continue
    rows.push({ command: r.command, title: titles.get(r.command) ?? r.command, rule: r })
    withKey.add(r.command)
  }
  for (const [id, title] of titles) if (!withKey.has(id)) rows.push({ command: id, title, rule: null })

  const words = query.toLowerCase().split(/\s+/).filter(Boolean)
  const shown = rows.filter((row) => {
    if (recording && recorded.length > 0) return !!row.rule && recorded.every((c, i) => row.rule!.chords[i] && sameChord(row.rule!.chords[i]!, c))
    const text = `${row.title} ${row.command} ${row.rule ? keysLabel(row.rule.chords) : ''} ${row.rule?.whenText ?? ''} ${row.rule?.source ?? ''}`.toLowerCase()
    return words.every((w) => text.includes(w))
  })

  /**
   * keybindings.json changed and saved: read again by the keymap at once, and by its tab when it is open with nothing
   * unsaved (so a save there is not of the text from before).
   */
  const change = async (edit: (text: string) => string) => {
    try {
      const now = await queryClient.fetchQuery(userFileQuery('keybindings.json'))
      const text = edit(now.text.trim() ? now.text : '[]')
      const saved = await writeUserFile('keybindings.json', text, now.version)
      queryClient.setQueryData(userFileQuery('keybindings.json').queryKey, { ...now, text, version: saved.version })
      await ed.refresh(userFile('keybindings.json'))
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const removal = (r: Rule) => ({ key: r.key, command: `-${r.command}`, ...(r.whenText ? { when: r.whenText } : {}) })

  const define = (row: Row, chords: Chord[], when?: string) =>
    change((text) => {
      const key = chords.map(chordText).join(' ')
      const r = row.rule
      if (r?.source === 'user' && r.index !== undefined) {
        let next = setProperty(text, [r.index, 'key'], key)
        if (when !== undefined) next = setProperty(next, [r.index, 'when'], when || undefined)
        return next
      }
      // A default changed: taken away, and the person's own rule instead.
      let next = r ? appendElement(text, [], removal(r)) : text
      const w = when ?? r?.whenText
      next = appendElement(next, [], { key, command: row.command, ...(w ? { when: w } : {}) })
      return next
    })

  const remove = (r: Rule) => change((text) => (r.source === 'user' && r.index !== undefined ? removeElement(text, [], r.index) : appendElement(text, [], removal(r))))

  /** The person's rules of this command (and their removals of its defaults) taken out, last first. */
  const reset = (command: string) =>
    change((text) => {
      let next = text
      const user = state.rules.filter((r) => r.source === 'user' && r.command === command && r.index !== undefined).map((r) => r.index!)
      const read = parse(next).value
      const parsed = Array.isArray(read) ? read : []
      const removals = parsed.flatMap((x, i) => (x && typeof x === 'object' && (x as { command?: string }).command === `-${command}` ? [i] : []))
      for (const i of [...new Set([...user, ...removals])].sort((a, b) => b - a)) next = removeElement(next, [], i)
      return next
    })

  const changeWhen = async (row: Row) => {
    const when = await inputBox({
      title: `When ${row.title} applies`,
      placeholder: 'A when clause, e.g. editorTextFocus && !inputFocus (empty: always)',
      value: row.rule?.whenText,
      validate: (v) => (v.trim() && parseWhen(v).error) || null,
    })
    if (when === undefined || !row.rule) return
    await define(row, row.rule.chords, when.trim())
  }

  const record = (e: KeyboardEvent<HTMLInputElement>) => {
    if (!recording) return
    e.preventDefault()
    const c = chordOf(e.nativeEvent)
    if (!c) return
    setRecorded((r) => (r.length >= 2 ? [c] : [...r, c]))
  }

  return (
    <section aria-label="Keyboard Shortcuts" className="absolute inset-0 flex flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b px-4 py-2">
        <label className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-md border bg-background px-2 focus-within:ring-2 focus-within:ring-ring">
          <Search className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <input
            type="search"
            data-keys-raw={recording ? '' : undefined}
            value={recording ? keysLabel(recorded) : query}
            onChange={(e) => !recording && setQuery(e.target.value)}
            onKeyDown={record}
            placeholder={recording ? 'Press the keys to look for' : 'Type to search in keybindings'}
            aria-label="Search keybindings"
            className="min-w-0 flex-1 bg-transparent text-sm outline-none"
          />
        </label>
        <Button
          size="sm"
          variant={recording ? 'default' : 'ghost'}
          aria-pressed={recording}
          onClick={() => {
            setRecording(!recording)
            setRecorded([])
          }}
          title="Record Keys: search by pressing the keys"
        >
          <Keyboard /> <span className="hidden md:inline">Record Keys</span>
        </Button>
        <Button size="sm" variant="ghost" onClick={() => void ed.open(userFile('keybindings.json'))} aria-label="Open Keyboard Shortcuts (JSON)" title="Open Keyboard Shortcuts (JSON)">
          <Braces />
        </Button>
      </div>
      {state.problems.length > 0 && (
        <Alert variant="warning" title="keybindings.json has rules that do nothing here" className="mx-4 mt-3">
          <ul className="list-disc pl-4">
            {state.problems.map((p) => (
              <li key={`${p.index}:${p.message}`}>
                Rule {p.index + 1}: {p.message}
              </li>
            ))}
          </ul>
        </Alert>
      )}
      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full text-sm">
          <thead className="sticky top-0 bg-background text-left text-xs text-muted-foreground">
            <tr>
              <th className="px-4 py-2 font-medium">Command</th>
              <th className="px-2 py-2 font-medium">Keybinding</th>
              <th className="hidden px-2 py-2 font-medium lg:table-cell">When</th>
              <th className="hidden px-2 py-2 font-medium md:table-cell">Source</th>
              <th className="w-8">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {shown.slice(0, 500).map((row, i) => (
              <tr key={`${row.command}:${row.rule?.key ?? ''}:${row.rule?.whenText ?? ''}:${i}`} className="group border-t hover:bg-accent/50">
                <td className="px-4 py-1.5">
                  <div className="truncate">{row.title}</div>
                  <div className="truncate font-mono text-[0.6875rem] text-muted-foreground">{row.command}</div>
                </td>
                <td className="px-2 py-1.5 whitespace-nowrap">
                  <button type="button" onDoubleClick={() => setDefining(row)} aria-label={`${row.title}: ${row.rule ? keysLabel(row.rule.chords) : 'no key'}. Double-click to change it`} className="rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring" onKeyDown={(e) => e.key === 'Enter' && setDefining(row)}>
                    {row.rule ? <Kbd className={cn(browserKeeps(row.rule.chords) && 'opacity-60')}>{keysLabel(row.rule.chords)}</Kbd> : <span className="text-muted-foreground">—</span>}
                  </button>
                  {row.rule && browserKeeps(row.rule.chords) && <span className="ml-1 text-xs text-muted-foreground">(the browser keeps it)</span>}
                </td>
                <td className="hidden max-w-64 truncate px-2 py-1.5 font-mono text-xs text-muted-foreground lg:table-cell">{row.rule?.whenText ?? ''}</td>
                <td className="hidden px-2 py-1.5 text-xs text-muted-foreground md:table-cell">{row.rule ? { default: 'Default', user: 'User', extension: 'Extension' }[row.rule.source] : ''}</td>
                <td className="pr-2">
                  <span className="sr-only">Actions</span>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <button type="button" aria-label={`Change the keys of ${row.title}`} className="grid size-6 place-items-center rounded-sm text-muted-foreground opacity-0 outline-none group-hover:opacity-100 hover:bg-accent focus-visible:opacity-100 focus-visible:ring-2 focus-visible:ring-ring">
                        <MoreHorizontal className="size-4" />
                      </button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end">
                      <DropdownMenuItem onSelect={() => setDefining(row)}>{row.rule ? 'Change Keybinding…' : 'Add Keybinding…'}</DropdownMenuItem>
                      {row.rule && <DropdownMenuItem onSelect={() => void changeWhen(row)}>Change When Expression…</DropdownMenuItem>}
                      {row.rule && <DropdownMenuItem onSelect={() => void remove(row.rule!)}>Remove Keybinding</DropdownMenuItem>}
                      <DropdownMenuItem onSelect={() => void reset(row.command)}>Reset Keybinding</DropdownMenuItem>
                      <DropdownMenuItem onSelect={() => void navigator.clipboard?.writeText(row.command)}>Copy Command ID</DropdownMenuItem>
                      {row.rule && (
                        <DropdownMenuItem onSelect={() => void navigator.clipboard?.writeText(JSON.stringify({ key: row.rule!.key, command: row.command, ...(row.rule!.whenText ? { when: row.rule!.whenText } : {}) }, null, 2))}>
                          Copy
                        </DropdownMenuItem>
                      )}
                    </DropdownMenuContent>
                  </DropdownMenu>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {shown.length === 0 && <p className="py-8 text-center text-sm text-muted-foreground">No command matches.</p>}
      </div>
      {defining && (
        <DefineKeys
          row={defining}
          rules={state.rules}
          onCancel={() => setDefining(null)}
          onDone={(chords) => {
            const row = defining
            setDefining(null)
            void define(row, chords)
          }}
        />
      )}
    </section>
  )
}

/** Pressing the keys for a command: one chord or two; Enter takes them, Escape does not. */
function DefineKeys({ row, rules, onCancel, onDone }: { row: Row; rules: Rule[]; onCancel: () => void; onDone: (chords: Chord[]) => void }) {
  const [chords, setChords] = useState<Chord[]>([])
  const others = chords.length > 0 ? rules.filter((r) => r.command !== row.command && !isRemoval(r) && r.chords.length === chords.length && r.chords.every((c, i) => sameChord(c, chords[i]!))) : []
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    e.preventDefault()
    e.stopPropagation()
    if (e.key === 'Enter' && !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && chords.length > 0) return onDone(chords)
    if (e.key === 'Escape' && !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey) return onCancel()
    const c = chordOf(e.nativeEvent)
    if (c) setChords((now) => (now.length >= 2 ? [c] : [...now, c]))
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onCancel()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Keys for {row.title}</DialogTitle>
          <DialogDescription>Press the keys (two for a chord), then Enter. Escape cancels.</DialogDescription>
        </DialogHeader>
        <input
          readOnly
          autoFocus
          data-keys-raw=""
          aria-label="Press the keys"
          value={chords.length > 0 ? keysLabel(chords) : ''}
          placeholder="Press the keys…"
          onKeyDown={onKey}
          className="h-12 rounded-md border bg-background text-center font-mono text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
        {others.length > 0 && (
          <p className="text-sm text-muted-foreground">
            {others.length} other {others.length === 1 ? 'command has' : 'commands have'} this key: {others.slice(0, 3).map((r) => r.command).join(', ')}
            {others.length > 3 ? '…' : ''}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button disabled={chords.length === 0} onClick={() => onDone(chords)}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
