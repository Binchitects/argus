import { useQuery } from '@tanstack/react-query'
import { Check, CheckCheck, GitCompareArrows, Undo2 } from 'lucide-react'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { changesQuery, nameOf, parentOf } from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5'

/**
 * The files the agent changed in this run, not accepted or reverted yet: each
 * opens as a diff (before the agent, and now), and is accepted (kept) or
 * reverted (put back as it was) on its own or all at once.
 */
export function ChangesPanel() {
  const editor = useEditor()
  const changes = useQuery(changesQuery)
  const list = changes.data ?? []
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Agent changes</h2>
        {list.length > 0 && (
          <Tooltip content="Accept all: keep every change">
            <button type="button" aria-label="Accept all changes" onClick={() => void editor.accept()} className={action}>
              <CheckCheck />
            </button>
          </Tooltip>
        )}
        <PartHelp part="changes" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        {changes.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
        {changes.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(changes.error)}</p>}
        {changes.data && list.length === 0 && (
          <div className="grid justify-items-center gap-2 px-6 py-8 text-center text-muted-foreground">
            <GitCompareArrows className="size-6 opacity-60" aria-hidden="true" />
            <p>No changes to review. The files the agent edits in this run show here, to compare, keep or revert.</p>
          </div>
        )}
        <ul aria-label="Changed files">
          {list.map((c) => (
            <li key={c.path} className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
              <button
                type="button"
                onClick={() => void editor.openDiff(c.path)}
                title={`${c.path}: compare before and now`}
                className="flex h-full min-w-0 flex-1 items-center gap-1.5 pl-3 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
              >
                <FileIcon path={c.path} />
                <span className={cn('truncate', c.deleted && 'line-through')}>{nameOf(c.path)}</span>
                <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(c.path)}</span>
                <span className="sr-only">{c.created ? ', made by the agent' : c.deleted ? ', deleted' : ', changed'}</span>
              </button>
              <span className="ml-2 shrink-0 font-mono text-[0.6875rem] tabular-nums group-hover:hidden group-focus-within:hidden">
                <span className="text-success-ink">+{c.added}</span> <span className="text-destructive-ink">−{c.removed}</span>
              </span>
              <span className={cn('ml-1 w-3 shrink-0 text-center font-mono text-[0.6875rem] font-semibold group-hover:hidden group-focus-within:hidden', c.created ? 'text-success-ink' : c.deleted ? 'text-destructive-ink' : 'text-warning-ink')} aria-hidden="true">
                {c.created ? 'A' : c.deleted ? 'D' : 'M'}
              </span>
              <span className="ml-1 hidden shrink-0 items-center group-focus-within:flex group-hover:flex">
                <Tooltip content="Revert: put it back as it was">
                  <button type="button" aria-label={`Revert ${c.path}`} onClick={() => void editor.revert(c.path)} className={action}>
                    <Undo2 />
                  </button>
                </Tooltip>
                <Tooltip content="Accept: keep the change">
                  <button type="button" aria-label={`Accept ${c.path}`} onClick={() => void editor.accept(c.path)} className={action}>
                    <Check />
                  </button>
                </Tooltip>
              </span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}
