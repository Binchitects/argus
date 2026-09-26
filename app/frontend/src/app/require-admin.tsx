import { ShieldAlert } from 'lucide-react'
import { Outlet, useOutletContext } from 'react-router'
import { EmptyState } from '@/components/ui/empty-state'
import type { Me } from '@/lib/api'

/** Admin pages: others get a plain refusal (the API refuses them too). */
export function RequireAdmin() {
  const me = useOutletContext<Me>()
  if (!me.isAdmin) {
    return (
      <div className="mx-auto max-w-lg py-16">
        <h1 className="sr-only">Admins only</h1>
        <EmptyState icon={ShieldAlert} title="Admins only">
          This page is for the people who run the service. Ask one of them if you need something here.
        </EmptyState>
      </div>
    )
  }
  return <Outlet context={me} />
}
