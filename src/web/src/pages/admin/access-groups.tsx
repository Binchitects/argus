import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Search, UserPlus } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { accessGroupsQuery, type AccessGroup } from './access-api'

/**
 * Each group with its members, the roles it holds and the rules on it; and which app groups new people join at their
 * first sign-in (an admin adds them, the directory, company sign-in or SCIM), saved together.
 */
export function AccessGroupsPanel() {
  const queryClient = useQueryClient()
  const groups = useQuery(accessGroupsQuery)
  const [q, setQ] = useState('')
  const [defaults, setDefaults] = useState<Set<string> | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  if (groups.error) return <QueryError error={groups.error} retry={() => groups.refetch()} />
  if (!groups.data) return <Skeleton className="h-64" />
  const saved = new Set(groups.data.filter((g) => g.joinsNewPeople).map((g) => g.id))
  const chosen = defaults ?? saved
  const dirty = chosen.size !== saved.size || [...chosen].some((id) => !saved.has(id))
  const query = q.trim().toLowerCase()
  const shown = groups.data.filter((g) => !query || g.name.toLowerCase().includes(query) || g.members.some((m) => m.name.toLowerCase().includes(query)))
  const save = async () => {
    setSaving(true)
    setError(null)
    try {
      await api('/api/admin/access/defaults', { method: 'PUT', body: { groups: [...chosen] } })
      toast.success('Default groups saved')
      setDefaults(null)
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <div className="grid gap-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <UserPlus className="size-4" aria-hidden /> New people join
          </CardTitle>
          <CardDescription>
            The app groups a new person joins when their account is first made: an admin adds them, or they first sign in from the directory, with company sign-in or through SCIM. Only then: someone
            taken out stays out.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          <p className="text-sm">
            {chosen.size === 0 ? (
              <span className="text-muted-foreground">None: new people join no group.</span>
            ) : (
              <>
                <span className="text-muted-foreground">Now: </span>
                {groups.data
                  .filter((g) => chosen.has(g.id))
                  .map((g) => g.name)
                  .join(', ')}
              </>
            )}
          </p>
          {error && <Alert variant="destructive">{error}</Alert>}
          {dirty && (
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => void save()} loading={saving}>
                Save default groups
              </Button>
              <Button variant="outline" onClick={() => setDefaults(null)} disabled={saving}>
                Discard
              </Button>
            </div>
          )}
        </CardContent>
      </Card>
      <div className="relative sm:max-w-sm">
        <Search className="pointer-events-none absolute top-2.5 left-2.5 size-4 text-muted-foreground" aria-hidden />
        <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search groups and members" aria-label="Search groups and members" className="pl-8" />
      </div>
      {groups.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No groups yet: make them under <Link to="/admin/groups" className="underline underline-offset-2">Groups</Link>.
        </p>
      ) : (
        <ul className="grid gap-3" aria-label="Groups">
          {shown.map((g) => (
            <GroupCard
              key={g.id}
              group={g}
              joins={chosen.has(g.id)}
              onJoins={(on) => {
                const next = new Set(chosen)
                if (on) next.add(g.id)
                else next.delete(g.id)
                setDefaults(next)
              }}
            />
          ))}
        </ul>
      )}
    </div>
  )
}

function GroupCard({ group: g, joins, onJoins }: { group: AccessGroup; joins: boolean; onJoins: (on: boolean) => void }) {
  return (
    <li className="grid gap-3 rounded-lg border p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-0.5">
          <p className="flex flex-wrap items-center gap-2 font-medium">
            <Link to={`/admin/groups/${g.id}`} className="hover:underline">
              {g.name}
            </Link>
            <Badge variant="outline">{g.directory ? 'Directory' : g.scim ? 'SCIM' : 'App group'}</Badge>
            <span className="text-xs font-normal text-muted-foreground">
              {g.memberCount} member{g.memberCount === 1 ? '' : 's'}
            </span>
          </p>
          {g.description && <p className="text-sm text-muted-foreground">{g.description}</p>}
        </div>
        {g.canBeDefault ? (
          <div className="flex items-center gap-2">
            <Switch id={`joins-${g.id}`} checked={joins} onCheckedChange={onJoins} />
            <Label htmlFor={`joins-${g.id}`}>New people join</Label>
          </div>
        ) : (
          <span className="text-xs text-muted-foreground">{g.directory ? 'The directory' : 'The identity provider'} decides its members.</span>
        )}
      </div>
      <dl className="grid gap-2 text-sm sm:grid-cols-[8rem_1fr]">
        <dt className="text-muted-foreground">Members</dt>
        <dd className="flex flex-wrap gap-1.5">
          {g.members.length === 0 && <span className="text-muted-foreground">Nobody yet.</span>}
          {g.members.map((m) => (
            <Link key={m.id} to={`/admin/people/${m.id}`} className={`rounded-md border px-2 py-0.5 text-xs hover:bg-muted ${m.isDisabled ? 'text-muted-foreground line-through' : ''}`}>
              {m.name}
            </Link>
          ))}
          {g.memberCount > g.members.length && <span className="text-xs text-muted-foreground">and {g.memberCount - g.members.length} more</span>}
        </dd>
        <dt className="text-muted-foreground">Roles</dt>
        <dd className="flex flex-wrap gap-1.5">
          {g.roles.length === 0 ? (
            <span className="text-muted-foreground">None of its own.</span>
          ) : (
            g.roles.map((r) => (
              <Badge key={r.id} variant="secondary">
                {r.role}
                {r.expiresAt ? ` until ${new Date(r.expiresAt).toLocaleDateString()}` : ''}
              </Badge>
            ))
          )}
        </dd>
        <dt className="text-muted-foreground">Rules</dt>
        <dd className="flex flex-wrap gap-1.5">
          {g.rules.length === 0 ? (
            <span className="text-muted-foreground">None.</span>
          ) : (
            g.rules.map((r) => (
              <Badge key={r.id} variant={r.effect === 'Deny' ? 'destructive' : 'success'} title={r.note ?? undefined}>
                {r.effect} {r.resourceKind}:{r.resource}
              </Badge>
            ))
          )}
        </dd>
      </dl>
    </li>
  )
}
