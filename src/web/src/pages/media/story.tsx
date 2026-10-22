import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Clapperboard, Download, Film, Plus, RefreshCw, Save, Trash2, X } from 'lucide-react'
import { useId, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { againShot, changeStory, makeStory, removeStory, storiesQuery, storyQuery, writeStory, type MediaConfig, type Story, type StoryShot } from './media-api'
import { VoiceSample } from './sound'

/** What the video is about, to a storyboard the model writes: it opens to be read and changed. */
export function StoryForm({ onWritten }: { onWritten: (id: string) => void }) {
  const [brief, setBrief] = useState('')
  const [shots, setShots] = useState('5')
  const queryClient = useQueryClient()
  const write = useMutation({
    mutationFn: () => writeStory(brief.trim(), Number(shots)),
    onSuccess: (s) => {
      void queryClient.invalidateQueries({ queryKey: storiesQuery.queryKey })
      setBrief('')
      onWritten(s.id)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        write.mutate()
      }}
    >
      <Field label="What the video is about" hint="The model writes a storyboard: shots, each what is seen and what the narrator says. You change it before it is made.">
        <Textarea rows={3} maxLength={8000} value={brief} onChange={(e) => setBrief(e.target.value)} placeholder="A one-minute welcome to our lunch club for new colleagues: where we meet, the food, the people, how to join." />
      </Field>
      <div className="flex flex-wrap items-center gap-2">
        <Segmented label="Shots" value={shots} onChange={setShots} options={[{ value: '3', label: '3 shots' }, { value: '5', label: '5 shots' }, { value: '8', label: '8 shots' }]} />
        <Button type="submit" className="ml-auto" loading={write.isPending} disabled={!brief.trim()}>
          <Clapperboard /> Write the storyboard
        </Button>
      </div>
    </form>
  )
}

const statusNames: Record<Story['status'], string> = { draft: 'Draft', making: 'Being made', done: 'Made', failed: 'Stopped' }

/** The person's videos being written or made, newest first: each opens its storyboard. */
export function StoryList({ onOpen }: { onOpen: (id: string) => void }) {
  const stories = useQuery(storiesQuery)
  if (!stories.data?.length) return null
  return (
    <section aria-label="Your videos" className="grid gap-2">
      <h2 className="text-base font-semibold">Your videos</h2>
      <ul className="flex snap-x gap-3 overflow-x-auto pb-1">
        {stories.data.map((s) => {
          const cover = s.shots.find((x) => x.picture)?.picture
          return (
            <li key={s.id} className="w-56 shrink-0 snap-start">
              <button type="button" onClick={() => onOpen(s.id)} className="grid w-full gap-1.5 rounded-xl border bg-card p-2 text-left text-sm outline-none hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring">
                <span className="relative block aspect-video overflow-hidden rounded-lg bg-muted">
                  {cover ? <img src={cover} alt="" loading="lazy" className="size-full object-cover" /> : <Film className="absolute inset-0 m-auto size-6 text-muted-foreground" aria-hidden="true" />}
                  {s.status === 'making' && (
                    <span className="absolute inset-0 flex items-center justify-center bg-black/40">
                      <Spinner label="Being made" className="text-white" />
                    </span>
                  )}
                </span>
                <span className="line-clamp-1 font-medium">{s.title}</span>
                <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                  <Badge variant={s.status === 'failed' ? 'destructive' : 'secondary'}>{statusNames[s.status]}</Badge>
                  <span className="truncate">{s.status === 'making' ? (s.phase ?? '') : `${s.shots.length} shots`}</span>
                </span>
              </button>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

type Draft = Pick<StoryShot, 'visual' | 'narration' | 'seconds'> & Pick<StoryShot, 'picture' | 'clip' | 'speech'>

/** A storyboard: its shots to read and change, its narrator and music, made into one video; any shot made again alone. */
export function StoryEditor({ id, config, onClose }: { id: string; config: MediaConfig; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const uid = useId()
  const story = useQuery(storyQuery(id))
  const [title, setTitle] = useState('')
  const [shots, setShots] = useState<Draft[]>([])
  const [narrator, setNarrator] = useState<'mine' | 'sample'>('mine')
  const [sample, setSample] = useState<{ id: string; name: string } | null>(null)
  const [consent, setConsent] = useState(false)
  const [music, setMusic] = useState('')
  const [loadedAt, setLoadedAt] = useState<string | null>(null)
  const s = story.data
  const making = s?.status === 'making'

  // What the server says, whenever it changed there (made, or saved): the drafts follow (set as it renders, React's way for state from props).
  if (s && s.updatedAt !== loadedAt) {
    setLoadedAt(s.updatedAt)
    setTitle(s.title)
    setShots(s.shots.map((x) => ({ ...x })))
    setMusic(s.music ?? '')
    if (s.voiceId) {
      setNarrator('sample')
      setSample((was) => (was?.id === s.voiceId ? was : { id: s.voiceId!, name: 'The voice chosen before' }))
      setConsent(true)
    }
  }

  const refresh = (next: Story) => {
    queryClient.setQueryData(storyQuery(id).queryKey, next)
    void queryClient.invalidateQueries({ queryKey: ['media'] })
  }
  const body = () => ({
    title: title.trim(),
    shots: shots.map((x) => ({ visual: x.visual, narration: x.narration, seconds: x.seconds })),
    voiceId: narrator === 'sample' ? (sample?.id ?? null) : null,
    consent: narrator === 'sample' && consent,
    music: config.music && music.trim() ? music.trim() : null,
  })
  const save = useMutation({ mutationFn: () => changeStory(id, body()), onSuccess: refresh, onError: (e) => toast.error(errorMessage(e)) })
  const make = useMutation({
    mutationFn: async () => {
      refresh(await changeStory(id, body()))
      return makeStory(id)
    },
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const again = useMutation({ mutationFn: (index: number) => againShot(id, index), onSuccess: refresh, onError: (e) => toast.error(errorMessage(e)) })
  const remove = useMutation({
    mutationFn: () => removeStory(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['media'] })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (!s) return <Spinner label="Opening the storyboard" />
  const set = (i: number, change: Partial<Draft>) => setShots(shots.map((x, n) => (n === i ? { ...x, ...change } : x)))
  const ready = shots.length >= 2 && shots.every((x) => x.visual.trim()) && (narrator === 'mine' || (sample && consent))
  const video = s.video && s.status !== 'making'
  return (
    <div className="grid gap-4">
      <DialogHeader>
        <DialogTitle className="sr-only">The storyboard</DialogTitle>
        <Input aria-label="Title" value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} disabled={making} className="h-10 text-lg font-semibold" />
        <DialogDescription className="line-clamp-2">{s.brief}</DialogDescription>
      </DialogHeader>
      {making && (
        <output className="flex items-center gap-2 rounded-lg bg-muted/60 px-3 py-2 text-sm" aria-live="polite">
          <Spinner label="Being made" /> {s.phase ?? 'Being made'}… It takes a few minutes a shot; the bell says when it is ready.
        </output>
      )}
      {s.status === 'failed' && s.error && <Alert variant="destructive">{s.error}</Alert>}
      {video && <FinalVideo story={s} />}
      <ol className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Shots">
        {shots.map((x, i) => (
          <li key={i} className="grid content-start gap-2 rounded-xl border bg-card p-3">
            <div className="relative aspect-video overflow-hidden rounded-lg bg-muted">
              {x.clip ? (
                // oxlint-disable-next-line jsx-a11y/media-has-caption -- its narration is beside it, and captions are in the video
                <video src={x.clip} muted loop autoPlay playsInline className="size-full object-cover" aria-label={`Shot ${i + 1}`} />
              ) : x.picture ? (
                <img src={x.picture} alt={`Shot ${i + 1}`} className="size-full object-cover" />
              ) : (
                <span className="absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">Not made yet</span>
              )}
              <span className="absolute top-1.5 left-1.5 rounded-full bg-black/60 px-2 py-0.5 text-[11px] font-medium text-white">{i + 1}</span>
            </div>
            <Field label="What is seen (in English)">
              <Textarea rows={3} maxLength={1000} value={x.visual} disabled={making} onChange={(e) => set(i, { visual: e.target.value })} />
            </Field>
            <Field label="What the narrator says">
              <Textarea rows={2} maxLength={400} value={x.narration} disabled={making} onChange={(e) => set(i, { narration: e.target.value })} />
            </Field>
            <div className="flex flex-wrap items-center gap-1.5">
              <Segmented label={`Shot ${i + 1} length`} value={String(x.seconds)} onChange={(v) => set(i, { seconds: Number(v) })} options={['2', '3', '4', '5'].map((v) => ({ value: v, label: `${v} s` }))} />
              {x.clip && (
                <Button type="button" variant="ghost" size="icon-sm" aria-label={`Make shot ${i + 1} again`} disabled={making} onClick={() => again.mutate(i)}>
                  <RefreshCw />
                </Button>
              )}
              <Button type="button" variant="ghost" size="icon-sm" className="ml-auto" aria-label={`Take shot ${i + 1} out`} disabled={making || shots.length <= 2} onClick={() => setShots(shots.filter((_, n) => n !== i))}>
                <X />
              </Button>
            </div>
          </li>
        ))}
        {shots.length < 8 && !making && (
          <li>
            <Button type="button" variant="outline" className="h-full min-h-24 w-full border-dashed" onClick={() => setShots([...shots, { visual: '', narration: '', seconds: 4 }])}>
              <Plus /> A shot more
            </Button>
          </li>
        )}
      </ol>
      <div className="grid gap-3 md:grid-cols-2">
        <div className="grid content-start gap-2">
          <span className="text-sm font-medium">The narrator</span>
          {config.voice ? (
            <Segmented label="The narrator" value={narrator} onChange={setNarrator} options={[{ value: 'mine', label: 'Your voice settings' }, { value: 'sample', label: 'A voice from a sample' }]} />
          ) : (
            <p className="text-sm text-muted-foreground">Your voice settings (Account → Voice).</p>
          )}
          {narrator === 'sample' && (
            <div className="grid gap-2 rounded-lg bg-muted/40 p-3">
              <VoiceSample value={sample} onChange={setSample} />
              <div className="flex items-start gap-2 text-sm">
                <Checkbox id={`${uid}-consent`} checked={consent} onCheckedChange={(c) => setConsent(!!c)} className="mt-0.5" />
                <label htmlFor={`${uid}-consent`}>This voice is mine, or its owner lets me use it.</label>
              </div>
            </div>
          )}
        </div>
        {config.music && (
          <Field label="Music under it (none: leave empty)">
            <Input value={music} maxLength={1000} disabled={making} onChange={(e) => setMusic(e.target.value)} placeholder="calm piano, soft strings" />
          </Field>
        )}
      </div>
      <div className="flex flex-wrap gap-2 border-t pt-3">
        <Button type="button" loading={make.isPending} disabled={making || !ready} onClick={() => make.mutate()}>
          <Clapperboard /> {s.videoItemId ? 'Make the video again' : 'Make the video'}
        </Button>
        <Button type="button" variant="outline" loading={save.isPending} disabled={making} onClick={() => save.mutate()}>
          <Save /> Save
        </Button>
        <Button
          type="button"
          variant="ghost"
          className="ml-auto text-destructive-ink"
          disabled={making}
          onClick={async () => {
            if (await confirm({ title: `Delete ${s.title}?`, description: 'Its storyboard and what was made for its shots go; the video stays in the gallery.', confirm: 'Delete', destructive: true })) remove.mutate()
          }}
        >
          <Trash2 /> Delete
        </Button>
      </div>
    </div>
  )
}

/** The video as cut: to watch and download. */
function FinalVideo({ story }: { story: Story }) {
  const src = story.video!
  return (
    <figure className="grid gap-2">
      {/* oxlint-disable-next-line jsx-a11y/media-has-caption -- its captions are burnt into it */}
      <video src={src} controls playsInline className="max-h-[50vh] w-full rounded-xl bg-black" aria-label={story.title} />
      <figcaption className="flex justify-end">
        <Button variant="outline" size="sm" asChild>
          <a href={`${src}?download`} download>
            <Download /> Download the MP4
          </a>
        </Button>
      </figcaption>
    </figure>
  )
}
