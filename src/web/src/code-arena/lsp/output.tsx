import { useQuery } from '@tanstack/react-query'
import { RotateCw } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { runCommand } from '../commands'
import { languageServerLogQuery } from '../ide-api'
import { languageServers, stateText, useLanguageServers } from './servers'

/**
 * The Output panel, as VS Code's, with a channel for each language server the page started: what the page saw of it
 * (started, its messages, why it ended) and the last lines it wrote to stderr, as code-arena keeps them (read again
 * every 2 seconds while shown).
 */
export function OutputPanel({ shown }: { shown: boolean }) {
  useLanguageServers()
  const channels = languageServers.channels()
  const id = languageServers.shown ?? channels[0]?.id ?? null
  const status = channels.find((c) => c.id === id)
  const log = useQuery({ ...languageServerLogQuery(id ?? ''), enabled: shown && !!id, refetchInterval: shown ? 2_000 : false, retry: false })
  const lines = id ? languageServers.outputOf(id) : []
  const end = useRef<HTMLDivElement>(null)
  const stderr = log.data?.lines ?? []
  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' })
  }, [lines.length, stderr.length, id])
  return (
    <section aria-label="Output" className="flex h-full min-h-0 flex-col">
      <div className="flex h-8 shrink-0 items-center gap-2 px-3">
        <select
          aria-label="Output channel"
          value={id ?? ''}
          onChange={(e) => languageServers.show(e.target.value)}
          className="h-6 min-w-0 rounded-sm border bg-background px-1 text-xs"
        >
          {channels.length === 0 && <option value="">No output yet</option>}
          {channels.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
        {status && <span className="min-w-0 truncate text-xs text-muted-foreground">{stateText(status)}{status.detail && status.state !== 'ready' ? `: ${status.detail}` : ''}</span>}
        <span className="flex-1" />
        {status && status.state !== 'missing' && (
          <Tooltip content={`Restart ${status.name}`}>
            <button
              type="button"
              aria-label={`Restart ${status.name}`}
              onClick={() => runCommand('codeArena.languageServer.restart', { id: status.id })}
              className="grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5"
            >
              <RotateCw />
            </button>
          </Tooltip>
        )}
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-3 pb-2 font-mono text-xs break-all whitespace-pre-wrap">
        {!id && <p className="font-sans text-muted-foreground">A language server's messages show here once one starts: open a file of a language it serves, in a trusted folder.</p>}
        {lines.map((l, i) => (
          <div key={i}>{l}</div>
        ))}
        {id && (stderr.length > 0 || log.data?.refused) && (
          <>
            <div className="mt-2 font-sans text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Its stderr{log.data?.running ? '' : log.data?.exitCode !== null && log.data?.exitCode !== undefined ? ` (ended, exit code ${log.data.exitCode})` : ''}</div>
            {log.data?.refused && <div className="text-destructive-ink">{log.data.refused}</div>}
            {stderr.map((l, i) => (
              <div key={`e${i}`} className="text-muted-foreground">
                {l}
              </div>
            ))}
          </>
        )}
        {log.error && <p className="font-sans text-muted-foreground">{errorMessage(log.error)}</p>}
        <div ref={end} />
      </div>
    </section>
  )
}
