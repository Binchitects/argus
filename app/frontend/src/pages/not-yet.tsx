import { Construction, ExternalLink } from 'lucide-react'
import { useLocation } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { PageHeader } from '@/components/app/page-header'
import { currentAppUrl, findNavItem } from '@/app/nav'

/** A page this web does not have yet: says when it comes, and opens it in the current app meanwhile. */
export function NotYetPage() {
  const { pathname, search } = useLocation()
  const item = findNavItem(pathname)
  return (
    <>
      <PageHeader title={item?.title ?? 'Coming soon'} />
      <EmptyState
        icon={Construction}
        title="Being rebuilt"
        action={
          <Button asChild variant="outline">
            <a href={currentAppUrl(pathname + search)}>
              Open in the current app <ExternalLink />
            </a>
          </Button>
        }
      >
        {item?.phase ? `This page arrives in phase ${item.phase} of the plan.` : 'This page arrives in a later phase.'} Until then it works in the current app.
      </EmptyState>
    </>
  )
}
