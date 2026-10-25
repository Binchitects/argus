import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Users } from 'lucide-react'
import { useId, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { assistantsQuery } from './api'
import { Markdown } from './markdown'

interface PanelState {
  id: string
  question: string
  status: 'working' | 'done' | 'failed'
  rounds: number
  verdict: string | null
  error: string | null
  panelists: { name: string }[]
  turns: { round: number; speaker: string; text: string }[]
}

/** A seat: a role by its name, or an assistant by its id. */
type Seat = { role: string } | { assistantId: string; name: string }

const seatKey = (s: Seat) => ('role' in s ? `role:${s.role}` : `assistant:${s.assistantId}`)

function Setup({ chatId, ensureChat, onStarted }: { chatId: string | undefined; ensureChat?: () => Promise<string | null>; onStarted: (panel: { id: string; chat: string }) => void }) {
  const id = useId()
  const roles = useQuery({ queryKey: ['panels', 'roles'], queryFn: ({ signal }) => api<{ name: string; role: string }[]>('/api/panels/roles', { signal }) })
  const assistants = useQuery(assistantsQuery)
  const [question, setQuestion] = useState('')
  const [seats, setSeats] = useState<Seat[]>([{ role: 'The expert' }, { role: 'The critic' }, { role: 'The one who says no' }])
  const [rounds, setRounds] = useState('2')
  const start = useMutation({
    mutationFn: async () => {
      // In a new chat, the chat is made first: the panel goes into it.
      const chat = chatId ?? (await ensureChat?.())
      if (!chat) throw new Error('The chat could not be made: try again.')
      const made = await api<{ id: string }>(`/api/chat/conversations/${chat}/panel`, {
        body: { question: question.trim(), rounds: Number(rounds), panelists: seats.map((s) => ('role' in s ? { role: s.role } : { assistantId: s.assistantId })) },
      })
      return { id: made.id, chat }
    },
    onSuccess: onStarted,
  })
  const toggle = (seat: Seat, on: boolean) => setSeats(on ? [...seats, seat] : seats.filter((s) => seatKey(s) !== seatKey(seat)))
  const seated = (seat: Seat) => seats.some((s) => seatKey(s) === seatKey(seat))
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        start.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>Ask a panel</DialogTitle>
        <DialogDescription>Assistants and roles discuss it in turns, each from their side; a moderator writes the verdict with who said what. It all goes into the chat.</DialogDescription>
      </DialogHeader>
      {start.error && <Alert variant="destructive">{errorMessage(start.error)}</Alert>}
      <Field label="The question">
        <Textarea rows={3} maxLength={4000} value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="Should we move the release to Thursday?" />
      </Field>
      <fieldset className="grid gap-1.5">
        <legend className="mb-1 text-sm font-medium">Who sits on it ({seats.length}, 2 to 5)</legend>
        <div className="grid gap-1 sm:grid-cols-2">
          {(roles.data ?? []).map((r) => (
            <div key={r.name} className="flex items-start gap-2 text-sm">
              <Checkbox id={`${id}-${r.name}`} checked={seated({ role: r.name })} onCheckedChange={(on) => toggle({ role: r.name }, !!on)} className="mt-0.5" />
              <label htmlFor={`${id}-${r.name}`}>
                {r.name}
                <span className="block text-xs text-muted-foreground">{r.role}</span>
              </label>
            </div>
          ))}
          {(assistants.data ?? []).slice(0, 12).map((a) => (
            <div key={a.id} className="flex items-start gap-2 text-sm">
              <Checkbox id={`${id}-${a.id}`} checked={seated({ assistantId: a.id, name: a.name })} onCheckedChange={(on) => toggle({ assistantId: a.id, name: a.name }, !!on)} className="mt-0.5" />
              <label htmlFor={`${id}-${a.id}`}>
                {a.name}
                <span className="block text-xs text-muted-foreground">An assistant{a.description ? `: ${a.description}` : ''}</span>
              </label>
            </div>
          ))}
        </div>
      </fieldset>
      <Segmented label="Rounds" value={rounds} onChange={setRounds} options={[{ value: '1', label: 'One round' }, { value: '2', label: 'Two rounds' }, { value: '3', label: 'Three rounds' }]} />
      <DialogFooter>
        <Button type="submit" loading={start.isPending} disabled={!question.trim() || seats.length < 2 || seats.length > 5}>
          <Users /> Ask the panel
        </Button>
      </DialogFooter>
    </form>
  )
}

/** The panel speaking, turn by turn, as it goes; then its verdict. */
function Live({ panelId, chatId, onClose }: { panelId: string; chatId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const panel = useQuery({
    queryKey: ['panels', panelId],
    queryFn: async ({ signal }) => {
      const p = await api<PanelState>(`/api/panels/${panelId}`, { signal })
      if (p.status === 'done') void queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', chatId] })
      return p
    },
    refetchInterval: (q) => (q.state.data?.status === 'working' || !q.state.data ? 1500 : false),
  })
  const p = panel.data
  if (!p) return <Spinner label="Seating the panel" />
  const next = p.status === 'working' ? (p.panelists[p.turns.length % p.panelists.length]?.name ?? 'The moderator') : null
  return (
    <div className="grid gap-3">
      <DialogHeader>
        <DialogTitle>The panel</DialogTitle>
        <DialogDescription>{p.question}</DialogDescription>
      </DialogHeader>
      {/* What was said and the verdict scroll together: the footer stays in view however long they are. */}
      <div className="grid max-h-[60vh] gap-2 overflow-y-auto pe-1">
        <ol className="grid gap-2" aria-label="What the panel said">
          {p.turns.map((t, i) => (
            <li key={i} className="rounded-lg border p-2 text-sm">
              <span className="font-medium">{t.speaker}</span> <span className="text-xs text-muted-foreground">round {t.round}</span>
              <p className="mt-0.5 whitespace-pre-wrap">{t.text}</p>
            </li>
          ))}
        </ol>
        {next && (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner label="Speaking" /> {p.turns.length >= p.panelists.length * p.rounds ? 'The moderator' : next} is speaking…
          </p>
        )}
        {p.error && <Alert variant="destructive">{p.error}</Alert>}
        {p.verdict && (
          <section aria-label="The verdict" className="rounded-xl border bg-muted/40 p-3 text-sm">
            <Markdown text={p.verdict} />
          </section>
        )}
      </div>
      <DialogFooter>
        <Button onClick={onClose}>{p.status === 'done' ? 'Done: it is in the chat' : 'Close (it goes on)'}</Button>
      </DialogFooter>
    </div>
  )
}

/** The chat's Panel button: a question for several assistants and roles to discuss (in a new chat, the chat is made for it). */
export function PanelButton({ chatId, ensureChat }: { chatId: string | undefined; ensureChat?: () => Promise<string | null> }) {
  const [open, setOpen] = useState(false)
  const [panel, setPanel] = useState<{ id: string; chat: string } | null>(null)
  return (
    <>
      <Tooltip content="Ask a panel: assistants and roles discuss it, and a moderator gives the verdict">
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Ask a panel" onClick={() => setOpen(true)}>
          <Users />
        </Button>
      </Tooltip>
      <Dialog
        open={open}
        onOpenChange={(o) => {
          setOpen(o)
          if (!o) setPanel(null)
        }}
      >
        <DialogContent className="sm:max-w-2xl">
          {open && (panel ? <Live panelId={panel.id} chatId={panel.chat} onClose={() => (setOpen(false), setPanel(null))} /> : <Setup chatId={chatId} ensureChat={ensureChat} onStarted={setPanel} />)}
        </DialogContent>
      </Dialog>
    </>
  )
}
