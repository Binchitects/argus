import { ArrowDown, ArrowUp, ChevronsUpDown, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
import type { Align, TableCell } from './markdown-blocks'
import { sortRows } from './table-sort'

/** A cell's sanitised HTML. */
function Html({ html }: { html: string }) {
  return <span dangerouslySetInnerHTML={{ __html: html }} />
}

/** Past this many rows a table in an answer gets a filter box. */
const FilterFrom = 8

/**
 * A table in an answer: a header click sorts by that column (up, down, back to the answer's order),
 * and a longer one filters to the rows holding what is typed.
 */
export function AnswerTable({ head, align, rows }: { head: TableCell[]; align: Align[]; rows: TableCell[][] }) {
  const [sort, setSort] = useState<{ column: number; desc: boolean } | null>(null)
  const [filter, setFilter] = useState('')
  const shown = useMemo(() => {
    const words = filter.trim().toLocaleLowerCase()
    const kept = words ? rows.filter((r) => r.some((c) => c.text.toLocaleLowerCase().includes(words))) : rows
    return sort ? sortRows(kept, sort.column, sort.desc) : kept
  }, [rows, sort, filter])
  const next = (column: number) =>
    setSort((s) => (s?.column !== column ? { column, desc: false } : s.desc ? null : { column, desc: true }))
  return (
    <div className="answer-table">
      {rows.length > FilterFrom && (
        <label className="relative mb-1.5 flex w-full max-w-56 items-center">
          <Search className="pointer-events-none absolute left-2 size-3.5 text-muted-foreground" aria-hidden="true" />
          <input
            type="search"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter rows"
            aria-label="Filter the table's rows"
            className="h-7 w-full rounded-md border bg-background pr-2 pl-7 text-xs outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </label>
      )}
      <table dir="auto">
        <thead>
          <tr>
            {head.map((h, i) => {
              const sorted = sort?.column === i ? (sort.desc ? 'descending' : 'ascending') : undefined
              return (
                <th key={i} scope="col" style={{ textAlign: align[i] ?? undefined }} aria-sort={sorted}>
                  <button
                    type="button"
                    onClick={() => next(i)}
                    className="inline-flex items-center gap-1 text-start font-semibold"
                    aria-label={`${h.text}, sort ${sorted === 'ascending' ? 'descending' : sorted === 'descending' ? 'as written' : 'ascending'}`}
                  >
                    <Html html={h.html} />
                    {sorted === 'ascending' ? (
                      <ArrowUp className="size-3 shrink-0" aria-hidden="true" />
                    ) : sorted === 'descending' ? (
                      <ArrowDown className="size-3 shrink-0" aria-hidden="true" />
                    ) : (
                      <ChevronsUpDown className="size-3 shrink-0 opacity-40" aria-hidden="true" />
                    )}
                  </button>
                </th>
              )
            })}
          </tr>
        </thead>
        <tbody>
          {shown.map((r) => (
            <tr key={rows.indexOf(r)}>
              {head.map((_, i) => (
                // Sanitised when the answer was read; the answer is data, never markup we trust.
                <td key={i} style={{ textAlign: align[i] ?? undefined }}>
                  <Html html={r[i]?.html ?? ''} />
                </td>
              ))}
            </tr>
          ))}
          {shown.length === 0 && (
            <tr>
              <td colSpan={head.length} className="text-center text-muted-foreground">
                No rows hold “{filter}”.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
