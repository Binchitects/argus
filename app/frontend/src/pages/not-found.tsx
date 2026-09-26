import { FileQuestion } from 'lucide-react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'

export function NotFoundPage() {
  return (
    <div className="mx-auto max-w-lg py-16">
      <h1 className="sr-only">Page not found</h1>
      <EmptyState
        icon={FileQuestion}
        title="Page not found"
        action={
          <Button asChild>
            <Link to="/">Go home</Link>
          </Button>
        }
      >
        The address may be mistyped, or the page has moved.
      </EmptyState>
    </div>
  )
}
