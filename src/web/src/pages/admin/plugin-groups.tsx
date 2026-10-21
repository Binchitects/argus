import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { groupsQuery } from './groups-api'

interface GroupSetting {
  key: string
  title: string
  type: 'text' | 'url' | 'secret'
  help: string | null
  value: string | null
  set: boolean
}

interface GroupRow {
  groupId: string
  name: string
  settings: GroupSetting[]
}

/** One group's own values: each setting given is the group's; one left empty is the plugin's. */
function GroupForm({ pluginId, groupId, settings, onSaved }: { pluginId: string; groupId: string; settings: GroupSetting[]; onSaved: () => void }) {
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(settings.map((s) => [s.key, s.type === 'secret' ? '' : (s.value ?? '')])))
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    // A secret left empty but set stays as it is; anything else empty is the plugin's own again.
    mutationFn: () =>
      api(`/api/admin/plugins/${pluginId}/groups/${groupId}`, {
        method: 'PUT',
        body: { settings: Object.fromEntries(settings.filter((s) => !(s.type === 'secret' && s.set && !values[s.key])).map((s) => [s.key, values[s.key] ?? ''])) },
      }),
    onSuccess: () => {
      toast.success('Saved')
      onSaved()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      {error && <Alert variant="destructive">{error}</Alert>}
      {settings.map((s) => (
        <Field key={s.key} label={s.title} hint={s.type === 'secret' && s.set ? 'The group’s own, never shown. Leave empty to keep it.' : 'Empty: the plugin’s own.'}>
          <Input
            type={s.type === 'secret' ? 'password' : s.type === 'url' ? 'url' : 'text'}
            autoComplete={s.type === 'secret' ? 'new-password' : 'off'}
            value={values[s.key] ?? ''}
            onChange={(e) => setValues({ ...values, [s.key]: e.target.value })}
          />
        </Field>
      ))}
      <div>
        <Button type="submit" size="sm" loading={save.isPending}>
          Save for the group
        </Button>
      </div>
    </form>
  )
}

/**
 * Admin → Plugins → For groups: a group's own values for a plugin's settings (another key, another address), which its
 * people use; everyone else uses the plugin's. Someone in several groups gets each setting from the first by name.
 */
export function PluginGroupsDialog({ plugin, open, onClose }: { plugin: { id: string; title: string; settings: { key: string; title: string; type: GroupSetting['type'] }[] }; open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const key = ['admin', 'plugins', plugin.id, 'groups']
  const rows = useQuery({ queryKey: key, queryFn: ({ signal }) => api<GroupRow[]>(`/api/admin/plugins/${plugin.id}/groups`, { signal }), enabled: open })
  const groups = useQuery({ ...groupsQuery, enabled: open })
  const [adding, setAdding] = useState('')
  const refresh = () => void queryClient.invalidateQueries({ queryKey: key })
  const remove = useMutation({
    mutationFn: (groupId: string) => api(`/api/admin/plugins/${plugin.id}/groups/${groupId}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const taken = new Set((rows.data ?? []).map((r) => r.groupId))
  const free = ((groups.data ?? []) as { id: string; name: string }[]).filter((g) => !taken.has(g.id))
  const fresh = plugin.settings.map((s) => ({ ...s, help: null, value: null, set: false }))
  const name = (groups.data as { id: string; name: string }[] | undefined)?.find((g) => g.id === adding)?.name
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{plugin.title}: for groups</DialogTitle>
          <DialogDescription>A group’s own values (another key, another address) are what its people use; everyone else uses the plugin’s.</DialogDescription>
        </DialogHeader>
        {rows.error ? (
          <QueryError error={rows.error} retry={() => rows.refetch()} />
        ) : rows.isPending ? (
          <Skeleton className="h-24" />
        ) : (
          <ul className="grid gap-3" aria-label="Groups with their own settings">
            {rows.data.map((r) => (
              <li key={r.groupId} className="grid gap-2 rounded-md border p-3">
                <div className="flex items-center gap-2">
                  <span className="flex-1 font-medium">{r.name}</span>
                  <Button size="icon-sm" variant="ghost" aria-label={`${r.name}: the plugin’s own settings`} onClick={() => remove.mutate(r.groupId)}>
                    <Trash2 />
                  </Button>
                </div>
                <GroupForm pluginId={plugin.id} groupId={r.groupId} settings={r.settings} onSaved={refresh} />
              </li>
            ))}
          </ul>
        )}
        {plugin.settings.length > 0 && free.length > 0 && (
          <section className="grid gap-3 rounded-md border border-dashed p-3" aria-label="Another group">
            <Field label="Another group">
              <Select value={adding} onValueChange={setAdding}>
                <SelectTrigger>
                  <SelectValue placeholder="A group…" />
                </SelectTrigger>
                <SelectContent>
                  {free.map((g) => (
                    <SelectItem key={g.id} value={g.id}>
                      {g.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            {adding && (
              <GroupForm
                key={adding}
                pluginId={plugin.id}
                groupId={adding}
                settings={fresh}
                onSaved={() => {
                  toast.success(`${name ?? 'The group'} has its own settings`)
                  setAdding('')
                  refresh()
                }}
              />
            )}
          </section>
        )}
      </DialogContent>
    </Dialog>
  )
}
