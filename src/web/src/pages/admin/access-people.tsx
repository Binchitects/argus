import { useQuery } from '@tanstack/react-query'
import { Check, Search, X } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import type { AccessOverview, Effective } from './access-api'
import { peopleQuery } from './people-api'

type Which = 'all' | 'yes' | 'no'

/**
 * One person's effective permissions: every permission, held or not, with the role or rule that decides it (the
 * checker's reasons), the roles they hold and how, their groups, and every rule that reaches them.
 */
export function EffectivePermissions({ data }: { data: AccessOverview }) {
  const people = useQuery(peopleQuery)
  const [person, setPerson] = useState('')
  const [q, setQ] = useState('')
  const [which, setWhich] = useState<Which>('all')
  const effective = useQuery({
    queryKey: ['admin', 'access', 'effective', person],
    queryFn: ({ signal }) => api<Effective>(`/api/admin/access/effective?person=${person}`, { signal }),
    enabled: !!person,
  })
  const query = q.trim().toLowerCase()
  const byKey = new Map(effective.data?.permissions.map((p) => [p.key, p]))
  const areas = [...new Set(data.permissions.map((p) => p.area))]
  return (
    <Card>
      <CardHeader>
        <CardTitle>What a person may do</CardTitle>
        <CardDescription>Every permission, held or not, with the role or rule that decides it.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <Field label="Person">
          <Select value={person} onValueChange={setPerson}>
            <SelectTrigger>
              <SelectValue placeholder="Choose…" />
            </SelectTrigger>
            <SelectContent>
              {(people.data?.people ?? []).map((p) => (
                <SelectItem key={p.id} value={p.id}>
                  {p.displayName || p.userName} ({p.userName})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        {effective.error ? (
          <QueryError error={effective.error} retry={() => effective.refetch()} />
        ) : person && !effective.data ? (
          <Skeleton className="h-40" />
        ) : (
          effective.data && (
            <>
              <div className="grid gap-2 text-sm">
                <p className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{effective.data.person.displayName || effective.data.person.userName}</span>
                  {effective.data.person.isOwner && <Badge>Owner</Badge>}
                  {effective.data.person.isDisabled && <Badge variant="destructive">Disabled</Badge>}
                  <Link to={`/admin/people/${effective.data.person.id}`} className="text-xs underline underline-offset-2">
                    Their page
                  </Link>
                </p>
                <p>
                  <span className="text-muted-foreground">Roles: </span>
                  {effective.data.roles.map((r) => `${r.name} (${r.via}${r.scope?.groups.length ? `, over ${r.scope.groups.length} groups` : ''})`).join(' · ') || 'none'}
                </p>
                <p>
                  <span className="text-muted-foreground">Groups: </span>
                  {effective.data.groups.map((g) => g.name).join(', ') || 'none'}
                </p>
              </div>
              <div className="flex flex-wrap items-end gap-3">
                <div className="relative min-w-0 flex-1 sm:max-w-sm">
                  <Search className="pointer-events-none absolute top-2.5 left-2.5 size-4 text-muted-foreground" aria-hidden />
                  <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search permissions" aria-label="Search their permissions" className="pl-8" />
                </div>
                <Segmented
                  label="Shown"
                  value={which}
                  onChange={setWhich}
                  options={[
                    { value: 'all', label: 'All' },
                    { value: 'yes', label: 'Held' },
                    { value: 'no', label: 'Not held' },
                  ]}
                />
              </div>
              {areas.map((area) => {
                const rows = data.permissions
                  .filter((p) => p.area === area && byKey.has(p.key))
                  .filter((p) => !query || p.key.includes(query) || p.description.toLowerCase().includes(query))
                  .filter((p) => which === 'all' || byKey.get(p.key)!.allowed === (which === 'yes'))
                if (rows.length === 0) return null
                return (
                  <section key={area} aria-label={area} className="grid gap-1">
                    <h3 className="text-sm font-medium">
                      {area}{' '}
                      <span className="text-xs font-normal text-muted-foreground">
                        {data.permissions.filter((p) => p.area === area && byKey.get(p.key)?.allowed).length} of {data.permissions.filter((p) => p.area === area).length} held
                      </span>
                    </h3>
                    <ul className="divide-y rounded-md border">
                      {rows.map((p) => {
                        const d = byKey.get(p.key)!
                        return (
                          <li key={p.key} className="grid gap-0.5 px-3 py-2 text-sm sm:grid-cols-[1.5rem_minmax(0,16rem)_1fr] sm:gap-3">
                            {d.allowed ? <Check className="size-4 text-success-ink" aria-label="held" /> : <X className="size-4 text-destructive-ink" aria-label="not held" />}
                            <span className="font-mono text-xs break-all">{p.key}</span>
                            <details className="text-muted-foreground">
                              <summary className="cursor-pointer">{d.because[0]}</summary>
                              <p className="mt-1 text-xs">{p.description}</p>
                              {d.because.length > 1 && (
                                <ol className="mt-1 list-decimal pl-5 text-xs">
                                  {d.because.slice(1).map((b, i) => (
                                    <li key={i}>{b}</li>
                                  ))}
                                </ol>
                              )}
                            </details>
                          </li>
                        )
                      })}
                    </ul>
                  </section>
                )
              })}
              <section aria-label="Rules that reach them" className="grid gap-1">
                <h3 className="text-sm font-medium">Rules that reach them</h3>
                {effective.data.rules.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No rule names them, their groups or their roles.</p>
                ) : (
                  <ul className="divide-y rounded-md border">
                    {effective.data.rules.map((r) => (
                      <li key={r.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm">
                        <Badge variant={r.effect === 'Deny' ? 'destructive' : 'success'}>{r.effect}</Badge>
                        <span className="font-mono text-xs">
                          {r.resourceKind}:{r.resource}
                        </span>
                        <span className="text-muted-foreground">for {r.subject.name}</span>
                        {r.note && <span className="text-xs text-muted-foreground">({r.note})</span>}
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            </>
          )
        )}
      </CardContent>
    </Card>
  )
}
