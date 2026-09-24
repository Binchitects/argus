export interface Person {
  id: string
  userName: string
  displayName: string
  email: string
  isAdmin: boolean
  source: 'local' | 'ldap'
  disabled: boolean
  disabledReason: string | null
  twoFactorEnabled: boolean
  lockedOut: boolean
  lastSignInAt: string | null
  createdAt: string
  spend: number | null
  budget: number | null
}

export interface Created {
  id: string
  password: string
  apiKey: string | null
  warning: string | null
}
