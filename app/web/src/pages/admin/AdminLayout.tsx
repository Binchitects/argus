import { NavLink, Outlet, useOutletContext } from 'react-router'
import type { Me } from '../../api'
import { legacyUrl, areas } from '../../areas'

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
  const panel = legacyUrl(areas.find((a) => a.path === '/admin')!)
  return (
    <>
      <h1>Admin</h1>
      <nav className="tabs" aria-label="Admin">
        <NavLink to="/admin" end>
          People
        </NavLink>
        <NavLink to="/admin/audit">Audit log</NavLink>
        <NavLink to="/admin/sign-in">Sign-in</NavLink>
        <a href={panel}>Model, indexing, packs ↗</a>
      </nav>
      <Outlet context={me} />
    </>
  )
}
