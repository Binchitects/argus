import { Construction, ExternalLink } from 'lucide-react'
import { useLocation } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { PageHeader } from '@/components/app/page-header'
import { findNavItem, serviceUrl } from '@/app/nav'

/** A page this web does not have yet: says when it comes, and where the job is done meanwhile. */
export function NotYetPage() {
  const { pathname } = useLocation()
  const item = findNavItem(pathname)
  const target = item?.elsewhere ? { name: item.elsewhere.name, href: serviceUrl(item.elsewhere.subdomain) } : null
  return (
    <>
      <PageHeader title={item?.title ?? 'Coming soon'} />
      <EmptyState
        icon={Construction}
        title="Being rebuilt"
        action={
          target && (
            <Button asChild variant="outline">
              <a href={target.href}>
                Open in {target.name} <ExternalLink />
              </a>
            </Button>
          )
        }
      >
        {item?.phase ? `This page arrives in phase ${item.phase} of the plan.` : 'This page arrives in a later phase.'}
        {target && ` Until then it is in ${target.name}.`}
      </EmptyState>
    </>
  )
}
