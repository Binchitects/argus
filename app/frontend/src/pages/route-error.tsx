import { AlertTriangle } from 'lucide-react'
import { isRouteErrorResponse, useRouteError } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { NotFoundPage } from './not-found'

/** Anything a page throws while rendering ends here instead of a blank screen. */
export function RouteErrorPage() {
  const error = useRouteError()
  if (isRouteErrorResponse(error) && error.status === 404) return <NotFoundPage />
  // The page's code or styles did not arrive: the connection dropped, or a new
  // deployment replaced the files this tab still points to. Both mean: reload.
  const notLoaded = error instanceof Error && /dynamically imported module|Importing a module script failed|Unable to preload CSS/i.test(error.message)
  return (
    <div className="mx-auto max-w-lg py-16">
      <h1 className="sr-only">{notLoaded ? 'This page did not load' : 'Something went wrong'}</h1>
      <EmptyState
        icon={AlertTriangle}
        title={notLoaded ? 'This page did not load' : 'Something went wrong'}
        action={<Button onClick={() => window.location.reload()}>Reload the page</Button>}
      >
        {notLoaded
          ? 'The connection dropped, or a newer version was deployed. Reloading fetches it again.'
          : 'This page hit an error. Reloading usually fixes it; if not, tell an admin what you were doing.'}
      </EmptyState>
    </div>
  )
}
