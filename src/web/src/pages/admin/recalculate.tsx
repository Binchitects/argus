import { useMutation } from '@tanstack/react-query'
import { Calculator } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue, money } from '@/lib/format'

interface Change {
  rows: number
  before: number
  after: number
  unpriced: number
}

export interface Recalculation {
  requests: Change
  answers: Change
  applied: boolean
}

const day = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/**
 * Settings → Prices: costs booked before a model had a price (or before cached input had one),
 * worked out again at today's prices over a range of days. Counted first; changed only when asked,
 * and audited. Requests at the gateway are what usage, credit and the dashboards count; the
 * answers' parts are what each answer in the chat shows.
 */
export function RecalculatePanel() {
  const confirm = useConfirm()
  const [from, setFrom] = useState(() => day(new Date(Date.now() - 30 * 86_400_000)))
  const [to, setTo] = useState(() => day(new Date()))
  const [onlyFree, setOnlyFree] = useState(true)
  const [counted, setCounted] = useState<Recalculation | null>(null)
  const body = (apply: boolean) => ({
    from: new Date(`${from}T00:00:00`).toISOString(),
    to: new Date(new Date(`${to}T00:00:00`).getTime() + 86_400_000).toISOString(),
    onlyFree,
    apply,
  })
  const count = useMutation({
    mutationFn: () => api<Recalculation>('/api/admin/usage/recalculate', { body: body(false) }),
    onSuccess: setCounted,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const apply = useMutation({
    mutationFn: () => api<Recalculation>('/api/admin/usage/recalculate', { body: body(true) }),
    onSuccess: (r) => {
      setCounted(r)
      toast.success('Past costs recalculated', { description: `${formatValue(r.requests.rows)} requests and ${formatValue(r.answers.rows)} parts of answers now cost what today's prices say.` })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const changes = counted ? counted.requests.rows + counted.answers.rows : 0
  const reset = () => setCounted(null)
  return (
    <section className="grid gap-3 pt-4" aria-labelledby="recalculate-title">
      <div>
        <h3 id="recalculate-title" className="text-sm font-medium">
          Recalculate past costs
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Requests booked before a model had a price (or before cached input had one) cost nothing, or too little. Work them out again at today's prices: requests at the gateway (what
          usage, credit and the dashboards count) and the parts of chat answers (what each answer shows). Count first; nothing changes until you recalculate. It never runs by itself.
        </p>
        <p className="mt-1 text-sm text-muted-foreground">
          A picture request at the gateway booked as free counts as one picture. One that has a cost keeps it, even with "Only costs booked as free" off: the gateway's log does not say how
          many pictures it made.
        </p>
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <Label className="grid gap-1 text-xs font-normal">
          From
          <Input type="date" className="h-9 w-40" value={from} max={to} onChange={(e) => (setFrom(e.target.value), reset())} />
        </Label>
        <Label className="grid gap-1 text-xs font-normal">
          To (inclusive)
          <Input type="date" className="h-9 w-40" value={to} min={from} onChange={(e) => (setTo(e.target.value), reset())} />
        </Label>
        <Label className="flex items-center gap-2 pb-2 font-normal">
          <Switch checked={onlyFree} onCheckedChange={(v) => (setOnlyFree(v), reset())} />
          Only costs booked as free
        </Label>
        <Button variant="outline" disabled={!from || !to} loading={count.isPending} onClick={() => count.mutate()}>
          <Calculator /> Count
        </Button>
      </div>
      {counted && (
        <Alert variant={counted.applied ? 'success' : undefined}>
          <ul className="grid gap-1">
            <li>
              {formatValue(counted.requests.rows)} requests at the gateway: {money(counted.requests.before)} → {money(counted.requests.after)}
              {counted.applied ? ' (done)' : ''}
            </li>
            <li>
              {formatValue(counted.answers.rows)} parts of chat answers: {money(counted.answers.before)} → {money(counted.answers.after)}
              {counted.applied ? ' (done)' : ''}
            </li>
            {counted.requests.unpriced + counted.answers.unpriced > 0 && (
              <li className="text-muted-foreground">
                {formatValue(counted.requests.unpriced + counted.answers.unpriced)} cannot be priced again: speech and transcriptions at the gateway (their length is not kept), and
                pictures, video or speech whose files were deleted since.
              </li>
            )}
          </ul>
        </Alert>
      )}
      {counted && !counted.applied && changes > 0 && (
        <Button
          className="w-fit"
          loading={apply.isPending}
          onClick={async () => {
            if (
              await confirm({
                title: 'Recalculate past costs?',
                description: `${formatValue(counted.requests.rows)} requests and ${formatValue(counted.answers.rows)} parts of answers will cost what today's prices say. People's spend this month, and their credit left, change with them. It is audited.`,
                confirm: 'Recalculate',
              })
            )
              apply.mutate()
          }}
        >
          Recalculate {formatValue(changes)}
        </Button>
      )}
    </section>
  )
}
