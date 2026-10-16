import { ArrowDown, ArrowLeftRight, ArrowUp, Check, CheckCheck, Columns2, FileText, FoldVertical, Minus, Plus, RotateCcw, Rows2, Space, Undo2 } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { sideLabel } from './compare'
import { useEditor, type Tab } from './editor-state'
import type { Change } from './ide-api'
import { useKeyFor } from './keyboard'
import { useSettings } from './settings'

function Toggle({ label, keys, on, onClick, children }: { label: string; keys?: string; on?: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <Tooltip content={keys ? `${label} (${keys})` : label}>
      <button
        type="button"
        aria-label={label}
        aria-pressed={on}
        onClick={onClick}
        className={cn('grid size-6 shrink-0 place-items-center rounded-sm outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5', on && 'bg-primary/15 text-primary-ink')}
      >
        {children}
      </button>
    </Tooltip>
  )
}

/**
 * A compare's bar, as VS Code's: its two sides, how many lines differ, the previous and the next change (Shift+Alt+F5,
 * Alt+F5), side by side or inline, whitespace ignored or not, unchanged lines folded (each a setting, as in VS Code),
 * the sides swapped, the file opened; for the agent's changes, Revert and Accept.
 */
export function CompareToolbar({ tab, change }: { tab: Tab; change?: Change }) {
  const ed = useEditor()
  const settings = useSettings()
  const [counts, setCounts] = useState<{ added: number; removed: number; changes: number } | null>(null)
  const next = useKeyFor('workbench.action.compareEditor.nextChange')
  const previous = useKeyFor('workbench.action.compareEditor.previousChange')
  const stageKey = useKeyFor('git.stageSelectedRanges')
  const unstageKey = useKeyFor('git.unstageSelectedRanges')
  const revertKey = useKeyFor('git.revertSelectedRanges')
  const acceptKey = useKeyFor('codeArena.changes.acceptSelectedRanges')
  const sideBySide = settings.get<boolean>('diffEditor.renderSideBySide')
  const ignoreWhitespace = settings.get<boolean>('diffEditor.ignoreTrimWhitespace')
  const folded = settings.get<boolean>('diffEditor.hideUnchangedRegions.enabled')
  // Made with the first compare shown, after this bar first rendered: diffMade renders it again then.
  const diff = tab.status === 'ready' && ed.diffMade ? ed.diffEditor() : null

  // The counts as Monaco works the compare out (again at each edit).
  useEffect(() => {
    if (!diff) return
    const count = () => {
      const lines = diff.getLineChanges?.()
      if (!lines) return
      let added = 0
      let removed = 0
      for (const c of lines) {
        if (c.modifiedEndLineNumber > 0) added += c.modifiedEndLineNumber - c.modifiedStartLineNumber + 1
        if (c.originalEndLineNumber > 0) removed += c.originalEndLineNumber - c.originalStartLineNumber + 1
      }
      setCounts({ added, removed, changes: lines.length })
    }
    count()
    const d = diff.onDidUpdateDiff?.(count)
    return () => d?.dispose()
  }, [diff, tab.id])

  const set = (key: string, value: boolean) => void settings.update(key, value).catch((e: unknown) => toast.error(errorMessage(e)))
  const shown = counts ?? (change ? { added: change.added, removed: change.removed, changes: -1 } : null)
  const tag = tab.source === 'git' ? 'not staged' : tab.source === 'staged' ? 'staged' : tab.source === 'agent' ? "the agent's changes" : null
  return (
    <div className="flex h-8 shrink-0 items-center gap-1.5 border-b px-3 text-xs text-muted-foreground">
      <span className="min-w-0 truncate font-mono" title={tab.path}>
        {tab.left && tab.right ? `${sideLabel(tab.left)} ↔ ${sideLabel(tab.right)}` : tab.path}
      </span>
      {tag && (
        <span className="hidden shrink-0 rounded-sm bg-primary/10 px-1.5 py-0.5 text-primary-ink lg:inline" title={tab.source === 'git' ? 'The index, and the file now' : tab.source === 'staged' ? 'The last commit, and the index' : 'Before the agent, and now'}>
          {tag}
        </span>
      )}
      {shown && (
        <span className="shrink-0 font-mono tabular-nums" title={shown.changes >= 0 ? `${shown.changes} ${shown.changes === 1 ? 'change' : 'changes'}` : undefined}>
          <span className="text-success-ink">+{shown.added}</span> <span className="text-destructive-ink">−{shown.removed}</span>
        </span>
      )}
      <span className="ml-auto" />
      <Toggle label="Previous change" keys={previous} onClick={() => diff?.goToDiff('previous')}>
        <ArrowUp />
      </Toggle>
      <Toggle label="Next change" keys={next} onClick={() => diff?.goToDiff('next')}>
        <ArrowDown />
      </Toggle>
      <Toggle label={sideBySide ? 'Show inline' : 'Show side by side'} on={!sideBySide} onClick={() => set('diffEditor.renderSideBySide', !sideBySide)}>
        {sideBySide ? <Rows2 /> : <Columns2 />}
      </Toggle>
      <Toggle label="Ignore leading and trailing whitespace" on={ignoreWhitespace} onClick={() => set('diffEditor.ignoreTrimWhitespace', !ignoreWhitespace)}>
        <Space />
      </Toggle>
      <Toggle label="Fold unchanged lines" on={folded} onClick={() => set('diffEditor.hideUnchangedRegions.enabled', !folded)}>
        <FoldVertical />
      </Toggle>
      <Toggle label="Swap the sides" onClick={() => ed.swapSides(tab.id)}>
        <ArrowLeftRight />
      </Toggle>
      {tab.source === 'git' && (
        <Toggle label="Stage the change at the cursor" keys={stageKey} onClick={() => void ed.changeAction('stage')}>
          <Plus />
        </Toggle>
      )}
      {tab.source === 'staged' && (
        <Toggle label="Unstage the change at the cursor" keys={unstageKey} onClick={() => void ed.changeAction('unstage')}>
          <Minus />
        </Toggle>
      )}
      {tab.source === 'agent' && (
        <Toggle label="Accept the change at the cursor" keys={acceptKey} onClick={() => void ed.changeAction('accept')}>
          <CheckCheck />
        </Toggle>
      )}
      {(tab.source === 'git' || tab.source === 'agent') && tab.right?.kind === 'file' && (
        <Toggle label="Revert the change at the cursor" keys={revertKey} onClick={() => void ed.changeAction('revert')}>
          <RotateCcw />
        </Toggle>
      )}
      {tab.right?.kind === 'file' && !change?.deleted && (
        <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void ed.open(tab.path)}>
          <FileText /> Open file
        </Button>
      )}
      {tab.source === 'agent' && (
        <>
          <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void ed.revert(tab.path)}>
            <Undo2 /> Revert
          </Button>
          <Button size="sm" className="h-6 px-2 text-xs" onClick={() => void ed.accept(tab.path)}>
            <Check /> Accept
          </Button>
        </>
      )}
    </div>
  )
}
