import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/api'
import { addBoard, boardsQuery, pinChart, refreshLabels, type ChartSpec, type Refresh } from './data-api'

export interface ToPin {
  sourceId: string
  title: string
  question: string
  sql: string
  chart: ChartSpec
}

const NewBoard = 'new'

/** Pin to a board: one of the person's own, or a new one; how often it is run again. */
export function PinDialog({ what, onClose }: { what: ToPin | null; onClose: () => void }) {
  return (
    <Dialog open={what !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">{what && <PinForm what={what} onClose={onClose} />}</DialogContent>
    </Dialog>
  )
}

function PinForm({ what, onClose }: { what: ToPin; onClose: () => void }) {
  const queryClient = useQueryClient()
  const boards = useQuery(boardsQuery)
  const mine = (boards.data ?? []).filter((b) => b.mine)
  const [board, setBoard] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [title, setTitle] = useState(what.title)
  const [refresh, setRefresh] = useState<Refresh>('daily')
  const [error, setError] = useState<string | null>(null)
  const [pinned, setPinned] = useState<string | null>(null)
  const target = board ?? mine[0]?.id ?? NewBoard
  const pin = useMutation({
    mutationFn: async () => {
      const id = target === NewBoard ? (await addBoard({ name: name.trim() })).id : target
      await pinChart(id, { sourceId: what.sourceId, title: title.trim(), question: what.question, sql: what.sql, chart: what.chart, refresh })
      return id
    },
    onSuccess: (id) => {
      void queryClient.invalidateQueries({ queryKey: ['data', 'boards'] })
      setPinned(id)
    },
    onError: (e) => setError(errorMessage(e)),
  })
  if (pinned) {
    return (
      <>
        <DialogHeader>
          <DialogTitle>Pinned</DialogTitle>
          <DialogDescription>{title} is on the board, run again {refresh === 'off' ? 'when you refresh it' : refreshLabels[refresh].toLowerCase()}.</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" asChild>
            <Link to={`/data/boards/${pinned}`}>Open the board</Link>
          </Button>
          <Button onClick={onClose}>Done</Button>
        </DialogFooter>
      </>
    )
  }
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        pin.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>Pin to a board</DialogTitle>
        <DialogDescription>Its SQL runs again on its schedule, as you; people you share the board with see its last result.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Board">
        <Select value={target} onValueChange={setBoard}>
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {mine.map((b) => (
              <SelectItem key={b.id} value={b.id}>
                {b.name}
              </SelectItem>
            ))}
            <SelectItem value={NewBoard}>A new board…</SelectItem>
          </SelectContent>
        </Select>
      </Field>
      {target === NewBoard && (
        <Field label="The new board's name">
          <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="Sales this week" />
        </Field>
      )}
      <Field label="Title">
        <Input value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
      </Field>
      <Segmented label="Run again" value={refresh} onChange={setRefresh} options={(['hourly', 'daily', 'off'] as const).map((r) => ({ value: r, label: refreshLabels[r] }))} />
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={pin.isPending} disabled={!title.trim() || (target === NewBoard && !name.trim())}>
          Pin
        </Button>
      </DialogFooter>
    </form>
  )
}
