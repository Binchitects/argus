import { useQuery } from '@tanstack/react-query'
import { Check, CheckCheck, Copy, FileText, GitCompareArrows, MessageSquarePlus, MessageSquareText, Undo2, Wrench } from 'lucide-react'
import { ContextMenu } from 'radix-ui'
import type { ReactNode } from 'react'
import { menuContent, menuItem } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { asks, chatBridge } from './bridge'
import { folderKeys, folderTree, rowKeys, treeIndent, useChangeView } from './change-folds'
import { ChangeFiles, FoldGroup, ViewTools } from './change-tree'
import { useEditor } from './editor-state'
import type { DiffSource } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { changesQuery, gitStatusQuery, nameOf, parentOf, type Change, type GitFile } from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5'

const heading = 'min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase'

/**
 * The changes to review: the files the agent changed in this run, not accepted or reverted yet (each opens as a diff,
 * before the agent and now, and is accepted or reverted on its own or all at once); and the person's own, made by hand in
 * the editor or anywhere else (git's, against what is staged or committed). Each file's right-click menu opens it, adds it
 * to the chat, or asks the agent to explain or fix the change. Each group folds, its files a list or a tree of their
 * folders (change-tree.tsx).
 */
export function ChangesPanel() {
  const editor = useEditor()
  const changes = useQuery(changesQuery)
  const git = useQuery(gitStatusQuery)
  const list = changes.data ?? []
  const agents = new Set(list.map((c) => c.path))
  // What the person changed: the folder's changes git sees that are not the agent's (those are under its own).
  const mine = (git.data?.files ?? []).filter((f) => !agents.has(f.path))
  const keys = ['agent', 'mine', ...folderKeys('agent', folderTree(list, (c) => c.path)), ...folderKeys('mine', folderTree(mine, (f) => f.path))]
  const view = useChangeView('changes', keys)
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className={heading}>Agent changes</h2>
        {list.length > 0 && (
          <Tooltip content="Accept all: keep every change">
            <button type="button" aria-label="Accept all changes" onClick={() => void editor.accept()} className={action}>
              <CheckCheck />
            </button>
          </Tooltip>
        )}
        {list.length + mine.length > 0 && <ViewTools view={view} keys={keys} />}
        <PartHelp part="changes" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]" data-tree>
        {changes.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
        {changes.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(changes.error)}</p>}
        {changes.data && list.length === 0 && (
          <div className="grid justify-items-center gap-2 px-6 py-8 text-center text-muted-foreground">
            <GitCompareArrows className="size-6 opacity-60" aria-hidden="true" />
            <p>No changes to review. The files the agent edits in this run show here, to compare, keep or revert.</p>
          </div>
        )}
        {list.length > 0 && (
          <FoldGroup label="The agent's changes" count={list.length} open={!view.folded('agent')} onToggle={() => view.toggle('agent')}>
            <ChangeFiles label="Changed files" group="agent" items={list} pathOf={(c) => c.path} view={view} row={(c, depth) => <AgentRow key={c.path} change={c} depth={depth} />} />
          </FoldGroup>
        )}
        {mine.length > 0 && (
          <FoldGroup label="Your changes" count={mine.length} open={!view.folded('mine')} onToggle={() => view.toggle('mine')} className="mt-2">
            <ChangeFiles label="Files you changed" group="mine" items={mine} pathOf={(f) => f.path} view={view} row={(f, depth) => <MineRow key={f.path} file={f} depth={depth} />} />
          </FoldGroup>
        )}
      </div>
    </div>
  )
}

/** A file the agent changed: it opens as its compare (before the agent and now); Revert and Accept on hover or focus. */
function AgentRow({ change: c, depth }: { change: Change; depth: number | null }) {
  const editor = useEditor()
  return (
    <ChangeMenu path={c.path} source="agent" deleted={c.deleted}>
      <li className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
        <button
          type="button"
          data-row
          onKeyDown={rowKeys}
          onClick={() => void editor.openDiff(c.path)}
          title={`${c.path}: compare before and now`}
          style={depth === null ? undefined : treeIndent(depth)}
          className={cn('flex h-full min-w-0 flex-1 items-center gap-1.5 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset', depth === null && 'pl-3')}
        >
          <FileIcon path={c.path} />
          <span className={cn('truncate', c.deleted && 'line-through')}>{nameOf(c.path)}</span>
          {depth === null && <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(c.path)}</span>}
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
    </ChangeMenu>
  )
}

/** git's letter for a file's change, and what it says. */
function gitMark(f: GitFile): { letter: string; says: string; tone: string } {
  const s = f.changed ?? f.staged
  if (s === '?' || s === 'A') return { letter: s === '?' ? 'U' : 'A', says: s === '?' ? ', new, not in git yet' : ', added', tone: 'text-success-ink' }
  if (s === 'D') return { letter: 'D', says: ', deleted', tone: 'text-destructive-ink' }
  if (s === 'R') return { letter: 'R', says: `, renamed from ${f.from ?? ''}`, tone: 'text-warning-ink' }
  return { letter: s ?? 'M', says: ', changed', tone: 'text-warning-ink' }
}

/** A file the person changed: it opens as git's compare (against what is staged, or staged against the last commit). */
function MineRow({ file: f, depth }: { file: GitFile; depth: number | null }) {
  const editor = useEditor()
  const source: DiffSource = f.changed ? 'git' : 'staged'
  const mark = gitMark(f)
  const deleted = (f.changed ?? f.staged) === 'D'
  return (
    <ChangeMenu path={f.path} source={source} deleted={deleted} mine>
      <li className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
        <button
          type="button"
          data-row
          onKeyDown={rowKeys}
          onClick={() => void editor.openDiff(f.path, source)}
          title={`${f.path}: compare with ${source === 'git' ? 'what is staged or committed' : 'the last commit'}`}
          style={depth === null ? undefined : treeIndent(depth)}
          className={cn('flex h-full min-w-0 flex-1 items-center gap-1.5 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset', depth === null && 'pl-3')}
        >
          <FileIcon path={f.path} />
          <span className={cn('truncate', deleted && 'line-through')}>{nameOf(f.path)}</span>
          {depth === null && <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(f.path)}</span>}
          <span className="sr-only">{mark.says}</span>
        </button>
        <span className={cn('ml-1 w-3 shrink-0 text-center font-mono text-[0.6875rem] font-semibold', mark.tone)} aria-hidden="true">
          {mark.letter}
        </span>
      </li>
    </ChangeMenu>
  )
}

/**
 * A changed file's right-click menu: its compare and the file itself, the file for the chat, the agent asked to explain
 * or fix the change, its path; and for the agent's, keep or put back.
 */
function ChangeMenu({ path, source, deleted, mine = false, children }: { path: string; source: DiffSource; deleted: boolean; mine?: boolean; children: ReactNode }) {
  const editor = useEditor()
  return (
    <ContextMenu.Root>
      <ContextMenu.Trigger asChild>{children}</ContextMenu.Trigger>
      <ContextMenu.Portal>
        <ContextMenu.Content className={menuContent} aria-label={`${path}: what to do`}>
          <ContextMenu.Item className={menuItem} onSelect={() => void editor.openDiff(path, source)}>
            <GitCompareArrows /> Open changes
          </ContextMenu.Item>
          {!deleted && (
            <ContextMenu.Item className={menuItem} onSelect={() => void editor.open(path)}>
              <FileText /> Open file
            </ContextMenu.Item>
          )}
          <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
          {!deleted && (
            <ContextMenu.Item className={menuItem} onSelect={() => chatBridge.current?.mention(path)}>
              <MessageSquarePlus /> Add to chat
            </ContextMenu.Item>
          )}
          <ContextMenu.Item className={menuItem} onSelect={() => chatBridge.current?.ask(asks.explainChange(path, mine), [])}>
            <MessageSquareText /> Explain these changes
          </ContextMenu.Item>
          <ContextMenu.Item className={menuItem} onSelect={() => chatBridge.current?.ask(asks.fixChange(path, mine), [])}>
            <Wrench /> Fix these changes
          </ContextMenu.Item>
          <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
          {!mine && (
            <>
              <ContextMenu.Item className={menuItem} onSelect={() => void editor.accept(path)}>
                <Check /> Accept
              </ContextMenu.Item>
              <ContextMenu.Item className={menuItem} onSelect={() => void editor.revert(path)}>
                <Undo2 /> Revert
              </ContextMenu.Item>
            </>
          )}
          <ContextMenu.Item className={menuItem} onSelect={() => void navigator.clipboard?.writeText(path).catch(() => undefined)}>
            <Copy /> Copy path
          </ContextMenu.Item>
        </ContextMenu.Content>
      </ContextMenu.Portal>
    </ContextMenu.Root>
  )
}
