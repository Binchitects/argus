import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link2, Unlink } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'

interface Connection {
  toolId: string
  title: string
  kind: 'oauth2' | 'api_key'
  help: string | null
  connected: boolean
  account: string | null
  since: string | null
}

const key = ['account', 'connections']

/** The plugins that work as the person: their own account at each service, connected by signing in there or with their key. */
export function Connections() {
  const queryClient = useQueryClient()
  const connections = useQuery({ queryKey: key, queryFn: () => api<Connection[]>('/api/account/connections') })
  const [params, setParams] = useSearchParams()
  useEffect(() => {
    const connected = params.get('connected')
    const failed = params.get('connection')
    if (!connected && !failed) return
    if (connected) toast.success(`${connected} connected`, { description: 'The chat can now use it as you.' })
    else toast.error(failed === 'declined' ? 'You did not allow the connection.' : 'The connection did not work. Try again.')
    setParams({}, { replace: true })
    void queryClient.invalidateQueries({ queryKey: key })
  }, [params, setParams, queryClient])
  if (!connections.data || connections.data.length === 0) return null
  return (
    <Card>
      <CardHeader>
        <CardTitle>Connections</CardTitle>
        <CardDescription>Tools in the chat that work as you, with your own account at each service. Only you can use what you connect.</CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="grid gap-3">
          {connections.data.map((c) => (
            <ConnectionRow key={c.toolId} connection={c} onChanged={() => queryClient.invalidateQueries({ queryKey: key })} />
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

function ConnectionRow({ connection: c, onChanged }: { connection: Connection; onChanged: () => void }) {
  const [secret, setSecret] = useState('')
  const path = `/api/account/connections/${encodeURIComponent(c.toolId)}`
  const save = useMutation({
    mutationFn: () => api(path, { method: 'PUT', body: { secret } }),
    onSuccess: () => {
      setSecret('')
      toast.success(`${c.title} connected`)
      onChanged()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const disconnect = useMutation({
    mutationFn: () => api(path, { method: 'DELETE' }),
    onSuccess: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <li className="grid gap-2 rounded-lg border p-3" aria-label={c.title}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{c.title}</span>
        {c.connected ? <Badge variant="success">Connected</Badge> : <Badge variant="secondary">Not connected</Badge>}
        {c.connected && c.since && <span className="text-xs text-muted-foreground">since {when(c.since)}</span>}
        <span className="flex-1" />
        {c.connected ? (
          <Button variant="outline" size="sm" loading={disconnect.isPending} onClick={() => disconnect.mutate()}>
            <Unlink /> Disconnect
          </Button>
        ) : (
          c.kind === 'oauth2' && (
            <Button size="sm" asChild>
              <a href={`${path}/connect`}>
                <Link2 /> Connect
              </a>
            </Button>
          )
        )}
      </div>
      {c.help && <p className="text-xs text-muted-foreground">{c.help}</p>}
      {!c.connected && c.kind === 'api_key' && (
        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            save.mutate()
          }}
        >
          <Input type="password" autoComplete="new-password" required aria-label={`${c.title} key`} placeholder="Your key" value={secret} onChange={(e) => setSecret(e.target.value)} />
          <Button type="submit" size="sm" loading={save.isPending}>
            Save
          </Button>
        </form>
      )}
    </li>
  )
}
