import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowLeft, ArrowUp, Copy, Download, FileText, History, MoreHorizontal, Pencil, Play, Plus, Presentation, RefreshCw, Sparkles, Trash2, Upload } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, meQuery } from '@/lib/api'
import { cn } from '@/lib/utils'
import {
  BuiltIn, changeDeck, deckConfigQuery, deckQuery, decksQuery, deckVersionsQuery, editDeck, makeDeck, makePicture, refreshDeck, removeDeck, restoreDeck,
  type Deck, type DeckTemplate, type Slide,
} from './decks-api'
import { DeckPrint, Present } from './present'
import { SlideForm } from './slide-form'
import { SlideView } from './slide-view'

/** Workspace → Decks: a new deck from a few words, and one's decks, the latest changed first. */
export function DecksPage() {
  const decks = useQuery(decksQuery)
  const config = useQuery(deckConfigQuery)
  const me = useQuery(meQuery)
  const navigate = useNavigate()
  const [about, setAbout] = useState('')
  const [template, setTemplate] = useState<string | null>(null)
  const [templates, setTemplates] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const make = useMutation({
    mutationFn: () => makeDeck({ about: about.trim(), templateId: template ?? undefined }),
    onSuccess: (d) => navigate(`/decks/${d.id}`),
    onError: (e) => setError(errorMessage(e)),
  })
  if (decks.isPending) return <PageSkeleton />
  if (decks.error) return <QueryError error={decks.error} retry={() => decks.refetch()} />
  const choices = config.data?.templates ?? []
  const chosen = template ?? choices.find((t) => t.isDefault)?.id ?? BuiltIn
  const themeOf = (id: string | null) => choices.find((t) => t.id === id)?.theme
  const mayBrand = me.data?.isAdmin || me.data?.permissions?.includes('settings.branding')
  return (
    <>
      <PageHeader
        title="Decks"
        description="Slides from a few words: the model writes them, with charts from the company's data and pictures from the picture model, in the company's look. Change them by asking, present them, download them as PowerPoint or PDF."
        actions={
          mayBrand && (
            <Button variant="outline" onClick={() => setTemplates(true)}>
              <Upload /> Templates
            </Button>
          )
        }
      />
      <Card>
        <CardContent className="pt-6">
          <form
            className="grid gap-3"
            onSubmit={(e) => {
              e.preventDefault()
              setError(null)
              make.mutate()
            }}
          >
            <Field label="Make a deck about…">
              <Textarea
                rows={3}
                maxLength={20000}
                value={about}
                onChange={(e) => setAbout(e.target.value)}
                placeholder="Our third quarter for the board: sales by region from Shop, the two new customers, hiring plans, and what we need from them."
              />
            </Field>
            {error && <Alert variant="destructive">{error}</Alert>}
            <div className="flex flex-wrap items-end justify-end gap-2">
              {choices.length > 0 && (
                <Field label="Look" className="mr-auto min-w-48">
                  <Select value={chosen} onValueChange={setTemplate}>
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {choices.map((t) => (
                        <SelectItem key={t.id} value={t.id}>
                          {t.name}
                        </SelectItem>
                      ))}
                      <SelectItem value={BuiltIn}>Built-in</SelectItem>
                    </SelectContent>
                  </Select>
                </Field>
              )}
              <Button type="submit" loading={make.isPending} disabled={!about.trim()}>
                <Sparkles /> {make.isPending ? 'Writing the slides…' : 'Make it'}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
      {decks.data.length === 0 ? (
        <EmptyState icon={Presentation} title="No decks yet" className="mt-4">
          Say above what a deck is about: the model writes its slides, and you change them by asking.
        </EmptyState>
      ) : (
        <ul className="mt-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-3" aria-label="Decks">
          {decks.data.map((d) => (
            <li key={d.id}>
              <Link to={`/decks/${d.id}`} className="group block rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
                <div className="overflow-hidden rounded-xl border shadow-xs transition-shadow group-hover:shadow-md" aria-hidden="true">
                  {d.first ? <SlideView slide={d.first} theme={themeOf(d.templateId)} thumbnail /> : <div className="aspect-video bg-muted" />}
                </div>
                <p className="mt-2 truncate text-sm font-medium">{d.title}</p>
                <p className="text-xs text-muted-foreground">
                  {d.slides} {d.slides === 1 ? 'slide' : 'slides'} · {new Date(d.updatedAt).toLocaleDateString()}
                </p>
              </Link>
            </li>
          ))}
        </ul>
      )}
      <Dialog open={templates} onOpenChange={setTemplates}>
        <DialogContent className="sm:max-w-lg">{templates && <TemplatesForm />}</DialogContent>
      </Dialog>
    </>
  )
}

/** The company's templates: uploaded as .pptx or .potx, one the default for new decks. */
function TemplatesForm() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery({ queryKey: ['admin', 'deck-templates'], queryFn: ({ signal }) => api<DeckTemplate[]>('/api/admin/deck-templates', { signal }) })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const changed = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'deck-templates'] })
    void queryClient.invalidateQueries({ queryKey: ['decks', 'config'] })
  }
  const upload = async (file: File) => {
    const form = new FormData()
    form.append('file', file)
    setBusy(true)
    setError(null)
    try {
      await api('/api/admin/deck-templates', { body: form })
      changed()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <DialogHeader>
        <DialogTitle>Templates</DialogTitle>
        <DialogDescription>A PowerPoint template of the company (.pptx or .potx): its masters, layouts, colours, fonts and logo carry into every deck made in it. Its own slides are left out.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <ul className="grid gap-2" aria-label="Templates">
        {(list.data ?? []).map((t) => (
          <li key={t.id} className="flex items-center gap-2 rounded-lg border px-3 py-2 text-sm">
            <span className="flex gap-0.5" aria-hidden="true">
              {['accent1', 'accent2', 'accent3', 'dk2'].map((k) => (
                <span key={k} className="size-3 rounded-sm border" style={{ background: t.theme.colors?.[k] }} />
              ))}
            </span>
            <span className="min-w-0 flex-1 truncate">{t.name}</span>
            {t.isDefault ? (
              <Badge variant="secondary">Default</Badge>
            ) : (
              <Button size="sm" variant="ghost" onClick={() => void api(`/api/admin/deck-templates/${t.id}`, { method: 'PATCH', body: { isDefault: true } }).then(changed)}>
                Make it the default
              </Button>
            )}
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Remove ${t.name}`}
              onClick={async () => {
                if (await confirm({ title: `Remove ${t.name}?`, description: 'Decks made in it take the built-in look.', confirm: 'Remove', destructive: true })) {
                  await api(`/api/admin/deck-templates/${t.id}`, { method: 'DELETE' }).catch((e: unknown) => setError(errorMessage(e)))
                  changed()
                }
              }}
            >
              <Trash2 />
            </Button>
          </li>
        ))}
        {list.data?.length === 0 && <li className="text-sm text-muted-foreground">None yet: decks have the built-in look.</li>}
      </ul>
      <DialogFooter>
        <Button asChild variant="outline" loading={busy}>
          <label className="cursor-pointer">
            <Upload /> Upload a template
            <input type="file" accept=".pptx,.potx" className="sr-only" onChange={(e) => e.target.files?.[0] && void upload(e.target.files[0])} />
          </label>
        </Button>
      </DialogFooter>
    </>
  )
}

/** Every version of the deck, newest first; one put back becomes the newest. */
function VersionsForm({ deck, onDone }: { deck: Deck; onDone: (d: Deck) => void }) {
  const versions = useQuery(deckVersionsQuery(deck.id))
  const restore = useMutation({ mutationFn: (v: number) => restoreDeck(deck.id, v), onSuccess: onDone, onError: (e) => toast.error(errorMessage(e)) })
  return (
    <>
      <DialogHeader>
        <DialogTitle>Versions of {deck.title}</DialogTitle>
        <DialogDescription>Each change, asked or by hand, is a version. Putting one back makes it the newest.</DialogDescription>
      </DialogHeader>
      <ul className="grid max-h-[60vh] gap-1.5 overflow-y-auto text-sm" aria-label="Versions">
        {(versions.data ?? []).map((v) => (
          <li key={v.version} className="flex items-center gap-2 rounded-md border px-3 py-2">
            <span className="min-w-0 flex-1">
              <span className="font-medium">Version {v.version}</span>
              <span className="block truncate text-xs text-muted-foreground">
                {new Date(v.createdAt).toLocaleString()} · {v.note}
              </span>
            </span>
            {v.version === deck.version ? (
              <Badge variant="secondary">Now</Badge>
            ) : (
              <Button size="sm" variant="outline" onClick={() => restore.mutate(v.version)} loading={restore.isPending && restore.variables === v.version}>
                Put back
              </Button>
            )}
          </li>
        ))}
      </ul>
    </>
  )
}

const blankSlide: Slide = { layout: 'bullets', title: 'A new slide', bullets: ['A point'] }

/** /decks/:id: the slides, the one chosen large; changed by asking or by hand; presented, printed, downloaded. */
export function DeckPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const deck = useQuery(deckQuery(id))
  const config = useQuery(deckConfigQuery)
  const [chosen, setChosen] = useState(0)
  const [change, setChange] = useState('')
  const [dialog, setDialog] = useState<'slide' | 'versions' | 'rename' | null>(null)
  const [name, setName] = useState('')
  const [presenting, setPresenting] = useState<number | null>(null)
  const [printing, setPrinting] = useState(false)
  const [failed, setFailed] = useState<string[]>([])
  const busy = useRef(false)
  const shown = (d: Deck) => {
    queryClient.setQueryData(deckQuery(id).queryKey, d)
    void queryClient.invalidateQueries({ queryKey: ['decks'], exact: true })
    void queryClient.invalidateQueries({ queryKey: deckVersionsQuery(id).queryKey })
  }
  const ask = useMutation({
    mutationFn: () => changeDeck(id, { change: change.trim(), slide: chosen }),
    onSuccess: (d) => {
      shown(d)
      setChange('')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const edit = useMutation({ mutationFn: (body: Parameters<typeof editDeck>[1]) => editDeck(id, body), onSuccess: shown, onError: (e) => toast.error(errorMessage(e)) })
  const refresh = useMutation({ mutationFn: () => refreshDeck(id), onSuccess: shown, onError: (e) => toast.error(errorMessage(e)) })
  const picture = useMutation({
    mutationFn: (index: number) => makePicture(id, index),
    onSuccess: (d) => queryClient.setQueryData(deckQuery(id).queryKey, d),
    onError: (e, index) => {
      const prompt = deck.data?.slides[index]?.picture?.prompt
      if (prompt) setFailed((f) => [...f, prompt])
      toast.error(errorMessage(e))
    },
  })
  // The slides' pictures, made one at a time while the deck is open (the picture model draws one at a time anyway).
  const wanted = config.data?.pictures ? (deck.data?.slides ?? []).findIndex((s) => s.picture?.prompt && !s.picture.fileId && !failed.includes(s.picture.prompt)) : -1
  useEffect(() => {
    if (wanted < 0 || busy.current) return
    busy.current = true
    picture.mutate(wanted, { onSettled: () => (busy.current = false) })
  }, [wanted, picture])
  const stopPresenting = useCallback((at: number) => {
    setPresenting(null)
    setChosen(at)
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {})
  }, [])
  const stopPrinting = useCallback(() => setPrinting(false), [])

  if (deck.isPending) return <PageSkeleton />
  if (deck.error) return <QueryError error={deck.error} retry={() => deck.refetch()} />
  const d = deck.data
  const at = Math.min(chosen, d.slides.length - 1)
  const slide = d.slides[at]!
  const theme = config.data?.templates.find((t) => t.id === d.templateId)?.theme
  const making = picture.isPending ? picture.variables : null
  const setSlides = (slides: Slide[], note: string, select?: number) => {
    edit.mutate({ slides, note })
    if (select !== undefined) setChosen(select)
  }
  const move = (from: number, to: number) => {
    const slides = [...d.slides]
    const [s] = slides.splice(from, 1)
    slides.splice(to, 0, s!)
    setSlides(slides, `Slide ${from + 1} moved`, to)
  }
  const present = () => {
    setPresenting(at)
    void document.documentElement.requestFullscreen?.().catch(() => {})
  }
  const asks = d.slides.some((s) => s.chart?.ask)

  return (
    <>
      <PageHeader
        title={d.title}
        description={`${d.slides.length} ${d.slides.length === 1 ? 'slide' : 'slides'} · version ${d.version}${theme ? '' : ' · the built-in look'}`}
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/decks">
                <ArrowLeft /> Decks
              </Link>
            </Button>
            <Button onClick={present}>
              <Play /> Present
            </Button>
            <Button variant="outline" asChild>
              <a href={`/api/decks/${d.id}/pptx`} download>
                <Download /> PowerPoint
              </a>
            </Button>
            <Button variant="outline" onClick={() => setPrinting(true)}>
              <FileText /> PDF
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label="More for this deck">
                  <MoreHorizontal />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem
                  onSelect={() => {
                    setName(d.title)
                    setDialog('rename')
                  }}
                >
                  <Pencil /> Rename
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => setDialog('versions')}>
                  <History /> Versions
                </DropdownMenuItem>
                {asks && (
                  <DropdownMenuItem onSelect={() => refresh.mutate()}>
                    <RefreshCw /> Ask the numbers again
                  </DropdownMenuItem>
                )}
                {(config.data?.templates.length ?? 0) > 0 && (
                  <>
                    <DropdownMenuSeparator />
                    <DropdownMenuLabel>Look</DropdownMenuLabel>
                    <DropdownMenuRadioGroup value={d.templateId ?? BuiltIn} onValueChange={(v) => edit.mutate({ templateId: v })}>
                      {config.data!.templates.map((t) => (
                        <DropdownMenuRadioItem key={t.id} value={t.id}>
                          {t.name}
                        </DropdownMenuRadioItem>
                      ))}
                      <DropdownMenuRadioItem value={BuiltIn}>Built-in</DropdownMenuRadioItem>
                    </DropdownMenuRadioGroup>
                  </>
                )}
                <DropdownMenuSeparator />
                <DropdownMenuItem
                  variant="destructive"
                  onSelect={async () => {
                    if (await confirm({ title: `Delete ${d.title}?`, description: 'Its slides and every version go.', confirm: 'Delete', destructive: true })) {
                      await removeDeck(d.id).catch((e: unknown) => toast.error(errorMessage(e)))
                      void queryClient.invalidateQueries({ queryKey: ['decks'] })
                      navigate('/decks')
                    }
                  }}
                >
                  <Trash2 /> Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        }
      />
      <div className="grid gap-4 lg:grid-cols-[11rem_1fr]">
        <ol className="flex gap-2 overflow-x-auto pb-1 lg:max-h-[calc(100dvh-12rem)] lg:flex-col lg:overflow-x-visible lg:overflow-y-auto lg:pr-1" aria-label="Slides">
          {d.slides.map((s, i) => (
            <li key={i} className="w-36 shrink-0 lg:w-auto">
              <button
                type="button"
                aria-current={i === at ? 'true' : undefined}
                aria-label={`Slide ${i + 1}: ${s.title ?? s.quote ?? ''}`}
                onClick={() => setChosen(i)}
                className={cn('block w-full overflow-hidden rounded-md border-2 outline-none focus-visible:ring-[3px] focus-visible:ring-ring', i === at ? 'border-primary' : 'border-transparent')}
              >
                <span className="flex items-start gap-1">
                  <span className="w-4 pt-0.5 text-right text-[0.65rem] text-muted-foreground tabular-nums">{i + 1}</span>
                  <span className="min-w-0 flex-1 overflow-hidden rounded-sm border" aria-hidden="true">
                    <SlideView slide={s} theme={theme} thumbnail making={making === i} />
                  </span>
                </span>
              </button>
            </li>
          ))}
        </ol>
        <div className="grid min-w-0 content-start gap-3">
          <section aria-label={`Slide ${at + 1}`} className="overflow-hidden rounded-xl border shadow-sm">
            <SlideView slide={slide} theme={theme} making={making === at} />
          </section>
          <div className="flex flex-wrap items-center gap-1.5">
            <Button size="sm" variant="outline" onClick={() => setDialog('slide')}>
              <Pencil /> Change by hand
            </Button>
            <Button size="sm" variant="ghost" aria-label="Move it up" disabled={at === 0 || edit.isPending} onClick={() => move(at, at - 1)}>
              <ArrowUp />
            </Button>
            <Button size="sm" variant="ghost" aria-label="Move it down" disabled={at === d.slides.length - 1 || edit.isPending} onClick={() => move(at, at + 1)}>
              <ArrowDown />
            </Button>
            <Button size="sm" variant="ghost" disabled={edit.isPending || d.slides.length >= 30} onClick={() => setSlides([...d.slides.slice(0, at + 1), { ...slide }, ...d.slides.slice(at + 1)], `Slide ${at + 1} copied`, at + 1)}>
              <Copy /> Copy
            </Button>
            <Button size="sm" variant="ghost" disabled={edit.isPending || d.slides.length >= 30} onClick={() => setSlides([...d.slides.slice(0, at + 1), blankSlide, ...d.slides.slice(at + 1)], `A slide added after ${at + 1}`, at + 1)}>
              <Plus /> A slide after
            </Button>
            <Button size="sm" variant="ghost" disabled={edit.isPending || d.slides.length === 1} onClick={() => setSlides(d.slides.filter((_, i) => i !== at), `Slide ${at + 1} taken out`, Math.max(0, at - 1))}>
              <Trash2 /> Take it out
            </Button>
          </div>
          {slide.notes && (
            <div className="rounded-lg border bg-muted/40 px-3 py-2 text-sm">
              <p className="text-xs font-medium text-muted-foreground">Speaker notes</p>
              <p className="whitespace-pre-wrap">{slide.notes}</p>
            </div>
          )}
          <form
            className="grid gap-2 rounded-xl border p-3"
            onSubmit={(e) => {
              e.preventDefault()
              if (change.trim()) ask.mutate()
            }}
          >
            <Field label="Change it by asking">
              <Textarea
                rows={2}
                maxLength={4000}
                value={change}
                onChange={(e) => setChange(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault()
                    if (change.trim() && !ask.isPending) ask.mutate()
                  }
                }}
                placeholder={`"A slide on costs after the third", "make this slide shorter", "a picture of our team at work here"`}
              />
            </Field>
            <div className="flex items-center justify-end gap-2">
              <span className="mr-auto text-xs text-muted-foreground">“This slide” is slide {at + 1}.</span>
              <Button type="submit" loading={ask.isPending} disabled={!change.trim()}>
                <Sparkles /> {ask.isPending ? 'Changing…' : 'Change'}
              </Button>
            </div>
          </form>
        </div>
      </div>
      <Dialog open={dialog !== null} onOpenChange={(o) => !o && setDialog(null)}>
        <DialogContent className={dialog === 'slide' ? 'sm:max-w-2xl' : 'sm:max-w-md'}>
          {dialog === 'slide' && (
            <SlideForm
              key={at}
              slide={slide}
              number={at + 1}
              sources={config.data?.sources ?? []}
              onSave={async (s) => shown(await editDeck(id, { slides: d.slides.map((x, i) => (i === at ? s : x)), note: `Slide ${at + 1} changed by hand` }))}
              onClose={() => setDialog(null)}
            />
          )}
          {dialog === 'versions' && (
            <VersionsForm
              deck={d}
              onDone={(v) => {
                shown(v)
                setDialog(null)
              }}
            />
          )}
          {dialog === 'rename' && (
            <form
              className="grid gap-4"
              onSubmit={(e) => {
                e.preventDefault()
                edit.mutate({ title: name.trim() }, { onSuccess: () => setDialog(null) })
              }}
            >
              <DialogHeader>
                <DialogTitle>Rename the deck</DialogTitle>
              </DialogHeader>
              <Field label="Title">
                <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />
              </Field>
              <DialogFooter>
                <Button type="submit" loading={edit.isPending} disabled={!name.trim()}>
                  Save
                </Button>
              </DialogFooter>
            </form>
          )}
        </DialogContent>
      </Dialog>
      {presenting !== null && <Present slides={d.slides} theme={theme} start={presenting} onClose={stopPresenting} />}
      {printing && <DeckPrint slides={d.slides} theme={theme} onDone={stopPrinting} />}
    </>
  )
}
