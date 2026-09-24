import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate, useOutletContext, useParams } from 'react-router'
import { api, type Me } from '../../api'
import { ErrorText } from '../../components/ErrorText'
import { OnceNotice, Secret } from '../../components/Secret'
import { money, when } from '../../format'
import { Badges } from './People'
import type { Person } from './types'

interface Detail {
  person: Person
  keys: { alias: string; preview: string | null; spend: number; blocked: boolean; createdAt: string | null }[]
  warning: string | null
}

export function PersonPage() {
  const { id } = useParams()
  const me = useOutletContext<Me>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const key = ['admin', 'person', id]
  const detail = useQuery({ queryKey: key, queryFn: () => api<Detail>(`/api/admin/people/${id}`) })
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null)
  const done = () => {
    queryClient.invalidateQueries({ queryKey: key })
    queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
  }
  const act = useMutation({
    mutationFn: ({ path, method = 'POST', body = {} }: { path: string; method?: string; body?: object }) =>
      api<{ password?: string; apiKey?: string } | undefined>(`/api/admin/people/${id}${path}`, { method, body }),
    onSuccess: (r) => {
      if (r?.password) setSecret({ label: 'New password', value: r.password })
      if (r?.apiKey) setSecret({ label: 'New API key', value: r.apiKey })
      done()
    },
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/admin/people/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
      navigate('/admin')
    },
  })

  if (detail.isPending) return <p aria-busy="true">Loading…</p>
  if (detail.error) return <ErrorText error={detail.error} />
  const { person: p, keys, warning } = detail.data
  const self = p.id === me.id
  const ldap = p.source === 'ldap'
  const confirmThen = (question: string, fn: () => void) => () => window.confirm(question) && fn()

  return (
    <div className="stack">
      <p>
        <Link to="/admin">← People</Link>
      </p>
      <section className="card">
        <h2>
          {p.displayName} <Badges p={p} />
        </h2>
        <dl className="facts">
          <dt>Username</dt>
          <dd>{p.userName}</dd>
          <dt>Email</dt>
          <dd>{p.email}</dd>
          <dt>Role</dt>
          <dd>{p.isAdmin ? 'Admin' : 'Member'}</dd>
          <dt>Signs in with</dt>
          <dd>{ldap ? 'the company directory (LDAP)' : 'a password here'}</dd>
          <dt>Spend / credit</dt>
          <dd>
            {money(p.spend ?? 0)} / {money(p.budget)}
          </dd>
          <dt>Last sign-in</dt>
          <dd>{when(p.lastSignInAt)}</dd>
          <dt>Added</dt>
          <dd>{when(p.createdAt)}</dd>
        </dl>
        {warning && <p className="warn-text">{warning}</p>}
      </section>

      {secret && (
        <OnceNotice>
          <Secret label={secret.label} value={secret.value} />
        </OnceNotice>
      )}
      <ErrorText error={act.error ?? remove.error} />

      <section className="card">
        <h2>Access</h2>
        <div className="actions">
          {!ldap && !self && (
            <button className="button secondary" onClick={() => act.mutate({ path: '', method: 'PATCH', body: { admin: !p.isAdmin } })}>
              {p.isAdmin ? 'Remove admin' : 'Make admin'}
            </button>
          )}
          {!self && (
            <button
              className="button secondary"
              onClick={confirmThen(
                p.disabled ? `Enable ${p.userName}?` : `Disable ${p.userName}? They are signed out and their API keys stop working.`,
                () => act.mutate({ path: '', method: 'PATCH', body: { disabled: !p.disabled } }),
              )}
            >
              {p.disabled ? 'Enable' : 'Disable'}
            </button>
          )}
          {!ldap && (
            <button className="button secondary" onClick={confirmThen(`Give ${p.userName} a new password? The old one stops working.`, () => act.mutate({ path: '/password' }))}>
              Reset password
            </button>
          )}
          {p.twoFactorEnabled && (
            <button className="button secondary" onClick={confirmThen(`Turn off two-factor sign-in for ${p.userName}?`, () => act.mutate({ path: '/2fa/reset' }))}>
              Reset 2FA
            </button>
          )}
          <button className="button secondary" onClick={() => act.mutate({ path: '/sign-out' })}>
            Sign out everywhere
          </button>
        </div>
        {ldap && <p className="muted">The directory decides this person&apos;s password and role.</p>}
      </section>

      <section className="card">
        <h2>API keys and credit</h2>
        <ul className="plain">
          {keys.length === 0 && <li>No key.</li>}
          {keys.map((k) => (
            <li key={k.alias + k.preview}>
              <code>{k.preview ?? k.alias}</code> · {money(k.spend)} {k.blocked && <span className="badge warn">blocked</span>}
            </li>
          ))}
        </ul>
        <div className="actions">
          <button className="button secondary" onClick={confirmThen(`New API key for ${p.userName}? The current one stops working.`, () => act.mutate({ path: '/key' }))}>
            New API key
          </button>
          <form
            className="inline-form"
            onSubmit={(e) => {
              e.preventDefault()
              const v = String(new FormData(e.currentTarget).get('budget') ?? '').trim()
              act.mutate({ path: '/budget', method: 'PUT', body: { budget: v === '' ? null : Number(v) } })
            }}
          >
            <label>
              Credit in $
              <input name="budget" type="number" min="0" step="0.01" defaultValue={p.budget ?? ''} placeholder="unlimited" />
            </label>
            <button className="button secondary">Set credit</button>
          </form>
        </div>
      </section>

      {!self && (
        <section className="card danger">
          <h2>Delete</h2>
          <p className="muted">Removes the person and their API keys. Their usage history stays.</p>
          <button
            className="button danger"
            onClick={() => window.prompt(`Type ${p.userName} to delete this person.`) === p.userName && remove.mutate()}
          >
            Delete {p.userName}
          </button>
        </section>
      )}
    </div>
  )
}
