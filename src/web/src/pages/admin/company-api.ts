import { api } from '@/lib/api'

/** Company sign-in (OIDC or SAML) and SCIM as the app sees them. */
export interface CompanyStatus {
  enabled: boolean
  protocol: 'oidc' | 'saml'
  /** The OIDC issuer, or the SAML provider's entity ID. */
  issuer: string | null
  label: string
  adminGroup: string | null
  requiredGroup: string | null
  /** What to register at an OIDC identity provider. */
  redirectUri: string
  /** What to give a SAML identity provider: this app's entity ID and ACS, or the address of its metadata. */
  saml: { entityId: string; acsUrl: string; metadataUrl: string }
  /** People who sign in with the company account (or SCIM made). */
  people: number
  scim: { url: string; tokenMadeAt: string | null; groups: number }
}

export const companyStatusQuery = {
  queryKey: ['admin', 'company-sign-in'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CompanyStatus>('/api/admin/company-sign-in', { signal }),
}
