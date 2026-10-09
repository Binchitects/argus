import { useMemo, useState, type ReactNode } from "react";
import { tableRows, type Key, type Sort } from "./table-rows";

export type { Key } from "./table-rows";

/**
 * Every table here sorts and filters: `header` makes a column's heading a sort button (up, then down), and
 * `search` is the box that keeps only the rows holding what is typed.
 */
export function useTable<T>(rows: T[], columns: Record<string, Key<T>>, initial: Sort | null = null) {
  const [sort, setSort] = useState<Sort | null>(initial);
  const [filter, setFilter] = useState("");
  const shown = useMemo(() => tableRows(rows, columns, filter, sort), [rows, columns, filter, sort]);
  const header = (by: string, label: ReactNode, className?: string) => (
    <th className={className} aria-sort={sort?.by === by ? (sort.desc ? "descending" : "ascending") : undefined}>
      <button type="button" className="sort" onClick={() => setSort(sort?.by === by ? { by, desc: !sort.desc } : { by, desc: false })}>
        {label} {sort?.by === by ? (sort.desc ? "↓" : "↑") : ""}
      </button>
    </th>
  );
  const search = (label: string) => (
    <input className="search" type="search" placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label={label} />
  );
  return { rows: shown, header, search, filter, total: rows.length };
}
