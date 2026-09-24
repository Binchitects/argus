import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { api, infoQuery, meQuery } from '../api'
import { areas } from '../areas'

export function Shell() {
  const info = useQuery(infoQuery)
  const me = useQuery(meQuery)
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  useEffect(() => {
    if (me.data === null) {
      navigate(`/login?rd=${encodeURIComponent(location.pathname + location.search)}`, { replace: true })
    }
  }, [me.data, location.pathname, location.search, navigate])

  if (me.isPending || me.data === null) {
    return <div className="loading" aria-busy="true" />
  }

  const person = me.data
  const visible = areas.filter((a) => !a.adminOnly || person?.isAdmin)

  async function signOut() {
    await api('/api/auth/logout', { method: 'POST', body: {} })
    queryClient.setQueryData(meQuery.queryKey, null)
  }

  return (
    <div className="shell">
      <aside className="sidebar">
        <NavLink to="/" end className="brand">
          {info.data?.name ?? 'LLM Service'}
        </NavLink>
        <nav aria-label="Main">
          <ul>
            {visible.map((a) => (
              <li key={a.path}>
                <NavLink to={a.path}>{a.title}</NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <div className="me">
          {person && (
            <>
              <NavLink to="/account" className="me-name">
                {person.displayName}
              </NavLink>
              <button type="button" className="link" onClick={signOut}>
                Sign out
              </button>
            </>
          )}
          <p className="version" aria-label="Version">
            {info.data ? `v${info.data.version}` : info.isError ? 'offline' : ''}
          </p>
        </div>
      </aside>
      <main className="content">
        <Outlet context={person} />
      </main>
    </div>
  )
}
