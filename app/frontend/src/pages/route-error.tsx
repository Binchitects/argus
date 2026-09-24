import { AlertTriangle } from 'lucide-react'
import { isRouteErrorResponse, useRouteError } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { NotFoundPage } from './not-found'

/** Anything a page throws while rendering ends here instead of a blank screen. */
export function RouteErrorPage() {
  const error = useRouteError()
  if (isRouteErrorResponse(error) && error.status === 404) return <NotFoundPage />
  // A new deployment replaced the lazily loaded file this tab still points to.
  const stale = error instanceof Error && /dynamically imported module|Importing a module script failed/i.test(error.message)
  return (
    <div className="mx-auto max-w-lg py-16">
      <h1 className="sr-only">Something went wrong</h1>
      <EmptyState
        icon={AlertTriangle}
        title={stale ? 'A newer version is available' : 'Something went wrong'}
        action={<Button onClick={() => window.location.reload()}>Reload the page</Button>}
      >
        {stale ? 'Reload to get it.' : 'This page hit an error. Reloading usually fixes it; if not, tell an admin what you were doing.'}
      </EmptyState>
    </div>
  )
}
