import { RotateCw } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/api'

/** Why a load failed, with a retry. */
export function QueryError({ error, retry }: { error: unknown; retry?: () => void }) {
  return (
    <Alert
      variant="destructive"
      action={
        retry && (
          <Button size="sm" variant="outline" onClick={retry}>
            <RotateCw /> Try again
          </Button>
        )
      }
    >
      {errorMessage(error)}
    </Alert>
  )
}

export function PageSkeleton() {
  return (
    <div className="grid gap-4" aria-busy="true" aria-label="Loading">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-24" />
        ))}
      </div>
      <Skeleton className="h-64" />
    </div>
  )
}
