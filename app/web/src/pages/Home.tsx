import { Link, useOutletContext } from 'react-router'
import type { Me } from '../api'
import { areas } from '../areas'

export function Home() {
  const me = useOutletContext<Me>()
  return (
    <>
      <h1>Overview</h1>
      <p className="lede">Everything the service does, in one place. Areas move here one phase at a time.</p>
      <ul className="cards">
        {areas.filter((a) => !a.adminOnly || me.isAdmin).map((a) => (
          <li key={a.path} className="card">
            <h2>
              <Link to={a.path}>{a.title}</Link>
            </h2>
            <p>{a.summary}</p>
            <p className="muted">{a.native ? 'Here now' : `Arrives in phase ${a.phase}`}</p>
          </li>
        ))}
      </ul>
    </>
  )
}
