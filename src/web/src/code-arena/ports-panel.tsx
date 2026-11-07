import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, Globe, Lock, PencilLine, Plus, Radio, Users, X } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { studioWorkspace } from './http'
import { parsePort, portName, portsQuery, removePort, setPort, type ForwardedPort, type PortChange } from './ports-api'

// The Ports panel, as VS Code's for a remote workspace: what the workspace's programs listen on (found as they start) and
// the ports forwarded by hand, each at its own address under Arena's domain, private to its owner unless shared.

function open(p: ForwardedPort) {
  window.open(p.url, '_blank', 'noopener,noreferrer')
}

async function copy(p: ForwardedPort) {
  try {
    await navigator.clipboard.writeText(p.url)
    toast.success(`Copied the address of ${portName(p)}.`)
  } catch {
    toast.error('The address could not be copied: select it and copy it.')
  }
}

function RowButton({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <Tooltip content={label}>
      <button
        type="button"
        aria-label={label}
        onClick={onClick}
        className="grid size-6 shrink-0 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5"
      >
        {children}
      </button>
    </Tooltip>
  )
}

function LabelInput({ port, onDone }: { port: ForwardedPort; onDone: (label: string | null) => void }) {
  const [text, setText] = useState(port.label ?? '')
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => ref.current?.select(), [])
  return (
    <Input
      ref={ref}
      autoFocus
      aria-label={`Label of port ${port.port}`}
      value={text}
      maxLength={100}
      className="h-6 px-1.5 text-xs"
      onChange={(e) => setText(e.target.value)}
      onBlur={() => onDone(text)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onDone(text)
        if (e.key === 'Escape') onDone(null)
      }}
    />
  )
}

/** Its state in words: forwarded and listening, forwarded but nothing listening yet, stopped. */
function state(p: ForwardedPort) {
  if (p.stopped) return 'Not forwarded'
  if (!p.listening) return 'Nothing listening yet'
  return null
}

export function PortsPanel({ shown }: { shown: boolean }) {
  const workspace = studioWorkspace()
  const queryClient = useQueryClient()
  const ports = useQuery({ ...portsQuery(workspace ?? ''), enabled: !!workspace, refetchInterval: shown ? 3_000 : false })
  const [editing, setEditing] = useState<number | null>(null)
  const [adding, setAdding] = useState(false)
  const [typed, setTyped] = useState('')
  const change = useMutation({
    mutationFn: ({ port, body }: { port: number; body: PortChange }) => setPort(workspace!, port, body),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: portsQuery(workspace!).queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (port: number) => removePort(workspace!, port),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: portsQuery(workspace!).queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const typedPort = parsePort(typed)

  if (!workspace) {
    return (
      <section aria-label="Ports" className="p-3 text-sm text-muted-foreground">
        Here the folder is on this machine: what its programs listen on is at localhost already.
      </section>
    )
  }
  const view = ports.data
  const forward = () => {
    if (typedPort === null) return
    change.mutate({ port: typedPort, body: { stopped: false } }, { onSuccess: () => (setAdding(false), setTyped('')) })
  }
  return (
    <section aria-label="Ports" className="flex h-full min-h-0 flex-col text-xs">
      <div className="flex h-8 shrink-0 items-center gap-2 px-3">
        {adding ? (
          <form
            className="flex min-w-0 items-center gap-1.5"
            onSubmit={(e) => {
              e.preventDefault()
              forward()
            }}
          >
            <Input
              autoFocus
              aria-label="Port number or address to forward"
              placeholder="3000"
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              onKeyDown={(e) => e.key === 'Escape' && setAdding(false)}
              className="h-6 w-36 px-1.5 text-xs"
              aria-invalid={typed.trim() !== '' && typedPort === null}
            />
            <Button type="submit" size="sm" className="h-6 px-2 text-xs" disabled={typedPort === null} loading={change.isPending}>
              Forward
            </Button>
            <Button type="button" size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setAdding(false)}>
              Cancel
            </Button>
          </form>
        ) : (
          <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={view?.available === false} onClick={() => setAdding(true)}>
            <Plus /> Forward a Port
          </Button>
        )}
        <span className="flex-1" />
        {view?.available && view.running === false && <span className="truncate text-muted-foreground">Asleep: its ports open again when it is started.</span>}
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-3 pb-2">
        {ports.isError && <Alert variant="destructive">{errorMessage(ports.error)}</Alert>}
        {view && !view.available && <p className="text-muted-foreground">{view.reason}</p>}
        {view?.available && view.ports.length === 0 && (
          <div className="grid max-w-prose gap-1 py-2 text-muted-foreground">
            <p>No forwarded ports. Start a dev server in a terminal (npm run dev, dotnet run, python -m http.server): it shows here as it listens, at an address of its own that only you open.</p>
            <p>Forward a Port adds one before it starts.</p>
          </div>
        )}
        {view?.available && view.ports.length > 0 && (
          <table className="w-full min-w-[36rem] border-collapse text-left">
            <thead className="sticky top-0 bg-background text-[0.6875rem] text-muted-foreground">
              <tr>
                <th scope="col" className="py-1 pr-3 font-medium">Port</th>
                <th scope="col" className="py-1 pr-3 font-medium">Forwarded Address</th>
                <th scope="col" className="py-1 pr-3 font-medium">Running Process</th>
                <th scope="col" className="py-1 pr-3 font-medium">Visibility</th>
                <th scope="col" className="py-1 pr-3 font-medium">Origin</th>
                <th scope="col" className="py-1 font-medium">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {view.ports.map((p) => {
                const shared = p.visibility === 'signed-in'
                const note = state(p)
                return (
                  <tr key={p.port} className="group border-t border-border/60 align-middle">
                    <td className="py-1 pr-3 whitespace-nowrap">
                      {editing === p.port ? (
                        <LabelInput
                          port={p}
                          onDone={(label) => {
                            setEditing(null)
                            if (label !== null && label !== (p.label ?? '')) change.mutate({ port: p.port, body: { label } })
                          }}
                        />
                      ) : (
                        <span className="flex items-center gap-1.5">
                          <Radio className={cn('size-3.5 shrink-0', p.listening && !p.stopped ? 'text-success' : 'text-muted-foreground')} aria-hidden="true" />
                          <span className="font-medium">{portName(p)}</span>
                        </span>
                      )}
                    </td>
                    <td className="max-w-64 py-1 pr-3">
                      {p.stopped ? (
                        <span className="text-muted-foreground">{note}</span>
                      ) : (
                        <span className="flex min-w-0 flex-col">
                          <a href={p.url} target="_blank" rel="noopener noreferrer" className="truncate text-primary-ink underline-offset-2 hover:underline" title={p.url}>
                            {p.url.replace(/^https:\/\//, '').replace(/\/$/, '')}
                          </a>
                          {note && <span className="text-muted-foreground">{note}</span>}
                        </span>
                      )}
                    </td>
                    <td className="max-w-56 truncate py-1 pr-3 font-mono text-[0.6875rem] text-muted-foreground" title={p.process ?? undefined}>
                      {p.process ?? ''}
                    </td>
                    <td className="py-1 pr-3 whitespace-nowrap">
                      <span className="flex items-center gap-1">
                        {shared ? <Users className="size-3.5" aria-hidden="true" /> : <Lock className="size-3.5" aria-hidden="true" />}
                        {shared ? 'Everyone signed in' : 'Private'}
                      </span>
                    </td>
                    <td className="py-1 pr-3 whitespace-nowrap text-muted-foreground">{p.origin === 'auto' ? 'Auto Forwarded' : 'User Forwarded'}</td>
                    <td className="py-1">
                      <span className="flex items-center justify-end gap-0.5">
                        {!p.stopped && (
                          <>
                            <RowButton label={`Open ${portName(p)} in Browser`} onClick={() => open(p)}>
                              <Globe />
                            </RowButton>
                            <RowButton label={`Copy the address of ${portName(p)}`} onClick={() => void copy(p)}>
                              <Copy />
                            </RowButton>
                          </>
                        )}
                        <RowButton label={`Set the label of port ${p.port}`} onClick={() => setEditing(p.port)}>
                          <PencilLine />
                        </RowButton>
                        <RowButton
                          label={shared ? `Make port ${p.port} private` : `Share port ${p.port} with everyone signed in`}
                          onClick={() => change.mutate({ port: p.port, body: { visibility: shared ? 'private' : 'signed-in' } })}
                        >
                          {shared ? <Lock /> : <Users />}
                        </RowButton>
                        {p.stopped ? (
                          <RowButton label={`Forward port ${p.port} again`} onClick={() => change.mutate({ port: p.port, body: { stopped: false } })}>
                            <Plus />
                          </RowButton>
                        ) : (
                          <RowButton
                            label={`Stop forwarding port ${p.port}`}
                            onClick={() => (p.origin === 'user' && !p.listening ? remove.mutate(p.port) : change.mutate({ port: p.port, body: { stopped: true } }))}
                          >
                            <X />
                          </RowButton>
                        )}
                      </span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </div>
    </section>
  )
}

/**
 * VS Code's notice when a program starts listening: "Your application running on port 3000 is available", with Open in
 * Browser. Read every 5 seconds while the IDE is open in a Code Studio workspace; the ports found at first say nothing.
 */
export function PortNotices({ onShow }: { onShow: () => void }) {
  const workspace = studioWorkspace()
  const ports = useQuery({ ...portsQuery(workspace ?? ''), enabled: !!workspace, refetchInterval: 5_000 })
  const known = useRef<Set<number> | null>(null)
  useEffect(() => {
    const list = ports.data?.ports
    if (!list) return
    const live = list.filter((p) => p.listening && !p.stopped)
    if (known.current) {
      for (const p of live.filter((x) => !known.current!.has(x.port))) {
        toast(`Your application running on port ${p.port} is available.`, {
          description: p.process ?? undefined,
          action: { label: 'Open in Browser', onClick: () => open(p) },
          cancel: { label: 'Show Ports', onClick: onShow },
        })
      }
    }
    known.current = new Set(live.map((p) => p.port))
  }, [ports.data, onShow])
  return null
}
