import { useQuery } from '@tanstack/react-query'
import { MessageSquareText, Scale, Share2, ThumbsDown, ThumbsUp, Vote } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Stat, StatGrid } from '@/components/app/stat'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { ago, formatValue, when } from '@/lib/format'
import { presets, resolve } from '@/lib/time'
import { cn } from '@/lib/utils'
import { LeaderboardTable, type Board } from '@/pages/leaderboard'
import { reasonLabel } from '@/pages/chat/quality'

interface Counts {
  answers: number
  rated: number
  up: number
  down: number
  ratedShare: number
  /** Of the rated, the share up; null when none was rated. */
  upRate: number | null
  reasons: Record<string, number>
}

interface Quality {
  from: string
  to: string
  total: Counts
  models: { model: string; counts: Counts }[]
  assistants: { id: string | null; name: string | null; counts: Counts }[]
  latest: { id: string; at: string; title: string; model: string | null; reason: string | null; comment: string | null; shared: boolean; assistant: string | null; person: string | null }[]
  leaderboard: Board
}

interface SharedChat {
  title: string
  reason: string | null
  comment: string | null
  person: string | null
  model: string | null
  messages: { id: string; role: 'user' | 'assistant' | 'tool'; content: string; toolName: string | null; model: string | null; rated: boolean }[]
}

/** "Too long 3 · Wrong 1": the reasons, most given first. */
const reasonsLine = (reasons: Record<string, number>) =>
  Object.entries(reasons)
    .sort((a, b) => b[1] - a[1])
    .map(([r, n]) => `${reasonLabel(r)} ${n}`)
    .join(' · ') || '—'

function CountsTable({ rows, first, label }: { rows: { key: string; name: string; counts: Counts }[]; first: string; label: string }) {
  return (
    <div className="overflow-x-auto rounded-lg border">
      <table className="w-full text-sm" aria-label={label}>
        <thead className="border-b bg-muted/40">
          <tr>
            {[first, 'Answers', 'Rated', 'Thumbs up', 'Why down'].map((h, i) => (
              <th key={h} scope="col" className={cn('h-9 px-3 text-xs font-medium text-muted-foreground', i === 0 || i === 4 ? 'text-left' : 'text-right')}>
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key} className="border-b last:border-0">
              <td className="px-3 py-2 font-medium">{r.name}</td>
              <td className="px-3 py-2 text-right tabular-nums">{r.counts.answers.toLocaleString()}</td>
              <td className="px-3 py-2 text-right tabular-nums">
                {r.counts.rated.toLocaleString()} <span className="text-muted-foreground">({formatValue(r.counts.ratedShare, 'percentunit', 0)})</span>
              </td>
              <td className="px-3 py-2 text-right tabular-nums">{r.counts.upRate === null ? '—' : formatValue(r.counts.upRate, 'percentunit', 0)}</td>
              <td className="px-3 py-2 text-muted-foreground">{reasonsLine(r.counts.reasons)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * Admin → Quality: how people rate the answers, per model and per assistant, in a time
 * range; the latest down-rated answers (a chat only opens when its owner shared it);
 * and the arena's leaderboard from the votes cast in the range.
 */
export function QualityPage() {
  const [from, setFrom] = useState('now-30d')
  const [open, setOpen] = useState<string | null>(null)
  const q = useQuery({
    queryKey: ['admin', 'quality', from],
    queryFn: ({ signal }) => api<Quality>(`/api/admin/quality?${new URLSearchParams({ from: resolve(from).toISOString(), to: new Date().toISOString() })}`, { signal }),
  })
  const d = q.data
  return (
    <>
      <PageHeader title="Quality" description="How people rate the answers (thumbs up or down, and why), per model and per assistant, and how the models fare when people compare them blind." />
      <div className="grid gap-6">
        <Segmented label="Time range" value={from} onChange={setFrom} options={presets.map((p) => ({ value: p.from, label: p.label.replace('Last ', '') }))} />
        {q.error && <QueryError error={q.error} retry={() => q.refetch()} />}
        {q.isPending && <PageSkeleton />}
        {d && (
          <>
            <StatGrid className="xl:grid-cols-4">
              <Stat icon={MessageSquareText} label="Answers" value={d.total.answers.toLocaleString()} />
              <Stat icon={Vote} label="Rated" value={formatValue(d.total.ratedShare, 'percentunit', 0)} hint={`${d.total.rated.toLocaleString()} answers`} />
              <Stat icon={ThumbsUp} label="Thumbs up" value={d.total.upRate === null ? '—' : formatValue(d.total.upRate, 'percentunit', 0)} hint="of the rated answers" />
              <Stat icon={ThumbsDown} label="Thumbs down" value={d.total.down.toLocaleString()} hint={reasonsLine(d.total.reasons)} />
            </StatGrid>
            <Card>
              <CardHeader>
                <CardTitle>By model</CardTitle>
                <CardDescription>The answers each model wrote in this time range, and how they were rated.</CardDescription>
              </CardHeader>
              <CardContent>
                {d.models.length === 0 ? (
                  <EmptyState title="No answers in this time range" />
                ) : (
                  <CountsTable label="By model" first="Model" rows={d.models.map((m) => ({ key: m.model, name: m.model, counts: m.counts }))} />
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>By assistant</CardTitle>
                <CardDescription>Chats with an assistant read its instructions and files: its ratings say how well those serve.</CardDescription>
              </CardHeader>
              <CardContent>
                {d.assistants.length === 0 ? (
                  <EmptyState title="No answers in this time range" />
                ) : (
                  <CountsTable label="By assistant" first="Assistant" rows={d.assistants.map((p) => ({ key: p.id ?? 'none', name: p.name ?? 'No assistant', counts: p.counts }))} />
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Latest down-rated answers</CardTitle>
                <CardDescription>The chat's title, the model and the reason. A chat opens only when its owner shared it with the admins; each opening is in the audit log.</CardDescription>
              </CardHeader>
              <CardContent>
                {d.latest.length === 0 ? (
                  <EmptyState icon={ThumbsDown} title="No answer was rated down in this time range" />
                ) : (
                  <ul className="divide-y rounded-lg border" aria-label="Latest down-rated answers">
                    {d.latest.map((f) => (
                      <li key={f.id} className="flex flex-wrap items-start gap-x-3 gap-y-1 px-3 py-2.5 text-sm">
                        <div className="grid min-w-0 flex-1 gap-0.5">
                          <span className="truncate font-medium">{f.title}</span>
                          <span className="flex flex-wrap items-center gap-x-2 text-xs text-muted-foreground">
                            <Badge variant="destructive">{reasonLabel(f.reason)}</Badge>
                            {f.model && <span>{f.model}</span>}
                            {f.assistant && <span>· {f.assistant}</span>}
                            <time dateTime={f.at} title={when(f.at)}>
                              · {ago(f.at)}
                            </time>
                            {f.person && <span>· {f.person}</span>}
                          </span>
                          {f.comment && <span className="text-muted-foreground">“{f.comment}”</span>}
                        </div>
                        {f.shared ? (
                          <Button variant="outline" size="sm" className="h-7" onClick={() => setOpen(f.id)}>
                            <Share2 /> Open the shared chat
                          </Button>
                        ) : (
                          <span className="text-xs text-muted-foreground">Not shared</span>
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Arena leaderboard</CardTitle>
                <CardDescription>
                  From the votes people cast in this time range with Compare in the chat: two models answer one question, blind, and the person picks the better answer.
                </CardDescription>
              </CardHeader>
              <CardContent>
                {d.leaderboard.models.length === 0 ? (
                  <EmptyState icon={Scale} title="No votes in this time range">People vote with Compare in the chat's message box.</EmptyState>
                ) : (
                  <LeaderboardTable board={d.leaderboard} label="Arena leaderboard" />
                )}
              </CardContent>
            </Card>
          </>
        )}
      </div>
      <SharedChatDialog id={open} onClose={() => setOpen(null)} />
    </>
  )
}

/** A chat its owner shared with the down vote: read only, down to the rated answer. */
function SharedChatDialog({ id, onClose }: { id: string | null; onClose: () => void }) {
  const chat = useQuery({
    queryKey: ['admin', 'quality', 'shared', id],
    queryFn: ({ signal }) => api<SharedChat>(`/api/admin/quality/feedback/${id}`, { signal }),
    enabled: !!id,
  })
  const c = chat.data
  return (
    <Dialog open={!!id} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{c?.title ?? 'Shared chat'}</DialogTitle>
          <DialogDescription>
            {c ? `Shared by ${c.person ?? 'its owner'} with a down vote: ${reasonLabel(c.reason)}${c.comment ? `, “${c.comment}”` : ''}.` : 'Loading the chat…'}
          </DialogDescription>
        </DialogHeader>
        {chat.error && <QueryError error={chat.error} retry={() => chat.refetch()} />}
        {chat.isPending && <Skeleton className="h-40" />}
        {c && (
          <ol className="grid gap-3" aria-label="Messages">
            {c.messages.map((m) => (
              <li key={m.id} className={cn('rounded-lg border px-3 py-2 text-sm', m.role === 'user' && 'bg-secondary', m.rated && 'border-destructive/60')}>
                <p className="mb-1 flex items-center gap-2 text-xs font-medium text-muted-foreground">
                  {m.role === 'user' ? 'Question' : m.role === 'tool' ? `Tool: ${m.toolName ?? 'a tool'}` : `Answer${m.model ? ` · ${m.model}` : ''}`}
                  {m.rated && <Badge variant="destructive">Rated down</Badge>}
                </p>
                {m.content && <p dir="auto" className="whitespace-pre-wrap break-words">{m.content}</p>}
              </li>
            ))}
          </ol>
        )}
      </DialogContent>
    </Dialog>
  )
}
