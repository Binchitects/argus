import { AudioLines, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { canTalk, type TalkState } from './use-talk'

const words: Record<Exclude<TalkState, 'off'>, string> = {
  starting: 'Starting the microphone…',
  listening: 'Listening: speak when you are ready.',
  hearing: 'Hearing you…',
  transcribing: 'Writing down what you said…',
  thinking: 'Thinking… speak to stop it.',
  speaking: 'Speaking… speak over it to stop it.',
}

/** The composer's button that starts a voice conversation, and ends it. */
export function TalkButton({ state, onStart, onEnd }: { state: TalkState; onStart: () => void; onEnd: () => void }) {
  if (!canTalk()) return null
  const on = state !== 'off'
  return (
    <Tooltip content={on ? 'End the voice conversation (Esc)' : 'Talk: a voice conversation. Speak, and the answer is read aloud as it is written; speak over it to stop it.'}>
      <Button
        type="button"
        variant={on ? 'secondary' : 'ghost'}
        size="icon-sm"
        className={cn(on && 'text-primary-ink')}
        aria-pressed={on}
        aria-label="Talk"
        onClick={on ? onEnd : onStart}
      >
        <AudioLines />
      </Button>
    </Tooltip>
  )
}

/** Above the composer while Talk is on: what it is doing, and the way out. */
export function TalkBar({ state, onEnd }: { state: TalkState; onEnd: () => void }) {
  if (state === 'off') return null
  const active = state === 'hearing' || state === 'speaking'
  return (
    <div className="mb-2 flex items-center gap-3 rounded-xl border bg-card px-3 py-2 text-sm shadow-sm" data-talk={state}>
      <span className="relative flex size-3 shrink-0" aria-hidden="true">
        {active && <span className="absolute inline-flex size-full animate-ping rounded-full bg-primary opacity-60" />}
        <span className={cn('relative inline-flex size-3 rounded-full', state === 'listening' || state === 'starting' ? 'bg-muted-foreground' : 'bg-primary')} />
      </span>
      <output className="min-w-0 flex-1" aria-live="polite" aria-label="Talk">
        {words[state]}
      </output>
      <span className="hidden text-xs text-muted-foreground lg:inline">Headphones keep the answer out of the microphone.</span>
      <Button type="button" variant="outline" size="sm" className="h-7 gap-1 px-2" onClick={onEnd}>
        <X /> End talk
      </Button>
    </div>
  )
}
