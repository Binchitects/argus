import { useQuery } from '@tanstack/react-query'
import { ListChecks, Pencil } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { api, errorMessage } from '@/lib/api'
import { languageName, voiceLabel, voicePairs, voiceValue, type SpeechOffer } from '@/lib/voice'
import { TryIt } from '../account-voice'

const offerQuery = {
  queryKey: ['admin', 'speech', 'voices'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SpeechOffer>('/api/admin/speech/voices', { signal }),
  staleTime: 60_000,
}

/** A language left to the first voice offered for it (a select's item cannot have an empty value). */
const first = '__first__'

const modelOf = (id: string) => id.slice(0, id.indexOf('/'))

interface Props {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  /** The reading speed on the page, saved or not; none: the company's. */
  speed?: number
  describedBy: string
  invalid: boolean
}

/**
 * Settings → Speech → Voice for each language: a row for each language a voice reads (and each the value names), its
 * voice chosen from those the speech models offer, and Try it at the speed on the page. The value stays the setting's
 * language:model/voice pairs, which can be edited as text too (and are, while the speech server lists no voice).
 */
export function SpeechVoices({ id, label, value, onChange, speed, describedBy, invalid }: Props) {
  const offer = useQuery(offerQuery)
  const [asText, setAsText] = useState(false)
  if (offer.isPending) return <Skeleton className="h-24" />
  const voices = offer.data?.voices ?? []
  const canPick = !!offer.data?.known && voices.length > 0
  if (asText || !canPick) {
    return (
      <div className="grid gap-1.5">
        <Input
          id={id}
          aria-describedby={describedBy}
          aria-invalid={invalid || undefined}
          spellCheck={false}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="en:kokoro/af_heart,fa:piper-fa/gyro"
          className="font-mono text-xs"
        />
        {offer.error && <p className="text-xs text-muted-foreground">The voices offered cannot be listed: {errorMessage(offer.error)}</p>}
        {offer.data && !offer.data.known && (
          <p className="text-xs text-muted-foreground">The speech server cannot be asked right now: type each language's voice as language:model/voice.</p>
        )}
        {offer.data?.known && voices.length === 0 && (
          <p className="text-xs text-muted-foreground">
            {offer.data.models.length > 0
              ? 'The speech server lists no voice yet (its models are still downloading): type each language’s voice as language:model/voice.'
              : 'No voice is offered here: the gateway has no text to speech model (the audio module).'}
          </p>
        )}
        {canPick && (
          <div>
            <Button variant="link" size="sm" className="h-auto px-0 text-xs" onClick={() => setAsText(false)}>
              <ListChecks /> Choose from the voices offered
            </Button>
          </div>
        )}
      </div>
    )
  }

  const pairs = voicePairs(value)
  const chosen = new Map(pairs)
  const models = offer.data!.models
  const languages = [...new Set([...voices.map((v) => v.language), ...chosen.keys()])].sort((a, b) => languageName(a).localeCompare(languageName(b)))
  const choose = (language: string, voice: string | null) => {
    let next: [string, string][] =
      voice === null
        ? pairs.filter(([l]) => l !== language)
        : chosen.has(language)
          ? pairs.map(([l, v]) => [l, l === language ? voice : v])
          : [...pairs, [language, voice]]
    // The setting names one voice at least: the last language left to the first offered names that voice.
    const firstOffered = voices.find((v) => v.language === language)
    if (next.length === 0 && firstOffered) next = [[language, firstOffered.id]]
    onChange(voiceValue(next))
  }
  return (
    <fieldset id={id} aria-describedby={describedBy} aria-invalid={invalid || undefined} className="grid gap-2">
      <legend className="sr-only">{label}</legend>
      {languages.map((language) => {
        const name = languageName(language)
        const offered = voices.filter((v) => v.language === language)
        const current = chosen.get(language)
        const listed = offered.find((v) => v.id === current)
        // A voice of a model the speech server has not listed yet (still downloading): used as named.
        const believed = !!current && !listed && models.includes(modelOf(current)) && !voices.some((v) => v.model === modelOf(current))
        const reads = listed?.id ?? (believed ? current : offered[0]?.id)
        return (
          <div key={language} className="grid grid-cols-[minmax(0,6rem)_minmax(0,1fr)_auto] items-center gap-x-2 gap-y-0.5">
            <span className="truncate text-sm">{name}</span>
            <Select value={current ?? first} onValueChange={(v) => choose(language, v === first ? null : v)}>
              <SelectTrigger aria-label={`Voice for ${name}`}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={first}>The first offered: {offered[0] ? voiceLabel(offered[0]) : 'none'}</SelectItem>
                {current && !listed && (
                  <SelectItem value={current}>
                    {current} ({believed ? 'its model is not listed yet' : 'not offered'})
                  </SelectItem>
                )}
                {offered.map((v) => (
                  <SelectItem key={v.id} value={v.id}>
                    {voiceLabel(v)} <span className="font-mono text-xs text-muted-foreground">{v.id}</span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {reads ? <TryIt voice={reads} language={language} label={name} speed={speed} path="/api/admin/speech/try" /> : <span />}
            {current && !listed && !believed && (
              <span className="col-span-full text-xs break-words text-muted-foreground sm:col-span-2 sm:col-start-2">
                {`${current} is not offered: ${reads ? `${reads} reads ${name}` : `no voice reads ${name}`}`}
              </span>
            )}
          </div>
        )
      })}
      <div>
        <Button variant="link" size="sm" className="h-auto px-0 text-xs" onClick={() => setAsText(true)}>
          <Pencil /> Edit as text
        </Button>
      </div>
    </fieldset>
  )
}
