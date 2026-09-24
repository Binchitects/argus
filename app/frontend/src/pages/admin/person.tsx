import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, KeyRound, LogOut, RefreshCw, ShieldOff, Trash2, UserCheck, UserCog, UserX } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useOutletContext, useParams } from 'react-router'
import { KeyValues } from '@/components/app/key-values'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Secret } from '@/components/app/secret'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago, money, when } from '@/lib/format'
import { CreditMeter, PersonBadges } from './person-badges'
import { parseCredit, personQuery, type Person } from './people-api'

export function PersonPage() {
  const { id = '' } = useParams()
  const me = useOutletContext<Me>()
  const detail = useQuery(personQuery(id))
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null)

  if (detail.isPending) return <PageSkeleton />
  if (detail.error) return <QueryError error={detail.error} retry={() => detail.refetch()} />
  const { person: p, keys, warning } = detail.data
  const self = p.id === me.id
  const ldap = p.source === 'ldap'

  return (
    <>
      <Button variant="ghost" size="sm" asChild className="mb-4 -ml-2 text-muted-foreground">
        <Link to="/admin/people">
          <ArrowLeft /> People
        </Link>
      </Button>
      <div className="mb-6 flex flex-wrap items-center gap-4">
        <Avatar name={p.displayName || p.userName} className="size-14 text-lg" />
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight">{p.displayName}</h1>
          <p className="text-muted-foreground">
            {p.userName} · {p.email}
          </p>
          <div className="mt-2 flex flex-wrap gap-1.5">
            <Badge variant={p.isAdmin ? 'default' : 'secondary'}>{p.isAdmin ? 'Admin' : 'Member'}</Badge>
            <Badge variant="outline">{ldap ? 'Company directory' : 'Local account'}</Badge>
            <PersonBadges p={p} />
            {self && <Badge variant="outline">You</Badge>}
          </div>
        </div>
      </div>
      {warning && (
        <Alert variant="warning" className="mb-4">
          {warning}
        </Alert>
      )}
      {secret && (
        <Alert variant="success" title={`${secret.label} — shown only this once`} className="mb-4">
          <div className="mt-2">
            <Secret label={secret.label} value={secret.value} />
          </div>
        </Alert>
      )}
      <div className="grid gap-6 lg:grid-cols-2">
        <div className="grid content-start gap-6">
          <Profile p={p} />
          <Credit p={p} keys={keys} onSecret={setSecret} />
        </div>
        <div className="grid content-start gap-6">
          <Access p={p} self={self} onSecret={setSecret} />
          {!self && <Danger p={p} />}
        </div>
      </div>
    </>
  )
}

function useRefresh(id: string) {
  const queryClient = useQueryClient()
  return () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'person', id] })
    void queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
  }
}

function Profile({ p }: { p: Person }) {
  const refresh = useRefresh(p.id)
  const [name, setName] = useState<string | null>(null)
  const rename = useMutation({
    mutationFn: (displayName: string) => api(`/api/admin/people/${p.id}`, { method: 'PATCH', body: { displayName } }),
    onSuccess: () => {
      setName(null)
      toast.success('Name changed')
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Profile</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-4">
        <KeyValues
          items={[
            ['Signs in with', p.source === 'ldap' ? 'the company directory (LDAP)' : 'a password here'],
            ['Last sign-in', p.lastSignInAt ? `${ago(p.lastSignInAt)} (${when(p.lastSignInAt)})` : 'never'],
            ['Added', when(p.createdAt)],
          ]}
        />
        {p.source === 'local' &&
          (name === null ? (
            <div>
              <Button variant="outline" size="sm" onClick={() => setName(p.displayName)}>
                Change name
              </Button>
            </div>
          ) : (
            <form
              className="flex flex-wrap items-end gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (name.trim()) rename.mutate(name.trim())
              }}
            >
              <Field label="Name" className="min-w-48 flex-1">
                <Input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
              </Field>
              <Button type="submit" loading={rename.isPending}>
                Save
              </Button>
              <Button type="button" variant="ghost" onClick={() => setName(null)}>
                Cancel
              </Button>
            </form>
          ))}
      </CardContent>
    </Card>
  )
}

function Access({ p, self, onSecret }: { p: Person; self: boolean; onSecret: (s: { label: string; value: string }) => void }) {
  const refresh = useRefresh(p.id)
  const confirm = useConfirm()
  const act = useMutation({
    mutationFn: ({ path, method = 'POST', body = {} }: { path: string; method?: string; body?: object; done: string }) =>
      api<{ password?: string } | undefined>(`/api/admin/people/${p.id}${path}`, { method, body }),
    onSuccess: (r, v) => {
      if (r?.password) onSecret({ label: 'New password', value: r.password })
      toast.success(v.done)
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const ldap = p.source === 'ldap'
  const ask = async (title: string, description: string, confirmText: string, run: () => void, destructive = true) => {
    if (await confirm({ title, description, confirm: confirmText, destructive })) run()
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle>Access</CardTitle>
        <CardDescription>{ldap ? "The directory decides this person's password and role." : 'Role, sign-in and sessions.'}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-wrap gap-2">
        {!ldap && !self && (
          <Button
            variant="outline"
            onClick={() =>
              ask(
                p.isAdmin ? `Remove admin from ${p.displayName}?` : `Make ${p.displayName} an admin?`,
                p.isAdmin ? 'They keep their account and credit, and lose the admin pages.' : 'Admins manage people, settings and the model.',
                p.isAdmin ? 'Remove admin' : 'Make admin',
                () => act.mutate({ path: '', method: 'PATCH', body: { admin: !p.isAdmin }, done: p.isAdmin ? 'No longer an admin' : 'Now an admin' }),
                p.isAdmin,
              )
            }
          >
            <UserCog /> {p.isAdmin ? 'Remove admin' : 'Make admin'}
          </Button>
        )}
        {!self && (
          <Button
            variant="outline"
            onClick={() =>
              p.disabled
                ? act.mutate({ path: '', method: 'PATCH', body: { disabled: false }, done: 'Enabled' })
                : ask(`Disable ${p.displayName}?`, 'They are signed out and their API keys stop working until enabled again.', 'Disable', () =>
                    act.mutate({ path: '', method: 'PATCH', body: { disabled: true }, done: 'Disabled' }),
                  )
            }
          >
            {p.disabled ? <UserCheck /> : <UserX />} {p.disabled ? 'Enable' : 'Disable'}
          </Button>
        )}
        {!ldap && (
          <Button
            variant="outline"
            onClick={() => ask(`Give ${p.displayName} a new password?`, 'The old one stops working at once. You will see the new one here, once.', 'Reset password', () => act.mutate({ path: '/password', done: 'Password reset' }))}
          >
            <KeyRound /> Reset password
          </Button>
        )}
        {p.twoFactorEnabled && (
          <Button
            variant="outline"
            onClick={() => ask(`Turn off two-factor sign-in for ${p.displayName}?`, 'For a lost phone. They can set it up again from their account page.', 'Turn off 2FA', () => act.mutate({ path: '/2fa/reset', done: 'Two-factor sign-in turned off' }))}
          >
            <ShieldOff /> Reset 2FA
          </Button>
        )}
        <Button variant="outline" onClick={() => act.mutate({ path: '/sign-out', done: 'Signed out everywhere' })}>
          <LogOut /> Sign out everywhere
        </Button>
      </CardContent>
    </Card>
  )
}

function Credit({ p, keys, onSecret }: { p: Person; keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]; onSecret: (s: { label: string; value: string }) => void }) {
  const refresh = useRefresh(p.id)
  const confirm = useConfirm()
  const [credit, setCredit] = useState(p.budget === null ? '' : String(p.budget))
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (budget: number | null) => api(`/api/admin/people/${p.id}/budget`, { method: 'PUT', body: { budget } }),
    onSuccess: () => {
      toast.success('Credit set', { description: 'API keys at once; the chat within about a minute (the gateway caches it).' })
      refresh()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const rotate = useMutation({
    mutationFn: () => api<{ apiKey: string }>(`/api/admin/people/${p.id}/key`, { body: {} }),
    onSuccess: (r) => {
      onSecret({ label: 'New API key', value: r.apiKey })
      refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle>Credit and API key</CardTitle>
        <CardDescription>Spend from the chat and the API key counts against the credit.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <CreditMeter spend={p.spend} budget={p.budget} />
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            const c = parseCredit(credit)
            if (c === 'invalid') setError('A number of dollars, or empty for no limit.')
            else save.mutate(c)
          }}
        >
          <Field label="Credit ($)" hint="Empty: no limit." error={error ?? undefined} className="w-40">
            <Input inputMode="decimal" value={credit} onChange={(e) => setCredit(e.target.value)} placeholder="no limit" />
          </Field>
          <Button type="submit" variant="outline" loading={save.isPending}>
            Set credit
          </Button>
        </form>
        <ul className="grid gap-2">
          {keys.length === 0 && <li className="text-sm text-muted-foreground">No API key.</li>}
          {keys.map((k) => (
            <li key={k.alias + k.preview} className="flex flex-wrap items-center gap-3 rounded-lg border px-3 py-2 text-sm">
              <KeyRound className="size-4 text-muted-foreground" aria-hidden="true" />
              <code className="font-mono text-[0.8125rem]">{k.preview ?? k.alias}</code>
              {k.blocked && <Badge variant="destructive">Blocked</Badge>}
              <span className="ml-auto text-xs text-muted-foreground tabular-nums">{money(k.spend)} spent</span>
            </li>
          ))}
        </ul>
      </CardContent>
      <CardFooter>
        <Button
          variant="outline"
          loading={rotate.isPending}
          onClick={async () => {
            if (await confirm({ title: `New API key for ${p.displayName}?`, description: 'The current key stops working at once.', confirm: 'Make a new key', destructive: true })) rotate.mutate()
          }}
        >
          <RefreshCw /> New API key
        </Button>
      </CardFooter>
    </Card>
  )
}

function Danger({ p }: { p: Person }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [typed, setTyped] = useState('')
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/people/${p.id}`, { method: 'DELETE' }),
    onSuccess: () => {
      toast.success(`${p.displayName} deleted`)
      void queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
      navigate('/admin/people')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card className="border-destructive/30">
      <CardHeader>
        <CardTitle>Delete</CardTitle>
        <CardDescription>Removes the person, their chats and their API keys. Their usage history stays in the reports.</CardDescription>
      </CardHeader>
      <CardContent>
        <Button variant="destructive" onClick={() => setOpen(true)}>
          <Trash2 /> Delete {p.userName}
        </Button>
      </CardContent>
      <Dialog open={open} onOpenChange={(o) => { setOpen(o); setTyped('') }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete {p.displayName}?</DialogTitle>
            <DialogDescription>This cannot be undone. Type {p.userName} to confirm.</DialogDescription>
          </DialogHeader>
          <Field label="Username">
            <Input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" autoFocus />
          </Field>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={typed !== p.userName} loading={remove.isPending} onClick={() => remove.mutate()}>
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
