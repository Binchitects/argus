import { useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound } from 'lucide-react'
import { useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

interface MyRequest {
  id: string
  role: string | null
  resourceKind: string | null
  resource: string | null
  status: 'pending' | 'approved' | 'denied'
  note: string | null
  createdAt: string
}

/** Ask for access: a role (Supervisor, Operator...) or a feature, model or tool by its name; those who manage access answer. */
export function AskForAccess() {
  const queryClient = useQueryClient()
  const roles = useQuery({ queryKey: ['access-requests', 'roles'], queryFn: ({ signal }) => api<{ id: string; name: string; description: string | null }[]>('/api/access-requests/roles', { signal }) })
  const mine = useQuery({ queryKey: ['access-requests', 'mine'], queryFn: ({ signal }) => api<MyRequest[]>('/api/access-requests', { signal }) })
  const [what, setWhat] = useState<'role' | 'resource'>('role')
  const [roleId, setRoleId] = useState('')
  const [kind, setKind] = useState('model')
  const [resource, setResource] = useState('')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const ask = async () => {
    setBusy(true)
    try {
      await api('/api/access-requests', { body: what === 'role' ? { roleId, reason } : { resourceKind: kind, resource: resource.trim(), reason } })
      toast.success('Asked: you hear back in your bell')
      setReason('')
      setResource('')
      await queryClient.invalidateQueries({ queryKey: ['access-requests', 'mine'] })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <KeyRound className="size-4" aria-hidden="true" /> Ask for access
        </CardTitle>
        <CardDescription>A role (to watch the service, run it, or manage a team), or a model, tool or feature you cannot use yet.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <Segmented
          label="Ask for"
          value={what}
          onChange={setWhat}
          options={[
            { value: 'role', label: 'A role' },
            { value: 'resource', label: 'A model, tool or feature' },
          ]}
        />
        {what === 'role' ? (
          <Field label="Role" hint={roles.data?.find((r) => r.id === roleId)?.description ?? undefined}>
            <Select value={roleId} onValueChange={setRoleId}>
              <SelectTrigger>
                <SelectValue placeholder="Choose…" />
              </SelectTrigger>
              <SelectContent>
                {(roles.data ?? []).map((r) => (
                  <SelectItem key={r.id} value={r.id}>
                    {r.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        ) : (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Kind">
              <Select value={kind} onValueChange={setKind}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="model">Model</SelectItem>
                  <SelectItem value="tool">Tool</SelectItem>
                  <SelectItem value="feature">Feature</SelectItem>
                  <SelectItem value="knowledge_source">Knowledge source</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field label="Name" hint={kind === 'feature' ? 'e.g. feature.video' : undefined}>
              <Input value={resource} onChange={(e) => setResource(e.target.value)} />
            </Field>
          </div>
        )}
        <Field label="Why">
          <Textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <div>
          <Button disabled={busy || !reason.trim() || (what === 'role' ? !roleId : !resource.trim())} onClick={() => void ask()}>
            Ask
          </Button>
        </div>
        {!!mine.data?.length && (
          <ul className="grid gap-1.5 text-sm" aria-label="Your requests">
            {mine.data.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-2">
                <span>{r.role ? `Role ${r.role}` : `${r.resourceKind?.replace('_', ' ')} ${r.resource}`}</span>
                <Badge variant={r.status === 'approved' ? 'success' : r.status === 'denied' ? 'destructive' : 'secondary'}>
                  {r.status === 'approved' ? 'Given' : r.status === 'denied' ? 'Declined' : 'Waiting'}
                </Badge>
                {r.note && <span className="text-xs text-muted-foreground">“{r.note}”</span>}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
