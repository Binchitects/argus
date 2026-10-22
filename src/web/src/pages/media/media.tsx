import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Brush, Download, Eraser, Expand, Film, ImagePlus, Images, Palette, Sparkles, Trash2, Undo2, Upload, Wand2, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { uploadFile } from '../chat/api'
import {
  actionNames, addUpload, contentUrl, fit, mediaConfigQuery, mediaItemsQuery, mediaJobsQuery, removeItem, startJob, thumbUrl, working,
  type MediaConfig, type MediaItem, type MediaJob, type MediaJobRequest,
} from './media-api'
import { PaintLayer, type PaintHandle } from './paint'

const shapes = [
  { value: 'square', label: 'Square', width: 1024, height: 1024 },
  { value: 'wide', label: 'Wide', width: 1024, height: 576 },
  { value: 'photo', label: 'Photo', width: 1024, height: 768 },
  { value: 'tall', label: 'Tall', width: 576, height: 1024 },
] as const

type Shape = (typeof shapes)[number]['value']

const day = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Starts a job, and says why when it cannot; the page follows it in the gallery. */
function useStart(onStarted?: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: startJob,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mediaJobsQuery.queryKey })
      onStarted?.()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
}

/** Words to a picture: its shape and how many. */
function MakeCard({ config }: { config: MediaConfig }) {
  const [prompt, setPrompt] = useState('')
  const [shape, setShape] = useState<Shape>('square')
  const [count, setCount] = useState('1')
  const start = useStart(() => setPrompt(''))
  const queryClient = useQueryClient()
  const file = useRef<HTMLInputElement | null>(null)
  const [uploading, setUploading] = useState(false)
  const size = shapes.find((s) => s.value === shape)!
  return (
    <Card>
      <CardContent className="pt-6">
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            start.mutate({ action: 'make', prompt: prompt.trim(), count: Number(count), width: size.width, height: size.height })
          }}
        >
          <Field label="Describe a picture">
            <Textarea
              rows={2}
              maxLength={2000}
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey && prompt.trim()) {
                  e.preventDefault()
                  e.currentTarget.form?.requestSubmit()
                }
              }}
              placeholder="A lighthouse on a cliff at dusk, warm light in the windows, waves below, a painting in oils"
            />
          </Field>
          <div className="flex flex-wrap items-center gap-2">
            <Segmented label="Shape" value={shape} onChange={setShape} options={shapes.map((s) => ({ value: s.value, label: s.label }))} />
            <Segmented label="How many" value={count} onChange={setCount} options={[{ value: '1', label: 'One' }, { value: '2', label: 'Two' }, { value: '4', label: 'Four' }]} />
            <div className="ml-auto flex gap-2">
              <input
                ref={file}
                type="file"
                accept="image/png,image/jpeg,image/webp"
                className="hidden"
                aria-label="Upload a picture"
                onChange={async (e) => {
                  const f = e.target.files?.[0]
                  e.target.value = ''
                  if (!f) return
                  setUploading(true)
                  try {
                    const a = await uploadFile(f, () => undefined)
                    await addUpload(a.id)
                    await queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
                    toast.success(`${f.name} is in the studio: open it to change it.`)
                  } catch (err) {
                    toast.error(errorMessage(err))
                  } finally {
                    setUploading(false)
                  }
                }}
              />
              <Button type="button" variant="outline" loading={uploading} onClick={() => file.current?.click()}>
                <Upload /> Upload a picture
              </Button>
              <Button type="submit" loading={start.isPending} disabled={!prompt.trim() || !config.picture}>
                <Sparkles /> Make it
              </Button>
            </div>
          </div>
          {!config.picture && <Alert>There is no picture model here you may use: an admin adds one under Admin → Models (the imagegen module runs it).</Alert>}
        </form>
      </CardContent>
    </Card>
  )
}

/** A piece of work going on: where it is, in the gallery's place for what it makes. */
function WorkingTile({ job }: { job: MediaJob }) {
  return (
    <li className="relative aspect-square overflow-hidden rounded-xl border bg-muted/40" aria-label={`${working[job.action]}: ${job.prompt ?? ''}`}>
      <Skeleton className="absolute inset-0 rounded-none" />
      <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 p-3 text-center">
        <Spinner label={working[job.action]} />
        <p className="text-xs font-medium">{job.phase ?? working[job.action]}…</p>
        {job.prompt && <p className="line-clamp-2 text-xs text-muted-foreground">{job.prompt}</p>}
      </div>
    </li>
  )
}

function Tile({ item, onOpen }: { item: MediaItem; onOpen: () => void }) {
  const label = item.prompt ?? item.fileName
  return (
    <li>
      <button
        type="button"
        onClick={onOpen}
        className="group relative block aspect-square w-full overflow-hidden rounded-xl border bg-muted outline-none transition-shadow hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring"
        aria-label={`${actionNames[item.action]}: ${label}`}
      >
        {item.kind === 'video' ? (
          <video src={contentUrl(item)} muted playsInline preload="metadata" className="size-full object-cover" aria-hidden="true" />
        ) : (
          <img src={thumbUrl(item)} alt="" loading="lazy" decoding="async" className="size-full object-cover transition-transform duration-300 group-hover:scale-[1.03]" />
        )}
        <span className="absolute inset-x-0 bottom-0 flex items-end gap-1.5 bg-gradient-to-t from-black/70 to-transparent p-2 pt-8 text-left text-xs text-white opacity-90 transition-opacity md:opacity-0 md:group-hover:opacity-100 md:group-focus-visible:opacity-100">
          {item.kind === 'video' && <Film className="size-3.5 shrink-0" aria-hidden="true" />}
          <span className="line-clamp-2">{label}</span>
        </span>
        {item.kind === 'video' && (
          <span className="absolute top-2 right-2 rounded-full bg-black/60 px-2 py-0.5 text-[11px] font-medium text-white">{item.seconds} s</span>
        )}
      </button>
    </li>
  )
}

/** One picture or clip, large, and what can be done with it. */
function Editor({ item, items, config, onOpen, onClose }: { item: MediaItem; items: MediaItem[]; config: MediaConfig; onOpen: (id: string) => void; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const start = useStart(() => {
    toast.success('Started: it shows in the gallery when done.')
    onClose()
  })
  const picture = useRef<HTMLImageElement | null>(null)
  const [loaded, setLoaded] = useState<HTMLImageElement | null>(null)
  const paint = useRef<PaintHandle | null>(null)
  const [tab, setTab] = useState('edit')
  const [change, setChange] = useState('')
  const [painting, setPainting] = useState(false)
  const [painted, setPainted] = useState(false)
  const [brush, setBrush] = useState(48)
  const [erase, setErase] = useState(false)
  const [count, setCount] = useState('2')
  const [strength, setStrength] = useState('0.55')
  const [motion, setMotion] = useState('')
  const [seconds, setSeconds] = useState('3')
  const parent = item.parentId ? items.find((i) => i.id === item.parentId) : undefined
  const natural = { width: loaded?.naturalWidth ?? item.width ?? 1024, height: loaded?.naturalHeight ?? item.height ?? 1024 }
  const work = fit(natural.width, natural.height)
  const tooLarge = Math.max(natural.width, natural.height) * 4 > 8192
  const remove = useMutation({
    mutationFn: () => removeItem(item.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const send = (body: MediaJobRequest) => start.mutate({ sourceId: item.id, width: natural.width, height: natural.height, ...body })
  const title = item.prompt ?? item.fileName
  return (
    <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_20rem]">
      <figure className="flex min-h-64 items-center justify-center overflow-hidden rounded-xl bg-[repeating-conic-gradient(var(--color-muted)_0%_25%,transparent_0%_50%)] bg-[length:20px_20px]">
        {item.kind === 'video' ? (
          <video src={contentUrl(item)} controls autoPlay loop muted playsInline className="max-h-[70vh] w-full object-contain" aria-label={title} />
        ) : (
          <div className="relative inline-block max-h-[70vh]">
            <img
              ref={picture}
              src={contentUrl(item)}
              alt={title}
              onLoad={(e) => setLoaded(e.currentTarget)}
              className="block max-h-[70vh] w-auto max-w-full object-contain select-none"
              draggable={false}
            />
            {painting && loaded && <PaintLayer ref={paint} image={loaded} width={work.width} height={work.height} brush={(brush * Math.max(work.width, work.height)) / 1024} erase={erase} onChange={setPainted} />}
          </div>
        )}
      </figure>
      <div className="grid content-start gap-4">
        <DialogHeader>
          <DialogTitle className="line-clamp-3 text-base leading-snug">{title}</DialogTitle>
          <DialogDescription>
            {actionNames[item.action]}
            {item.width && item.height ? ` · ${item.width} × ${item.height}` : ''}
            {item.seconds ? ` · ${item.seconds} s` : ''}
            {item.model ? ` · ${item.model}` : ''} · {day.format(new Date(item.createdAt))}
          </DialogDescription>
        </DialogHeader>
        {parent && (
          <button
            type="button"
            className="flex items-center gap-2 rounded-lg border p-1.5 text-left text-xs hover:bg-muted"
            aria-label={`Made from: ${parent.prompt ?? parent.fileName}`}
            onClick={() => onOpen(parent.id)}
          >
            <img src={thumbUrl(parent)} alt="" className="size-10 shrink-0 rounded object-cover" />
            <span className="min-w-0">
              <span className="block text-muted-foreground">Made from</span>
              <span className="line-clamp-1">{parent.prompt ?? parent.fileName}</span>
            </span>
          </button>
        )}
        {item.kind === 'picture' && config.picture && (
          <Tabs value={tab} onValueChange={setTab}>
            <TabsList className="w-full">
              <TabsTrigger value="edit">Change</TabsTrigger>
              <TabsTrigger value="vary">Variations</TabsTrigger>
              {config.enlarge && <TabsTrigger value="enlarge">Enlarge</TabsTrigger>}
              {config.video && <TabsTrigger value="move">Move</TabsTrigger>}
            </TabsList>
            <TabsContent value="edit" className="grid gap-3 pt-2">
              <Field label="What to change" hint={painting ? 'Paint over the part to change: the rest stays as it is.' : 'All of it changes as you say; or paint over the part to change.'}>
                <Textarea rows={3} maxLength={2000} value={change} onChange={(e) => setChange(e.target.value)} placeholder="Give the fox a blue knitted hat" />
              </Field>
              <div className="flex flex-wrap items-center gap-1.5">
                <Button type="button" size="sm" variant={painting ? 'secondary' : 'outline'} aria-pressed={painting} onClick={() => setPainting(!painting)}>
                  <Brush /> {painting ? 'Painting' : 'Paint the part'}
                </Button>
                {painting && (
                  <>
                    <Button type="button" size="icon-sm" variant={erase ? 'secondary' : 'ghost'} aria-pressed={erase} aria-label="Erase" onClick={() => setErase(!erase)}>
                      <Eraser />
                    </Button>
                    <Button type="button" size="icon-sm" variant="ghost" aria-label="Undo" onClick={() => paint.current?.undo()}>
                      <Undo2 />
                    </Button>
                    <Button type="button" size="icon-sm" variant="ghost" aria-label="Clear the painting" onClick={() => paint.current?.clear()}>
                      <X />
                    </Button>
                  </>
                )}
              </div>
              {painting && (
                <label className="grid gap-1 text-xs text-muted-foreground">
                  Brush
                  <input type="range" min={12} max={160} value={brush} onChange={(e) => setBrush(Number(e.target.value))} className="accent-primary" />
                </label>
              )}
              <Button
                loading={start.isPending}
                disabled={!change.trim() || (painting && !painted)}
                onClick={() => {
                  const p = painting ? paint.current?.export() : null
                  if (painting && !p) return
                  send({ action: 'edit', prompt: change.trim(), ...(p ?? {}) })
                }}
              >
                <Wand2 /> {painting ? 'Change the painted part' : 'Change it'}
              </Button>
            </TabsContent>
            <TabsContent value="vary" className="grid gap-3 pt-2">
              <Segmented label="How many" value={count} onChange={setCount} options={[{ value: '1', label: 'One' }, { value: '2', label: 'Two' }, { value: '4', label: 'Four' }]} />
              <Segmented label="How different" value={strength} onChange={setStrength} options={[{ value: '0.35', label: 'A little' }, { value: '0.55', label: 'Some' }, { value: '0.75', label: 'A lot' }]} />
              <Button loading={start.isPending} onClick={() => send({ action: 'vary', count: Number(count), strength: Number(strength) })}>
                <Images /> Make variations
              </Button>
            </TabsContent>
            {config.enlarge && (
              <TabsContent value="enlarge" className="grid gap-3 pt-2">
                <p className="text-sm text-muted-foreground">
                  {tooLarge
                    ? 'This picture is large already: enlarging makes it four times as large, at most 8,192 pixels a side.'
                    : `Four times as large, its details drawn in: ${natural.width} × ${natural.height} becomes ${natural.width * 4} × ${natural.height * 4}.`}
                </p>
                <Button loading={start.isPending} disabled={tooLarge} onClick={() => send({ action: 'enlarge' })}>
                  <Expand /> Enlarge it
                </Button>
              </TabsContent>
            )}
            {config.video && (
              <TabsContent value="move" className="grid gap-3 pt-2">
                <Field label="What happens" hint={`A short clip from ${config.video} that starts from this picture; it takes a few minutes.`}>
                  <Textarea rows={2} maxLength={2000} value={motion} onChange={(e) => setMotion(e.target.value)} placeholder="The fox turns its head and the snow starts to fall" />
                </Field>
                <Segmented label="How long" value={seconds} onChange={setSeconds} options={[{ value: '2', label: '2 s' }, { value: '3', label: '3 s' }, { value: '5', label: '5 s' }]} />
                <Button loading={start.isPending} onClick={() => send({ action: 'move', prompt: motion.trim() || undefined, seconds: Number(seconds) })}>
                  <Film /> Make the clip
                </Button>
              </TabsContent>
            )}
          </Tabs>
        )}
        <div className="flex flex-wrap gap-2 border-t pt-3">
          <Button variant="outline" size="sm" asChild>
            <a href={`${contentUrl(item)}?download`} download={item.fileName}>
              <Download /> Download
            </a>
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="ml-auto text-destructive-ink"
            loading={remove.isPending}
            onClick={async () => {
              if (await confirm({ title: 'Delete it?', description: item.action === 'upload' ? 'It leaves the studio; your upload stays in your files.' : 'It goes, with its file.', confirm: 'Delete', destructive: true })) remove.mutate()
            }}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </div>
    </div>
  )
}

/** Workspace → Media studio: pictures from words, changed (all or where painted), varied, enlarged, set moving. */
export function MediaPage() {
  const queryClient = useQueryClient()
  const config = useQuery(mediaConfigQuery)
  const [kind, setKind] = useState<'all' | 'picture' | 'video'>('all')
  const items = useInfiniteQuery(mediaItemsQuery(kind))
  const jobs = useQuery(mediaJobsQuery)
  const [open, setOpen] = useState<string | null>(null)
  const [hidden, setHidden] = useState<string[]>([])
  const seen = useRef(new Map<string, MediaJob['status']>())

  // Work that finishes: what it made comes into the gallery.
  useEffect(() => {
    let done = false
    for (const j of jobs.data ?? []) {
      const was = seen.current.get(j.id)
      if (was === 'working' && j.status !== 'working') done = true
      seen.current.set(j.id, j.status)
    }
    if (done) {
      void queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
      void queryClient.invalidateQueries({ queryKey: mediaConfigQuery.queryKey })
    }
  }, [jobs.data, queryClient])

  if (config.error) return <QueryError error={config.error} retry={() => config.refetch()} />
  const all = items.data?.pages.flatMap((p) => p.items) ?? []
  const going = (jobs.data ?? []).filter((j) => j.status === 'working' && (kind === 'all' || (kind === 'video') === (j.action === 'move')))
  const failed = (jobs.data ?? []).filter((j) => j.status === 'failed' && !hidden.includes(j.id))
  const chosen = all.find((i) => i.id === open)
  return (
    <>
      <PageHeader
        title="Media studio"
        description="Pictures from a few words; change one by saying how (all of it, or only the part you paint over), make variations, enlarge it, or make it move."
      />
      <div className="grid gap-4">
        {config.data ? <MakeCard config={config.data} /> : <Skeleton className="h-40 rounded-xl" />}
        {failed.map((j) => (
          <Alert key={j.id} variant="destructive" className="flex items-start gap-2">
            <span className="min-w-0 flex-1">{j.error}</span>
            <Button variant="ghost" size="icon-sm" aria-label="Dismiss" onClick={() => setHidden([...hidden, j.id])}>
              <X />
            </Button>
          </Alert>
        ))}
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-base font-semibold">Your pictures and clips</h2>
          <Segmented label="Show" value={kind} onChange={setKind} options={[{ value: 'all', label: 'All' }, { value: 'picture', label: 'Pictures' }, { value: 'video', label: 'Clips' }]} />
        </div>
        {items.error ? (
          <QueryError error={items.error} retry={() => items.refetch()} />
        ) : items.isPending ? (
          <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5" aria-label="Loading">
            {Array.from({ length: 10 }, (_, i) => (
              <li key={i}>
                <Skeleton className="aspect-square rounded-xl" />
              </li>
            ))}
          </ul>
        ) : all.length === 0 && going.length === 0 ? (
          <EmptyState icon={Palette} title="Nothing here yet">
            Describe a picture above, or upload one of yours to change it.
          </EmptyState>
        ) : (
          <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5" aria-label="Your pictures and clips">
            {going.flatMap((j) => Array.from({ length: j.action === 'make' || j.action === 'vary' ? j.count : 1 }, (_, i) => <WorkingTile key={`${j.id}-${i}`} job={j} />))}
            {all.map((item) => (
              <Tile key={item.id} item={item} onOpen={() => setOpen(item.id)} />
            ))}
          </ul>
        )}
        {items.hasNextPage && (
          <Button variant="outline" className="justify-self-center" loading={items.isFetchingNextPage} onClick={() => void items.fetchNextPage()}>
            <ImagePlus /> Show more
          </Button>
        )}
      </div>
      <Dialog open={!!chosen} onOpenChange={(o) => !o && setOpen(null)}>
        <DialogContent className="sm:max-w-6xl">{chosen && config.data && <Editor key={chosen.id} item={chosen} items={all} config={config.data} onOpen={setOpen} onClose={() => setOpen(null)} />}</DialogContent>
      </Dialog>
    </>
  )
}
