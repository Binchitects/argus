/** What a column sorts and filters by, for one row. */
export type Key<T> = (row: T) => string | number | boolean | null | undefined;

export type Sort = { by: string; desc: boolean };

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

/** Two values in order: numbers as numbers, text as people read it (2 before 10). Empty ones are left to the caller. */
export function compare(a: string | number | boolean, b: string | number | boolean): number {
  return typeof a === "number" && typeof b === "number" ? a - b : collator.compare(String(a), String(b));
}

/** The rows in view: those holding what is typed (in any column), in the order of the column clicked; empty values last. */
export function tableRows<T>(rows: T[], columns: Record<string, Key<T>>, filter: string, sort: Sort | null): T[] {
  const q = filter.trim().toLowerCase();
  const keys = Object.values(columns);
  const kept = q ? rows.filter((r) => keys.some((k) => String(k(r) ?? "").toLowerCase().includes(q))) : [...rows];
  const key = sort ? columns[sort.by] : undefined;
  if (sort && key) {
    kept.sort((x, y) => {
      const [a, b] = [key(x), key(y)];
      const [none, nothing] = [a === null || a === undefined || a === "", b === null || b === undefined || b === ""];
      if (none || nothing) return none === nothing ? 0 : none ? 1 : -1;
      return compare(a!, b!) * (sort.desc ? -1 : 1);
    });
  }
  return kept;
}

/** A table cell's text as a number when all of it reads as one ("1,204", "35%", "$4.20", "−3"), else the text itself. */
export function cellValue(text: string): string | number {
  const t = text.trim().replace(/^[$€£¥]/, "").replace(/%$/, "").replaceAll(",", "");
  return /^[-+−]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?$/i.test(t) ? Number(t.replace("−", "-")) : text.trim();
}
