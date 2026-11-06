import { useQueryClient } from '@tanstack/react-query'
import { Search } from 'lucide-react'
import { Fragment, useMemo, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { gives, type AccessOverview, type Permission, type Role } from './access-api'

type Show = 'all' | 'custom'

/** Whether a permission matches what was typed: its key, its description or its area. */
const matches = (p: Permission, q: string) => !q || [p.key, p.description, p.area].some((s) => s.toLowerCase().includes(q))

/**
 * Roles × permissions, grouped by area: what each role gives, with the permissions it names ticked and those it gives
 * through a broader one shown ticked and greyed. A custom role's ticks change; the changes are kept until Save, which
 * changes each role at once (one entry each in the history).
 */
export function Matrix({ data }: { data: AccessOverview }) {
  const queryClient = useQueryClient()
  const [q, setQ] = useState('')
  const [show, setShow] = useState<Show>('all')
  const [edits, setEdits] = useState<Record<string, Set<string>>>({})
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const roles = data.roles.filter((r) => show === 'all' || !r.builtIn)
  const query = q.trim().toLowerCase()
  const areas = useMemo(() => {
    const out: { area: string; permissions: Permission[] }[] = []
    for (const p of data.permissions) {
      if (!matches(p, query)) continue
      let a = out.find((x) => x.area === p.area)
      if (!a) out.push((a = { area: p.area, permissions: [] }))
      a.permissions.push(p)
    }
    return out
  }, [data.permissions, query])
  const named = (r: Role) => edits[r.id] ?? new Set(r.permissions)
  const changed = Object.keys(edits).filter((id) => {
    const r = data.roles.find((x) => x.id === id)!
    const now = edits[id]
    return now.size !== r.permissions.length || r.permissions.some((p) => !now.has(p))
  })
  const set = (r: Role, keys: string[], on: boolean) => {
    const next = new Set(named(r))
    for (const k of keys) {
      if (on) next.add(k)
      else next.delete(k)
    }
    setEdits({ ...edits, [r.id]: next })
  }
  const save = async () => {
    setSaving(true)
    setError(null)
    try {
      for (const id of changed) {
        await api(`/api/admin/access/roles/${id}`, { method: 'PATCH', body: { permissions: [...edits[id]].sort() } })
      }
      toast.success(`${changed.length} role${changed.length === 1 ? '' : 's'} changed`)
      setEdits({})
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="relative min-w-0 flex-1 sm:max-w-sm">
          <Search className="pointer-events-none absolute top-2.5 left-2.5 size-4 text-muted-foreground" aria-hidden />
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search permissions" aria-label="Search permissions" className="pl-8" />
        </div>
        <Segmented
          label="Roles shown"
          value={show}
          onChange={setShow}
          options={[
            { value: 'all', label: 'All roles' },
            { value: 'custom', label: 'Custom roles' },
          ]}
        />
      </div>
      <p className="text-sm text-muted-foreground">
        A tick is what the role names; a grey tick, what it gives through a broader permission or by acting in the area (who resets passwords sees the people). Built-in roles stay as they are.
      </p>
      {error && <Alert variant="destructive">{error}</Alert>}
      {roles.length === 0 ? (
        <p className="text-sm text-muted-foreground">No custom roles yet: make one on the Roles tab, or from a template.</p>
      ) : (
        <div className="max-h-[70vh] overflow-auto rounded-md border">
          <table className="w-full border-separate border-spacing-0 text-sm">
            <caption className="sr-only">Roles and the permissions they give</caption>
            <thead className="sticky top-0 z-20 bg-card">
              <tr>
                <th scope="col" className="sticky left-0 z-30 min-w-56 border-b bg-card px-3 py-2 text-left text-xs font-medium text-muted-foreground">
                  Permission
                </th>
                {roles.map((r) => (
                  <th key={r.id} scope="col" className="min-w-24 border-b px-2 py-2 text-center text-xs font-medium">
                    <span className="block">{r.name}</span>
                    {r.builtIn ? (
                      <span className="font-normal text-muted-foreground">built-in</span>
                    ) : (
                      changed.includes(r.id) && <Badge variant="warning">changed</Badge>
                    )}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {areas.length === 0 && (
                <tr>
                  <td colSpan={roles.length + 1} className="px-3 py-4 text-muted-foreground">
                    No permission matches “{q}”.
                  </td>
                </tr>
              )}
              {areas.map((a) => (
                <Fragment key={a.area}>
                  <tr className="bg-muted/50">
                    <th scope="rowgroup" className="sticky left-0 z-10 border-b bg-muted px-3 py-1.5 text-left text-xs font-semibold">
                      {a.area}
                    </th>
                    {roles.map((r) => {
                      const keys = a.permissions.map((p) => p.key)
                      const all = keys.every((k) => named(r).has(k))
                      return (
                        <td key={r.id} className="border-b px-2 py-1.5 text-center">
                          {!r.builtIn && (
                            <Checkbox
                              checked={all}
                              onCheckedChange={(on) => set(r, keys, on === true)}
                              aria-label={`${r.name}: every permission of ${a.area}${query ? ' shown' : ''}`}
                            />
                          )}
                        </td>
                      )
                    })}
                  </tr>
                  {a.permissions.map((p) => (
                    <tr key={p.key} className="hover:bg-muted/30">
                      <th scope="row" className={`sticky left-0 z-10 border-b bg-card px-3 py-1.5 text-left font-normal ${p.parent ? 'pl-7' : ''}`}>
                        <span className="block font-mono text-xs">{p.key}</span>
                        <span className="block text-xs text-muted-foreground">{p.description}</span>
                      </th>
                      {roles.map((r) => {
                        const own = named(r)
                        const isNamed = own.has(p.key)
                        const given = isNamed || (edits[r.id] ? gives(own, p.key, data.permissions) : (r.gives ?? r.permissions).includes(p.key))
                        const through = given && !isNamed
                        return (
                          <td key={r.id} className="border-b px-2 py-1.5 text-center">
                            <Checkbox
                              checked={given}
                              disabled={r.builtIn || through}
                              className={through ? 'opacity-50' : undefined}
                              onCheckedChange={(on) => set(r, [p.key], on === true)}
                              aria-label={`${r.name}: ${p.key}${through ? ' (given through a broader permission)' : ''}`}
                            />
                          </td>
                        )
                      })}
                    </tr>
                  ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {changed.length > 0 && (
        <section className="sticky bottom-2 z-30 flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-card p-3 shadow-md" aria-label="Changes not saved">
          <span className="text-sm">
            {changed.length} role{changed.length === 1 ? '' : 's'} changed: {changed.map((id) => data.roles.find((r) => r.id === id)!.name).join(', ')}
          </span>
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => setEdits({})} disabled={saving}>
              Discard
            </Button>
            <Button onClick={() => void save()} loading={saving}>
              Save changes
            </Button>
          </div>
        </section>
      )}
    </div>
  )
}
