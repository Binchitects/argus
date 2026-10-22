import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { api, errorMessage } from '@/lib/api'
import { layoutLabels, type Layout, type Slide } from './decks-api'

const lines = (text: string) => text.split('\n').map((l) => l.trim()).filter(Boolean)

/** A slide changed by hand: its layout, words, picture (described, or one's own) and chart (asked of a database). */
export function SlideForm({ slide, number, sources, onSave, onClose }: { slide: Slide; number: number; sources: string[]; onSave: (s: Slide) => Promise<void>; onClose: () => void }) {
  const [layout, setLayout] = useState<Layout>(slide.layout)
  const [title, setTitle] = useState(slide.title ?? '')
  const [subtitle, setSubtitle] = useState(slide.subtitle ?? '')
  const [bullets, setBullets] = useState((slide.bullets ?? []).join('\n'))
  const [left, setLeft] = useState((slide.left ?? []).join('\n'))
  const [right, setRight] = useState((slide.right ?? []).join('\n'))
  const [quote, setQuote] = useState(slide.quote ?? '')
  const [by, setBy] = useState(slide.by ?? '')
  const [prompt, setPrompt] = useState(slide.picture?.prompt ?? '')
  const [fileId, setFileId] = useState(slide.picture?.fileId ?? null)
  const [source, setSource] = useState(slide.chart?.ask?.source ?? sources[0] ?? '')
  const [question, setQuestion] = useState(slide.chart?.ask?.question ?? '')
  const [chartType, setChartType] = useState<'bar' | 'line'>(slide.chart?.type ?? 'bar')
  const [notes, setNotes] = useState(slide.notes ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const upload = async (file: File) => {
    const form = new FormData()
    form.append('file', file)
    try {
      const sent = await api<{ id: string }>('/api/chat/attachments', { body: form })
      setFileId(sent.id)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const save = async () => {
    const asked = question.trim()
    const chart = layout === 'chart'
      ? asked && (asked !== slide.chart?.ask?.question || source !== slide.chart?.ask?.source)
        ? { type: chartType, categories: [], series: [], ask: { source, question: asked } }
        : slide.chart && { ...slide.chart, type: chartType }
      : null
    // A new description makes a new picture; one's own file stays as it is.
    const picture = prompt.trim() || fileId ? { prompt: prompt.trim(), fileId: prompt.trim() !== (slide.picture?.prompt ?? '') && fileId === slide.picture?.fileId ? null : fileId } : null
    setSaving(true)
    setError(null)
    try {
      await onSave({
        layout, title: title.trim() || null, subtitle: subtitle.trim() || null, bullets: lines(bullets), left: lines(left), right: lines(right), quote: quote.trim() || null,
        by: by.trim() || null, chart, picture: ['bullets', 'picture'].includes(layout) ? picture : null, notes: notes.trim() || null,
      })
      onClose()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <form
      className="grid max-h-[80vh] gap-4 overflow-y-auto pr-1"
      onSubmit={(e) => {
        e.preventDefault()
        void save()
      }}
    >
      <DialogHeader>
        <DialogTitle>Slide {number}</DialogTitle>
        <DialogDescription>Change it by hand. To change it in words, close this and ask below the slide.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Layout">
          <Select value={layout} onValueChange={(v) => setLayout(v as Layout)}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(Object.keys(layoutLabels) as Layout[]).map((l) => (
                <SelectItem key={l} value={l}>
                  {layoutLabels[l]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        {layout !== 'quote' && (
          <Field label="Title">
            <Input value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
          </Field>
        )}
      </div>
      {['title', 'section', 'picture'].includes(layout) && (
        <Field label={layout === 'picture' ? 'Caption' : 'Subtitle'}>
          <Input value={subtitle} maxLength={300} onChange={(e) => setSubtitle(e.target.value)} />
        </Field>
      )}
      {['bullets', 'chart'].includes(layout) && (
        <Field label={layout === 'chart' ? 'The takeaway (one a line)' : 'Points (one a line)'}>
          <Textarea rows={5} value={bullets} onChange={(e) => setBullets(e.target.value)} />
        </Field>
      )}
      {layout === 'two' && (
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Left (one a line)">
            <Textarea rows={5} value={left} onChange={(e) => setLeft(e.target.value)} />
          </Field>
          <Field label="Right (one a line)">
            <Textarea rows={5} value={right} onChange={(e) => setRight(e.target.value)} />
          </Field>
        </div>
      )}
      {layout === 'quote' && (
        <>
          <Field label="Quote">
            <Textarea rows={3} maxLength={500} value={quote} onChange={(e) => setQuote(e.target.value)} />
          </Field>
          <Field label="By">
            <Input value={by} maxLength={200} onChange={(e) => setBy(e.target.value)} />
          </Field>
        </>
      )}
      {['bullets', 'picture'].includes(layout) && (
        <fieldset className="grid gap-2 rounded-lg border p-3">
          <legend className="px-1 text-sm font-medium">Picture</legend>
          <Field label="Describe it (the picture model draws it)">
            <Textarea rows={2} maxLength={1000} value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="A rising arrow over a city at dawn, flat illustration" />
          </Field>
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <label className="cursor-pointer text-primary underline-offset-4 hover:underline">
              Use a picture of yours
              <input type="file" accept="image/png,image/jpeg,image/gif" className="sr-only" onChange={(e) => e.target.files?.[0] && void upload(e.target.files[0])} />
            </label>
            {fileId && (
              <Button type="button" size="sm" variant="ghost" onClick={() => setFileId(null)}>
                Take the picture off
              </Button>
            )}
          </div>
        </fieldset>
      )}
      {layout === 'chart' && (
        <fieldset className="grid gap-2 rounded-lg border p-3">
          <legend className="px-1 text-sm font-medium">Chart</legend>
          {sources.length > 0 ? (
            <div className="grid gap-2 sm:grid-cols-[12rem_1fr]">
              <Field label="Database">
                <Select value={source} onValueChange={setSource}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {sources.map((s) => (
                      <SelectItem key={s} value={s}>
                        {s}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              <Field label="What it counts">
                <Input value={question} maxLength={1000} onChange={(e) => setQuestion(e.target.value)} placeholder="Orders per month this year" />
              </Field>
            </div>
          ) : (
            <p className="text-xs text-muted-foreground">No database is open to you: ask for its numbers in words below the slide, with the numbers.</p>
          )}
          <Field label="Drawn as">
            <Select value={chartType} onValueChange={(v) => setChartType(v as 'bar' | 'line')}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="bar">Bars</SelectItem>
                <SelectItem value="line">A line</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          {slide.chart?.note && <p className="text-xs break-words text-muted-foreground">{slide.chart.note}</p>}
        </fieldset>
      )}
      <Field label="Speaker notes">
        <Textarea rows={3} maxLength={4000} value={notes} onChange={(e) => setNotes(e.target.value)} />
      </Field>
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={saving}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}
