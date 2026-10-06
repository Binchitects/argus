/** A heading's anchor, as GitHub makes it: lower case, punctuation dropped, spaces as hyphens. */
export function slugify(text: string): string {
  return text.toLowerCase().replace(/[^\p{L}\p{N}\p{M}\s_-]/gu, '').replace(/\s/g, '-')
}

/** Anchors for a page's headings in order: a repeated one gets -1, -2 (GitHub's rule). */
export class Slugger {
  private seen = new Map<string, number>()
  slug(text: string): string {
    const base = slugify(text.trim())
    let slug = base
    let n = this.seen.get(base) ?? 0
    while (this.seen.has(slug)) slug = `${base}-${++n}`
    this.seen.set(base, n)
    this.seen.set(slug, 0)
    return slug
  }
}
