import { useQuery } from '@tanstack/react-query'
import { FileCode2, Folder } from 'lucide-react'
import { useState, type KeyboardEvent, type RefObject } from 'react'
import { cn } from '@/lib/utils'
import { rankFiles } from './editor-state'
import { listFolders } from './folders'
import { allFilesQuery } from './ide-api'

/** How many matches the list shows at first, and how many more each "Show more" adds. */
const Page = 50

/**
 * Every file and folder of the project that matches what is typed, best first: the folders the files are in rank with
 * them as Ctrl+P ranks paths, so a whole folder can go with a message too.
 */
export function withFolders(files: string[], query: string): string[] {
  const folders = new Set<string>()
  for (const f of files) {
    for (let at = f.indexOf('/'); at > 0; at = f.indexOf('/', at + 1)) folders.add(f.slice(0, at + 1))
  }
  return rankFiles([...folders, ...files], query, Number.MAX_SAFE_INTEGER)
}

/** An @ being typed before the caret, and what follows it so far; null: none. */
const typing = (text: string, caret: number) => /(?:^|[\s([{])@([^\s"'`@]*)$/.exec(text.slice(0, caret))?.[1] ?? null

/**
 * A whole path being typed after the @ (/, ~/ or a drive, \ read as /): the folder to list, and the start of a name in it.
 * Null for a path in the folder.
 */
export function outsidePath(query: string | null): { folder: string; start: string } | null {
  const q = query?.replace(/\\/g, '/') ?? ''
  if (!/^(\/|~\/|[A-Za-z]:\/)/.test(q)) return null
  const cut = q.lastIndexOf('/') + 1
  return { folder: q.slice(0, cut), start: q.slice(cut) }
}

/**
 * @ in the chat's box offers the folder's files, best first as Ctrl+P ranks them: ↑ and ↓ choose, Enter or Tab puts
 * `@path ` in place of what was typed after the @ (a path is then read with the message, `:12-30` after it for lines),
 * Esc leaves it as typed. A whole path (/, ~/, C:/) offers what is there instead, outside the folder: a folder chosen
 * goes into it, a file chosen goes with the message, and the agent may use it from then on.
 */
export function useFileMention({ area, text, setText }: { area: RefObject<HTMLTextAreaElement | null>; text: string; setText: (text: string) => void }) {
  const [caret, setCaret] = useState(0)
  const [chosen, setChosen] = useState(0)
  const [dismissed, setDismissed] = useState<string | null>(null)
  // Offered only for what the person types: a message brought back with ↑ that ends in @path keeps ↑ and Enter for itself.
  const [typed, setTyped] = useState<string | null>(null)
  // How many of the matches are listed: the first page, then a page more each time "Show more" is used.
  const [shown, setShown] = useState(Page)
  const query = typed === text ? typing(text, caret) : null
  const outside = outsidePath(query)
  const all = useQuery({ ...allFilesQuery, enabled: query !== null && !outside })
  const there = useQuery({ queryKey: ['code', 'mention-folder', outside?.folder], queryFn: () => listFolders(outside!.folder, true), enabled: !!outside, retry: false })
  const named = (list: string[], suffix: string) => list.filter((n) => n.toLowerCase().startsWith(outside!.start.toLowerCase())).map((n) => outside!.folder + n + suffix)
  // Every match, best first: the files, and the folders they are in (a folder goes as its list of files).
  const matches =
    query === null || dismissed === text ? [] : outside ? [...named(there.data?.folders ?? [], '/'), ...named(there.data?.files ?? [], '')] : withFolders(all.data?.files ?? [], query)
  const files = matches.slice(0, shown)
  const left = matches.length - files.length
  const open = files.length > 0
  const at = Math.min(chosen, files.length - 1)

  const pick = (path: string) => {
    const el = area.current
    const end = el?.selectionStart ?? caret
    const start = text.slice(0, end).lastIndexOf('@')
    // A folder outside: gone into, its own offered next (the list stays open).
    const into = path.endsWith('/') && outsidePath(path) !== null
    const put = into ? `@${path}` : `@${path.includes(' ') ? `"${path}"` : path} `
    const next = text.slice(0, start) + put + text.slice(end)
    setText(next)
    setTyped(into ? next : null)
    setChosen(0)
    setShown(Page)
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
      setShown(Page)
    },
    /** The box's caret moved. */
    onCaret: () => setCaret(area.current?.selectionStart ?? 0),
    /** Keys while the files are offered: true when one was used here. */
    onKeyDown: (e: KeyboardEvent<HTMLTextAreaElement>) => {
      if (!open) return false
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        // Past the last one listed, the next page comes in (as Show more does).
        if (e.key === 'ArrowDown' && at === files.length - 1 && left > 0) {
          setShown((n) => n + Page)
          setChosen(at + 1)
          return true
        }
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
      <div className="absolute inset-x-2 bottom-full z-20 mb-1 grid overflow-hidden rounded-xl border bg-popover text-sm shadow-lg">
      <ul role="listbox" id="mention-files" aria-label="Files to send with the message" className="grid max-h-72 overflow-y-auto p-1">
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
            {f.endsWith('/') ? <Folder className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" /> : <FileCode2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />}
            <span className="truncate">{f}</span>
          </li>
        ))}
      </ul>
      <div className="flex items-center justify-between gap-2 border-t px-3 py-1.5 text-xs text-muted-foreground">
        <span aria-live="polite">
          {matches.length === 1 ? '1 match' : `${files.length} of ${matches.length} ${query ? 'matches' : 'files'}`}
          {all.data?.truncated && !outside ? ' (the project has more files than are listed)' : ''}
        </span>
        {left > 0 && (
          <button
            type="button"
            className="rounded px-1.5 py-0.5 text-primary-ink hover:bg-accent"
            onMouseDown={(e) => {
              e.preventDefault()
              setShown((n) => n + Page)
            }}
          >
            Show {Math.min(left, Page)} more
          </button>
        )}
      </div>
      </div>
    ) : null,
    activeId: open ? `mention-${at}` : undefined,
  }
}
