import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Play, RotateCcw, Square } from 'lucide-react'
import { useEffect, useId, useRef, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { languageName, readsAloud, voiceDetail, voiceName, voiceQuery, type OfferedVoice, type VoiceChange, type VoiceSettings } from '@/lib/voice'
import { play } from './chat/sound'

/** The choice that leaves it to the company (a select's item cannot have an empty value). */
const theirs = 'company'

/** Every choice back to the company's. */
const nothing: VoiceChange = { language: null, voices: null, speed: null, readAloud: null }

const times = (n: number) => `${Number(n.toFixed(2))}×`

const describe = (v: OfferedVoice) => (voiceDetail(v) ? `${voiceName(v)} (${voiceDetail(v)})` : voiceName(v))

const spoken = (code: string) => (code === 'auto' ? 'Detect it' : languageName(code))

/**
 * How the chat hears the person and reads to them: the language they speak (for speech to text), the voice that reads
 * each language, the speed, and whether Talk reads its answers aloud. Each is theirs or the company's; Try it reads a sample.
 */
export function VoiceSection() {
  const queryClient = useQueryClient()
  const voice = useQuery(voiceQuery)
  const save = useMutation({
    // Only what changed is sent, one change after another: each answer is the choices with every change before it.
    mutationFn: (change: VoiceChange) => api<VoiceSettings>('/api/account/voice', { method: 'PATCH', body: change }),
    scope: { id: 'account-voice' },
    onSuccess: (v) => {
      queryClient.setQueryData(voiceQuery.queryKey, v)
      toast.success('Saved')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const v = voice.data
  const mine = v?.chosen
  const change = (next: VoiceChange) => save.mutate(next)
  const chosenAny = !!mine && (mine.language !== null || Object.keys(mine.voices).length > 0 || mine.speed !== null || mine.readAloud !== null)
  return (
    <Card id="voice">
      <CardHeader>
        <CardTitle>Voice</CardTitle>
        <CardDescription>How the chat hears you and reads to you: read aloud, Talk, voice messages, and your API key's speech when it names no voice.</CardDescription>
        {chosenAny && (
          <CardAction>
            <Button variant="outline" size="sm" disabled={save.isPending} onClick={() => save.mutate(nothing)}>
              <RotateCcw /> Use the company's
            </Button>
          </CardAction>
        )}
      </CardHeader>
      <CardContent className="grid gap-5">
        {voice.isPending && <Skeleton className="h-40" />}
        {voice.error && <Alert variant="destructive">{errorMessage(voice.error)}</Alert>}
        {v && mine && (
          <>
            <Field
              label="The language you speak"
              hint={
                v.hears
                  ? 'For writing down what you say in Talk and voice messages. Detect it suits most people; naming it helps short or accented speech.'
                  : 'Speech to text is not set up here (the audio module).'
              }
            >
              <Select value={mine.language ?? theirs} onValueChange={(l) => change({ language: l === theirs ? null : l })} disabled={!v.hears || save.isPending}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={theirs}>The company's: {spoken(v.company.language)}</SelectItem>
                  <SelectItem value="auto">Detect it</SelectItem>
                  {[...v.languages]
                    .sort((a, b) => languageName(a).localeCompare(languageName(b)))
                    .map((l) => (
                      <SelectItem key={l} value={l}>
                        {languageName(l)}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </Field>
            <Voices settings={v} disabled={save.isPending} onChange={(language, id) => change({ voices: { [language]: id } })} />
            <Speed key={mine.speed ?? v.company.speed} settings={v} onChange={(speed) => change({ speed })} />
            <ReadAloud settings={v} disabled={save.isPending} onChange={(readAloud) => change({ readAloud })} />
          </>
        )}
      </CardContent>
    </Card>
  )
}

/** The speed the person reads at now: theirs, else the company's. */
const speedOf = (v: VoiceSettings) => v.chosen.speed ?? v.company.speed

/** The language the person speaks now (theirs, else the company's); null when it is detected. */
const speaks = (v: VoiceSettings) => {
  const language = v.chosen.language ?? v.company.language
  return language === 'auto' ? null : language
}

/**
 * A row for each language a voice reads (and each the person chose a voice for that is not offered now): the voice
 * that reads it, its id (as Settings → Speech names it), and Try it. The language they speak comes first.
 */
function Voices({ settings: v, disabled, onChange }: { settings: VoiceSettings; disabled: boolean; onChange: (language: string, id: string | null) => void }) {
  const first = speaks(v)
  const languages = [...new Set([...v.voices.map((x) => x.language), ...Object.keys(v.chosen.voices)])].sort((a, b) =>
    a === first ? -1 : b === first ? 1 : languageName(a).localeCompare(languageName(b)),
  )
  return (
    <div className="grid gap-2">
      <div>
        <p className="text-sm font-medium">Voices</p>
        <p className="text-xs text-muted-foreground">A text is read in the voice of its language: Persian in the Persian voice, whatever language you speak.</p>
      </div>
      {languages.length === 0 && <Alert>Reading aloud is not set up here: the gateway has no text to speech model (the audio module).</Alert>}
      {!v.known && languages.length > 0 && (
        <p className="text-xs text-muted-foreground">The speech server cannot be asked right now: only the voices already chosen are listed.</p>
      )}
      {languages.map((language) => {
        const label = languageName(language)
        const offered = v.voices.filter((x) => x.language === language)
        const company = offered.find((x) => x.id === v.company.voices[language])
        const chosen = v.chosen.voices[language]
        // A voice of theirs the speech models no longer offer: kept, and the company's reads until it is offered again.
        const gone = chosen !== undefined && !offered.some((x) => x.id === chosen)
        const reads = gone ? company : (offered.find((x) => x.id === chosen) ?? company)
        return (
          <div key={language} className="grid grid-cols-[minmax(0,6rem)_minmax(0,1fr)_auto] items-center gap-x-2 gap-y-0.5">
            <span className="truncate text-sm">{label}</span>
            <Select value={chosen ?? theirs} onValueChange={(id) => onChange(language, id === theirs ? null : id)} disabled={disabled}>
              <SelectTrigger aria-label={`Voice for ${label}`}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={theirs}>The company's: {company ? describe(company) : 'none'}</SelectItem>
                {gone && <SelectItem value={chosen}>{chosen} (not offered now)</SelectItem>}
                {offered.map((x) => (
                  <SelectItem key={x.id} value={x.id}>
                    {describe(x)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {reads ? <TryIt voice={reads.id} language={language} label={label} speed={speedOf(v)} /> : <span />}
            <span className="col-start-2 truncate font-mono text-xs text-muted-foreground" title="The voice's id, as Settings → Speech names it">
              {gone ? `${chosen} is not offered now: ${reads ? `${reads.id} reads ${label}` : `no voice reads ${label}`}` : (reads?.id ?? 'No voice reads it here')}
            </span>
          </div>
        )
      })}
    </div>
  )
}

/** Reads a sample in a voice, at the person's speed; again to stop it. */
function TryIt({ voice, language, label, speed }: { voice: string | null; language: string; label: string; speed: number }) {
  const [playing, setPlaying] = useState(false)
  const stopRef = useRef<(() => void) | null>(null)
  useEffect(() => () => stopRef.current?.(), [])
  const click = async () => {
    if (playing) return stopRef.current?.()
    setPlaying(true)
    try {
      stopRef.current = await play('/api/account/voice/try', { voice, language, speed }, () => {
        stopRef.current = null
        setPlaying(false)
      })
    } catch (e) {
      setPlaying(false)
      toast.error('The voice cannot be tried', { description: errorMessage(e) })
    }
  }
  return (
    <Button type="button" variant="outline" size="sm" onClick={() => void click()} aria-label={playing ? `Stop: ${label}` : `Try it: ${label}`} aria-pressed={playing}>
      {playing ? <Square /> : <Play />}
      <span className="hidden sm:inline">{playing ? 'Stop' : 'Try it'}</span>
    </Button>
  )
}

/** How fast texts are read: saved a moment after the slider stops moving. */
function Speed({ settings: v, onChange }: { settings: VoiceSettings; onChange: (speed: number | null) => void }) {
  const id = useId()
  const saved = speedOf(v)
  const [speed, setSpeed] = useState(saved)
  const latest = useRef(onChange)
  useEffect(() => {
    latest.current = onChange
  })
  useEffect(() => {
    if (speed === saved) return
    const timer = setTimeout(() => latest.current(speed), 500)
    return () => clearTimeout(timer)
  }, [speed, saved])
  return (
    <div className="grid gap-2">
      <div className="flex items-center justify-between gap-2">
        <label htmlFor={id} className="text-sm font-medium">
          Speed
        </label>
        {v.chosen.speed !== null ? (
          <Button type="button" variant="link" size="sm" className="h-auto px-0" onClick={() => onChange(null)}>
            The company's ({times(v.company.speed)})
          </Button>
        ) : (
          <span className="text-xs text-muted-foreground">The company's</span>
        )}
      </div>
      <div className="flex items-center gap-3">
        <input
          id={id}
          type="range"
          min={0.5}
          max={2}
          step={0.05}
          value={speed}
          onChange={(e) => setSpeed(Number(e.target.value))}
          className="h-2 w-full cursor-pointer accent-primary"
        />
        <output htmlFor={id} className="w-12 text-right text-sm tabular-nums">
          {times(speed)}
        </output>
      </div>
    </div>
  )
}

/** Whether Talk (and the answer to a voice message) reads the answer aloud. */
function ReadAloud({ settings: v, disabled, onChange }: { settings: VoiceSettings; disabled: boolean; onChange: (on: boolean) => void }) {
  const id = useId()
  return (
    <div className="flex items-start justify-between gap-3 rounded-lg border px-3 py-2">
      <div className="grid gap-0.5">
        <span id={id} className="text-sm font-medium">
          Read answers aloud in Talk
        </span>
        <span className="text-xs text-muted-foreground">
          And the answer to a voice message. Off: answers are only shown.{v.chosen.readAloud === null && " The company's choice."}
        </span>
      </div>
      <Switch checked={readsAloud(v)} disabled={disabled} onCheckedChange={onChange} aria-labelledby={id} />
    </div>
  )
}
