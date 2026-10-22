import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Code2, Crosshair, Download, Film, History, PenTool, Plus, Sparkles, Trash2, Upload, Wand2, X } from 'lucide-react'
import { useRef, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Spinner } from '@/components/ui/spinner'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import {
  changeVector, contentUrl, mediaJobsQuery, notSvg, removeItem, restoreVersion, startJob, svgUrl, uploadVector, vectorConfigQuery, vectorQuery, vectorVersionsQuery, working,
  type MediaJobRequest, type MediaItem, type Motion, type VectorConfig,
} from './media-api'
import { VectorStage, type StageHandle } from './vector-stage'

const shapes = [
  { value: 'square', label: 'Square', width: 512, height: 512 },
  { value: 'wide', label: 'Wide', width: 640, height: 400 },
  { value: 'tall', label: 'Tall', width: 400, height: 640 },
] as const

function useStart(onStarted?: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: MediaJobRequest) => startJob(body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mediaJobsQuery.queryKey })
      onStarted?.()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
}

/**
 * A vector drawing from words: an illustration (the picture model draws it flat, and it is traced into shapes: the
 * richest), or drawn as SVG by the model (named parts, best to animate: icons, diagrams, simple scenes); or an SVG of one's own.
 */
export function VectorForm({ config }: { config: VectorConfig }) {
  const queryClient = useQueryClient()
  const [how, setHow] = useState<'trace' | 'draw'>(config.drawsPictures && config.tracesPictures ? 'trace' : 'draw')
  const [prompt, setPrompt] = useState('')
  const [style, setStyle] = useState('flat')
  const [trace, setTrace] = useState('illustration')
  const [shape, setShape] = useState<(typeof shapes)[number]['value']>('square')
  const [moving, setMoving] = useState<'always' | 'never'>('always')
  const file = useRef<HTMLInputElement | null>(null)
  const start = useStart(() => setPrompt(''))
  const size = shapes.find((s) => s.value === shape)!
  const hows = [
    ...(config.drawsPictures && config.tracesPictures ? [{ value: 'trace' as const, label: 'Illustration' }] : []),
    { value: 'draw' as const, label: 'Drawn as SVG' },
  ]
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        start.mutate(
          how === 'trace'
            ? { action: 'trace', prompt: prompt.trim(), trace, animate: moving }
            : { action: 'draw', prompt: prompt.trim(), style, width: size.width, height: size.height, animate: moving },
        )
      }}
    >
      <Segmented label="How it is made" value={how} onChange={setHow} options={hows} />
      <p className="text-xs text-muted-foreground">
        {how === 'trace'
          ? 'The picture model draws it flat, and it is traced into clean vector shapes: rich illustrations, logos and stickers.'
          : 'The model writes the SVG itself, every part named: icons, diagrams and simple scenes, the best to animate part by part.'}
      </p>
      <Field label="Describe it, and how it moves" hint="What is in it, and what moves how: it is drawn, then animated, in one go. Said nothing about motion, it comes gently alive.">
        <Textarea
          rows={3}
          maxLength={2000}
          value={prompt}
          onChange={(e) => setPrompt(e.target.value)}
          placeholder="A laptop on the right and a server rack on the left; a file flies from the laptop to the server again and again, and the server's green light blinks"
        />
      </Field>
      <div className="flex flex-wrap items-center gap-2">
        {how === 'trace' ? (
          <Segmented label="Traced as" value={trace} onChange={setTrace} options={config.traces.map((t) => ({ value: t.name, label: t.name[0].toUpperCase() + t.name.slice(1) }))} />
        ) : (
          <>
            <Select value={style} onValueChange={setStyle}>
              <SelectTrigger className="w-40" aria-label="Style">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {config.styles.map((s) => (
                  <SelectItem key={s.name} value={s.name}>
                    {s.name[0].toUpperCase() + s.name.slice(1)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Segmented label="Shape" value={shape} onChange={setShape} options={shapes.map((s) => ({ value: s.value, label: s.label }))} />
          </>
        )}
        <Segmented label="Moving or still" value={moving} onChange={setMoving} options={[{ value: 'always', label: 'Moving' }, { value: 'never', label: 'Still' }]} />
        <div className="ml-auto flex gap-2">
          <input
            ref={file}
            type="file"
            accept=".svg,image/svg+xml"
            className="hidden"
            aria-label="An SVG file"
            onChange={async (e) => {
              const f = e.target.files?.[0]
              e.target.value = ''
              if (!f) return
              try {
                const r = await uploadVector(await f.text(), f.name)
                await queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
                toast.success(r.changed.length ? `${f.name} is in the studio, made safe: ${r.changed.join('; ')}.` : `${f.name} is in the studio.`)
              } catch (err) {
                toast.error(errorMessage(err))
              }
            }}
          />
          <Button type="button" variant="outline" onClick={() => file.current?.click()}>
            <Upload /> An SVG
          </Button>
          <Button type="submit" loading={start.isPending} disabled={!prompt.trim()}>
            <PenTool /> Make it
          </Button>
        </div>
      </div>
    </form>
  )
}

/** The sizes each format is made at (a GIF's colours and size keep it small), the middle one first chosen. */
const sides: Record<string, string[]> = {
  png: ['1024', '2048', '4096'], gif: ['320', '480', '640'], mp4: ['720', '1080', '1920'], webm: ['720', '1080', '1920'], sprite: ['128', '256', '512'],
}

/** How each format is named here (a sprite sheet: every frame in one PNG, in rows). */
const formatNames: Record<string, string> = { png: 'PNG', gif: 'GIF', mp4: 'MP4', webm: 'WebM', sprite: 'Sprite sheet' }
const formatName = (f: string) => formatNames[f] ?? f.toUpperCase()

const repeats = [
  { value: 'once', label: 'Once' },
  { value: 'loop', label: 'Loop' },
]

/** A motion's row: what moves and how, its timing, all changeable in place. */
function MotionRow({ m, kinds, parts, onChange, onRemove }: { m: Motion; kinds: VectorConfig['kinds']; parts: string[]; onChange: (m: Motion) => void; onRemove: () => void }) {
  const repeat = m.repeat ?? (['spin', 'orbit', 'float', 'bounce', 'sway', 'swing', 'wave', 'pulse', 'heartbeat', 'blink', 'color'].includes(m.kind) ? 'loop' : 'once')
  return (
    <li className="grid gap-1.5 rounded-lg border p-2">
      <div className="flex items-center gap-1.5">
        <Select value={m.target} onValueChange={(target) => onChange({ ...m, target })}>
          <SelectTrigger className="h-8 min-w-0 flex-1" aria-label="What moves">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {['scene', ...parts].map((p) => (
              <SelectItem key={p} value={p}>
                {p === 'scene' ? 'The whole drawing' : `#${p}`}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={m.kind} onValueChange={(kind) => onChange({ ...m, kind })}>
          <SelectTrigger className="h-8 w-32" aria-label="How it moves">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((k) => (
              <SelectItem key={k.kind} value={k.kind}>
                {k.kind}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button type="button" variant="ghost" size="icon-sm" aria-label={`Take the ${m.kind} of ${m.target} out`} onClick={onRemove}>
          <X />
        </Button>
      </div>
      <div className="flex flex-wrap items-center gap-1.5 text-xs">
        <span className="flex items-center gap-1">
          Lasts
          <Input type="number" min={0.1} max={60} step={0.1} value={m.duration ?? ''} placeholder="auto" aria-label="How long it lasts, in seconds" className="h-7 w-20" onChange={(e) => onChange({ ...m, duration: e.target.value ? Number(e.target.value) : undefined })} />s
        </span>
        <span className="flex items-center gap-1">
          after
          <Input type="number" min={0} max={60} step={0.1} value={m.delay ?? 0} aria-label="When it starts, in seconds" className="h-7 w-16" onChange={(e) => onChange({ ...m, delay: Number(e.target.value) || 0 })} />s
        </span>
        <Segmented label="Repeat" value={repeat} onChange={(r) => onChange({ ...m, repeat: r })} options={repeats} className="h-7" />
      </div>
    </li>
  )
}

/** One vector drawing: its stage (played, scrubbed, its parts picked), its motions, changes by words and by hand, exports, versions. */
export function VectorEditor({ item, items, onClose }: { item: MediaItem; items: MediaItem[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const config = useQuery(vectorConfigQuery)
  const drawing = useQuery(vectorQuery(item.id))
  const [tab, setTab] = useState('animate')
  const versions = useQuery({ ...vectorVersionsQuery(item.id), enabled: tab === 'versions' })
  const jobs = useQuery(mediaJobsQuery)
  const stage = useRef<StageHandle | null>(null)
  const [picking, setPicking] = useState(false)
  const [picked, setPicked] = useState<string | null>(null)
  const [motions, setMotions] = useState<Motion[] | null>(null)
  const [words, setWords] = useState('')
  const [change, setChange] = useState('')
  const [code, setCode] = useState<string | null>(null)
  const [format, setFormat] = useState('gif')
  const [side, setSide] = useState(sides.gif[1])
  const [background, setBackground] = useState('#ffffff')
  const [problems, setProblems] = useState<string[]>([])
  const d = drawing.data
  // Code typed by hand shows on the stage as it is typed (still: its motions come back when it is used), once it is SVG.
  const typed = d && code !== null && code !== d.still ? code : null
  const typedWrong = typed !== null ? notSvg(typed) : null
  const preview = typed !== null && typedWrong === null
  const started = useStart(() => toast.success('Started: it shows here when done.'))
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['media', 'vector', item.id] })
    void queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
  }
  const save = useMutation({
    mutationFn: (body: { still?: string; motions?: Motion[] }) => changeVector(item.id, body),
    onSuccess: (r) => {
      queryClient.setQueryData(vectorQuery(item.id).queryKey, r.drawing)
      setProblems(r.problems)
      setMotions(null)
      setCode(null)
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => removeItem(item.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['media', 'items'] })
      onClose()
    },
  })
  if (!d || !config.data) return <Spinner label="Opening the drawing" />
  // Its own work: what goes on now (changes and motions come back into it), and the last that failed.
  const mine = (jobs.data ?? []).filter((j) => j.sourceId === item.id).sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  const busy = mine.find((j) => j.status === 'working')
  const failed = mine[0]?.status === 'failed' ? mine[0] : undefined
  const exports = items.filter((i) => i.parentId === item.id)
  const parts = [...new Set([...d.still.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]))].filter((p) => !/^(m\d|k\d)/.test(p))
  const shown = motions ?? d.motions
  const dirty = motions !== null
  const job = (body: Omit<MediaJobRequest, 'sourceId'>) => started.mutate({ ...body, sourceId: item.id })
  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
      <div className="grid content-start gap-2">
        <VectorStage
          ref={stage}
          markup={preview ? typed : d.markup}
          seconds={preview ? 0 : d.seconds}
          loops={preview ? false : d.loops}
          picking={picking}
          picked={picked}
          onPick={(id) => {
            setPicked(id)
            setPicking(false)
          }}
        />
        <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          <Button type="button" size="sm" variant={picking ? 'secondary' : 'outline'} aria-pressed={picking} onClick={() => setPicking(!picking)}>
            <Crosshair /> {picking ? 'Click a part' : 'Pick a part'}
          </Button>
          {picked && (
            <>
              <span className="font-mono text-foreground">#{picked}</span>
              <Button type="button" size="sm" variant="ghost" onClick={() => setMotions([...shown, { target: picked, kind: 'float' }])}>
                <Plus /> Give it a motion
              </Button>
            </>
          )}
          <span className="ml-auto">
            {d.width}×{d.height} · {d.source} · version {d.version}
          </span>
        </div>
      </div>
      <div className="grid content-start gap-3">
        <DialogHeader>
          <DialogTitle className="line-clamp-2 text-base">{d.prompt ?? 'A drawing'}</DialogTitle>
          <DialogDescription>{d.seconds > 0 ? `Plays ${d.seconds.toFixed(1)} s${d.loops ? ', then loops' : ''}.` : 'It stands still: give it motions.'}</DialogDescription>
        </DialogHeader>
        {busy && (
          <Alert className="flex items-center gap-2">
            <Spinner label={working[busy.action]} />
            <span>{busy.phase ?? working[busy.action]}…</span>
          </Alert>
        )}
        {failed && !busy && <Alert variant="destructive">{failed.error}</Alert>}
        {problems.length > 0 && (
          <Alert>
            Not done: {problems.join('; ')}.
          </Alert>
        )}
        <Tabs value={tab} onValueChange={setTab}>
          <TabsList className="w-full">
            <TabsTrigger value="animate">Animate</TabsTrigger>
            <TabsTrigger value="change">Change</TabsTrigger>
            <TabsTrigger value="export">Export</TabsTrigger>
            <TabsTrigger value="versions">
              <History className="size-4" />
              <span className="sr-only">Versions</span>
            </TabsTrigger>
          </TabsList>
          <TabsContent value="animate" className="grid gap-3 pt-2">
            <Field label="Animate by words" hint="Say what moves and how; the model plans the motions from its parts.">
              <Textarea rows={2} maxLength={2000} value={words} onChange={(e) => setWords(e.target.value)} placeholder="The sun rises slowly, the clouds drift, the boat rocks on the water" />
            </Field>
            <Button
              loading={started.isPending}
              disabled={!words.trim()}
              onClick={() => {
                job({ action: 'animate', prompt: words.trim() })
                setWords('')
              }}
            >
              <Wand2 /> Animate it
            </Button>
            <div className="grid gap-1.5">
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium">Its motions ({shown.length})</span>
                <Button type="button" size="sm" variant="ghost" onClick={() => setMotions([...shown, { target: picked ?? 'scene', kind: 'fade-in' }])}>
                  <Plus /> A motion
                </Button>
              </div>
              <ul className="grid max-h-80 gap-1.5 overflow-y-auto" aria-label="Its motions">
                {shown.map((m, i) => (
                  <MotionRow
                    key={i}
                    m={m}
                    kinds={config.data.kinds}
                    parts={parts}
                    onChange={(next) => setMotions(shown.map((x, n) => (n === i ? next : x)))}
                    onRemove={() => setMotions(shown.filter((_, n) => n !== i))}
                  />
                ))}
              </ul>
              {dirty && (
                <div className="flex gap-2">
                  <Button size="sm" loading={save.isPending} onClick={() => save.mutate({ motions: shown })}>
                    <Sparkles /> Play these
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => setMotions(null)}>
                    Undo
                  </Button>
                </div>
              )}
            </div>
          </TabsContent>
          <TabsContent value="change" className="grid gap-3 pt-2">
            <Field label="Change it by words">
              <Textarea rows={2} maxLength={2000} value={change} onChange={(e) => setChange(e.target.value)} placeholder="Make the boat red and add two birds in the sky" />
            </Field>
            <Button
              loading={started.isPending}
              disabled={!change.trim()}
              onClick={() => {
                job({ action: 'reshape', prompt: change.trim() })
                setChange('')
              }}
            >
              <Wand2 /> Change it
            </Button>
            <Field label="Its code" hint="The drawing as it stands still; its motions are kept apart.">
              <Textarea rows={8} spellCheck={false} className="font-mono text-xs" value={code ?? d.still} onChange={(e) => setCode(e.target.value)} />
            </Field>
            {typedWrong && <p className="text-xs text-destructive-ink">Not SVG yet: {typedWrong}</p>}
            {typed !== null && (
              <div className="flex flex-wrap items-center gap-2">
                <Button variant="outline" loading={save.isPending} disabled={!!typedWrong} onClick={() => save.mutate({ still: typed })}>
                  <Code2 /> Use this code
                </Button>
                <Button variant="ghost" onClick={() => setCode(null)}>
                  Undo
                </Button>
                {preview && <span className="text-xs text-muted-foreground">The stage shows it as typed.</span>}
              </div>
            )}
          </TabsContent>
          <TabsContent value="export" className="grid gap-3 pt-2">
            <Segmented
              label="Format"
              value={format}
              onChange={(f) => {
                setFormat(f)
                setSide((sides[f] ?? sides.png)[1])
              }}
              options={config.data.formats.map((f) => ({ value: f, label: formatName(f) }))}
            />
            {format !== 'png' && !config.data.moves && <Alert>Animations are recorded by the browser module, which does not run here.</Alert>}
            {format !== 'png' && d.seconds <= 0 && <Alert>It does not move yet: give it motions first.</Alert>}
            <Segmented
              label="Size"
              value={side}
              onChange={setSide}
              options={(sides[format] ?? sides.png).map((s) => ({ value: s, label: `${s} px` }))}
            />
            {format === 'mp4' && (
              <label className="flex items-center gap-2 text-sm">
                Background
                <input type="color" value={background} onChange={(e) => setBackground(e.target.value)} className="h-8 w-12 rounded border" aria-label="Background colour" />
              </label>
            )}
            <Button
              loading={started.isPending}
              disabled={format !== 'png' && (!config.data.moves || d.seconds <= 0)}
              onClick={() => job({ action: 'export', format, side: Number(side), ...(format === 'mp4' ? { background } : {}) })}
            >
              <Film /> Make the {format === 'sprite' ? 'sprite sheet' : formatName(format)}
            </Button>
            <p className="text-xs text-muted-foreground">It goes into the gallery beside the drawing. The SVG itself plays its motions anywhere: download it below.</p>
            {exports.length > 0 && (
              <ul className="grid gap-1 text-sm" aria-label="Made from it">
                {exports.map((x) => (
                  <li key={x.id} className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate">{x.fileName}</span>
                    <span className="text-xs text-muted-foreground tabular-nums">{Math.max(1, Math.round(x.size / 1024))} KB</span>
                    <Button variant="ghost" size="icon-sm" asChild>
                      <a href={`${contentUrl(x)}?download`} download={x.fileName} aria-label={`Download ${x.fileName}`}>
                        <Download />
                      </a>
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </TabsContent>
          <TabsContent value="versions" className="pt-2">
            <ul className="grid max-h-80 gap-1 overflow-y-auto text-sm" aria-label="Versions">
              {(versions.data ?? []).map((v) => (
                <li key={v.version} className="flex items-center gap-2 rounded-md px-2 py-1 hover:bg-muted">
                  <span className="tabular-nums text-muted-foreground">{v.version}</span>
                  <span className="min-w-0 flex-1 truncate">{v.change}</span>
                  {v.version !== d.version && (
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={async () => {
                        await restoreVersion(item.id, v.version).catch((e: unknown) => toast.error(errorMessage(e)))
                        refresh()
                      }}
                    >
                      Put back
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          </TabsContent>
        </Tabs>
        <div className="flex flex-wrap gap-2 border-t pt-3">
          <Button variant="outline" size="sm" asChild>
            <a href={`${svgUrl(item.id)}?download`} download={item.fileName}>
              <Download /> SVG
            </a>
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="ml-auto text-destructive-ink"
            onClick={async () => {
              if (await confirm({ title: 'Delete the drawing?', description: 'It goes with its versions; what was exported from it stays.', confirm: 'Delete', destructive: true })) remove.mutate()
            }}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </div>
    </div>
  )
}
