import { Mic, MicOff, Phone, PhoneOff } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { canCall, pageId, type Caption, type useRoomCall } from './room-call'

type Call = ReturnType<typeof useRoomCall>

/** A caption as this reader reads it: translated when they read the room in another language, the original a click away. */
function CaptionLine({ c, name, mine, readIn }: { c: Caption; name: string; mine: boolean; readIn: string }) {
  const [original, setOriginal] = useState(false)
  const translated = !mine && readIn ? c.translations?.[readIn] : null
  return (
    <li className="text-sm">
      <span className="me-1.5 font-semibold">{name}:</span>
      <span dir="auto">{translated && !original ? translated : c.text}</span>
      {translated && (
        <button type="button" className="ms-1.5 text-xs text-muted-foreground underline-offset-2 hover:underline" onClick={() => setOriginal((o) => !o)}>
          {original ? 'Show the translation' : 'Show the original'}
        </button>
      )}
    </li>
  )
}

/**
 * Above a room's composer: its call. Who is in it (and who speaks now, and who is muted), joining and leaving, muting;
 * and its live captions, each in the reader's language.
 */
export function CallBar({ call, names, me, canSpeak, readIn }: { call: Call; names: (person: string) => string; me: string; canSpeak: boolean; readIn: string }) {
  if (!canCall()) return null
  const others = call.seats.filter((s) => s.page !== pageId)
  if (!call.joined && call.seats.length === 0) return null
  const people = [...new Set(call.seats.map((s) => s.person))]
  return (
    <section className="mb-2 grid gap-2 rounded-xl border bg-card px-3 py-2 shadow-sm" aria-label="The call">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span className="relative flex size-3 shrink-0" aria-hidden="true">
          {call.joined && call.speaking && <span className="absolute inline-flex size-full animate-ping rounded-full bg-success opacity-60" />}
          <span className={cn('relative inline-flex size-3 rounded-full', call.joined ? 'bg-success' : 'bg-muted-foreground')} />
        </span>
        <output className="min-w-0 flex-1" aria-label="In the call">
          {call.joined
            ? others.length === 0
              ? 'In the call: waiting for the others.'
              : `In the call with ${[...new Set(others.map((s) => s.person))].filter((p) => p !== me).map(names).join(', ') || 'yourself, on another page'}.`
            : `${people.map(names).join(', ')} ${people.length === 1 ? 'is' : 'are'} in a call.`}
        </output>
        <ul className="flex flex-wrap gap-1" aria-label="Muted">
          {call.seats.filter((s) => s.muted).map((s) => (
            <li key={s.page} className="inline-flex items-center gap-1 rounded-md bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">
              <MicOff className="size-3" aria-hidden="true" /> {s.page === pageId ? 'You' : names(s.person)}
            </li>
          ))}
        </ul>
        {call.joined ? (
          <>
            <Button type="button" variant="outline" size="sm" className="h-7 gap-1 px-2" aria-pressed={call.muted} onClick={() => call.mute(!call.muted)}>
              {call.muted ? <MicOff /> : <Mic />} {call.muted ? 'Unmute' : 'Mute'}
            </Button>
            <Button type="button" variant="destructive" size="sm" className="h-7 gap-1 px-2" onClick={call.leave}>
              <PhoneOff /> Leave
            </Button>
          </>
        ) : (
          canSpeak && (
            <Button type="button" size="sm" className="h-7 gap-1 px-2" loading={call.joining} onClick={() => void call.join()}>
              <Phone /> Join
            </Button>
          )
        )}
      </div>
      {call.joined && call.captions.length > 0 && (
        <ol className="grid max-h-28 gap-0.5 overflow-auto" aria-label="Captions" aria-live="polite">
          {call.captions.slice(-8).map((c) => (
            <CaptionLine key={c.id} c={c} name={c.by === me ? 'You' : names(c.by)} mine={c.by === me} readIn={readIn} />
          ))}
        </ol>
      )}
    </section>
  )
}

/** The room header's button that starts the room's call (or joins it). */
export function CallButton({ call, canSpeak }: { call: Call; canSpeak: boolean }) {
  if (!canCall() || !canSpeak || call.joined) return null
  return (
    <Button type="button" variant="ghost" size="sm" className="gap-1.5" loading={call.joining} onClick={() => void call.join()} aria-label={call.seats.length ? 'Join the call' : 'Start a call'}>
      <Phone /> <span className="hidden sm:inline">{call.seats.length ? 'Join the call' : 'Call'}</span>
    </Button>
  )
}
