import { useQuery } from '@tanstack/react-query'
import { CaseSensitive, ChevronRight, Regex, WholeWord } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { nameOf, parentOf, searchQuery, type SearchMatch, type SearchOptions } from './ide-api'

/** A value that settles: the last one given, once it has not changed for `ms`. */
function useSettled(value: string, ms: number): string {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setSettled(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return settled
}

function Toggle({ label, on, onChange, children }: { label: string; on: boolean; onChange: (on: boolean) => void; children: ReactNode }) {
  return (
    <Tooltip content={label}>
      <button
        type="button"
        aria-label={label}
        aria-pressed={on}
        onClick={() => onChange(!on)}
        className={cn(
          'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-4',
          on && 'bg-primary/15 text-primary-ink ring-1 ring-primary/40 hover:bg-primary/20 hover:text-primary-ink',
        )}
      >
        {children}
      </button>
    </Tooltip>
  )
}

const field = 'h-7 w-full min-w-0 rounded-sm border border-input bg-card px-2 text-[0.8125rem] outline-none placeholder:text-muted-foreground focus-visible:border-primary focus-visible:ring-2 focus-visible:ring-ring/40'

/** A match's line, the match marked. */
function Preview({ m }: { m: SearchMatch }) {
  const before = m.preview.slice(0, m.start)
  // A long line before the match is cut to its last words, so the match stays in view.
  const lead = before.length > 40 ? `…${before.slice(-36).trimStart()}` : before.trimStart()
  return (
    <span className="min-w-0 truncate font-mono text-xs">
      {lead}
      <mark className="rounded-[2px] bg-warning/35 text-foreground">{m.preview.slice(m.start, m.start + m.length)}</mark>
      {m.preview.slice(m.start + m.length)}
    </span>
  )
}

/**
 * Text search across the working directory's files (git's list, so
 * .gitignore holds): case, whole word and regular expressions, files to
 * include and leave out. A result opens its file at the match.
 */
export function SearchPanel({ focusKey }: { focusKey: number }) {
  const editor = useEditor()
  const [text, setText] = useState('')
  const [options, setOptions] = useState({ case: false, word: false, regex: false })
  const [include, setInclude] = useState('')
  const [exclude, setExclude] = useState('')
  const [folded, setFolded] = useState<Set<string>>(() => new Set())
  const asked = JSON.parse(useSettled(JSON.stringify({ q: text, ...options, include, exclude }), 300)) as SearchOptions
  const results = useQuery({ ...searchQuery(asked), enabled: asked.q.length > 0, staleTime: 5_000 })
  const set = (key: keyof typeof options) => (on: boolean) => setOptions((o) => ({ ...o, [key]: on }))
  const box = useRef<HTMLInputElement>(null)
  // Ctrl+Shift+F (or the activity bar) puts the focus in the search box.
  useEffect(() => {
    if (focusKey > 0) box.current?.focus()
  }, [focusKey])

  const data = results.data
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center justify-between pr-1.5 pl-3">
        <h2 className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Search</h2>
        <PartHelp part="search" />
      </div>
      <form
        aria-label="Search the files"
        className="grid shrink-0 grid-cols-1 gap-1.5 px-3 pb-2"
        onSubmit={(e) => {
          e.preventDefault()
          void results.refetch()
        }}
      >
        <div className="flex items-center gap-0.5 rounded-sm border border-input bg-card pr-0.5 focus-within:border-primary focus-within:ring-2 focus-within:ring-ring/40">
          <input
            ref={box}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="Search"
            aria-label="Search"
            className="h-7 min-w-0 flex-1 bg-transparent px-2 text-[0.8125rem] outline-none placeholder:text-muted-foreground"
          />
          <Toggle label="Match case" on={options.case} onChange={set('case')}>
            <CaseSensitive />
          </Toggle>
          <Toggle label="Match whole word" on={options.word} onChange={set('word')}>
            <WholeWord />
          </Toggle>
          <Toggle label="Use regular expression" on={options.regex} onChange={set('regex')}>
            <Regex />
          </Toggle>
        </div>
        <label className="grid gap-0.5 text-[0.6875rem] text-muted-foreground">
          files to include
          <input value={include} onChange={(e) => setInclude(e.target.value)} placeholder="e.g. src, *.ts" className={field} />
        </label>
        <label className="grid gap-0.5 text-[0.6875rem] text-muted-foreground">
          files to exclude
          <input value={exclude} onChange={(e) => setExclude(e.target.value)} placeholder="e.g. tests/**, *.min.js" className={field} />
        </label>
      </form>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]" aria-busy={results.isFetching}>
        {asked.q && results.isFetching && !data && (
          <div className="flex items-center gap-2 px-3 py-2 text-muted-foreground">
            <Spinner /> Searching…
          </div>
        )}
        {results.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(results.error)}</p>}
        {data && asked.q && (
          <output className="block px-3 pb-1 text-xs text-muted-foreground">
            {data.count === 0
              ? 'No results.'
              : `${data.count} ${data.count === 1 ? 'result' : 'results'} in ${data.files.length} ${data.files.length === 1 ? 'file' : 'files'}${data.truncated ? ' (the first ones: make the search narrower for the rest)' : ''}`}
          </output>
        )}
        {data && asked.q && (
          <ul aria-label="Search results">
            {data.files.map((f) => {
              const open = !folded.has(f.path)
              return (
                <li key={f.path}>
                  <button
                    type="button"
                    aria-expanded={open}
                    onClick={() =>
                      setFolded((s) => {
                        const next = new Set(s)
                        if (open) next.add(f.path)
                        else next.delete(f.path)
                        return next
                      })
                    }
                    className="flex h-[1.375rem] w-full items-center gap-1 pr-2 pl-2 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                    title={f.path}
                  >
                    <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform', open && 'rotate-90')} aria-hidden="true" />
                    <FileIcon path={f.path} />
                    <span className="truncate font-medium">{nameOf(f.path)}</span>
                    <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(f.path)}</span>
                    <span className="ml-auto shrink-0 rounded-full bg-muted px-1.5 text-[0.6875rem] text-muted-foreground tabular-nums">{f.matches.length}</span>
                  </button>
                  {open && (
                    <ul>
                      {f.matches.map((m) => (
                        <li key={`${m.line}:${m.column}`}>
                          <button
                            type="button"
                            onClick={() => void editor.open(f.path, { line: m.line, column: m.column, length: m.length })}
                            aria-label={`${f.path}, line ${m.line}, column ${m.column}: ${m.preview.trim()}`}
                            className="flex h-[1.375rem] w-full items-center gap-2 pr-2 pl-9 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                          >
                            <Preview m={m} />
                            <span className="ml-auto shrink-0 text-[0.6875rem] text-muted-foreground tabular-nums">{m.line}</span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </div>
  )
}
