import { useQuery } from '@tanstack/react-query'
import { EyeOff, Scale, Trophy } from 'lucide-react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
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

const right = { className: 'text-right tabular-nums' }
const count = (id: string, header: string, value: (s: Standing) => number): ColumnDef<Standing & { place: number }> => ({
  id,
  header,
  accessorFn: value,
  meta: right,
})

const boardColumns: ColumnDef<Standing & { place: number }>[] = [
  { id: 'place', header: '#', accessorKey: 'place', meta: { className: 'tabular-nums text-muted-foreground' } },
  {
    id: 'model',
    header: 'Model',
    accessorKey: 'model',
    cell: ({ row: { original: s } }) => (
      <span className="inline-flex items-center gap-1.5 font-medium">
        {s.place === 1 && <Trophy className="size-3.5 text-warning-ink" aria-label="First" />}
        {s.model}
      </span>
    ),
  },
  { id: 'rating', header: 'Rating', accessorKey: 'rating', meta: { className: 'text-right font-semibold tabular-nums' } },
  { ...count('winRate', 'Win rate', (s) => s.winRate), cell: ({ row }) => formatValue(row.original.winRate, 'percentunit', 0) },
  count('wins', 'Won', (s) => s.wins),
  count('losses', 'Lost', (s) => s.losses),
  count('tied', 'Tied', (s) => s.ties - s.bad),
  count('bad', 'Both bad', (s) => s.bad),
  count('matches', 'Votes', (s) => s.matches),
]

/** The models by rating, with their votes. */
export function LeaderboardTable({ board, label }: { board: Board; label: string }) {
  return (
    <DataTable
      columns={boardColumns}
      data={board.models.map((s, i) => ({ ...s, place: i + 1 }))}
      noun="models"
      label={label}
      compact
      getRowId={(s) => s.model}
      initialSorting={[{ id: 'place', desc: false }]}
    />
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
