// What the code index covers, as the Indexing page's heading says it: repositories, the branches beside them, and
// symbols. From the index's own summary, never from the rows on screen (a repository indexed on three branches is
// three rows). Plain functions: test/index-counts.test.mjs.

/** The index's summary as /admin/index/status gives it; a count it could not read is left out. */
export interface IndexCounts {
  repos?: number;
  branches?: number;
  symbols?: number;
}

const many = (n: number | undefined, one: string, more: string) => (n === undefined ? `? ${more}` : `${n.toLocaleString("en-US")} ${n === 1 ? one : more}`);

/** "12 repositories, 30 branches, 4,812 symbols"; a count the summary lacks is "?". */
export function indexCounts(index: IndexCounts): string {
  return [many(index.repos, "repository", "repositories"), many(index.branches, "branch", "branches"), many(index.symbols, "symbol", "symbols")].join(", ");
}
