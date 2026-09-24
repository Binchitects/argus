import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../../api'
import { ErrorText } from '../../components/ErrorText'
import { OnceNotice, Secret } from '../../components/Secret'
import { money, when } from '../../format'
import type { Created, Person } from './types'

export function People() {
  const people = useQuery({ queryKey: ['admin', 'people'], queryFn: () => api<{ warning: string | null; people: Person[] }>('/api/admin/people') })
  const [filter, setFilter] = useState('')
  const shown = (people.data?.people ?? []).filter((p) =>
    `${p.userName} ${p.displayName} ${p.email}`.toLowerCase().includes(filter.trim().toLowerCase()),
  )
  return (
    <div className="stack">
      <AddPerson />
      <section className="card">
        <div className="row">
          <h2>People</h2>
          <div className="row">
            <input type="search" placeholder="Search" aria-label="Search people" value={filter} onChange={(e) => setFilter(e.target.value)} />
            <a className="button small secondary" href="/api/admin/people.csv" download>
              Export CSV
            </a>
          </div>
        </div>
        {people.data?.warning && <p className="warn-text">{people.data.warning}</p>}
        <ErrorText error={people.error} />
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Person</th>
                <th>Email</th>
                <th>Role</th>
                <th>Spend / credit</th>
                <th>Last sign-in</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((p) => (
                <tr key={p.id}>
                  <td>
                    <Link to={`/admin/people/${p.id}`}>{p.displayName}</Link>
                    <div className="muted">
                      {p.userName}
                      {p.source === 'ldap' && ' · directory'}
                    </div>
                  </td>
                  <td>{p.email}</td>
                  <td>
                    {p.isAdmin ? 'Admin' : 'Member'} <Badges p={p} />
                  </td>
                  <td>
                    {money(p.spend ?? 0)} / {money(p.budget)}
                  </td>
                  <td>{when(p.lastSignInAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}

export function Badges({ p }: { p: Person }) {
  return (
    <>
      {p.disabled && <span className="badge warn">disabled{p.disabledReason === 'ldap' ? ' by directory' : ''}</span>}
      {p.lockedOut && <span className="badge warn">locked</span>}
      {p.twoFactorEnabled && <span className="badge">2FA</span>}
    </>
  )
}

function AddPerson() {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const create = useMutation({
    mutationFn: (body: object) => api<Created>('/api/admin/people', { body }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'people'] }),
  })
  if (create.data) {
    return (
      <section className="card">
        <h2>Person added</h2>
        <OnceNotice>
          <Secret label="Password" value={create.data.password} />
          {create.data.apiKey && <Secret label="API key" value={create.data.apiKey} />}
          {create.data.warning && <p className="warn-text">{create.data.warning}</p>}
        </OnceNotice>
        <button className="button secondary" onClick={() => create.reset()}>
          Done
        </button>
      </section>
    )
  }
  if (!open) {
    return (
      <div>
        <button className="button" onClick={() => setOpen(true)}>
          Add a person
        </button>
      </div>
    )
  }
  return (
    <section className="card">
      <h2>Add a person</h2>
      <form
        className="grid-form"
        onSubmit={(e) => {
          e.preventDefault()
          const f = new FormData(e.currentTarget)
          const budget = String(f.get('budget') ?? '').trim()
          create.mutate({
            userName: f.get('userName'),
            email: f.get('email'),
            displayName: f.get('displayName'),
            admin: f.get('admin') === 'on',
            budget: budget === '' ? null : Number(budget),
          })
        }}
      >
        <label>
          Username <span className="muted">(their GitLab username, for Argus)</span>
          <input name="userName" required pattern="[a-z0-9][a-z0-9._\-]{1,63}" autoComplete="off" />
        </label>
        <label>
          Email
          <input name="email" type="email" required autoComplete="off" />
        </label>
        <label>
          Name
          <input name="displayName" autoComplete="off" />
        </label>
        <label>
          Credit in $ <span className="muted">(empty = unlimited)</span>
          <input name="budget" type="number" min="0" step="0.01" />
        </label>
        <label className="check">
          <input name="admin" type="checkbox" /> Admin
        </label>
        <ErrorText error={create.error} />
        <div className="row">
          <button className="button" disabled={create.isPending}>
            Add
          </button>
          <button type="button" className="button secondary" onClick={() => setOpen(false)}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  )
}
