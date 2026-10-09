import { useQueryClient } from '@tanstack/react-query'
import { Scale, Trophy } from 'lucide-react'
import { useId, useState } from 'react'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { conversationQuery } from './api'
import type { ToolRunning } from './live'
import { comparable, sideMessages, type ArenaView, type CompareChoice } from './quality'
import type { ChatTree } from './tree'
import { AnswerTurn } from './turns'
import type { AgentWork, ArenaVote, ChatConfig, ChatModel, InLine, Message } from './types'

/**
 * Compare, beside Deep research in the composer: the next question goes to two models,
 * side by side, their names hidden until the person votes. Two chosen here, or two at random.
 */
export function ComparePicker({ models, value, onChange }: { models: ChatModel[]; value: CompareChoice | null; onChange: (v: CompareChoice | null) => void }) {
  const usable = comparable(models)
  const ids = useId()
  const [open, setOpen] = useState(false)
  const [chosen, setChosen] = useState<string[]>(value?.models ?? [])
  if (usable.length < 2) return null
  const ready = chosen.length === 0 || chosen.length === 2
  return (
    <Popover
      open={open}
      onOpenChange={(o) => {
        setOpen(o)
        if (o) setChosen(value?.models ?? [])
      }}
    >
      <Tooltip content="Compare: two models answer the next question side by side, their names hidden until you vote">
        <PopoverTrigger asChild>
          <Button type="button" variant={value ? 'secondary' : 'ghost'} size="sm" className={cn('h-8 gap-1.5 rounded-full px-2.5', value && 'text-primary-ink')} aria-pressed={!!value} aria-label="Compare">
            <Scale /> <span className="hidden sm:inline">Compare</span>
          </Button>
        </PopoverTrigger>
      </Tooltip>
      <PopoverContent align="start" className="grid w-80 gap-3" aria-label="Compare two models">
        <div>
          <p className="text-sm font-medium">Compare two models</p>
          <p className="text-xs text-muted-foreground">
            Your next question goes to two models, answered one after the other and shown side by side as Model A and Model B. Vote for the better one to see which is which.
          </p>
        </div>
        <fieldset className="m-0 grid min-w-0 gap-1.5 border-0 p-0">
          <legend className="mb-1 text-xs text-muted-foreground">Choose two, or none for two at random</legend>
          {usable.map((m) => {
            const on = chosen.includes(m.name)
            const id = `${ids}-${m.name}`
            return (
              <div key={m.name} className={cn('flex items-center gap-2 text-sm', !on && chosen.length >= 2 && 'opacity-50')}>
                <Checkbox id={id} checked={on} disabled={!on && chosen.length >= 2} onCheckedChange={(v) => setChosen(v === true ? [...chosen, m.name] : chosen.filter((x) => x !== m.name))} />
                <label htmlFor={id} className="truncate">
                  {m.name}
                </label>
              </div>
            )
          })}
        </fieldset>
        <div className="flex items-center justify-between gap-2">
          {value ? (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                onChange(null)
                setOpen(false)
              }}
            >
              Turn off
            </Button>
          ) : (
            <span className="text-xs text-muted-foreground">{chosen.length === 1 ? 'Choose one more.' : chosen.length === 2 ? 'Which is A is drawn.' : 'Two at random.'}</span>
          )}
          <Button
            type="button"
            size="sm"
            disabled={!ready}
            onClick={() => {
              onChange({ models: chosen.length === 2 ? [chosen[0]!, chosen[1]!] : null })
              setOpen(false)
            }}
          >
            Compare the next question
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  )
}

const voteWords: Record<ArenaVote, string> = { a: 'A is better', b: 'B is better', tie: 'A tie', bad: 'Both are bad' }

/**
 * Two answers to one question, side by side and blind: "Model A" and "Model B" until
 * the person votes (A, B, a tie, both bad), then the names. They are answered one
 * after the other: the page says which is answering.
 */
export function ArenaTurn({
  arena,
  tree,
  question,
  config,
  chatId,
  thinkingSince,
  queued,
  busy,
  onOpenFile,
  onPreview,
  approvals,
  onDecide,
  calls,
  agents,
  onHurry,
}: {
  arena: ArenaView
  tree: ChatTree
  question: Message
  config: ChatConfig
  chatId?: string
  thinkingSince: number | null
  queued?: InLine | null
  busy: boolean
  onOpenFile: (name: string) => void
  onPreview?: (code: string) => void
  approvals?: string[]
  onDecide?: (callId: string, allow: boolean) => void
  calls?: Record<string, ToolRunning>
  agents?: Record<string, AgentWork[]>
  onHurry?: () => void
}) {
  const queryClient = useQueryClient()
  const [voting, setVoting] = useState(false)
  const sides = { a: sideMessages(tree, arena.a), b: sideMessages(tree, arena.b) }
  const done = !arena.answering && sides.a.length > 0 && sides.b.length > 0
  const winner = arena.vote === 'a' || arena.vote === 'b' ? arena.vote : null

  const vote = async (v: ArenaVote) => {
    setVoting(true)
    try {
      const r = await api<{ vote: ArenaVote; models: { a: string; b: string } }>(`/api/chat/arena/${arena.id}/vote`, { body: { vote: v } })
      toast.success(`Model A was ${r.models.a}, Model B was ${r.models.b}`)
      if (chatId) await queryClient.invalidateQueries({ queryKey: conversationQuery(chatId).queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setVoting(false)
    }
  }

  return (
    <section className="grid min-w-0 animate-enter gap-3" aria-label="Comparison" aria-busy={!!arena.answering}>
      <p className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
        <Scale className="size-4 shrink-0 text-primary" aria-hidden="true" />
        {arena.answering
          ? `Comparing two models: answering ${arena.step ?? 1} of ${arena.of ?? 2}, one after the other`
          : arena.vote
            ? `Compared. Your vote: ${voteWords[arena.vote]}`
            : 'Compared: two models answered, their names hidden until you vote'}
      </p>
      <div className="grid gap-4 xl:grid-cols-2">
        {(['a', 'b'] as const).map((side) => {
          const live = arena.answering === side
          const label = side === 'a' ? 'Model A' : 'Model B'
          return (
            <section key={side} className={cn('min-w-0 rounded-xl border bg-card/40 p-4', winner === side && 'border-primary/60 ring-1 ring-primary/30')} aria-label={label}>
              <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
                <span className="font-semibold">{label}</span>
                {arena.models && <span className="truncate text-muted-foreground">{arena.models[side]}</span>}
                {winner === side && <Badge>Your pick</Badge>}
              </div>
              {sides[side].length > 0 || live ? (
                <AnswerTurn
                  answer={sides[side]}
                  siblings={[]}
                  live={live}
                  thinkingSince={live ? thinkingSince : null}
                  queued={live ? queued : null}
                  notices={[]}
                  config={config}
                  question={question}
                  onSwitch={() => {}}
                  onOpenFile={onOpenFile}
                  onPreview={onPreview}
                  approvals={live ? approvals : undefined}
                  onDecide={onDecide}
                  calls={live ? calls : undefined}
                  agents={agents}
                  onHurry={live ? onHurry : undefined}
                  busy={busy}
                />
              ) : (
                <p className="text-sm text-muted-foreground">{arena.answering === 'a' ? 'Answers once Model A is done.' : 'Not answered: the comparison was stopped.'}</p>
              )}
            </section>
          )
        })}
      </div>
      {done && !arena.vote && (
        <fieldset className="m-0 flex min-w-0 flex-wrap items-center gap-2 border-0 p-0">
          <legend className="float-left me-2 text-sm font-medium">Which is better?</legend>
          {(['a', 'b', 'tie', 'bad'] as const).map((v) => (
            <Button key={v} variant="outline" size="sm" disabled={voting || busy} onClick={() => void vote(v)}>
              {voteWords[v]}
            </Button>
          ))}
        </fieldset>
      )}
      {!arena.answering && !done && !arena.vote && <p className="text-sm text-muted-foreground">Stopped before both models answered: there is nothing to vote on.</p>}
      {arena.vote && (
        <p className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
          <Trophy className="size-3.5" aria-hidden="true" /> Your vote is on the
          <Link to="/leaderboard" className="font-medium text-foreground underline-offset-2 hover:underline">
            leaderboard
          </Link>
          {winner && ': the chat goes on from your pick.'}
        </p>
      )}
    </section>
  )
}
