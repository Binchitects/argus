import { Children, isValidElement, useMemo, useState, type ReactElement, type ReactNode } from "react";
import { cellValue, tableRows, type Key, type Sort } from "./table-rows";

type Element = ReactElement<{ children?: ReactNode; style?: React.CSSProperties }>;

/** The words a cell shows, however it is formatted (bold, code, links). */
function textOf(node: ReactNode): string {
  if (node === null || node === undefined || typeof node === "boolean") return "";
  if (typeof node === "string" || typeof node === "number") return String(node);
  if (Array.isArray(node)) return node.map(textOf).join("");
  return isValidElement(node) ? textOf((node as Element).props.children) : "";
}

const elements = (node: ReactNode) => Children.toArray(node).filter(isValidElement) as Element[];

/** Past this many rows a table in an answer gets a filter box. */
const FILTER_FROM = 8;

/**
 * A table in an answer (as react-markdown makes it): a heading click sorts by that column (up, down, back to
 * the answer's order), and a longer one filters to the rows holding what is typed.
 */
export default function AnswerTable({ children }: { children?: ReactNode }) {
  const [head, body] = elements(children);
  const headCells = head ? elements(elements(head.props.children)[0]?.props.children) : [];
  const rows = useMemo(
    () => (body ? elements(body.props.children) : []).map((r, i) => {
      const cells = elements(r.props.children);
      return { i, cells, values: cells.map((c) => cellValue(textOf(c.props.children))) };
    }),
    [body],
  );
  const columns = useMemo(
    () => Object.fromEntries(headCells.map((_, j): [string, Key<(typeof rows)[number]>] => [String(j), (r) => r.values[j]])),
    [headCells.length], // eslint-disable-line react-hooks/exhaustive-deps
  );
  const [sort, setSort] = useState<Sort | null>(null);
  const [filter, setFilter] = useState("");
  const shown = useMemo(() => (sort || filter ? tableRows(rows, columns, filter, sort) : rows), [rows, columns, sort, filter]);
  const next = (by: string) => setSort((s) => (s?.by !== by ? { by, desc: false } : s.desc ? null : { by, desc: true }));
  return (
    <div className="answer-table">
      {rows.length > FILTER_FROM && (
        <input className="search" type="search" placeholder="Filter rows" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter the table's rows" />
      )}
      <table>
        <thead>
          <tr>
            {headCells.map((c, j) => {
              const sorted = sort?.by === String(j) ? (sort.desc ? "descending" : "ascending") : undefined;
              return (
                <th key={j} style={c.props.style} aria-sort={sorted}>
                  <button type="button" className="sort" onClick={() => next(String(j))}>
                    {c.props.children} {sorted === "ascending" ? "↑" : sorted === "descending" ? "↓" : ""}
                  </button>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {shown.map((r) => <tr key={r.i}>{r.cells}</tr>)}
          {shown.length === 0 && <tr><td colSpan={headCells.length} className="dim">No rows hold “{filter}”.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}
