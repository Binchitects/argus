import { type Area, legacyUrl } from '../areas'

/** Stand-in for an area that is not native yet: says when it will be, and where it is today. */
export function AreaPage({ area }: { area: Area }) {
  return (
    <>
      <h1>{area.title}</h1>
      <p className="lede">{area.summary}</p>
      <div className="notice" role="status">
        <p>
          This area moves here in phase {area.phase}. Until then it is served by {area.legacy.name}.
        </p>
        <a className="button" href={legacyUrl(area)}>
          Open {area.legacy.name}
        </a>
      </div>
    </>
  )
}
