import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useOutletContext } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, ApiError, errorMessage, type Me } from '@/lib/api'
import { seconds } from '@/pages/chat/format'

interface LineEntry {
  /** Its ticket: Remove or Stop takes it out by this. */
  id: string
  person: { id: string; userName: string | null; displayName: string | null }
  model: string
  /** chat: an answer; api: a request with a key. */
  kind: 'chat' | 'api'
  since: string
  seconds: number
  /** Waiting: 1 is served next. */
  position: number | null
  priority: number | null
}

interface Line {
  /** The replica that answered, when there are several: each keeps its own line. */
  replica?: string | null
  running: LineEntry[]
  waiting: LineEntry[]
  /** Each model's places (its answers at once); 0: no limit of its own. */
  places: Record<string, number>
  answersAtOnce: number
  answersPerPerson: number
  queueLength: number
  queueTimeoutMinutes: number
}

/** The most an admin's reason says (the server's limit). */
const MostReason = 300

/** Reasons an admin gives often: one fills the box, to say more after it. */
const Reasons = ['The model is needed for others now', 'A runaway agent', 'Too long a request for the model']

const who = (e: LineEntry) => (e.person.displayName ?? e.person.userName ?? 'someone') + (e.person.userName && e.person.displayName ? ` (${e.person.userName})` : '')
const whose = (e: LineEntry) => `${e.person.displayName ?? e.person.userName ?? 'someone'}’s`

function Entries({ title, description, rows, waiting, onRemove }: { title: string; description: string; rows: LineEntry[]; waiting?: boolean; onRemove?: (e: LineEntry) => void }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {title} <span className="font-normal text-muted-foreground tabular-nums">{rows.length}</span>
        </CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">{waiting ? 'Nobody waits.' : 'Nothing runs now.'}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm" aria-label={title}>
              <thead>
                <tr className="text-left text-xs text-muted-foreground">
                  {waiting && <th className="py-1.5 pr-3 font-medium">Place</th>}
                  <th className="py-1.5 pr-3 font-medium">Person</th>
                  <th className="py-1.5 pr-3 font-medium">Model</th>
                  <th className="py-1.5 pr-3 font-medium">Kind</th>
                  <th className="py-1.5 pr-3 text-right font-medium">{waiting ? 'Waiting' : 'Running'}</th>
                  {waiting && <th className="py-1.5 pr-3 text-right font-medium">Priority</th>}
                  {onRemove && (
                    <th className="py-1.5">
                      <span className="sr-only">{waiting ? 'Remove' : 'Stop'}</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {rows.map((e) => (
                  <tr key={e.id} className="border-t">
                    {waiting && <td className="py-1.5 pr-3 tabular-nums">{e.position}</td>}
                    <td className="py-1.5 pr-3">
                      {e.person.userName ? <Link to={`/admin/people/${e.person.id}`} className="underline-offset-2 hover:underline">{who(e)}</Link> : who(e)}
                    </td>
                    <td className="py-1.5 pr-3 font-mono text-xs">{e.model}</td>
                    <td className="py-1.5 pr-3">
                      <Badge variant={e.kind === 'api' ? 'outline' : 'secondary'}>{e.kind === 'api' ? 'API' : 'Chat'}</Badge>
                    </td>
                    <td className="py-1.5 pr-3 text-right tabular-nums">{seconds(e.seconds * 1000)}</td>
                    {waiting && <td className="py-1.5 pr-3 text-right tabular-nums">{e.priority ?? 0}</td>}
                    {onRemove && (
                      <td className="py-1 text-right">
                        <Button variant="ghost" size="sm" aria-label={`${waiting ? 'Remove' : 'Stop'} ${whose(e)} request on ${e.model}`} onClick={() => onRemove(e)}>
                          {waiting ? 'Remove' : 'Stop'}
                        </Button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

/** Why a request leaves the line: the person (in the chat) or the caller (in the API's error) is told, and it is audited. */
function RemoveDialog({ target, open, onClose }: { target: { entry: LineEntry; running: boolean } | null; open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const close = () => {
    setReason('')
    setError(null)
    onClose()
  }
  // It goes with how the admin saw it (running or waiting): one seen waiting that started meanwhile is not stopped.
  const remove = useMutation({
    mutationFn: (v: { id: string; reason: string; running: boolean }) =>
      api<{ was: 'waiting' | 'running' | null }>(`/api/admin/line/${v.id}/remove`, { method: 'POST', body: { reason: v.reason, running: v.running } }),
    onSuccess: (r) => {
      // was null: another replica had it (each keeps its own line), and did it.
      const done = r.was === null ? 'Done by the replica that had it' : r.was === 'running' ? 'Stopped' : 'Taken out of the line'
      toast.success(done, { description: target?.entry.kind === 'api' ? 'The caller’s error says why.' : 'They are told why in the chat.' })
      close()
      void queryClient.invalidateQueries({ queryKey: ['admin', 'line'] })
    },
    onError: (e) => {
      // It moved on while the reason was written: nothing was changed, and the line shows it as it is now.
      const moved: Record<string, string> = {
        not_in_line: target?.running ? 'It had ended already' : 'It had left the line already',
        started: 'It started meanwhile',
        already_stopping: 'Already being stopped',
        waiting: 'It still waits',
      }
      const said = e instanceof ApiError ? moved[e.status] : undefined
      if (said) {
        toast(said, { description: e.message })
        close()
        void queryClient.invalidateQueries({ queryKey: ['admin', 'line'] })
      } else setError(errorMessage(e))
    },
  })
  if (!target) return null
  const { entry: e, running } = target
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{running ? `Stop ${whose(e)} request?` : `Take ${whose(e)} request out of the line?`}</DialogTitle>
          <DialogDescription>
            {running
              ? `It stops now on ${e.model}, and keeps what it wrote so far.`
              : `Nothing of it was sent to ${e.model}; the next in line moves up.`}{' '}
            Say why: {e.kind === 'api' ? 'the caller’s error says it' : 'they read it under their answer'}, and it is in the audit log, with you.
          </DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(ev) => {
            ev.preventDefault()
            setError(null)
            if (!reason.trim()) setError('Say why: they are told.')
            else remove.mutate({ id: e.id, reason: reason.trim(), running })
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          <div className="flex flex-wrap gap-2">
            {Reasons.map((r) => (
              <Button key={r} type="button" variant="outline" size="sm" onClick={() => setReason(r)}>
                {r}
              </Button>
            ))}
          </div>
          <Field label="Reason">
            <Textarea rows={2} value={reason} onChange={(ev) => setReason(ev.target.value)} maxLength={MostReason} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" variant="destructive" loading={remove.isPending}>
              {running ? 'Stop it' : 'Take it out'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Admin → Line: who has a request running and who waits, in the order they will be served, every two seconds; with usage.line, take one out. */
export function LinePage() {
  const me = useOutletContext<Me>()
  const may = !!me.isAdmin || !!me.permissions?.includes('usage.line')
  // The request chosen stays while the dialog closes, so its words do not go blank as it fades.
  const [target, setTarget] = useState<{ entry: LineEntry; running: boolean } | null>(null)
  const [open, setOpen] = useState(false)
  const choose = (entry: LineEntry, running: boolean) => {
    setTarget({ entry, running })
    setOpen(true)
  }
  const line = useQuery({ queryKey: ['admin', 'line'], queryFn: ({ signal }) => api<Line>('/api/admin/line', { signal }), refetchInterval: 2000 })
  if (line.isPending) return <PageSkeleton />
  if (line.error) return <QueryError error={line.error} retry={() => line.refetch()} />
  const l = line.data
  const models = Object.entries(l.places)
  return (
    <>
      <PageHeader
        title="Line"
        description="Chat answers and API requests running now, and those waiting their turn in the order they will be served: a higher priority first, then whoever has fewest running, then whoever was served longest ago."
      />
      <p className="mb-4 text-sm text-muted-foreground">
        {l.answersAtOnce > 0 ? `${l.answersAtOnce} at once for everyone` : 'No limit for everyone beyond each model’s places'} · {l.answersPerPerson} per person unless set for them or their group ·{' '}
        {l.queueLength > 0 ? `at most ${l.queueLength} waiting, the next refused as busy` : 'no limit on the line’s length'} · a wait gives up after {l.queueTimeoutMinutes} minutes.{' '}
        <Link to="/admin/settings" className="underline underline-offset-2">
          Settings → Chat
        </Link>
        {l.replica && <> · the line of replica <span className="font-mono">{l.replica}</span>: each keeps its own.</>}
      </p>
      {models.length > 0 && (
        <p className="mb-4 flex flex-wrap gap-2 text-xs text-muted-foreground">
          {models.map(([m, n]) => (
            <span key={m} className="rounded-md border px-2 py-0.5">
              <span className="font-mono">{m}</span>: {n > 0 ? `${n} at once` : 'no limit of its own'}
            </span>
          ))}
        </p>
      )}
      <div className="grid gap-6 xl:grid-cols-2">
        <Entries title="Waiting" description="In the order they will be served." rows={l.waiting} waiting onRemove={may ? (entry) => choose(entry, false) : undefined} />
        <Entries title="Running" description="The longest running first." rows={l.running} onRemove={may ? (entry) => choose(entry, true) : undefined} />
      </div>
      <RemoveDialog target={target} open={open} onClose={() => setOpen(false)} />
    </>
  )
}
