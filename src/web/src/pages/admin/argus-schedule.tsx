import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { blankSchedule, cronOf, describe, scheduleOf, weekdays, type Repeat, type Schedule } from '../tasks-schedule'

interface IndexSchedule {
  schedule: string
  timeZone: string
  problem: string | null
  nextRuns: string[]
}

/** When Argus brings the index up to date by itself: a schedule in words (or cron), in a time zone, and the next passes. */
export function ScheduleCard() {
  const queryClient = useQueryClient()
  const current = useQuery({ queryKey: ['admin', 'argus', 'schedule'], queryFn: ({ signal }) => api<IndexSchedule>('/api/admin/argus/schedule', { signal }) })
  if (!current.data) return null
  return <ScheduleForm key={`${current.data.schedule}|${current.data.timeZone}`} saved={current.data} onSaved={() => queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'schedule'] })} />
}

function ScheduleForm({ saved, onSaved }: { saved: IndexSchedule; onSaved: () => Promise<unknown> }) {
  const [on, setOn] = useState(saved.schedule.length > 0)
  const [schedule, setSchedule] = useState<Schedule>(saved.schedule ? scheduleOf(saved.schedule) : { ...blankSchedule, repeat: 'minutes', minutes: 15 })
  const [timeZone, setTimeZone] = useState(saved.timeZone)
  const [error, setError] = useState<string | null>(null)
  const set = (patch: Partial<Schedule>) => setSchedule({ ...schedule, ...patch })
  const save = useMutation({
    mutationFn: () => api('/api/admin/argus/schedule', { method: 'PUT', body: { schedule: on ? cronOf(schedule) : '', timeZone } }),
    onSuccess: async () => {
      await onSaved()
      toast.success('Schedule saved', { description: on ? `${describe(cronOf(schedule))}.` : 'Only pushes and Index now update the index.' })
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []
  const when = (at: string) => new Date(at).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit', timeZone: saved.timeZone })
  return (
    <Card aria-label="Schedule">
      <CardHeader>
        <CardTitle>Schedule</CardTitle>
        <CardDescription>
          When the index is brought up to date by itself. Each pass reads only what changed since the commit indexed last, for every chosen repository and branch.
          {saved.nextRuns.length > 0 && ` Next: ${saved.nextRuns.map(when).join(' · ')} (${saved.timeZone}).`}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          {saved.problem && <Alert variant="warning">{saved.problem}</Alert>}
          <div className="flex items-center gap-2">
            <Switch id="index-on-schedule" checked={on} onCheckedChange={setOn} />
            <Label htmlFor="index-on-schedule">Reindex on a schedule</Label>
          </div>
          {on && (
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label="Repeat">
                <Select value={schedule.repeat} onValueChange={(v) => set({ repeat: v as Repeat, cron: cronOf(schedule) })}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="minutes">Every few minutes</SelectItem>
                    <SelectItem value="hours">Every few hours</SelectItem>
                    <SelectItem value="daily">Every day</SelectItem>
                    <SelectItem value="weekdays">Weekdays</SelectItem>
                    <SelectItem value="weekly">Once a week</SelectItem>
                    <SelectItem value="custom">Custom (cron)</SelectItem>
                  </SelectContent>
                </Select>
              </Field>
              {schedule.repeat === 'minutes' && (
                <Field label="Every (minutes)" hint="5 to 30.">
                  <Input type="number" min={5} max={30} required value={schedule.minutes} onChange={(e) => set({ minutes: Number(e.target.value) })} />
                </Field>
              )}
              {schedule.repeat === 'hours' && (
                <Field label="Every (hours)">
                  <Input type="number" min={1} max={23} required value={schedule.hours} onChange={(e) => set({ hours: Number(e.target.value) })} />
                </Field>
              )}
              {['daily', 'weekdays', 'weekly'].includes(schedule.repeat) && (
                <Field label="At">
                  <Input type="time" required value={schedule.time} onChange={(e) => set({ time: e.target.value })} />
                </Field>
              )}
              {schedule.repeat === 'weekly' && (
                <Field label="On">
                  <Select value={String(schedule.day)} onValueChange={(v) => set({ day: Number(v) })}>
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
              {schedule.repeat === 'custom' && (
                <Field label="Cron" hint="Minute, hour, day of the month, month, day of the week.">
                  <Input required className="font-mono" autoComplete="off" value={schedule.cron} onChange={(e) => set({ cron: e.target.value })} />
                </Field>
              )}
              <Field label="Time zone">
                <Input list="index-time-zones" required autoComplete="off" value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
              </Field>
              <datalist id="index-time-zones">
                {zones.map((z) => (
                  <option key={z} value={z}>
                    {z}
                  </option>
                ))}
              </datalist>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" loading={save.isPending}>
              Save
            </Button>
            <span className="text-xs text-muted-foreground">{on ? `${describe(cronOf(schedule))}.` : 'Only GitLab pushes (when the webhook is set) and Index now update it.'}</span>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
