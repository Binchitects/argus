import { api } from '@/lib/api'

/** The known tags (Tags:Known) and the person's own in use. */
export const tagsQuery = {
  queryKey: ['tags'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ known: string[]; used: { tag: string; count: number }[] }>('/api/tags', { signal }),
  staleTime: 60_000,
}

/** A tag as the server keeps it: lower case, a dash for a space, letters, digits, '-' and '_'. */
export function cleanTag(tag: string): string {
  return tag
    .trim()
    .replace(/^#+/, '')
    .toLowerCase()
    .replace(/\s+/g, '-')
    .replace(/[^\p{L}\p{N}_-]/gu, '')
    .replace(/^-+|-+$/g, '')
    .slice(0, 32)
}
