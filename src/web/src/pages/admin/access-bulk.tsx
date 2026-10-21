import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2 } from 'lucide-react'
import { useId, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { PeoplePicker, type Person } from '../chat/assistant-sharing'
import type { AccessOverview } from './access-api'
import { groupsQuery } from './groups-api'

type Kind = 'role.give' | 'role.take' | 'group.join' | 'group.leave' | 'feature.allow' | 'feature.deny' | 'feature.default' | 'account.disable' | 'account.enable'

interface Step {
  kind: Kind
  roleId?: string
  groupId?: string
  feature?: string
  expiresAt?: string
}

interface Report {
  dryRun: boolean
  done: boolean
  people: number
  changes: number
  lines: { id: string; name: string; changes: string[]; unchanged: string[] }[]
}

const kinds: { value: Kind; label: string }[] = [
  { value: 'role.give', label: 'Give a role' },
  { value: 'role.take', label: 'Take a role' },
  { value: 'group.join', label: 'Add to a group' },
  { value: 'group.leave', label: 'Take out of a group' },
  { value: 'feature.allow', label: 'Allow a feature' },
  { value: 'feature.deny', label: 'Deny a feature' },
  { value: 'feature.default', label: 'A feature as their roles say' },
  { value: 'account.disable', label: 'Disable the account' },
  { value: 'account.enable', label: 'Enable the account' },
]

/** Admin → Access → Many people: changes for many people at once, a dry run first, then for real. */
export function ManyPeople({ data }: { data: AccessOverview }) {
  const queryClient = useQueryClient()
  const groups = useQuery(groupsQuery)
  const [people, setPeople] = useState<Person[]>([])
  const [chosenGroups, setChosenGroups] = useState<string[]>([])
  const [steps, setSteps] = useState<Step[]>([{ kind: 'role.give' }])
  const [report, setReport] = useState<Report | null>(null)
  const [error, setError] = useState<string | null>(null)
  const listId = useId()
  const features = data.permissions.filter((p) => !p.admin)
  const appGroups = (groups.data ?? []) as { id: string; name: string; directory?: string | null; scim?: boolean }[]
  const body = (dryRun: boolean) => ({
    people: people.map((p) => p.id), groups: chosenGroups, dryRun,
    steps: steps.map((s) => ({ ...s, expiresAt: s.expiresAt ? new Date(`${s.expiresAt}T23:59:59Z`).toISOString() : undefined })),
  })
  const run = useMutation({
    mutationFn: (dryRun: boolean) => api<Report>('/api/admin/access/bulk', { body: body(dryRun) }),
    onSuccess: (r) => {
      setError(null)
      setReport(r)
      if (r.done) {
        toast.success(`${r.changes} changes made`, { description: 'They are in the history as one change.' })
        void queryClient.invalidateQueries({ queryKey: ['admin'] })
      }
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const change = (i: number, patch: Partial<Step>) => {
    setReport(null)
    setSteps(steps.map((s, j) => (j === i ? { ...s, ...patch } : s)))
  }
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Many people at once</CardTitle>
          <CardDescription>Choose people and groups, then what changes for each. A dry run says what would change; nothing does until you make the changes.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <PeoplePicker chosen={people} onChange={(p) => { setReport(null); setPeople(p) }} label="People" />
          <fieldset className="grid gap-1.5">
            <legend className="mb-1 text-sm font-medium">And everyone in these groups</legend>
            <div className="flex flex-wrap gap-x-4 gap-y-1.5">
              {appGroups.map((g) => (
                <div key={g.id} className="flex items-center gap-1.5 text-sm">
                  <Checkbox id={`${listId}-${g.id}`} checked={chosenGroups.includes(g.id)} onCheckedChange={(on) => { setReport(null); setChosenGroups(on ? [...chosenGroups, g.id] : chosenGroups.filter((x) => x !== g.id)) }} />
                  <label htmlFor={`${listId}-${g.id}`}>{g.name}</label>
                </div>
              ))}
            </div>
          </fieldset>
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">What changes</legend>
            {steps.map((s, i) => (
              <div key={i} className="grid gap-2 rounded-md border p-2 sm:grid-cols-[12rem_1fr_9rem_auto]">
                <Select value={s.kind} onValueChange={(v) => change(i, { kind: v as Kind, roleId: undefined, groupId: undefined, feature: undefined })}>
                  <SelectTrigger aria-label={`Change ${i + 1}`}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {kinds.map((k) => (
                      <SelectItem key={k.value} value={k.value}>
                        {k.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {s.kind.startsWith('role.') ? (
                  <Select value={s.roleId ?? ''} onValueChange={(roleId) => change(i, { roleId })}>
                    <SelectTrigger aria-label={`Change ${i + 1}: the role`}>
                      <SelectValue placeholder="The role…" />
                    </SelectTrigger>
                    <SelectContent>
                      {data.roles.filter((r) => r.key !== 'member').map((r) => (
                        <SelectItem key={r.id} value={r.id}>
                          {r.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                ) : s.kind.startsWith('group.') ? (
                  <Select value={s.groupId ?? ''} onValueChange={(groupId) => change(i, { groupId })}>
                    <SelectTrigger aria-label={`Change ${i + 1}: the group`}>
                      <SelectValue placeholder="The group…" />
                    </SelectTrigger>
                    <SelectContent>
                      {appGroups.filter((g) => !g.directory && !g.scim).map((g) => (
                        <SelectItem key={g.id} value={g.id}>
                          {g.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                ) : s.kind.startsWith('feature.') ? (
                  <Select value={s.feature ?? ''} onValueChange={(feature) => change(i, { feature })}>
                    <SelectTrigger aria-label={`Change ${i + 1}: the feature`}>
                      <SelectValue placeholder="The feature…" />
                    </SelectTrigger>
                    <SelectContent>
                      {features.map((f) => (
                        <SelectItem key={f.key} value={f.key}>
                          {f.description}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                ) : (
                  <span />
                )}
                {s.kind === 'role.give' || s.kind === 'feature.allow' || s.kind === 'feature.deny' ? (
                  <Input type="date" aria-label={`Change ${i + 1}: until`} value={s.expiresAt ?? ''} onChange={(e) => change(i, { expiresAt: e.target.value || undefined })} />
                ) : (
                  <span />
                )}
                <Button variant="ghost" size="icon-sm" aria-label={`Remove change ${i + 1}`} disabled={steps.length === 1} onClick={() => { setReport(null); setSteps(steps.filter((_, j) => j !== i)) }}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            <Button variant="outline" size="sm" className="w-fit" disabled={steps.length >= 20} onClick={() => setSteps([...steps, { kind: 'role.give' }])}>
              <Plus /> Another change
            </Button>
          </fieldset>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => run.mutate(true)} loading={run.isPending && run.variables === true} disabled={people.length === 0 && chosenGroups.length === 0}>
              Dry run
            </Button>
            <Button onClick={() => run.mutate(false)} loading={run.isPending && run.variables === false} disabled={!report || report.done || report.changes === 0}>
              Make the changes
            </Button>
          </div>
        </CardContent>
      </Card>
      {report && (
        <Card aria-live="polite">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              {report.done ? 'Done' : 'Dry run'} <Badge variant={report.done ? 'success' : 'outline'}>{report.changes} changes for {report.people} people</Badge>
            </CardTitle>
            <CardDescription>{report.done ? 'Made, as one change in the history.' : 'Nothing has changed yet.'}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="grid max-h-[32rem] gap-2 overflow-y-auto text-sm" aria-label="Person by person">
              {report.lines.map((l) => (
                <li key={l.id} className="rounded-md border p-2">
                  <p className="font-medium">{l.name}</p>
                  {l.changes.map((c) => (
                    <p key={c} className="text-foreground">• {c}</p>
                  ))}
                  {l.unchanged.map((c) => (
                    <p key={c} className="text-muted-foreground">– {c}</p>
                  ))}
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
