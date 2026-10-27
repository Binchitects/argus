/**
 * A drawing a tool made (the science lab's molecule as RDKit draws it, a chart of counts or energies): an SVG shown as a
 * picture, so nothing in it runs, on white, as its dark lines need in either theme.
 */
export function Drawing({ svg, label }: { svg: string; label: string }) {
  return <img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`} alt={label} className="mx-auto my-2 h-auto max-h-80 w-full max-w-lg rounded-md bg-white p-1" />
}

/** What the science lab shows beside a result: a circuit's diagram (text, as Qiskit draws it) and its figures. */
export interface LabShow {
  figures?: { svg: string; label: string }[]
  diagram?: string
}

export function LabFigures({ lab }: { lab: LabShow | null | undefined }) {
  if (!lab) return null
  return (
    <>
      {lab.diagram && (
        <pre aria-label="The circuit, drawn" className="my-2 overflow-x-auto rounded-md border bg-muted/40 p-2 font-mono text-xs leading-tight">
          {lab.diagram}
        </pre>
      )}
      {lab.figures?.map((f, i) => (
        <figure key={i} className="my-2">
          <Drawing svg={f.svg} label={f.label} />
          <figcaption className="text-center text-xs text-muted-foreground">{f.label}</figcaption>
        </figure>
      ))}
    </>
  )
}
