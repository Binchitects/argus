import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, Eraser, ShieldOff } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { DATA_REQUEST_KINDS, dataRequestLabel, dataRequestsQuery, type DataRequest, type DataRequestKind } from './compliance-api'

/** Where a request stands: answered, refused, late (past its month) or open. */
export function RequestStatus({ r, now = new Date() }: { r: DataRequest; now?: Date }) {
  if (r.status === 'done') return <Badge variant="success">Answered</Badge>
  if (r.status === 'refused') return <Badge variant="secondary">Refused</Badge>
  const late = new Date(r.dueAt) < now
  return <Badge variant={late ? 'destructive' : 'warning'}>{late ? 'Late' : `Due ${new Date(r.dueAt).toLocaleDateString()}`}</Badge>
}

/** What to say, for an answer or a refusal. */
function SayDialog({ title, description, label, action, onSay, onClose }: { title: string; description: string; label: string; action: string; onSay: (text: string) => Promise<unknown>; onClose: () => void }) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault()
            setBusy(true)
            try {
              await onSay(text)
              onClose()
            } finally {
              setBusy(false)
            }
          }}
        >
          <Field label={label}>
            <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} maxLength={4000} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={busy} disabled={text.trim().length === 0}>
              {action}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** One request, and what an admin with privacy.manage can do about it. */
function RequestRow({ r, mayAnswer }: { r: DataRequest; mayAnswer: boolean }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [saying, setSaying] = useState<'complete' | 'refuse' | null>(null)
  const done = () => queryClient.invalidateQueries({ queryKey: ['admin'] })
  const act = useMutation({
    mutationFn: ({ what, outcome }: { what: 'complete' | 'refuse' | 'restrict' | 'erase'; outcome?: string }) =>
      api<DataRequest>(`/api/admin/compliance/data-requests/${r.id}/${what}`, { method: 'POST', body: outcome === undefined ? {} : { outcome } }),
    onSuccess: (answered) => {
      toast.success(answered.status === 'refused' ? 'Refused: they are told why' : 'Answered: they are told')
      void done()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const open = r.status === 'open'
  return (
    <li className="grid gap-2 p-3 text-sm">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="font-medium">{r.person}</span>
        <span>{dataRequestLabel(r.kind)}</span>
        <RequestStatus r={r} />
        <span className="text-xs text-muted-foreground">
          taken in{' '}
          <time dateTime={r.receivedAt} title={when(r.receivedAt)}>
            {ago(r.receivedAt)}
          </time>{' '}
          {r.receivedBy === 'themselves' ? 'from them' : `by ${r.receivedBy}`}
        </span>
      </div>
      {r.details && <p className="text-muted-foreground">“{r.details}”</p>}
      {r.outcome && (
        <p>
          <span className="text-muted-foreground">{r.status === 'refused' ? 'Refused' : 'Answered'} by {r.doneBy}: </span>
          {r.outcome}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        {r.certificate && (
          <Button asChild size="sm" variant="outline">
            <a href={`/api/admin/compliance/data-requests/${r.id}/certificate`} download={`erasure-${r.person}.md`}>
              <Download /> Certificate
            </a>
          </Button>
        )}
        {open && mayAnswer && (
          <>
            {(r.kind === 'restriction' || r.kind === 'objection') && (
              <Button size="sm" onClick={() => act.mutate({ what: 'restrict' })} loading={act.isPending}>
                <ShieldOff /> Restrict their processing
              </Button>
            )}
            {r.kind === 'erasure' && (
              <Button
                size="sm"
                variant="destructive"
                loading={act.isPending}
                onClick={async () => {
                  if (
                    await confirm({
                      title: `Erase everything of ${r.person}?`,
                      description: 'Their account, chats, files, memories, skills, meetings, media and the rest, now and for good; their words in others’ rooms are erased too. The audit log keeps naming them, as the law requires.',
                      confirm: 'Erase everything',
                      destructive: true,
                    })
                  )
                    act.mutate({ what: 'erase' })
                }}
              >
                <Eraser /> Erase everything
              </Button>
            )}
            {r.kind !== 'erasure' && (
              <Button size="sm" variant="outline" onClick={() => setSaying('complete')}>
                Answered…
              </Button>
            )}
            <Button size="sm" variant="ghost" onClick={() => setSaying('refuse')}>
              Refuse…
            </Button>
          </>
        )}
      </div>
      {saying && (
        <SayDialog
          title={saying === 'complete' ? `Answer ${r.person}'s request` : `Refuse ${r.person}'s request`}
          description={saying === 'complete' ? 'What was done: they are told, and it is kept with the request.' : 'Why the law lets it be refused: they are told, and may complain to the supervisory authority.'}
          label={saying === 'complete' ? 'What was done' : 'Why'}
          action={saying === 'complete' ? 'Answer it' : 'Refuse it'}
          onSay={(outcome) => act.mutateAsync({ what: saying, outcome })}
          onClose={() => setSaying(null)}
        />
      )}
    </li>
  )
}

/** Admin → Compliance: every data request, the open ones first by their due date. */
export function DataRequestsSection({ mayAnswer }: { mayAnswer: boolean }) {
  const requests = useQuery(dataRequestsQuery)
  return (
    <section id="data-requests" aria-labelledby="data-requests-title" className="grid scroll-mt-20 gap-3">
      <div>
        <h2 id="data-requests-title" className="text-lg font-semibold">
          Data requests
        </h2>
        <p className="text-sm text-muted-foreground">People’s requests about their data, each answered within a month (GDPR 12–22). They ask from Your account; take one in for someone on their page under Admin → People.</p>
      </div>
      {requests.data?.length === 0 && <p className="text-sm text-muted-foreground">None yet.</p>}
      {(requests.data?.length ?? 0) > 0 && (
        <ul className="divide-y rounded-lg border bg-card">
          {requests.data!.map((r) => (
            <RequestRow key={r.id} r={r} mayAnswer={mayAnswer} />
          ))}
        </ul>
      )}
    </section>
  )
}

/** On a person's page: their requests, and one taken in for them (asked by email or in person). */
export function PersonDataRequests({ person }: { person: { id: string; userName: string } }) {
  const queryClient = useQueryClient()
  const requests = useQuery(dataRequestsQuery)
  const [kind, setKind] = useState<DataRequestKind>('access')
  const [details, setDetails] = useState('')
  const take = useMutation({
    mutationFn: () => api<DataRequest>('/api/admin/compliance/data-requests', { method: 'POST', body: { userId: person.id, kind, details } }),
    onSuccess: () => {
      toast.success('Taken in: it is due in a month')
      setDetails('')
      void queryClient.invalidateQueries({ queryKey: dataRequestsQuery.queryKey })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const theirs = (requests.data ?? []).filter((r) => r.userId === person.id)
  return (
    <Card>
      <CardHeader>
        <CardTitle>Their data requests</CardTitle>
        <CardDescription>A request {person.userName} made to you some other way (by email, in person): taken in here, it is answered from Admin → Compliance within a month.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {theirs.length > 0 && (
          <ul className="divide-y rounded-lg border">
            {theirs.map((r) => (
              <RequestRow key={r.id} r={r} mayAnswer />
            ))}
          </ul>
        )}
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            take.mutate()
          }}
        >
          <Field label="What they ask for">
            <Select value={kind} onValueChange={(v) => setKind(v as DataRequestKind)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {DATA_REQUEST_KINDS.map((k) => (
                  <SelectItem key={k.kind} value={k.kind}>
                    {k.label.replace('my data', 'their data').replace('My data', 'Their data')}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="In their words">
            <Textarea value={details} onChange={(e) => setDetails(e.target.value)} rows={3} maxLength={4000} />
          </Field>
          <div>
            <Button type="submit" variant="outline" loading={take.isPending}>
              Take it in
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
