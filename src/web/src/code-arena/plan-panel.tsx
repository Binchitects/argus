import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, CircleCheck, CircleDashed, CircleDot, CircleSlash, ExternalLink, FileCode, PencilLine, Shuffle, X } from 'lucide-react'
import { useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { streamChat } from '@/pages/chat/api'
import { planQuery, type CodePlanStep } from './api'
import { useEditor } from './editor-state'
import { ideUrl } from './http'

const marks: Record<CodePlanStep['status'], { icon: typeof CircleCheck; label: string; className: string }> = {
  done: { icon: CircleCheck, label: 'Done', className: 'text-success' },
  doing: { icon: CircleDot, label: 'Doing', className: 'text-primary' },
  blocked: { icon: CircleSlash, label: 'Blocked', className: 'text-destructive' },
  todo: { icon: CircleDashed, label: 'To do', className: 'text-muted-foreground' },
}

/**
 * The side bar's Plan: the agent's working plan as it changes (its plan tool), each step with what done means, how its
 * check went and the files it changes (a click opens one); where Arena shows it, when the session is kept in step.
 */
export function PlanPanel({ arena }: { arena: string | null }) {
  const plan = useQuery(planQuery)
  const queryClient = useQueryClient()
  const editor = useEditor()
  // A review's words: what to change in a step (its number), another way's why, or a rejection's.
  const [writing, setWriting] = useState<{ action: 'change' | 'another' | 'reject'; step?: number } | null>(null)
  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const p = plan.data
  const proposed = p?.review === 'proposed'
  /** The review goes to the agent as the person's next message: the chat shows the turn it starts. */
  const review = async (action: string, step?: number, words?: string) => {
    setSending(true)
    const stop = new AbortController()
    try {
      await streamChat(ideUrl('/api/plan/review'), { action, step, text: words }, () => stop.abort(), stop.signal)
    } catch (e) {
      if (!stop.signal.aborted) toast.error(errorMessage(e))
    } finally {
      setSending(false)
      setWriting(null)
      setText('')
      await queryClient.invalidateQueries({ queryKey: planQuery.queryKey })
    }
  }
  const done = p?.steps.filter((s) => s.status === 'done').length ?? 0
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Plan</h2>
        {p && p.steps.length > 0 && (
          <span className="text-xs text-muted-foreground tabular-nums">
            {done}/{p.steps.length} done
          </span>
        )}
      </div>
      {!p || (p.steps.length === 0 && !p.title) ? (
        <p className="px-3 text-xs text-muted-foreground">No plan yet: for work of several steps the agent makes one before it starts, and keeps it here as it goes.</p>
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-3">
          <p className="text-sm font-medium">{p.title}</p>
          {p.goal && <p className="mt-0.5 text-xs text-muted-foreground">{p.goal}</p>}
          {proposed && (
            <section aria-label="Review the plan" className="mt-2 grid gap-1.5 rounded border border-warning/40 bg-warning/8 p-2 text-xs">
              <p>The agent proposes this plan. Approve it to carry it out, change a step, ask for another way, or reject it.</p>
              {p.critique ? (
                <div aria-label="Its second reading" className="rounded bg-background/60 p-1.5">
                  <p className="font-medium">A second reading, with fresh eyes, said:</p>
                  <p className="whitespace-pre-wrap text-muted-foreground">{p.critique}</p>
                </div>
              ) : (
                p.critique === '' && <p className="text-muted-foreground">A second reading found nothing to add.</p>
              )}
              <div className="flex flex-wrap gap-1">
                <button type="button" disabled={sending} onClick={() => void review('approve')} className="flex h-6 items-center gap-1 rounded bg-primary px-2 text-primary-foreground disabled:opacity-50">
                  <Check className="size-3.5" aria-hidden="true" /> Approve
                </button>
                <button type="button" disabled={sending} onClick={() => void review('approve-ask')} className="flex h-6 items-center gap-1 rounded border px-2 hover:bg-accent disabled:opacity-50">
                  Approve, asking before each change
                </button>
                <button type="button" disabled={sending} onClick={() => setWriting({ action: 'another' })} className="flex h-6 items-center gap-1 rounded border px-2 hover:bg-accent disabled:opacity-50">
                  <Shuffle className="size-3.5" aria-hidden="true" /> Another way
                </button>
                <button type="button" disabled={sending} onClick={() => setWriting({ action: 'reject' })} className="flex h-6 items-center gap-1 rounded border px-2 hover:bg-accent disabled:opacity-50">
                  <X className="size-3.5" aria-hidden="true" /> Reject
                </button>
              </div>
              {writing && (
                <form
                  className="grid gap-1"
                  onSubmit={(e) => {
                    e.preventDefault()
                    if (writing.action === 'change' && !text.trim()) return
                    void review(writing.action, writing.step, text.trim() || undefined)
                  }}
                >
                  <textarea
                    autoFocus
                    value={text}
                    onChange={(e) => setText(e.target.value)}
                    rows={2}
                    aria-label={writing.action === 'change' ? `What should change in step ${writing.step}` : writing.action === 'another' ? 'Why another way' : 'Why reject it'}
                    placeholder={writing.action === 'change' ? `What should change in step ${writing.step}?` : writing.action === 'another' ? 'Why another way? (optional)' : 'Why? (optional)'}
                    className="resize-none rounded border bg-background px-2 py-1 outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  />
                  <div className="flex gap-1">
                    <button type="submit" disabled={sending || (writing.action === 'change' && !text.trim())} className="h-6 rounded bg-primary px-2 text-primary-foreground disabled:opacity-50">
                      Send
                    </button>
                    <button type="button" onClick={() => setWriting(null)} className="h-6 rounded border px-2 hover:bg-accent">
                      Cancel
                    </button>
                  </div>
                </form>
              )}
            </section>
          )}
          {arena && p.chat && (
            <a href={`${arena.replace(/\/$/, '')}/chat/${p.chat}`} target="_blank" rel="noopener noreferrer" className="mt-1 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
              In Arena, with its chat <ExternalLink className="size-3" aria-hidden="true" />
            </a>
          )}
          <ol className="mt-2 grid gap-2" aria-label="Steps">
            {p.steps.map((s) => {
              const mark = marks[s.status]
              const Icon = mark.icon
              return (
                <li key={s.number} className={cn('grid gap-0.5 rounded border p-2 text-xs', s.status === 'doing' && 'border-primary/50 bg-primary/5')}>
                  <p className="flex items-start gap-1.5">
                    <Icon className={cn('mt-px size-3.5 shrink-0', mark.className)} aria-label={mark.label} />
                    <span className={cn('min-w-0 flex-1', s.status === 'done' && 'text-muted-foreground')}>
                      {s.number}. {s.title}
                    </span>
                  </p>
                  {s.check && (
                    <p className="ms-5 text-muted-foreground">
                      Done when: <code className="break-words text-foreground">{s.check}</code>
                      {s.checkResult && (
                        <span title={s.checkOutput ?? undefined} className={cn('ms-1 font-medium', s.checkResult === 'passed' ? 'text-success' : 'text-destructive')}>
                          {s.checkResult === 'passed' ? '· passed' : '· failed'}
                        </span>
                      )}
                    </p>
                  )}
                  {s.dependsOn.length > 0 && <p className="ms-5 text-muted-foreground">after {s.dependsOn.join(', ')}</p>}
                  {s.files.length > 0 && (
                    <ul className="ms-5 flex flex-wrap gap-1" aria-label={`Step ${s.number}'s files`}>
                      {s.files.map((f) => (
                        <li key={f}>
                          <button type="button" onClick={() => void editor.open(f)} className="flex items-center gap-1 rounded bg-muted px-1 py-0.5 hover:bg-accent">
                            <FileCode className="size-3" aria-hidden="true" />
                            {f}
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  {s.symbols && s.symbols.length > 0 && (
                    <p className="ms-5 text-muted-foreground">
                      Changes:{' '}
                      {s.symbols.map((n, i) => (
                        <span key={n}>
                          {i > 0 && ', '}
                          <code className="text-foreground">{n}</code>
                        </span>
                      ))}
                    </p>
                  )}
                  {s.impact && (
                    <p aria-label={`What step ${s.number} reaches`} className="ms-5 whitespace-pre-wrap rounded bg-muted/60 px-1.5 py-1 text-muted-foreground">
                      {s.impact}
                    </p>
                  )}
                  {s.notes && <p className="ms-5 whitespace-pre-wrap text-muted-foreground">{s.notes}</p>}
                  {proposed && (
                    <button type="button" disabled={sending} onClick={() => setWriting({ action: 'change', step: s.number })} className="ms-5 flex w-fit items-center gap-1 text-muted-foreground hover:text-foreground disabled:opacity-50">
                      <PencilLine className="size-3" aria-hidden="true" /> Change step {s.number}
                    </button>
                  )}
                </li>
              )
            })}
          </ol>
        </div>
      )}
    </div>
  )
}
