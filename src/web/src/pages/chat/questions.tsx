import { MessageCircleQuestion, Send } from 'lucide-react'
import { useId, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { questionsOf, replyOf } from './ask'

/**
 * Questions the model put to the person, as Claude asks them: pick one (or
 * several), or write your own. Sent, the choices are the person's next message.
 * Without onAnswer (answered already, or the answer still being written) it only shows them.
 */
export function QuestionCard({ raw, onAnswer }: { raw: string; onAnswer?: (text: string) => Promise<boolean> }) {
  const questions = useMemo(() => questionsOf(raw), [raw])
  const [picked, setPicked] = useState<string[][]>(() => questions.map(() => []))
  const [other, setOther] = useState<string[]>(() => questions.map(() => ''))
  const [sending, setSending] = useState(false)
  const id = useId()
  if (!questions.length) return null
  const ready = questions.every((_, i) => picked[i]!.length > 0 || other[i]!.trim())
  const choose = (i: number, label: string, multiple: boolean, on: boolean) => {
    setPicked((p) => p.map((x, j) => (j !== i ? x : multiple ? (on ? [...x, label] : x.filter((l) => l !== label)) : [label])))
    // One choice: picking one clears what was written as "Other".
    if (!multiple) setOther((o) => o.map((x, j) => (j === i ? '' : x)))
  }
  const write = (i: number, text: string) => {
    setOther((o) => o.map((x, j) => (j === i ? text : x)))
    if (!questions[i]!.multiple && text) setPicked((p) => p.map((x, j) => (j === i ? [] : x)))
  }
  const submit = async () => {
    if (!onAnswer || !ready) return
    setSending(true)
    if (!(await onAnswer(replyOf(questions, picked, other)))) setSending(false)
  }

  return (
    <section aria-label="Questions for you" className="my-3 animate-enter rounded-xl border bg-card p-4 shadow-xs">
      <p className="mb-3 flex items-center gap-2 text-sm font-medium">
        <MessageCircleQuestion className="size-4 text-primary-ink" aria-hidden="true" />
        {onAnswer ? (questions.length > 1 ? 'A few questions for you' : 'A question for you') : 'Asked you'}
      </p>
      <form
        className="grid gap-5"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        {questions.map((q, i) => (
          <fieldset key={i} disabled={!onAnswer || sending} className="grid min-w-0 gap-2">
            <legend dir="auto" className="mb-2 text-[0.9375rem] font-medium">
              {q.question}
              {q.multiple && <span className="ms-2 text-xs font-normal text-muted-foreground">Pick any</span>}
            </legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {q.options.map((o) => (
                <label
                  key={o.label}
                  dir="auto"
                  className={cn(
                    'flex cursor-pointer items-start gap-2.5 rounded-lg border px-3 py-2 text-sm transition-colors hover:bg-accent/50 has-[:checked]:border-primary has-[:checked]:bg-primary/5 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring',
                    !onAnswer && 'cursor-default opacity-80 hover:bg-transparent',
                  )}
                >
                  <input
                    type={q.multiple ? 'checkbox' : 'radio'}
                    name={`${id}-${i}`}
                    value={o.label}
                    checked={picked[i]!.includes(o.label)}
                    onChange={(e) => choose(i, o.label, q.multiple, e.target.checked)}
                    className="mt-0.5 size-4 shrink-0 accent-primary outline-none"
                  />
                  <span className="grid min-w-0">
                    <span className="font-medium">{o.label}</span>
                    {o.description && <span className="text-xs text-muted-foreground">{o.description}</span>}
                  </span>
                </label>
              ))}
            </div>
            {onAnswer && (
              <Input dir="auto" value={other[i]} onChange={(e) => write(i, e.target.value)} placeholder="Or write your own" aria-label={`Your own answer: ${q.question}`} className="h-9" />
            )}
          </fieldset>
        ))}
        {onAnswer && (
          <div className="flex items-center justify-end gap-3">
            {!ready && <span className="text-xs text-muted-foreground">Answer {questions.length > 1 ? 'each question' : 'the question'} to send.</span>}
            <Button type="submit" size="sm" disabled={!ready} loading={sending}>
              <Send /> Send {questions.length > 1 ? 'answers' : 'answer'}
            </Button>
          </div>
        )}
      </form>
    </section>
  )
}
