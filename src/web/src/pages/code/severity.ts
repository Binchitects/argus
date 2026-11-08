/** A finding's severity, worst first, and the badge each wears. */
export const severities = ['critical', 'high', 'medium', 'low'] as const

export const severityVariant = { critical: 'destructive', high: 'destructive', medium: 'warning', low: 'secondary' } as const

/** The repositories asked for: one a line (or comma), each a path; the same one twice once. */
export const repositoriesOf = (text: string) => [...new Set(text.split(/[\n,]+/).map((x) => x.trim().replace(/^\/+|\/+$/g, '')).filter(Boolean))]
