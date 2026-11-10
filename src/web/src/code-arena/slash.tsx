import { useQuery } from '@tanstack/react-query'
import { SquareSlash, WandSparkles } from 'lucide-react'
import { useState, type KeyboardEvent, type RefObject } from 'react'
import { cn } from '@/lib/utils'
import { skillsQuery } from './ide-api'

/** What / offers: the chat's own commands, the person's and the project's commands, and every skill. */
interface SlashItem {
  name: string
  description: string
  kind: 'chat' | 'command' | 'skill'
}

const own: SlashItem[] = [
  { name: 'compact', description: "Summarize the conversation to free the model's window", kind: 'chat' },
  { name: 'context', description: 'What fills the window: each tool, skill, file and message, in tokens', kind: 'chat' },
  { name: 'clear', description: 'Start a new session (this one stays saved)', kind: 'chat' },
  { name: 'project', description: 'The session\'s project in Arena (alone: open it); NAME or none puts the session in one; new NAME makes one', kind: 'chat' },
  { name: 'projects', description: 'Show the Projects view: Arena\'s projects you see', kind: 'chat' },
]

/**
 * A /name being typed at the start of the box, or after the skills chosen there (/review /te…: several go together), before
 * the caret: what follows its / so far; null: none.
 */
const typing = (text: string, caret: number) => /^(?:\/[\w:.-]+\s+)*\/([\w:.-]*)$/.exec(text.slice(0, caret))?.[1] ?? null

/** A /name with a space after it at the start of the box: a command or skill chosen, the words for it after. */
const chosenName = /^\/[\w:.-]+(\s+|$)/

/**
 * / at the start of the chat's box offers the chat's commands, the person's and the project's own and the skills (as the
 * web chat's / does): those whose name starts with what is typed first, then those that hold it, all of them (the list
 * scrolls); ↑ and ↓ choose, Enter or Tab puts `/name ` in the box for the words after it, Esc leaves it as typed. A name
 * typed whole and chosen, Enter sends as it is (/clear runs at once). A skill sent so goes with its instructions. Esc with
 * the list shut lets a chosen `/name` go, and keeps the words after it.
 */
export function useSlashMenu({ area, text, setText }: { area: RefObject<HTMLTextAreaElement | null>; text: string; setText: (text: string) => void }) {
  const [caret, setCaret] = useState(0)
  const [chosen, setChosen] = useState(0)
  const [dismissed, setDismissed] = useState<string | null>(null)
  // Offered only for what the person types: a message brought back with ↑ keeps ↑ and Enter for itself.
  const [typed, setTyped] = useState<string | null>(null)
  const query = typed === text && dismissed !== text ? typing(text, caret) : null
  const listed = useQuery({ ...skillsQuery, enabled: query !== null })
  const all: SlashItem[] = [
    ...own,
    ...(listed.data?.commands ?? []).map((c): SlashItem => ({ name: c.name, description: c.argumentHint ? `${c.description} (${c.argumentHint})` : c.description, kind: 'command' })),
    ...(listed.data?.skills ?? []).map((s): SlashItem => ({ name: s.name, description: s.description, kind: 'skill' })),
  ]
  const q = query?.toLowerCase() ?? ''
  const items =
    query === null
      ? []
      : [...all.filter((i) => i.name.toLowerCase().startsWith(q)), ...all.filter((i) => !i.name.toLowerCase().startsWith(q) && i.name.toLowerCase().includes(q))]
          // A name once: a command wins over a skill of its name, as code-arena reads them.
          .filter((i, at, list) => list.findIndex((o) => o.name === i.name) === at)
  const open = items.length > 0
  const at = Math.min(chosen, items.length - 1)

  const pick = (item: SlashItem) => {
    const el = area.current
    // After the skills chosen before it: they stay, this one joins them.
    const before = text.slice(0, caret).replace(/\/[\w:.-]*$/, '')
    const put = `${before}/${item.name} `
    const next = put + text.slice(el?.selectionEnd ?? caret).trimStart()
    setText(next)
    setTyped(null)
    setChosen(0)
    requestAnimationFrame(() => {
      el?.focus()
      el?.setSelectionRange(put.length, put.length)
      setCaret(put.length)
    })
  }

  return {
    open,
    /** The person typed in the box. */
    onTyped: (now: string) => {
      setTyped(now)
      setCaret(area.current?.selectionStart ?? now.length)
      setChosen(0)
    },
    /** The box's caret moved. */
    onCaret: () => setCaret(area.current?.selectionStart ?? 0),
    /** Keys while the list is offered: true when one was used here. */
    onKeyDown: (e: KeyboardEvent<HTMLTextAreaElement>) => {
      // A /name chosen at the start of the box: Escape lets it go, and keeps the words after it.
      if (!open && e.key === 'Escape' && !e.nativeEvent.isComposing && chosenName.test(text)) {
        e.preventDefault()
        const rest = text.replace(chosenName, '')
        setText(rest)
        setDismissed(rest)
        requestAnimationFrame(() => area.current?.setSelectionRange(rest.length, rest.length))
        return true
      }
      if (!open) return false
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        setChosen((at + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length)
        return true
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        // The whole name is typed already: Enter is the box's (it sends, or runs /clear and /compact); Tab still completes.
        if (e.key === 'Enter' && items[at]!.name.toLowerCase() === q) return false
        e.preventDefault()
        pick(items[at]!)
        return true
      }
      if (e.key === 'Escape') {
        e.preventDefault()
        setDismissed(text)
        return true
      }
      return false
    },
    /** The list over the box, while it offers something. */
    list: open ? (
      // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- the message box's popup list (a combobox's listbox); a <select> cannot be one
      <ul role="listbox" id="slash-commands" aria-label="Skills and commands" className="absolute inset-x-2 bottom-full z-20 mb-1 grid max-h-72 overflow-y-auto rounded-xl border bg-popover p-1 text-sm shadow-lg">
        {items.map((item, i) => (
          <li
            key={item.name}
            id={`slash-${i}`}
            // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- an option of the listbox above
            role="option"
            aria-selected={i === at}
            // The list scrolls: ↑ and ↓ keep the chosen one in sight.
            ref={i === at ? (el) => el?.scrollIntoView?.({ block: 'nearest' }) : undefined}
            onMouseDown={(e) => {
              // The box keeps the focus.
              e.preventDefault()
              pick(item)
            }}
            className={cn('flex min-w-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1 text-xs', i === at ? 'bg-accent text-accent-foreground' : 'text-foreground/85')}
          >
            {item.kind === 'skill' ? <WandSparkles className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" /> : <SquareSlash className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />}
            <span className="shrink-0 font-mono">/{item.name}</span>
            <span className="min-w-0 truncate text-muted-foreground">{item.description}</span>
            <span className="sr-only">{item.kind === 'skill' ? ', a skill' : ', a command'}</span>
          </li>
        ))}
      </ul>
    ) : null,
    activeId: open ? `slash-${at}` : undefined,
  }
}
