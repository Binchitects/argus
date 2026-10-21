import { useMutation, useQuery } from '@tanstack/react-query'
import { Database, LayoutDashboard, Pin as PinIcon, Play, Reply, Table2, X } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { DataChart, ResultTable } from '@/components/charts/data-chart'
import { planChart } from '@/components/charts/data-plan'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/api'
import { askData, runSql, sourcesQuery, type DataAnswer, type DataSourceBrief } from './data-api'
import { PinDialog } from './pin-dialog'

/** What was asked of which database, its answer (and the SQL as the person changed it). */
interface Asked extends DataAnswer {
  key: number
  sourceId: string
}

/** Workspace → Data: ask the databases an admin connected; each answer its SQL, shown and explained, a chart and the rows. */
export function DataPage() {
  const sources = useQuery(sourcesQuery)
  const [chosen, setChosen] = useState<string | null>(null)
  const [question, setQuestion] = useState('')
  const [answers, setAnswers] = useState<Asked[]>([])
  const [earlier, setEarlier] = useState<Asked | null>(null)
  const [showTables, setShowTables] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const source = sources.data?.find((s) => s.id === chosen) ?? sources.data?.[0]
  const ask = useMutation({
    mutationFn: (q: string) => askData({ sourceId: source!.id, question: q, earlier: earlier && earlier.sourceId === source!.id ? { question: earlier.question, sql: earlier.sql } : undefined }),
    onSuccess: (a) => {
      setAnswers((all) => [{ ...a, key: Date.now(), sourceId: source!.id }, ...all])
      setQuestion('')
      setEarlier(null)
    },
    onError: (e) => setError(errorMessage(e)),
  })
  if (sources.isPending) return <PageSkeleton />
  if (sources.error) return <QueryError error={sources.error} retry={() => sources.refetch()} />
  const header = (
    <PageHeader
      title="Data"
      description="Ask the company's databases in words. Each answer shows the SQL it ran (read only) and what it means, with a chart and the rows. Pin the ones you want to keep to a board."
      actions={
        <Button variant="outline" asChild>
          <Link to="/data/boards">
            <LayoutDashboard /> Boards
          </Link>
        </Button>
      }
    />
  )
  if (!source) {
    return (
      <>
        {header}
        <EmptyState icon={Database} title="No database is open to you yet">
          An admin connects one under Administration → Data sources, and chooses which tables people may ask.
        </EmptyState>
      </>
    )
  }
  const submit = () => {
    const q = question.trim()
    if (!q || ask.isPending) return
    setError(null)
    ask.mutate(q)
  }
  return (
    <>
      {header}
      <Card>
        <CardContent className="grid gap-3 pt-6">
          <div className="flex flex-wrap items-end gap-2">
            <Field label="Database" className="min-w-56">
              <Select value={source.id} onValueChange={setChosen}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {sources.data.map((s) => (
                    <SelectItem key={s.id} value={s.id}>
                      {s.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Button variant="ghost" size="sm" onClick={() => setShowTables(true)}>
              <Table2 /> {source.tables.length} {source.tables.length === 1 ? 'table' : 'tables'}
            </Button>
            {source.description && <p className="min-w-0 flex-1 truncate pb-2 text-sm text-muted-foreground">{source.description}</p>}
          </div>
          <form
            className="grid gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              submit()
            }}
          >
            {earlier && (
              <div className="flex items-center gap-2 text-xs text-muted-foreground">
                <Reply className="size-3.5" aria-hidden="true" />
                <span className="min-w-0 truncate">Building on: {earlier.question}</span>
                <Button type="button" variant="ghost" size="icon-sm" aria-label="Ask afresh instead" onClick={() => setEarlier(null)}>
                  <X />
                </Button>
              </div>
            )}
            <Field label={`Ask ${source.name}`}>
              <Textarea
                rows={2}
                maxLength={2000}
                value={question}
                placeholder="How many orders did we have each week this quarter?"
                onChange={(e) => setQuestion(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault()
                    submit()
                  }
                }}
              />
            </Field>
            {error && <Alert variant="destructive">{error}</Alert>}
            <div className="flex justify-end">
              <Button type="submit" loading={ask.isPending} disabled={!question.trim()}>
                Ask
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
      <ul className="mt-4 grid gap-4" aria-label="Answers">
        {answers.map((a) => (
          <li key={a.key}>
            <AnswerCard
              answer={a}
              source={sources.data.find((s) => s.id === a.sourceId)}
              onChange={(changed) => setAnswers((all) => all.map((x) => (x.key === a.key ? { ...x, ...changed } : x)))}
              onFollowUp={() => {
                setChosen(a.sourceId)
                setEarlier(a)
              }}
            />
          </li>
        ))}
      </ul>
      <Dialog open={showTables} onOpenChange={setShowTables}>
        <DialogContent className="sm:max-w-2xl">
          <TablesList source={source} />
        </DialogContent>
      </Dialog>
    </>
  )
}

/** The tables open to questions, their columns and what they are. */
function TablesList({ source }: { source: DataSourceBrief }) {
  return (
    <>
      <DialogHeader>
        <DialogTitle>Tables of {source.name}</DialogTitle>
        <DialogDescription>What questions can be answered from: the tables an admin opened, and their columns.</DialogDescription>
      </DialogHeader>
      <div className="grid max-h-[60vh] gap-3 overflow-y-auto">
        {source.tables.map((t) => (
          <section key={t.name} aria-label={t.name} className="rounded-lg border p-3">
            <h3 className="flex items-center gap-2 font-mono text-sm font-medium">
              {t.name}
              {t.kind !== 'table' && <Badge variant="outline">{t.kind}</Badge>}
            </h3>
            {t.comment && <p className="mt-0.5 text-xs text-muted-foreground">{t.comment}</p>}
            <ul className="mt-2 grid gap-0.5 text-xs sm:grid-cols-2">
              {t.columns.map((c) => (
                <li key={c.name} className="truncate">
                  <span className="font-mono">{c.name}</span> <span className="text-muted-foreground">{c.type}</span>
                  {c.comment && <span className="text-muted-foreground"> · {c.comment}</span>}
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </>
  )
}

/** An answer: what it means, the chart or the rows, the SQL (changed and run again by hand), pinned or built on. */
function AnswerCard({ answer, source, onChange, onFollowUp }: { answer: Asked; source: DataSourceBrief | undefined; onChange: (a: Partial<Asked>) => void; onFollowUp: () => void }) {
  const [view, setView] = useState<'chart' | 'table'>('chart')
  const [sql, setSql] = useState(answer.sql)
  const [pinning, setPinning] = useState(false)
  const run = useMutation({
    mutationFn: () => runSql({ sourceId: answer.sourceId, sql, question: answer.question }),
    onSuccess: (result) => onChange({ sql, result, error: null }),
    onError: (e) => onChange({ error: errorMessage(e) }),
  })
  const result = answer.result
  const plan = result ? planChart(result.columns, result.rows, answer.chart) : null
  const title = answer.title || answer.question
  return (
    <Card>
      <CardHeader className="gap-1">
        <CardTitle className="text-base">{title}</CardTitle>
        <p className="text-xs text-muted-foreground">
          {answer.title && answer.title !== answer.question ? `${answer.question} · ` : ''}
          {source?.name}
          {result ? ` · ${result.rows.length}${result.truncated ? '+' : ''} ${result.rows.length === 1 ? 'row' : 'rows'} in ${result.milliseconds} ms` : ''}
          {answer.model ? ` · ${answer.model}` : ''}
        </p>
      </CardHeader>
      <CardContent className="grid gap-3">
        {answer.explanation && <p className="text-sm">{answer.explanation}</p>}
        {answer.error && (
          <Alert variant="destructive">
            {answer.error}
            {answer.sql ? ' Change the SQL below and run it, or ask again in other words.' : ''}
          </Alert>
        )}
        {result && plan && (
          <>
            {(plan.type === 'bar' || plan.type === 'line') && (
              <Segmented label="Shown as" value={view} onChange={setView} options={[{ value: 'chart', label: 'Chart' }, { value: 'table', label: 'Table' }]} />
            )}
            {view === 'table' && plan.type !== 'number' ? <ResultTable result={result} /> : <DataChart result={result} chart={answer.chart} label={title} />}
          </>
        )}
        {answer.sql && (
          <details className="group rounded-lg border">
            <summary className="cursor-pointer px-3 py-2 text-sm font-medium select-none">SQL</summary>
            <div className="grid gap-2 border-t p-3">
              <Textarea rows={Math.min(12, sql.split('\n').length + 1)} value={sql} onChange={(e) => setSql(e.target.value)} className="font-mono text-xs" aria-label="The SQL" spellCheck={false} />
              <div className="flex justify-end">
                <Button size="sm" variant="outline" onClick={() => run.mutate()} loading={run.isPending} disabled={!sql.trim()}>
                  <Play /> Run
                </Button>
              </div>
            </div>
          </details>
        )}
        <div className="flex flex-wrap gap-2">
          {result && answer.sql && (
            <Button size="sm" variant="outline" onClick={() => setPinning(true)}>
              <PinIcon /> Pin to a board
            </Button>
          )}
          {answer.sql && (
            <Button size="sm" variant="ghost" onClick={onFollowUp}>
              <Reply /> Ask a follow-up
            </Button>
          )}
        </div>
      </CardContent>
      <PinDialog
        what={pinning ? { sourceId: answer.sourceId, title, question: answer.question, sql: answer.sql, chart: { ...answer.chart, type: view === 'table' ? 'table' : answer.chart.type } } : null}
        onClose={() => setPinning(false)}
      />
    </Card>
  )
}
