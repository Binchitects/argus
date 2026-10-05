import { useQuery } from '@tanstack/react-query'
import { Bot, ExternalLink, GitBranch, GitCompareArrows, Info, ShieldCheck, SquareTerminal } from 'lucide-react'
import type { ComponentProps } from 'react'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { modeLabels, type CodeState } from './api'
import { useEditor } from './editor-state'
import { changesQuery } from './ide-api'

function Item({ className, ...props }: ComponentProps<'button'>) {
  return (
    <button
      type="button"
      className={cn('flex h-full min-w-0 shrink-0 items-center gap-1 px-2 outline-none hover:bg-white/15 focus-visible:bg-white/20 focus-visible:ring-1 focus-visible:ring-white/70 focus-visible:ring-inset [&_svg]:size-3.5 [&_svg]:shrink-0', className)}
      {...props}
    />
  )
}

/**
 * The line along the bottom, as an editor has: the branch, the agent's changes
 * and the terminal on the left; the cursor, the file's language, the mode and
 * the model on the right, with the About box (the version, the licence and the
 * source, which the AGPL offers to everyone who uses it).
 */
export function StatusBar({ state, onChanges, onTerminal, onChat, onAbout }: { state: CodeState; onChanges: () => void; onTerminal: () => void; onChat: () => void; onAbout: () => void }) {
  const { active, cursor } = useEditor()
  const changes = useQuery(changesQuery)
  const count = changes.data?.length ?? 0
  return (
    <footer aria-label="Status bar" className="flex h-6 shrink-0 items-stretch overflow-hidden bg-primary text-[0.75rem] text-primary-foreground">
      {state.branch && (
        <span className="flex min-w-0 items-center gap-1 px-2" title={`Git branch ${state.branch}`}>
          <GitBranch className="size-3.5 shrink-0" aria-hidden="true" />
          <span className="sr-only">Branch</span>
          <span className="truncate">{state.branch}</span>
        </span>
      )}
      <Item onClick={onChanges} aria-label={`Agent changes: ${count} ${count === 1 ? 'file' : 'files'}`} title="The files the agent changed">
        <GitCompareArrows aria-hidden="true" /> {count}
      </Item>
      <Item onClick={onTerminal} aria-label="Show or hide the terminal" title="Terminal (Ctrl+`)">
        <SquareTerminal aria-hidden="true" />
      </Item>
      <span className="flex-1" />
      {active?.kind === 'file' && active.status === 'ready' && cursor && (
        <span className="flex items-center px-2 tabular-nums" aria-label={`Line ${cursor.line}, column ${cursor.column}`}>
          Ln {cursor.line}, Col {cursor.column}
        </span>
      )}
      {active?.status === 'ready' && active.language && (
        <span className="hidden items-center px-2 sm:flex" title="The file's language">
          <span className="sr-only">Language: </span>
          {active.language}
        </span>
      )}
      <Item onClick={onChat} title="How much runs without asking: change it in the chat" aria-label={`Agent mode ${modeLabels[state.mode]}`} className={cn(state.mode === 'yolo' && 'bg-destructive text-destructive-foreground')}>
        <ShieldCheck aria-hidden="true" /> {modeLabels[state.mode]}
      </Item>
      <Item onClick={onChat} title="The agent's model: change it in the chat" aria-label={`Agent model ${state.model}`} className="max-w-[40%]">
        <Bot aria-hidden="true" /> <span className="truncate">{state.model}</span>
      </Item>
      <Item onClick={onAbout} aria-label={`code-arena ${state.version}: about, licence and source`} title="About Code Arena: version, licence and source">
        <Info aria-hidden="true" /> <span className="hidden md:inline">code-arena {state.version}</span>
      </Item>
    </footer>
  )
}

/** The version, the licence and the "Source" link (LICENSING.md, section 7(b): every copy keeps them). */
export function About({ open, onOpenChange, state }: { open: boolean; onOpenChange: (open: boolean) => void; state: CodeState }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-sm text-foreground">
        <DialogHeader className="items-center text-center">
          <img src="/favicon.svg" alt="" className="mb-1 size-12 rounded-xl" />
          <DialogTitle>{state.name}</DialogTitle>
          <DialogDescription>Argus Arena&apos;s coding agent, with its editor, terminals and chat in the browser.</DialogDescription>
        </DialogHeader>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
          <dt className="text-muted-foreground">Version</dt>
          <dd className="font-mono">{state.version}</dd>
          <dt className="text-muted-foreground">Licence</dt>
          <dd>{state.license}</dd>
          <dt className="text-muted-foreground">Folder</dt>
          <dd className="min-w-0 truncate font-mono text-xs leading-5" title={state.folder}>
            {state.folder}
          </dd>
        </dl>
        <p className="text-xs text-muted-foreground">
          Free software under the GNU Affero General Public License: its complete source is offered to everyone who uses it, and comes with no warranty.
        </p>
        <a href={state.source} target="_blank" rel="noreferrer" className="inline-flex items-center justify-center gap-1.5 rounded-md border px-3 py-2 text-sm font-medium outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
          Source <ExternalLink className="size-3.5" aria-hidden="true" />
        </a>
      </DialogContent>
    </Dialog>
  )
}
