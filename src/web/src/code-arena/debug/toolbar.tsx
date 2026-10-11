import { ArrowDownToLine, ArrowRightFromLine, ArrowUpFromLine, Pause, Play, RotateCcw, Square } from 'lucide-react'
import type { ReactNode } from 'react'
import { Tooltip } from '@/components/ui/tooltip'
import { runCommand } from '../commands'
import { useKeyFor } from '../keyboard'
import { useDebug } from './session'

function Act({ label, command, disabled, children }: { label: string; command: string; disabled?: boolean; children: ReactNode }) {
  const keys = useKeyFor(command)
  return (
    <Tooltip content={keys ? `${label} (${keys})` : label}>
      <button
        type="button"
        aria-label={label}
        disabled={disabled}
        onClick={() => runCommand(command)}
        className="grid size-7 place-items-center rounded-sm text-foreground outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 [&_svg]:size-4"
      >
        {children}
      </button>
    </Tooltip>
  )
}

/** The debug toolbar over the editor while a session runs, as VS Code's: continue or pause, step over, into, out, restart, stop. */
export function DebugToolbar() {
  const d = useDebug()
  const s = d.active
  if (!s || s.state === 'ended') return null
  const stopped = s.state === 'stopped'
  return (
    <div role="toolbar" aria-label="Debug" className="absolute top-1 left-1/2 z-20 flex -translate-x-1/2 items-center gap-0.5 rounded-md border bg-popover px-1 py-0.5 shadow-md">
      {stopped ? (
        <Act label="Continue" command="workbench.action.debug.continue">
          <Play className="text-success-ink" />
        </Act>
      ) : (
        <Act label="Pause" command="workbench.action.debug.pause" disabled={s.state !== 'running'}>
          <Pause />
        </Act>
      )}
      <Act label="Step Over" command="workbench.action.debug.stepOver" disabled={!stopped}>
        <ArrowRightFromLine />
      </Act>
      <Act label="Step Into" command="workbench.action.debug.stepInto" disabled={!stopped}>
        <ArrowDownToLine />
      </Act>
      <Act label="Step Out" command="workbench.action.debug.stepOut" disabled={!stopped}>
        <ArrowUpFromLine />
      </Act>
      <Act label="Restart" command="workbench.action.debug.restart">
        <RotateCcw className="text-success-ink" />
      </Act>
      <Act label="Stop" command="workbench.action.debug.stop">
        <Square className="text-destructive-ink" />
      </Act>
      <span className="px-1.5 text-xs text-muted-foreground">{s.name}</span>
    </div>
  )
}
