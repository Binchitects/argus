import { useQuery } from '@tanstack/react-query'
import { EyeOff, Scale, Trophy } from 'lucide-react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { api, ApiError } from '@/lib/api'
import { formatValue } from '@/lib/format'

/** A model's place: its Elo rating and its votes ("ties" counts tie and both bad; "bad" the second alone). */
export interface Standing {
  model: string
  rating: number
  matches: number
  wins: number
  losses: number
  ties: number
  bad: number
  winRate: number
}

export interface Board {
  votes: number
  people: number
  models: Standing[]
}

/** The models by rating, with their votes. */
export function LeaderboardTable({ board, label }: { board: Board; label: string }) {
  return (
    <div className="overflow-x-auto rounded-lg border">
      <table className="w-full text-sm" aria-label={label}>
        <thead className="border-b bg-muted/40">
          <tr>
            {['#', 'Model', 'Rating', 'Win rate', 'Won', 'Lost', 'Tied', 'Both bad', 'Votes'].map((h, i) => (
              <th key={h} scope="col" className={`h-9 px-3 text-xs font-medium text-muted-foreground ${i > 1 ? 'text-right' : 'text-left'}`}>
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {board.models.map((s, i) => (
            <tr key={s.model} className="border-b last:border-0">
              <td className="px-3 py-2 tabular-nums text-muted-foreground">{i + 1}</td>
              <td className="px-3 py-2 font-medium">
                <span className="inline-flex items-center gap-1.5">
                  {i === 0 && <Trophy className="size-3.5 text-warning-ink" aria-label="First" />}
                  {s.model}
                </span>
              </td>
              <td className="px-3 py-2 text-right font-semibold tabular-nums">{s.rating}</td>
              <td className="px-3 py-2 text-right tabular-nums">{formatValue(s.winRate, 'percentunit', 0)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{s.wins}</td>
              <td className="px-3 py-2 text-right tabular-nums">{s.losses}</td>
              <td className="px-3 py-2 text-right tabular-nums">{s.ties - s.bad}</td>
              <td className="px-3 py-2 text-right tabular-nums">{s.bad}</td>
              <td className="px-3 py-2 text-right tabular-nums">{s.matches}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** The company's models on its own questions: the arena's votes, for everyone unless an admin keeps it to the admins. */
export function LeaderboardPage() {
  const q = useQuery({
    queryKey: ['arena', 'leaderboard'],
    queryFn: ({ signal }) => api<{ public: boolean; board: Board }>('/api/arena/leaderboard', { signal }),
  })
  const hidden = q.error instanceof ApiError && q.error.http === 403
  return (
    <>
      <PageHeader
        title="Leaderboard"
        description="The company's models on its own questions: people compare two models in the chat, blind, and vote for the better answer. Each vote moves an Elo rating."
      />
      {q.isPending && <PageSkeleton />}
      {hidden && (
        <EmptyState icon={EyeOff} title="Your admins keep the leaderboard to themselves">
          Compare in the chat still works: you see which model was which after each vote.
        </EmptyState>
      )}
      {q.error && !hidden && <QueryError error={q.error} retry={() => q.refetch()} />}
      {q.data &&
        (q.data.board.models.length === 0 ? (
          <EmptyState icon={Scale} title="No votes yet">
            Turn on <strong>Compare</strong> in the chat's message box: two models answer your next question side by side, and your vote lands here.{' '}
            <Link to="/chat" className="font-medium text-foreground underline-offset-2 hover:underline">
              Open the chat
            </Link>
          </EmptyState>
        ) : (
          <Card>
            <CardHeader>
              <CardTitle>Models by rating</CardTitle>
              <CardDescription>
                {q.data.board.votes.toLocaleString()} {q.data.board.votes === 1 ? 'vote' : 'votes'} from {q.data.board.people.toLocaleString()} {q.data.board.people === 1 ? 'person' : 'people'}. Every model starts at 1000; a win against a stronger model counts more.
                {!q.data.public && ' Only admins see this page (Settings → Chat → Arena leaderboard for everyone).'}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <LeaderboardTable board={q.data.board} label="Leaderboard" />
            </CardContent>
          </Card>
        ))}
    </>
  )
}
