import { useQuery } from '@tanstack/react-query'
import { Users } from 'lucide-react'
import { useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { groupsQuery } from './groups-api'

export type Audience = 'Everyone' | 'Admins' | 'Groups'

/** Who may use a tool or a model: everyone, admins, or chosen groups (admins always may). */
export interface AccessRule {
  audience: Audience
  groups: { id: string; name: string }[]
}

/**
 * "Who may use it": everyone, admins only, or chosen groups. "Chosen groups" is
 * saved once a group is chosen: until then it would let nobody in.
 */
export function AccessPicker({ value, onChange, label = 'Who may use it' }: { value: AccessRule; onChange: (audience: Audience, groups: string[]) => void; label?: string }) {
  const id = useId()
  const [choosingGroups, setChoosingGroups] = useState(false)
  const chosen = value.groups.map((g) => g.id)
  const audience = choosingGroups ? 'Groups' : value.audience
  return (
    <div className="grid gap-2 sm:grid-cols-[auto_minmax(0,1fr)] sm:items-center sm:gap-3">
      <Label htmlFor={id} className="text-muted-foreground">
        {label}
      </Label>
      <div className="flex flex-wrap items-center gap-2">
        <Select
          value={audience}
          onValueChange={(a: Audience) => {
            setChoosingGroups(a === 'Groups' && chosen.length === 0)
            if (a !== 'Groups' || chosen.length > 0) onChange(a, a === 'Groups' ? chosen : [])
          }}
        >
          <SelectTrigger id={id} size="sm" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="Everyone">Everyone</SelectItem>
            <SelectItem value="Admins">Admins only</SelectItem>
            <SelectItem value="Groups">Chosen groups</SelectItem>
          </SelectContent>
        </Select>
        {audience === 'Groups' && (
          <GroupsPicker
            chosen={chosen}
            names={value.groups}
            onChange={(groups) => {
              setChoosingGroups(false)
              onChange('Groups', groups)
            }}
          />
        )}
      </div>
    </div>
  )
}

function GroupsPicker({ chosen, onChange, names }: { chosen: string[]; onChange: (groups: string[]) => void; names: { id: string; name: string }[] }) {
  const groups = useQuery(groupsQuery)
  const label = chosen.length === 0 ? 'Choose groups' : names.map((g) => g.name).join(', ') || `${chosen.length} groups`
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="max-w-64 min-w-0 justify-start">
          <Users /> <span className="truncate">{label}</span>
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-2">
        {groups.data?.length === 0 ? (
          <p className="p-2 text-sm text-muted-foreground">
            No groups yet. Make one under <a href="/admin/groups" className="underline underline-offset-2">Groups</a>.
          </p>
        ) : (
          <ul className="grid max-h-64 gap-0.5 overflow-y-auto">
            {groups.data?.map((g) => (
              <li key={g.id} className="flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent">
                <Checkbox
                  id={`pick-${g.id}`}
                  checked={chosen.includes(g.id)}
                  onCheckedChange={(v) => {
                    const next = v === true ? [...chosen, g.id] : chosen.filter((x) => x !== g.id)
                    if (next.length > 0) onChange(next)
                    else toast.error('Keep at least one group, or let everyone use it.')
                  }}
                />
                <label htmlFor={`pick-${g.id}`} className="min-w-0 flex-1 cursor-pointer truncate text-sm">
                  {g.name}
                  <span className="ml-1 text-xs text-muted-foreground">({g.members})</span>
                </label>
              </li>
            ))}
          </ul>
        )}
      </PopoverContent>
    </Popover>
  )
}
