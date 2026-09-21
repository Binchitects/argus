import { useQuery } from '@tanstack/react-query'
import { NavLink, Outlet } from 'react-router'
import { infoQuery } from '../api'
import { areas } from '../areas'

export function Shell() {
  const info = useQuery(infoQuery)
  return (
    <div className="shell">
      <aside className="sidebar">
        <NavLink to="/" end className="brand">
          {info.data?.name ?? 'LLM Service'}
        </NavLink>
        <nav aria-label="Main">
          <ul>
            {areas.map((a) => (
              <li key={a.path}>
                <NavLink to={a.path}>{a.title}</NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <p className="version" aria-label="Version">
          {info.data ? `v${info.data.version}` : info.isError ? 'offline' : ''}
        </p>
      </aside>
      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}
