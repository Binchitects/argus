import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, CircleCheck, CircleDashed, CircleDot, CircleSlash, ListChecks, PencilLine, X } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { Plan, PlanStep } from '@/pages/plans'

const marks: Record<PlanStep['status'], { icon: typeof CircleCheck; label: string; className: string }> = {
  done: { icon: CircleCheck, label: 'Done', className: 'text-success' },
  doing: { icon: CircleDot, label: 'Doing', className: 'text-primary-ink' },
  blocked: { icon: CircleSlash, label: 'Blocked', className: 'text-destructive' },
  todo: { icon: CircleDashed, label: 'To do', className: 'text-muted-foreground' },
}

/**
 * The plan an answer made or changed, as it stands now, under the answer: each step as the chat carries it out. A plan the
 * chat proposed is reviewed here: approved (the chat carries it out), a step changed, or rejected; the answer is sent as
 * the person's next message (<paramref name="onAnswer"/>; without it, the card only shows the plan).
 */
export function PlanCard({ id, live, onAnswer }: { id: string; live: boolean; onAnswer?: (text: string) => Promise<boolean> }) {
  const queryClient = useQueryClient()
  const plan = useQuery({
    queryKey: ['plans', id] as const,
    queryFn: ({ signal }) => api<Plan>(`/api/plans/${id}`, { signal }),
    // Read again while the chat works on it.
    refetchInterval: live ? 2000 : false,
    retry: false,
  })
  const [changing, setChanging] = useState<number | null>(null)
  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const p = plan.data
  if (!p) return null
  const proposed = p.review === 'proposed'
  const review = async (action: 'approve' | 'change' | 'reject', step?: number, words?: string) => {
    if (!onAnswer) return
    setSending(true)
    try {
      const { message } = await api<{ message: string }>(`/api/plans/${id}/review`, { body: { action, step, text: words } })
      await queryClient.invalidateQueries({ queryKey: ['plans', id] })
      if (await onAnswer(message)) {
        setChanging(null)
        setText('')
      }
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setSending(false)
    }
  }
  const done = p.steps.filter((s) => s.status === 'done').length
  return (
    <section aria-label={`Plan: ${p.title}`} className={cn('my-2 grid gap-2 rounded-lg border p-3 text-sm', proposed && 'border-warning/40 bg-warning/5')}>
      <div className="flex flex-wrap items-center gap-2">
        <ListChecks className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <p className="min-w-0 flex-1 font-medium">{p.title}</p>
        <span className="text-xs text-muted-foreground tabular-nums">
          {proposed ? 'Proposed: waiting for you' : `${done} of ${p.steps.length} done`}
        </span>
      </div>
      {p.goal && <p className="text-muted-foreground">{p.goal}</p>}
      <ol className="grid gap-1.5" aria-label="Its steps">
        {p.steps.map((s) => {
          const mark = marks[s.status]
          const Icon = mark.icon
          return (
            <li key={s.number} className="grid gap-1">
              <p className="flex items-start gap-2">
                <Icon className={cn('mt-0.5 size-4 shrink-0', mark.className)} aria-label={mark.label} />
                <span className={cn('min-w-0 flex-1', s.status === 'done' && 'text-muted-foreground')}>
                  {s.number}. {s.title}
                  {s.check && <span className="block text-xs text-muted-foreground">Done when: {s.check}</span>}
                </span>
                {proposed && onAnswer && (
                  <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={sending} onClick={() => setChanging(s.number)} aria-label={`Change step ${s.number}`}>
                    <PencilLine /> Change
                  </Button>
                )}
              </p>
              {changing === s.number && (
                <form
                  className="ms-6 grid gap-1.5"
                  onSubmit={(e) => {
                    e.preventDefault()
                    if (text.trim()) void review('change', s.number, text.trim())
                  }}
                >
                  <Textarea autoFocus rows={2} value={text} onChange={(e) => setText(e.target.value)} aria-label={`What should change in step ${s.number}`} placeholder={`What should change in step ${s.number}?`} />
                  <div className="flex gap-1.5">
                    <Button type="submit" size="sm" disabled={sending || !text.trim()}>
                      Send
                    </Button>
                    <Button type="button" size="sm" variant="ghost" onClick={() => setChanging(null)}>
                      Cancel
                    </Button>
                  </div>
                </form>
              )}
            </li>
          )
        })}
      </ol>
      <div className="flex flex-wrap items-center gap-2">
        {proposed && onAnswer && (
          <>
            <Button size="sm" disabled={sending} onClick={() => void review('approve')}>
              <Check /> Approve and carry it out
            </Button>
            <Button size="sm" variant="outline" disabled={sending} onClick={() => void review('reject')}>
              <X /> Reject
            </Button>
          </>
        )}
        <Link to={`/plans/${p.id}`} className="ms-auto text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline">
          Board and timeline
        </Link>
      </div>
    </section>
  )
}
