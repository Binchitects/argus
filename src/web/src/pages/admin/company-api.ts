import { api } from '@/lib/api'

/** Company sign-in (OIDC) and SCIM as the app sees them. */
export interface CompanyStatus {
  enabled: boolean
  issuer: string | null
  label: string
  adminGroup: string | null
  requiredGroup: string | null
  /** What to register at the identity provider. */
  redirectUri: string
  /** People who sign in with the company account (or SCIM made). */
  people: number
  scim: { url: string; tokenMadeAt: string | null; groups: number }
}

export const companyStatusQuery = {
  queryKey: ['admin', 'company-sign-in'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CompanyStatus>('/api/admin/company-sign-in', { signal }),
}
