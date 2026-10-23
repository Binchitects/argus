import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, CheckSquare, ListChecks, Loader2, MessageSquareText, Plus, RefreshCw, Search, Trash2, UserRound } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage, meQuery } from '@/lib/api'
import { cn } from '@/lib/utils'
import { sharingPeopleQuery } from '@/pages/chat/api'
import { Markdown } from '@/pages/chat/markdown'
import {
  addAction,
  askMeeting,
  changeAction,
  clock,
  deleteMeeting,
  makeAgain,
  meetingAudioUrl,
  meetingQuery,
  meetingsQuery,
  nameSpeaker,
  planActions,
  removeAction,
  renameMeeting,
  type ActionChange,
  type MeetingAction,
  type MeetingFull,
  type MeetingSpeaker,
} from './api'
import { MeetingState } from './meetings'

/** Each speaker's colour, in the order they first speak (then again). */
const voices = ['text-primary-ink', 'text-success-ink', 'text-warning-ink', 'text-destructive-ink', 'text-muted-foreground']
const voice = (speaker: number) => voices[speaker % voices.length]

/** Whose an action item is, as its select picks it: a speaker by number, someone else by name, or nobody. */
function OwnerPicker({ a, speakers, onChange }: { a: MeetingAction; speakers: MeetingSpeaker[]; onChange: (c: ActionChange) => void }) {
  const [other, setOther] = useState(a.speaker === null && a.given ? a.given : '')
  const [typing, setTyping] = useState(false)
  const value = a.speaker !== null ? `s${a.speaker}` : a.given ? 'other' : 'none'
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <select
        aria-label={`Whose: ${a.text}`}
        value={typing ? 'other' : value}
        onChange={(e) => {
          const v = e.target.value
          if (v === 'none') {
            setTyping(false)
            onChange({ owner: '' })
          } else if (v === 'other') setTyping(true)
          else {
            setTyping(false)
            onChange({ speaker: Number(v.slice(1)) })
          }
        }}
        className="h-7 max-w-40 rounded-md border bg-background px-1.5 text-xs"
      >
        <option value="none">Nobody yet</option>
        {speakers.map((s) => (
          <option key={s.speaker} value={`s${s.speaker}`}>
            {s.name}
          </option>
        ))}
        <option value="other">Someone else…</option>
      </select>
      {(typing || value === 'other') && (
        <Input
          aria-label={`Who, by name: ${a.text}`}
          value={other}
          onChange={(e) => setOther(e.target.value)}
          onBlur={() => other.trim() && other.trim() !== a.given && onChange({ owner: other.trim() })}
          className="h-7 w-32 text-xs"
          placeholder="Their name"
        />
      )}
    </span>
  )
}

/** A speaker: their name (as the meeting said, or as its owner puts it) and the person in the app they are, whose their action items become. */
function SpeakerRow({ s, onChange, me }: { s: MeetingSpeaker; onChange: (body: { name?: string; personId?: string | null }) => void; me: { id: string; name: string } | null }) {
  const [name, setName] = useState(s.name)
  const [find, setFind] = useState('')
  const people = useQuery(sharingPeopleQuery(find))
  return (
    <li className="grid gap-1.5 rounded-lg border p-2.5 text-sm" aria-label={s.name}>
      <div className="flex items-center gap-2">
        <UserRound className={cn('size-4 shrink-0', voice(s.speaker))} aria-hidden="true" />
        <Input
          aria-label={`Name of speaker ${s.speaker + 1}`}
          value={name}
          maxLength={100}
          onChange={(e) => setName(e.target.value)}
          onBlur={() => name.trim() && name.trim() !== s.name && onChange({ name: name.trim() })}
          className="h-7"
        />
      </div>
      {s.personId ? (
        <p className="flex items-center gap-2 text-xs text-muted-foreground">
          Their action items go to <span className="font-medium text-foreground">{s.person}</span>, reminded on the day due.
          <button type="button" className="underline-offset-2 hover:underline" onClick={() => onChange({ personId: null })}>
            Not them
          </button>
        </p>
      ) : (
        <div className="grid gap-1">
          <div className="flex flex-wrap items-center gap-1.5">
            <Input aria-label={`Who speaker ${s.speaker + 1} is in Arena`} value={find} onChange={(e) => setFind(e.target.value)} placeholder="Who they are here: a name" className="h-7 max-w-56 text-xs" />
            {me && (
              <Button size="sm" variant="ghost" className="h-7 text-xs" onClick={() => onChange({ personId: me.id })}>
                It is me
              </Button>
            )}
          </div>
          {find.trim().length >= 2 && (people.data?.length ?? 0) > 0 && (
            <ul className="flex flex-wrap gap-1" aria-label={`People named like ${find}`}>
              {people.data!.map((p) => (
                <li key={p.id}>
                  <button
                    type="button"
                    className="rounded-md border px-2 py-0.5 text-xs hover:bg-accent"
                    onClick={() => {
                      setFind('')
                      onChange({ personId: p.id })
                    }}
                  >
                    {p.name}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </li>
  )
}

/** One meeting: its minutes (summary, decisions, action items), its speakers, and its transcript, played from any line. */
export function MeetingPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const me = useQuery(meQuery).data
  const got = useQuery(meetingQuery(id))
  const audio = useRef<HTMLAudioElement>(null)
  const [at, setAt] = useState(0)
  const [title, setTitle] = useState<string | null>(null)
  const [newAction, setNewAction] = useState('')
  const [search, setSearch] = useState('')
  const set = (data: MeetingFull) => queryClient.setQueryData(meetingQuery(id).queryKey, data)
  const fail = (e: unknown) => toast.error(errorMessage(e))
  const change = useMutation({ mutationFn: ({ actionId, body }: { actionId: string; body: ActionChange }) => changeAction(id, actionId, body), onSuccess: set, onError: fail })
  const add = useMutation({ mutationFn: (text: string) => addAction(id, { text }), onSuccess: (d) => (set(d), setNewAction('')), onError: fail })
  const remove = useMutation({ mutationFn: (actionId: string) => removeAction(id, actionId), onSuccess: set, onError: fail })
  const speaker = useMutation({ mutationFn: ({ n, body }: { n: number; body: { name?: string; personId?: string | null } }) => nameSpeaker(id, n, body), onSuccess: set, onError: fail })
  const rename = useMutation({ mutationFn: (t: string) => renameMeeting(id, t), onSuccess: (d) => (set(d), setTitle(null), void queryClient.invalidateQueries({ queryKey: meetingsQuery.queryKey })), onError: fail })
  const again = useMutation({ mutationFn: () => makeAgain(id), onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['meetings'] }), onError: fail })
  const plan = useMutation({ mutationFn: () => planActions(id), onSuccess: (p) => navigate(`/plans/${p.id}`), onError: fail })
  const ask = useMutation({
    mutationFn: () => askMeeting(id),
    onSuccess: (chat) => navigate(`/chat/${chat.id}?ask=${encodeURIComponent(chat.message)}`, { state: { files: [chat.attachment] } }),
    onError: fail,
  })
  const drop = useMutation({
    mutationFn: () => deleteMeeting(id),
    onSuccess: () => (void queryClient.invalidateQueries({ queryKey: meetingsQuery.queryKey }), navigate('/meetings')),
    onError: fail,
  })
  // The line being played, kept in view.
  const playing = useRef<HTMLLIElement>(null)
  useEffect(() => {
    playing.current?.scrollIntoView?.({ block: 'nearest' })
  }, [at])

  if (got.isPending) return <PageSkeleton />
  if (got.error) return <QueryError error={got.error} retry={() => got.refetch()} />
  const d = got.data
  const m = d.meeting
  const names = new Map(d.speakers.map((s) => [s.speaker, s.name]))
  const current = d.transcript.findLastIndex((l) => l.start <= at)
  const shown = search.trim() ? d.transcript.filter((l) => l.text.toLowerCase().includes(search.trim().toLowerCase())) : d.transcript
  const playFrom = (seconds: number) => {
    if (!audio.current) return
    audio.current.currentTime = seconds
    void audio.current.play().catch(() => undefined)
  }
  return (
    <>
      <Button variant="ghost" size="sm" className="mb-2 -ml-2" asChild>
        <Link to="/meetings">
          <ArrowLeft /> Meetings
        </Link>
      </Button>
      <header className="mb-6 grid gap-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          {title === null ? (
            <h1 className="min-w-0 text-2xl font-semibold tracking-tight [overflow-wrap:anywhere]">
              <button type="button" className="text-left hover:underline" onClick={() => setTitle(m.title)} aria-label={`Rename ${m.title || 'the meeting'}`}>
                {m.title || 'A meeting'}
              </button>
            </h1>
          ) : (
            <form
              className="flex flex-wrap gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (title.trim()) rename.mutate(title.trim())
              }}
            >
              <Input value={title} onChange={(e) => setTitle(e.target.value)} aria-label="Title" maxLength={300} autoFocus className="w-80" />
              <Button type="submit" loading={rename.isPending}>
                Save
              </Button>
              <Button type="button" variant="ghost" onClick={() => setTitle(null)}>
                Cancel
              </Button>
            </form>
          )}
          <div className="flex flex-wrap gap-2">
            {m.status === 'ready' && (
              <Button variant="outline" onClick={() => ask.mutate()} loading={ask.isPending}>
                <MessageSquareText /> Ask about it
              </Button>
            )}
            {m.status === 'ready' && d.actions.length > 0 && (
              <Button variant="outline" onClick={() => plan.mutate()} loading={plan.isPending}>
                <ListChecks /> {d.planId ? 'Make the plan again' : 'Make a plan'}
              </Button>
            )}
            {m.status !== 'processing' && (
              <Button variant="outline" onClick={() => again.mutate()} loading={again.isPending}>
                <RefreshCw /> Make again
              </Button>
            )}
            <Button
              variant="outline"
              aria-label="Delete the meeting"
              onClick={async () => {
                if (await confirm({ title: `Delete ${m.title || 'this meeting'}?`, description: 'Its recording, transcript and minutes go.', confirm: 'Delete', destructive: true })) drop.mutate()
              }}
            >
              <Trash2 />
            </Button>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
          <MeetingState m={m} />
          {m.seconds ? <span>{clock(m.seconds)} long</span> : null}
          {d.model && <span>Minutes by {d.model}</span>}
        </div>
        {m.error && <Alert variant={m.status === 'failed' ? 'destructive' : 'warning'}>{m.error}</Alert>}
        {/* oxlint-disable-next-line jsx-a11y/media-has-caption -- its transcript is beside it, line by line as it plays */}
        <audio ref={audio} controls preload="metadata" src={meetingAudioUrl(id)} onTimeUpdate={(e) => setAt(e.currentTarget.currentTime)} className="w-full max-w-2xl" aria-label="The recording" />
      </header>

      {m.status === 'processing' ? (
        <div className="flex items-center gap-2 text-sm text-muted-foreground" aria-live="polite">
          <Loader2 className="size-4 animate-spin" aria-hidden="true" /> {m.step ?? 'Waiting its turn'}…
        </div>
      ) : (
        <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <div className="grid content-start gap-6">
            {d.summary && (
              <Card aria-label="Summary">
                <CardHeader>
                  <CardTitle className="text-base">Summary</CardTitle>
                </CardHeader>
                <CardContent className="text-sm">
                  <Markdown text={d.summary} />
                </CardContent>
              </Card>
            )}
            {d.decisions.length > 0 && (
              <Card aria-label="Decisions">
                <CardHeader>
                  <CardTitle className="text-base">Decisions</CardTitle>
                </CardHeader>
                <CardContent>
                  <ul className="grid list-disc gap-1 ps-5 text-sm">
                    {d.decisions.map((x, i) => (
                      <li key={i}>{x}</li>
                    ))}
                  </ul>
                </CardContent>
              </Card>
            )}
            <Card aria-label="Action items">
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-base">
                  <CheckSquare className="size-4 text-primary-ink" aria-hidden="true" /> Action items
                </CardTitle>
                <CardDescription>Whose each is, and by when. Tell below which person a speaker is: their items go to them, reminded on the day due.</CardDescription>
              </CardHeader>
              <CardContent className="grid gap-3">
                <ul className="grid gap-2" aria-label="The action items">
                  {d.actions.map((a) => (
                    <li key={a.id} className="grid gap-1.5 rounded-lg border p-2.5 text-sm" aria-label={a.text}>
                      <label className="flex items-start gap-2">
                        <input
                          type="checkbox"
                          className="mt-0.5 size-4 accent-primary"
                          checked={a.done}
                          onChange={(e) => change.mutate({ actionId: a.id, body: { done: e.target.checked } })}
                        />
                        <span className={cn('min-w-0 [overflow-wrap:anywhere]', a.done && 'text-muted-foreground line-through')}>{a.text}</span>
                      </label>
                      <div className="flex flex-wrap items-center gap-2 ps-6">
                        <OwnerPicker a={a} speakers={d.speakers} onChange={(body) => change.mutate({ actionId: a.id, body })} />
                        <input
                          type="date"
                          aria-label={`Due: ${a.text}`}
                          value={a.due ?? ''}
                          onChange={(e) => change.mutate({ actionId: a.id, body: e.target.value ? { due: e.target.value } : { clearDue: true } })}
                          className="h-7 rounded-md border bg-background px-1.5 text-xs"
                        />
                        {a.person && <span className="text-xs text-muted-foreground">→ {a.person}</span>}
                        <Button size="icon-sm" variant="ghost" className="ml-auto" aria-label={`Remove ${a.text}`} onClick={() => remove.mutate(a.id)}>
                          <Trash2 />
                        </Button>
                      </div>
                    </li>
                  ))}
                </ul>
                <form
                  className="flex gap-2"
                  onSubmit={(e) => {
                    e.preventDefault()
                    if (newAction.trim()) add.mutate(newAction.trim())
                  }}
                >
                  <Input value={newAction} onChange={(e) => setNewAction(e.target.value)} placeholder="Another action item" aria-label="Another action item" maxLength={1000} />
                  <Button type="submit" variant="outline" loading={add.isPending} disabled={!newAction.trim()}>
                    <Plus /> Add
                  </Button>
                </form>
              </CardContent>
            </Card>
            {d.speakers.length > 0 && (
              <Card aria-label="Speakers">
                <CardHeader>
                  <CardTitle className="text-base">Speakers</CardTitle>
                  <CardDescription>Told apart by their voices; named as the meeting named them, or as you name them.</CardDescription>
                </CardHeader>
                <CardContent>
                  <ul className="grid gap-2">
                    {d.speakers.map((s) => (
                      <SpeakerRow key={`${s.speaker}:${s.name}:${s.personId}`} s={s} me={me ? { id: me.id, name: me.displayName || me.userName } : null} onChange={(body) => speaker.mutate({ n: s.speaker, body })} />
                    ))}
                  </ul>
                </CardContent>
              </Card>
            )}
          </div>
          <Card aria-label="Transcript" className="xl:sticky xl:top-4 xl:max-h-[calc(100dvh-2rem)] xl:overflow-hidden">
            <CardHeader>
              <CardTitle className="text-base">Transcript</CardTitle>
              <div className="relative max-w-xs">
                <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
                <Input className="h-8 ps-8" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Find in what was said" aria-label="Find in the transcript" />
              </div>
            </CardHeader>
            <CardContent className="xl:max-h-[calc(100dvh-12rem)] xl:overflow-auto">
              <ol className="grid gap-1 text-sm" aria-label="What was said">
                {shown.map((l) => {
                  const i = d.transcript.indexOf(l)
                  return (
                    <li key={i} ref={i === current ? playing : undefined} className={cn('rounded-md px-2 py-1', i === current && 'bg-accent')}>
                      <button type="button" className="grid w-full gap-0.5 text-left" onClick={() => playFrom(l.start)} aria-label={`Play from ${clock(l.start)}: ${names.get(l.speaker) ?? `Speaker ${l.speaker + 1}`}`}>
                        <span className="flex items-center gap-2 text-xs">
                          <span className={cn('font-semibold', voice(l.speaker))}>{names.get(l.speaker) ?? `Speaker ${l.speaker + 1}`}</span>
                          <span className="font-mono text-muted-foreground">{clock(l.start)}</span>
                        </span>
                        <span dir="auto">{l.text}</span>
                      </button>
                    </li>
                  )
                })}
              </ol>
            </CardContent>
          </Card>
        </div>
      )}
    </>
  )
}
