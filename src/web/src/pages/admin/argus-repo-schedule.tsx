import { useState } from 'react'
import { TimeZonePicker } from '@/components/app/time-zone-picker'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { weekdays } from '../tasks-schedule'
import { scheduleForm, scheduleSpec, scheduleWords, type RepoSchedule, type ScheduleKind } from './argus-schedule-spec'

/** The fields of a schedule: how often, and when. */
export function ScheduleFields({ value, onChange, defaultWords }: { value: RepoSchedule; onChange: (s: RepoSchedule) => void; defaultWords?: string }) {
  const set = (patch: Partial<RepoSchedule>) => onChange({ ...value, ...patch })
  return (
    <div className="grid gap-4 sm:grid-cols-3">
      <Field label="Reindex" className="sm:col-span-3">
        <Select value={value.kind} onValueChange={(v) => set({ kind: v as ScheduleKind })}>
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {defaultWords !== undefined && <SelectItem value="default">Same as all ({defaultWords.toLowerCase()})</SelectItem>}
            <SelectItem value="pass">With each scheduled pass</SelectItem>
            <SelectItem value="hours">Every few hours</SelectItem>
            <SelectItem value="daily">Every day</SelectItem>
            <SelectItem value="weekly">Once a week</SelectItem>
            <SelectItem value="off">Off: only pushes and when asked</SelectItem>
          </SelectContent>
        </Select>
      </Field>
      {value.kind === 'hours' && (
        <Field label="Every (hours)" hint="1 to 168, counted from its last check.">
          <Input type="number" min={1} max={168} required value={value.hours} onChange={(e) => set({ hours: Number(e.target.value) })} />
        </Field>
      )}
      {value.kind === 'weekly' && (
        <Field label="On">
          <Select value={String(value.day)} onValueChange={(v) => set({ day: Number(v) })}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {weekdays.map((d, i) => (
                <SelectItem key={d} value={String(i + 1)}>
                  {d}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}
      {(value.kind === 'daily' || value.kind === 'weekly') && (
        <Field label="At">
          <Input type="time" required value={value.time} onChange={(e) => set({ time: e.target.value })} />
        </Field>
      )}
    </div>
  )
}

/**
 * Choosing a schedule: for some repositories (one may follow the schedule for all) or the
 * schedule for all itself (with the time zone times of day are in).
 */
export function ScheduleForm({
  title,
  description,
  initial,
  defaultWords,
  timeZone,
  pending,
  error,
  submit,
  onSubmit,
  onCancel,
}: {
  title: string
  description: string
  initial: string
  /** For repositories: what the schedule for all is, so one can follow it. */
  defaultWords?: string
  /** For the schedule for all: its time zone. */
  timeZone?: string
  pending: boolean
  error: string | null
  submit: string
  onSubmit: (spec: string, timeZone?: string) => void
  onCancel: () => void
}) {
  const [value, setValue] = useState(() => scheduleForm(initial))
  const [zone, setZone] = useState(timeZone ?? 'UTC')
  const spec = scheduleSpec(value)
  return (
    <>
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>{description}</DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          onSubmit(spec, timeZone === undefined ? undefined : zone)
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <ScheduleFields value={value} onChange={setValue} defaultWords={defaultWords} />
        {timeZone !== undefined && (
          <Field label="Time zone" hint="Times of day, for every repository, are in this zone.">
            <TimeZonePicker value={zone} onChange={setZone} />
          </Field>
        )}
        <p className="text-sm text-muted-foreground" aria-live="polite">
          {scheduleWords(spec, defaultWords)}.
        </p>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" loading={pending}>
            {submit}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
