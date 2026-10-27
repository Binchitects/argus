/**
 * A drawing a tool made (the science lab's molecule, as RDKit draws it): an SVG shown as a picture, so nothing in it runs,
 * on white, as its black lines need in either theme.
 */
export function Drawing({ svg, label }: { svg: string; label: string }) {
  return <img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`} alt={label} className="mx-auto my-2 h-auto max-h-64 w-full max-w-sm rounded-md bg-white p-1" />
}
