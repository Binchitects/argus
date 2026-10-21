import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GitPullRequest } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

interface CodeHost {
  kind: 'github' | 'gitea'
  title: string
  linked: boolean
  account: string | null
  linkedAt: string | null
}

const key = ['account', 'code-hosts']

/** One host: linked as whom (and unlink), or a token to paste. */
function Host({ host }: { host: CodeHost }) {
  const queryClient = useQueryClient()
  const [token, setToken] = useState('')
  const done = () => void queryClient.invalidateQueries({ queryKey: key })
  const link = useMutation({
    mutationFn: () => api<{ account: string }>(`/api/account/code-hosts/${host.kind}`, { method: 'PUT', body: { token: token.trim() } }),
    onSuccess: (r) => {
      setToken('')
      toast.success(`${host.title} linked as ${r.account}`)
      done()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const unlink = useMutation({
    mutationFn: () => api(`/api/account/code-hosts/${host.kind}`, { method: 'DELETE' }),
    onSuccess: done,
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <li className="grid gap-2 rounded-md border p-3 text-sm">
      <div className="flex items-center gap-2">
        <span className="flex-1 font-medium">{host.title}</span>
        {host.linked ? <Badge variant="success">as {host.account}</Badge> : <Badge variant="outline">Not linked</Badge>}
        {host.linked && (
          <Button size="sm" variant="ghost" onClick={() => unlink.mutate()} loading={unlink.isPending}>
            Unlink
          </Button>
        )}
      </div>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          link.mutate()
        }}
      >
        <Input
          type="password"
          autoComplete="off"
          value={token}
          onChange={(e) => setToken(e.target.value)}
          placeholder={host.linked ? 'A new token, to replace it' : 'A read-only access token'}
          aria-label={`${host.title} token`}
        />
        <Button type="submit" variant="outline" disabled={!token.trim()} loading={link.isPending}>
          {host.linked ? 'Replace' : 'Link'}
        </Button>
      </form>
    </li>
  )
}

/**
 * Account → Code hosts: your own read-only token at GitHub or Gitea, so that reviews, the code assistant and the chat's
 * code tool read pull requests there as you (projects named github:owner/repo or gitea:owner/repo).
 */
export function CodeHosts() {
  const hosts = useQuery({ queryKey: key, queryFn: ({ signal }) => api<CodeHost[]>('/api/account/code-hosts', { signal }) })
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <GitPullRequest className="size-4" aria-hidden="true" /> Code hosts
        </CardTitle>
        <CardDescription>
          Review pull requests on GitHub or Gitea as you: link a read-only token (contents, pull requests and metadata), then name projects as github:owner/repo. It is checked, kept encrypted and never shown again.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {hosts.error ? (
          <QueryError error={hosts.error} retry={() => hosts.refetch()} />
        ) : hosts.isPending ? (
          <Skeleton className="h-24" />
        ) : (
          <ul className="grid gap-2" aria-label="Code hosts">
            {hosts.data.map((h) => (
              <Host key={h.kind} host={h} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
