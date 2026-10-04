import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Building2, Lock, Search, Users, X } from 'lucide-react'
import { useId, useState } from 'react'
import { ChoiceCard } from '@/components/app/choice-card'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, meQuery } from '@/lib/api'
import { sharingGroupsQuery } from './api'
import type { Assistant, Named, Reach } from './types'

/**
 * The groups a person can share with (theirs; every group for an admin), and those
 * chosen already, as checkboxes.
 */
export function GroupChecklist({ chosen, known = [], onChange, label }: { chosen: string[]; known?: Named[]; onChange: (ids: string[]) => void; label: string }) {
  const groups = useQuery(sharingGroupsQuery)
  const all = [...(groups.data ?? []), ...known.filter((k) => !groups.data?.some((g) => g.id === k.id))]
  if (groups.isPending) return <p className="text-sm text-muted-foreground">Loading groups…</p>
  if (all.length === 0) return <p className="text-sm text-muted-foreground">You are in no group yet: an admin puts people in groups (Admin → Groups).</p>
  return (
    <ul className="grid max-h-56 gap-0.5 overflow-y-auto rounded-lg border p-1" aria-label={label}>
      {all.map((g) => (
        <li key={g.id} className="flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent">
          <Checkbox
            id={`${label}-${g.id}`}
            checked={chosen.includes(g.id)}
            onCheckedChange={(v) => onChange(v === true ? [...chosen, g.id] : chosen.filter((x) => x !== g.id))}
          />
          <label htmlFor={`${label}-${g.id}`} className="min-w-0 flex-1 cursor-pointer truncate text-sm">
            {g.name}
          </label>
        </li>
      ))}
    </ul>
  )
}

type Person = Named & { userName: string }

/** Choosing people by name: two letters find them. */
function PeoplePicker({ chosen, onChange }: { chosen: Person[]; onChange: (people: Person[]) => void }) {
  const id = useId()
  const [q, setQ] = useState('')
  const term = q.trim()
  const found = useQuery({
    queryKey: ['sharing', 'people', term],
    queryFn: ({ signal }) => api<Person[]>(`/api/sharing/people?q=${encodeURIComponent(term)}`, { signal }),
    enabled: term.length >= 2,
  })
  const results = (found.data ?? []).filter((p) => !chosen.some((c) => c.id === p.id))
  return (
    <div className="grid gap-2">
      {chosen.length > 0 && (
        <ul className="flex flex-wrap gap-1.5" aria-label="People who may edit it">
          {chosen.map((p) => (
            <li key={p.id} className="flex items-center gap-1 rounded-full border bg-muted/40 py-0.5 pr-1 pl-2.5 text-sm">
              <span className="max-w-48 truncate">{p.name}</span>
              <Button variant="ghost" size="icon-sm" className="size-5 rounded-full" onClick={() => onChange(chosen.filter((c) => c.id !== p.id))} aria-label={`Remove ${p.name}`}>
                <X />
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
        <Input id={id} type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find people by name" aria-label="Find people by name" className="pl-8" autoComplete="off" />
      </div>
      {term.length >= 2 && (
        <ul className="grid max-h-48 gap-0.5 overflow-y-auto" aria-label="People found">
          {found.isSuccess && results.length === 0 && <li className="px-2 py-1 text-sm text-muted-foreground">Nobody else by that name.</li>}
          {results.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => {
                  onChange([...chosen, p])
                  setQ('')
                }}
                className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm outline-none hover:bg-accent focus-visible:bg-accent"
              >
                <span className="truncate font-medium">{p.name}</span>
                <span className="truncate text-xs text-muted-foreground">{p.userName}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

const reaches: { value: Reach; label: string; hint: string; icon: typeof Lock }[] = [
  { value: 'Private', label: 'Only you', hint: 'And the people you let edit it.', icon: Lock },
  { value: 'Groups', label: 'Chosen groups', hint: 'Their members use it; nobody else sees it.', icon: Users },
  { value: 'Company', label: 'Everyone', hint: 'Everyone who signs in. Admins only.', icon: Building2 },
]

/** Who may use an assistant, and who may edit it: for its owner (and admins, once it is the company's). */
export function AssistantSharing({ assistant }: { assistant: Assistant }) {
  const queryClient = useQueryClient()
  const me = useQuery(meQuery).data
  const shared = assistant.sharing
  const start = { reach: assistant.reach, groups: shared?.groups.map((g) => g.id) ?? [], editorPeople: shared?.editorPeople ?? [], editorGroups: shared?.editorGroups.map((g) => g.id) ?? [] }
  const [form, setForm] = useState(start)
  const known = [...(shared?.groups ?? []), ...(shared?.editorGroups ?? [])]
  const dirty = JSON.stringify(form) !== JSON.stringify(start)
  const save = useMutation({
    mutationFn: () =>
      api(`/api/assistants/${assistant.id}/sharing`, {
        method: 'PUT',
        body: { reach: form.reach, groups: form.reach === 'Groups' ? form.groups : [], editorPeople: form.editorPeople.map((p) => p.id), editorGroups: form.editorGroups },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['assistants'] })
      toast.success('Sharing saved', { description: form.reach === 'Private' ? 'Only you and its editors see it.' : 'It is in the gallery of everyone it is shared with.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card aria-label="Sharing">
      <CardHeader>
        <CardTitle className="text-base">Sharing</CardTitle>
        <CardDescription>Who may use it, and who may change it. Chats with it stay each person's own.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">Who may use it</legend>
          <div className="grid gap-2 sm:grid-cols-3">
            {reaches.map((r) => (
              <ChoiceCard
                key={r.value}
                name={`reach-${assistant.id}`}
                checked={form.reach === r.value}
                onChange={() => setForm({ ...form, reach: r.value })}
                icon={r.icon}
                title={r.label}
                hint={r.hint}
                disabled={r.value === 'Company' && !me?.isAdmin && assistant.reach !== 'Company'}
              />
            ))}
          </div>
        </fieldset>
        {form.reach === 'Groups' && (
          <fieldset className="grid gap-2">
            <legend className="mb-2 text-sm font-medium">Groups that use it</legend>
            <GroupChecklist label="Groups that use it" chosen={form.groups} known={known} onChange={(groups) => setForm({ ...form, groups })} />
          </fieldset>
        )}
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">People who may edit it</legend>
          <PeoplePicker chosen={form.editorPeople} onChange={(editorPeople) => setForm({ ...form, editorPeople })} />
        </fieldset>
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">Groups whose members may edit it</legend>
          <GroupChecklist label="Groups that edit it" chosen={form.editorGroups} known={known} onChange={(editorGroups) => setForm({ ...form, editorGroups })} />
        </fieldset>
        {dirty && (
          <div className="flex gap-2">
            <Button size="sm" loading={save.isPending} disabled={form.reach === 'Groups' && form.groups.length === 0} onClick={() => save.mutate()}>
              Save sharing
            </Button>
            <Button size="sm" variant="outline" onClick={() => setForm(start)}>
              Cancel
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
