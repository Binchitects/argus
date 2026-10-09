import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Bot, ExternalLink, Gauge, GitBranch, GitCompareArrows, Info, LoaderCircle, Plug, RotateCw, ShieldCheck, SquareTerminal, Unplug } from 'lucide-react'
import { useState, type ComponentProps, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { changeSettings, compaction, modeLabels, retryServers, stateQuery, type CodeState, type ServerStatus } from './api'
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

/** The share of the window in use, in whole percent. */
const share = (state: CodeState) => (state.context > 0 ? Math.round((100 * state.contextUsed) / state.context) : 0)

/** "Arena connected, Argus connecting". */
const serversSaid = (servers: ServerStatus[]) => servers.map((s) => `${s.title} ${s.state === 'failed' ? 'not connected' : s.state === 'unavailable' ? 'not available' : s.state}`).join(', ')

/**
 * The line along the bottom, as an editor has: the branch, the agent's changes,
 * the terminal and the commands running with no time limit on the left; the
 * cursor, the file's language, the MCP servers, the window's use, the mode and
 * the model on the right, with the About box (the version, the licence and the
 * source, which the AGPL offers to everyone who uses it).
 */
export function StatusBar({ state, onChanges, onTerminal, onChat, onAbout }: { state: CodeState; onChanges: () => void; onTerminal: () => void; onChat: () => void; onAbout: () => void }) {
  const { active, cursor } = useEditor()
  const changes = useQuery(changesQuery)
  const count = changes.data?.length ?? 0
  const [dialog, setDialog] = useState<'context' | 'servers' | null>(null)
  const running = state.jobs.filter((j) => j.running).length
  const connected = state.servers.filter((s) => s.state === 'connected').length
  const down = state.servers.some((s) => s.state === 'failed')
  const used = share(state)
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
      {running > 0 && (
        <Item onClick={onChat} aria-label={`${running} ${running === 1 ? 'command' : 'commands'} running with no time limit: show them in the chat`} title="Commands running with no time limit: their output and Stop are in the chat">
          <LoaderCircle className="animate-spin motion-reduce:animate-none" aria-hidden="true" /> {running}
        </Item>
      )}
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
      {state.servers.length > 0 && (
        <Item onClick={() => setDialog('servers')} aria-label={`MCP servers: ${serversSaid(state.servers)}`} title="The MCP servers: Arena's, Argus's and your own" className={cn(down && 'bg-warning text-black')}>
          {down ? <Unplug aria-hidden="true" /> : <Plug aria-hidden="true" />}
          <span className="hidden tabular-nums md:inline">
            {connected}/{state.servers.length}
          </span>
        </Item>
      )}
      <Item
        onClick={() => setDialog('context')}
        aria-label={`Context: ${formatValue(state.contextUsed)} of ${formatValue(state.context)} tokens (${used}%), compacts at ${state.compactAt}%`}
        title="How full the model's window is, and when the session compacts itself"
        className={cn(used >= state.compactAt && 'bg-warning text-black')}
      >
        <Gauge aria-hidden="true" />
        {/* The numbers from md up: on a phone the icon says it, so the model and About keep their room. */}
        <span className="hidden tabular-nums md:inline">
          {formatValue(state.contextUsed)} / {formatValue(state.context)}
        </span>
      </Item>
      <Item onClick={onChat} title="How much runs without asking: change it in the chat" aria-label={`Agent mode ${modeLabels[state.mode]}`} className={cn(state.mode === 'yolo' && 'bg-destructive text-destructive-foreground')}>
        <ShieldCheck aria-hidden="true" /> {modeLabels[state.mode]}
      </Item>
      <Item onClick={onChat} title="The agent's model: change it in the chat" aria-label={`Agent model ${state.model}`} className="max-w-[40%]">
        <Bot aria-hidden="true" /> <span className="truncate">{state.model}</span>
      </Item>
      <Item onClick={onAbout} aria-label={`code-arena ${state.version}: about, licence and source`} title="About Code Arena: version, licence and source">
        <Info aria-hidden="true" /> <span className="hidden md:inline">code-arena {state.version}</span>
      </Item>
      <ContextSettings open={dialog === 'context'} onOpenChange={(open) => setDialog(open ? 'context' : null)} state={state} />
      <Servers open={dialog === 'servers'} onOpenChange={(open) => setDialog(open ? 'servers' : null)} state={state} />
    </footer>
  )
}

/**
 * How full the model's window is, and when the session compacts itself: the
 * threshold (a share of the window) and what the recent part may keep whole.
 * Kept in code-arena's config.json, as /compact-at keeps them.
 */
function ContextSettings({ open, onOpenChange, state }: { open: boolean; onOpenChange: (open: boolean) => void; state: CodeState }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md text-foreground">
        {/* Mounted at each opening: it starts from the session's values, not from what was typed and cancelled. */}
        <ContextForm state={state} onDone={() => onOpenChange(false)} />
      </DialogContent>
    </Dialog>
  )
}

function ContextForm({ state, onDone }: { state: CodeState; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [at, setAt] = useState(String(state.compactAt))
  const [target, setTarget] = useState(String(state.compactTarget))
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const save = async (e: FormEvent) => {
    e.preventDefault()
    const a = Number(at)
    const t = Number(target)
    if (!Number.isInteger(a) || a < compaction.minAt || a > compaction.maxAt) return setError(`The threshold is from ${compaction.minAt}% to ${compaction.maxAt}% of the window.`)
    if (!Number.isInteger(t) || t < compaction.minTarget || t > a - compaction.gap) return setError(`What is kept is from ${compaction.minTarget}% to ${a - compaction.gap}% (${compaction.gap} points under the threshold).`)
    setSaving(true)
    try {
      queryClient.setQueryData(stateQuery.queryKey, await changeSettings({ compactAt: a, compactTarget: t }))
      toast.success(`The session compacts at ${a}% of the window, keeping ${t}%.`)
      onDone()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }
  const used = share(state)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Context</DialogTitle>
        <DialogDescription>
          About {formatValue(state.contextUsed)} of {formatValue(state.context)} tokens in use ({used}%). When the conversation reaches the threshold, its older part is summarized and the recent part kept
          whole.
        </DialogDescription>
      </DialogHeader>
      <div className="h-2 overflow-hidden rounded-full bg-muted" aria-hidden="true">
        <div className="relative h-full rounded-full bg-primary" style={{ width: `${Math.min(100, used)}%` }} />
      </div>
      <form className="grid gap-4" onSubmit={(e) => void save(e)} noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Compact at (% of the window)" hint={`Default ${compaction.defaultAt}%`}>
            <Input type="number" inputMode="numeric" min={compaction.minAt} max={compaction.maxAt} value={at} onChange={(e) => setAt(e.target.value)} />
          </Field>
          <Field label="Keep the recent part within (%)" hint={`Default ${compaction.defaultTarget}%`}>
            <Input type="number" inputMode="numeric" min={compaction.minTarget} max={Math.max(compaction.minTarget, Number(at) - compaction.gap)} value={target} onChange={(e) => setTarget(e.target.value)} />
          </Field>
        </div>
        {error && (
          <p role="alert" className="text-sm font-medium text-destructive">
            {error}
          </p>
        )}
        <DialogFooter>
          <Button
            type="button"
            variant="ghost"
            onClick={() => {
              setAt(String(compaction.defaultAt))
              setTarget(String(compaction.defaultTarget))
              setError(null)
            }}
          >
            Defaults
          </Button>
          <Button type="submit" loading={saving}>
            Save
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}

/** The session's MCP servers: connected or not and why, when they are tried again, and Try again. Turned off in config.json (arenaTools, argusTools). */
function Servers({ open, onOpenChange, state }: { open: boolean; onOpenChange: (open: boolean) => void; state: CodeState }) {
  const queryClient = useQueryClient()
  const retry = async (name?: string) => {
    try {
      queryClient.setQueryData(stateQuery.queryKey, await retryServers(name))
      // It says when it is connected: the state is read again in a moment.
      setTimeout(() => void queryClient.invalidateQueries({ queryKey: stateQuery.queryKey }), 1500)
    } catch (err) {
      toast.error(errorMessage(err))
    }
  }
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg text-foreground">
        <DialogHeader>
          <DialogTitle>MCP servers</DialogTitle>
          <DialogDescription>Each connects in the background: the session does not wait for one that is slow or down, and tries it again.</DialogDescription>
        </DialogHeader>
        <ul className="grid gap-2" aria-label="Servers">
          {state.servers.map((s) => (
            <li key={s.name} className="flex min-w-0 items-start gap-3 rounded-lg border p-3">
              <span
                className={cn('mt-1.5 size-2 shrink-0 rounded-full', s.state === 'connected' ? 'bg-success' : s.state === 'connecting' ? 'animate-pulse bg-warning' : s.state === 'failed' ? 'bg-destructive' : 'bg-muted-foreground')}
                aria-hidden="true"
              />
              <div className="grid min-w-0 flex-1 gap-0.5">
                <span className="text-sm font-medium">{s.status}</span>
                {s.url && <span className="truncate font-mono text-xs text-muted-foreground" title={s.url}>{s.url}</span>}
              </div>
              {s.state !== 'connecting' && (
                <Button variant="outline" size="sm" onClick={() => void retry(s.name)} aria-label={`Try ${s.title} again`}>
                  <RotateCw /> Try again
                </Button>
              )}
            </li>
          ))}
        </ul>
        <p className="text-xs text-muted-foreground">
          Arena&apos;s and Argus&apos;s tools are on by default; turn them off with <code>&quot;arenaTools&quot;: false</code> or <code>&quot;argusTools&quot;: false</code> in code-arena&apos;s config.json.
        </p>
      </DialogContent>
    </Dialog>
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
