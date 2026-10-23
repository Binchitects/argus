import { useQuery } from '@tanstack/react-query'
import { ArrowUp, Clock3, ListEnd, Paperclip, Square, TextQuote } from 'lucide-react'
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { missingOf, skillsQuery, slashItems, slashQuery, type SkillCall, type SkillItem, type SkillUse, type SlashItem } from '@/lib/skills'
import { cn } from '@/lib/utils'
import type { CompactSetting, ContextView } from './context'
import { ContextGauge } from './context-gauge'
import { VoiceButton } from './media'
import { useRecall, type RecallSource } from './recall'
import type { Quote } from './quote'
import { QuoteChip } from './quote-parts'
import { SkillFields, SlashMenu } from './slash'
import type { Attachment, ChatModel } from './types'
import { UploadChips } from './upload-chips'

/** A message written while an answer runs, waiting on the server: sent when the answer ends, or at once with Send now. */
export interface Queued {
  key: string
  text: string
  attachments: Attachment[]
  /** The part of an answer it quotes. */
  quote?: string | null
  /** The skill it uses. */
  skill?: SkillCall | null
}
import type { Uploads } from './uploads'

/**
 * Where the question is written. Enter sends, Shift+Enter adds a line; files come
 * by the button, by pasting, or by dropping them on the page. A question that did
 * not reach the server comes back here rather than being lost. "/" at the start
 * finds a skill: chosen, it sits above the box with a field for each of its
 * parameters, and goes with the message (what is written is its $ARGUMENTS);
 * Backspace in the empty box, or its X, lets it go. ↑ brings back what was sent
 * before (useRecall). Part of an answer quoted (Reply) shows above it, and goes with
 * the message: Backspace at the start of an empty box, or its X, takes it away.
 */
export function Composer({
  streaming,
  onSend,
  onStop,
  uploads,
  model,
  autoFocus,
  big,
  tools,
  context,
  compact,
  onCompact,
  queued,
  onQueue,
  onSendNow,
  onUnqueue,
  compare,
  talk,
  history,
  elsewhere,
  quote,
  onClearQuote,
  focusSignal,
  initialText,
}: {
  streaming: boolean
  onSend: (text: string, skill?: SkillUse) => Promise<boolean>
  onStop: () => void
  uploads: Uploads
  model?: ChatModel
  autoFocus?: boolean
  big?: boolean
  /** The chat's tools picker, beside the attach button. */
  tools?: ReactNode
  /** How full the model's context is, and what fills it (from the last answer). */
  context?: ContextView
  /** When the chat compacts itself, for the context gauge. */
  compact?: CompactSetting
  /** Summarize the chat's older messages (also: send /compact). */
  onCompact?: () => void
  /** While an answer runs: messages waiting to be sent, and how to add (false: it was not taken), hurry or drop one. */
  queued?: Queued[]
  onQueue?: (text: string, skill?: SkillUse) => Promise<boolean>
  onSendNow?: (key: string) => void
  onUnqueue?: (key: string) => void
  /** Compare (arena mode): the next question to two models. */
  compare?: ReactNode
  /** The Talk button: a voice conversation. */
  talk?: ReactNode
  /** What ↑ brings back: this chat's messages, then the person's others. */
  history?: RecallSource
  /** Answering elsewhere ("Code Arena is answering"): what is sent is queued, and there is nothing here to stop. */
  elsewhere?: string
  /** The part of an answer the next message quotes, and how to take it away. */
  quote?: Quote | null
  onClearQuote?: () => void
  /** Each change puts the focus in the box (a quote was just added). */
  focusSignal?: number
  /** What the box starts with (what another app shared). */
  initialText?: string
}) {
  const [text, setText] = useState(initialText ?? '')
  const area = useRef<HTMLTextAreaElement>(null)
  const picker = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (focusSignal) area.current?.focus()
  }, [focusSignal])

  // The / menu: the skills are fetched once a message starts with "/".
  const menuId = useId()
  const query = slashQuery(text)
  const library = useQuery({ ...skillsQuery, enabled: query !== null, staleTime: 60_000 })
  const [active, setActive] = useState(0)
  const [closedAt, setClosedAt] = useState<string | null>(null)
  const items = query === null ? [] : slashItems(query, library.data?.skills ?? [], !!onCompact)
  const menuOpen = items.length > 0 && closedAt !== text
  // The skill chosen for the message, and its parameters' values.
  const [chosen, setChosen] = useState<SkillItem | null>(null)
  const [values, setValues] = useState<Record<string, string>>({})
  const [missing, setMissing] = useState<string[]>([])
  const fields = useRef<Record<string, HTMLTextAreaElement | null>>({})
  // ↑ and ↓: what was sent before. Not while a skill is chosen; a message brought back keeps the / menu shut.
  const recall = useRecall({ area, setText, source: history, enabled: !chosen, onRecall: setClosedAt })

  useEffect(() => {
    const el = area.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 280)}px`
  }, [text])

  const unchoose = () => {
    setChosen(null)
    setValues({})
    setMissing([])
  }
  const pick = (item: SlashItem) => {
    setActive(0)
    if (item.command === 'compact') {
      setText('')
      onCompact?.()
      return
    }
    const skill = item.skill!
    setText('')
    setChosen(skill)
    setValues({})
    setMissing([])
    const first = skill.parameters[0]?.name
    requestAnimationFrame(() => (first ? fields.current[first] : area.current)?.focus())
  }

  const hasContent = text.trim().length > 0 || uploads.attachments.length > 0 || !!chosen
  // An answer runs, here or elsewhere: what is sent waits its turn.
  const queueing = streaming || !!elsewhere
  const canSend = (!queueing || !!onQueue) && !uploads.busy && hasContent
  const submit = async () => {
    if (!canSend) return
    // A skill goes with the parameters it needs filled in.
    const empty = chosen ? missingOf(chosen, values) : []
    if (empty.length > 0) {
      setMissing(empty)
      fields.current[empty[0]!]?.focus()
      return
    }
    const t = text.trim()
    const skill = chosen ? { name: chosen.name, args: Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim())) } : undefined
    const before = { text, chosen, values }
    setText('')
    unchoose()
    recall.reset()
    // An answer is running: this one waits its turn (or goes at once with Send now).
    if (!(await (queueing && onQueue ? onQueue(t, skill) : onSend(t, skill)))) {
      setText((now) => now || before.text)
      if (before.chosen) {
        setChosen(before.chosen)
        setValues(before.values)
      }
    }
    area.current?.focus()
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (menuOpen) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        setActive((i) => (i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length)
        return
      }
      if ((e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) || e.key === 'Tab') {
        e.preventDefault()
        pick(items[Math.min(active, items.length - 1)]!)
        return
      }
      if (e.key === 'Escape') {
        e.preventDefault()
        setClosedAt(text)
        return
      }
    }
    // Backspace at the start of an empty box lets the skill go, then takes the quote away.
    if (e.key === 'Backspace' && (chosen || (quote && onClearQuote)) && !text && e.currentTarget.selectionStart === 0 && e.currentTarget.selectionEnd === 0) {
      e.preventDefault()
      if (chosen) unchoose()
      else onClearQuote?.()
      return
    }
    if (recall.onKeyDown(e)) return
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void submit()
    }
  }
  return (
    <form
      className={cn('relative rounded-2xl border bg-card shadow-sm transition-shadow focus-within:border-primary/50 focus-within:shadow-md', big && 'shadow-md')}
      onSubmit={(e) => {
        e.preventDefault()
        void submit()
      }}
    >
      {menuOpen && <SlashMenu id={menuId} items={items} active={Math.min(active, items.length - 1)} onPick={pick} onActive={setActive} />}
      {chosen && (
        <SkillFields
          skill={chosen}
          values={values}
          missing={missing}
          refs={fields}
          onChange={(name, value) => {
            setValues((v) => ({ ...v, [name]: value }))
            setMissing((m) => m.filter((x) => x !== name))
          }}
          onClear={() => {
            unchoose()
            area.current?.focus()
          }}
          onLast={() => area.current?.focus()}
        />
      )}
      {queued && queued.length > 0 && (
        <div className="grid gap-1 px-3 pt-3">
          <ul className="grid gap-1" aria-label="Queued messages">
            {queued.map((q) => (
              <li key={q.key} className="flex min-w-0 items-center gap-2 rounded-lg border border-dashed bg-muted/40 py-1 ps-2.5 pe-1 text-sm">
                <Clock3 className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span className="sr-only">Queued:</span>
                {q.quote && (
                  <>
                    <TextQuote className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="sr-only">Quoting part of an answer:</span>
                  </>
                )}
                <span dir="auto" className="min-w-0 flex-1 truncate">
                  {q.skill && <span className="font-mono text-[0.8125rem] font-medium">/{q.skill.name} </span>}
                  {q.text || (q.skill ? '' : `${q.attachments.length} file${q.attachments.length === 1 ? '' : 's'}`)}
                  {q.text && q.attachments.length > 0 && <span className="text-muted-foreground"> · {q.attachments.length} file{q.attachments.length === 1 ? '' : 's'}</span>}
                </span>
                <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => onSendNow?.(q.key)}>
                  Send now
                </Button>
                <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => onUnqueue?.(q.key)} aria-label={`Cancel queued message: ${q.text || 'files'}`}>
                  Cancel
                </Button>
              </li>
            ))}
          </ul>
          <p className="text-xs text-muted-foreground">Sent in turn when the answer ends, even if you close the page; Send now stops the answer and sends it at once.</p>
        </div>
      )}
      {quote && onClearQuote && (
        <QuoteChip
          quote={quote}
          onRemove={() => {
            onClearQuote()
            area.current?.focus()
          }}
        />
      )}
      <output className="sr-only">{quote ? 'Quoted in your reply' : ''}</output>
      <UploadChips uploads={uploads} model={model} />
      <textarea
        ref={area}
        dir="auto"
        rows={big ? 3 : 1}
        value={text}
        onChange={(e) => {
          setText(e.target.value)
          setActive(0)
        }}
        onKeyDown={onKey}
        aria-autocomplete="list"
        aria-controls={menuOpen ? menuId : undefined}
        aria-activedescendant={menuOpen ? `${menuId}-${Math.min(active, items.length - 1)}` : undefined}
        onPaste={(e) => {
          if (e.clipboardData.files.length) {
            e.preventDefault()
            uploads.add(e.clipboardData.files)
          }
        }}
        placeholder={queueing && onQueue ? 'Queue a message…' : chosen ? `Anything to add for /${chosen.name} (optional)` : 'Message, or / for a skill'}
        aria-label="Message"
        // oxlint-disable-next-line jsx-a11y/no-autofocus -- the chat's whole purpose is this box
        autoFocus={autoFocus}
        className={cn('block max-h-72 w-full resize-none bg-transparent px-4 pt-3 text-[0.9375rem] leading-relaxed outline-none placeholder:text-muted-foreground', big && 'min-h-20')}
      />
      <div className="flex items-center gap-2 px-2 pt-1 pb-2">
        <input ref={picker} type="file" multiple hidden onChange={(e) => { if (e.target.files) uploads.add(e.target.files); e.target.value = '' }} aria-label="Attach files" />
        <Tooltip content="Attach files: Word, Excel, PowerPoint, PDF, text, code, images, sound, video">
          <Button type="button" variant="ghost" size="icon-sm" onClick={() => picker.current?.click()} aria-label="Attach">
            <Paperclip />
          </Button>
        </Tooltip>
        <VoiceButton onRecorded={(f) => uploads.add([f])} />
        {talk}
        {tools}
        {compare}
        <span className="hidden text-xs text-muted-foreground xl:inline">Enter to send · Shift+Enter for a new line</span>
        <span className="ml-auto" />
        {context && <ContextGauge context={context} onCompact={onCompact} busy={streaming} compact={compact} />}
        {streaming && onQueue && hasContent && (
          <Tooltip content="Queue: sent when the answer ends (Enter)">
            <Button type="submit" size="icon-sm" variant="outline" className="animate-pop rounded-full" disabled={!canSend} aria-label="Queue">
              <ListEnd />
            </Button>
          </Tooltip>
        )}
        {streaming ? (
          <Button type="button" size="icon-sm" variant="secondary" className="animate-pop rounded-full" onClick={onStop} aria-label="Stop">
            <Square className="fill-current" />
          </Button>
        ) : elsewhere && onQueue ? (
          // Answered elsewhere: nothing to stop here, and what is sent waits for it.
          <Tooltip content={`${elsewhere}: queued, it is sent when that ends (Enter)`}>
            <Button type="submit" size="icon-sm" className="animate-pop rounded-full" disabled={!canSend} aria-label="Queue">
              <ListEnd />
            </Button>
          </Tooltip>
        ) : (
          <Button type="submit" size="icon-sm" className="animate-pop rounded-full" disabled={!canSend} aria-label="Send">
            <ArrowUp />
          </Button>
        )}
      </div>
    </form>
  )
}
