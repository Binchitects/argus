import { useQuery } from '@tanstack/react-query'
import { FileCode2 } from 'lucide-react'
import { useState, type KeyboardEvent, type RefObject } from 'react'
import { cn } from '@/lib/utils'
import { rankFiles } from './editor-state'
import { allFilesQuery } from './ide-api'

/** An @ being typed before the caret, and what follows it so far; null: none. */
const typing = (text: string, caret: number) => /(?:^|[\s([{])@([^\s"'`@]*)$/.exec(text.slice(0, caret))?.[1] ?? null

/**
 * @ in the chat's box offers the folder's files, best first as Ctrl+P ranks them: ↑ and ↓ choose, Enter or Tab puts
 * `@path ` in place of what was typed after the @ (a path is then read with the message, `:12-30` after it for lines),
 * Esc leaves it as typed.
 */
export function useFileMention({ area, text, setText }: { area: RefObject<HTMLTextAreaElement | null>; text: string; setText: (text: string) => void }) {
  const [caret, setCaret] = useState(0)
  const [chosen, setChosen] = useState(0)
  const [dismissed, setDismissed] = useState<string | null>(null)
  // Offered only for what the person types: a message brought back with ↑ that ends in @path keeps ↑ and Enter for itself.
  const [typed, setTyped] = useState<string | null>(null)
  const query = typed === text ? typing(text, caret) : null
  const all = useQuery({ ...allFilesQuery, enabled: query !== null })
  const files = query === null || dismissed === text ? [] : rankFiles(all.data?.files ?? [], query, 8)
  const open = files.length > 0
  const at = Math.min(chosen, files.length - 1)

  const pick = (path: string) => {
    const el = area.current
    const end = el?.selectionStart ?? caret
    const start = text.slice(0, end).lastIndexOf('@')
    const put = `@${path.includes(' ') ? `"${path}"` : path} `
    const next = text.slice(0, start) + put + text.slice(end)
    setText(next)
    setTyped(null)
    setChosen(0)
    requestAnimationFrame(() => {
      el?.focus()
      el?.setSelectionRange(start + put.length, start + put.length)
      setCaret(start + put.length)
    })
  }

  return {
    open,
    /** The person typed in the box: what it holds now is theirs (the files are offered for an @ in it). */
    onTyped: (now: string) => {
      setTyped(now)
      setCaret(area.current?.selectionStart ?? now.length)
      setChosen(0)
    },
    /** The box's caret moved. */
    onCaret: () => setCaret(area.current?.selectionStart ?? 0),
    /** Keys while the files are offered: true when one was used here. */
    onKeyDown: (e: KeyboardEvent<HTMLTextAreaElement>) => {
      if (!open) return false
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        setChosen((at + (e.key === 'ArrowDown' ? 1 : files.length - 1)) % files.length)
        return true
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault()
        pick(files[at]!)
        return true
      }
      if (e.key === 'Escape') {
        e.preventDefault()
        setDismissed(text)
        return true
      }
      return false
    },
    /** The list over the box, while it offers files. */
    list: open ? (
      // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- the message box's popup list (a combobox's listbox); a <select> cannot be one
      <ul role="listbox" id="mention-files" aria-label="Files to send with the message" className="absolute inset-x-2 bottom-full z-20 mb-1 grid max-h-64 overflow-y-auto rounded-xl border bg-popover p-1 text-sm shadow-lg">
        {files.map((f, i) => (
          <li
            key={f}
            id={`mention-${i}`}
            // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- an option of the listbox above
            role="option"
            aria-selected={i === at}
            onMouseDown={(e) => {
              // The box keeps the focus.
              e.preventDefault()
              pick(f)
            }}
            className={cn('flex min-w-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1 font-mono text-xs', i === at ? 'bg-accent text-accent-foreground' : 'text-foreground/85')}
          >
            <FileCode2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span className="truncate">{f}</span>
          </li>
        ))}
      </ul>
    ) : null,
    activeId: open ? `mention-${at}` : undefined,
  }
}
