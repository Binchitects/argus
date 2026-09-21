import { Link } from 'react-router'
import { areas } from '../areas'

export function Home() {
  return (
    <>
      <h1>Overview</h1>
      <p className="lede">Everything the service does, in one place. Areas move here one phase at a time.</p>
      <ul className="cards">
        {areas.map((a) => (
          <li key={a.path} className="card">
            <h2>
              <Link to={a.path}>{a.title}</Link>
            </h2>
            <p>{a.summary}</p>
            <p className="muted">Arrives in phase {a.phase}</p>
          </li>
        ))}
      </ul>
    </>
  )
}
