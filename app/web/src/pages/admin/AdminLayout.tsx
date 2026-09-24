import { NavLink, Outlet, useOutletContext } from 'react-router'
import type { Me } from '../../api'

const tabs: [string, string][] = [
  ['/admin', 'Overview'],
  ['/admin/people', 'People'],
  ['/admin/model', 'Model'],
  ['/admin/indexing', 'Indexing'],
  ['/admin/packs', 'Packs'],
  ['/admin/explore', 'Explore'],
  ['/admin/monitoring', 'Monitoring'],
  ['/admin/settings', 'Settings'],
  ['/admin/audit', 'Audit log'],
  ['/admin/sign-in', 'Sign-in'],
]

export function AdminLayout() {
  const me = useOutletContext<Me>()
  if (!me.isAdmin) {
    return (
      <>
        <h1>Admin</h1>
        <p>Only admins can open this page.</p>
      </>
    )
  }
  return (
    <>
      <h1>Admin</h1>
      <nav className="tabs" aria-label="Admin">
        {tabs.map(([to, label]) => (
          <NavLink key={to} to={to} end={to === '/admin'}>
            {label}
          </NavLink>
        ))}
      </nav>
      <Outlet context={me} />
    </>
  )
}
