/**
 * The command palette's matching: every word typed must start a word of the
 * item's title or keywords. Prefix-of-word, not cmdk's default subsequence
 * match, so "audit" finds "Audit log" and not "Usage & cost". A match in the
 * title outranks one in the keywords, and a whole word outranks a prefix.
 * Returns a score (higher first) or 0 to hide the item.
 */
export function commandFilter(value: string, search: string, keywords: string[] = []): number {
  const query = search.toLowerCase().trim().split(/\s+/).filter(Boolean)
  if (query.length === 0) return 1
  const split = (s: string) => s.toLowerCase().split(/[\s&/,.-]+/).filter(Boolean)
  const title = split(value)
  const extra = keywords.flatMap(split)
  let score = 0
  for (const q of query) {
    const inTitle = title.find((w) => w.startsWith(q))
    const inExtra = inTitle ? undefined : extra.find((w) => w.startsWith(q))
    if (!inTitle && !inExtra) return 0
    score += inTitle ? (inTitle === q ? 4 : 3) : inExtra === q ? 2 : 1
  }
  return score
}
