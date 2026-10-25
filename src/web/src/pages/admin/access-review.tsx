import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Check, CircleSlash } from 'lucide-react'
import { useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { reviewQuery, reviewsQuery, reviewState, type ReviewItem, type ReviewKind } from './compliance-api'

const KINDS: { kind: ReviewKind; title: string; about: string }[] = [
  { kind: 'role', title: 'Privileged roles', about: 'Each administrative role given to a person, a group or everyone. Revoking takes the role away at once.' },
  { kind: 'member', title: 'Members of privileged groups', about: 'Each member of a group that holds one. Revoking removes them from the group; the directory’s and SCIM’s groups are changed there.' },
  { kind: 'unused', title: 'Unused accounts', about: 'Nobody signed in to these for long. Revoking disables the account; an admin can enable it again.' },
]

function Decision({ item }: { item: ReviewItem }) {
  if (!item.decision) return <Badge variant="secondary">To decide</Badge>
  const revoked = item.decision === 'revoke'
  return (
    <span className="grid gap-0.5 text-xs">
      <Badge variant={revoked ? 'destructive' : 'success'}>{revoked ? 'Revoked' : 'Kept'}</Badge>
      <span className="text-muted-foreground">
        by {item.decidedBy}{' '}
        {item.decidedAt && (
          <time dateTime={item.decidedAt} title={when(item.decidedAt)}>
            {ago(item.decidedAt)}
          </time>
        )}
      </span>
      {item.outcome && <span className={item.outcome.startsWith('Not done') ? 'text-warning-ink' : 'text-muted-foreground'}>{item.outcome}</span>}
    </span>
  )
}

/** An access review: each privileged role, member and unused account, kept or revoked, and the review closed when all are decided. */
export function AccessReviewPage() {
  const { id = '' } = useParams()
  const me = useOutletContext<Me>()
  const mayDecide = !!me.isAdmin || !!me.permissions?.includes('compliance.manage')
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const detail = useQuery(reviewQuery(id))
  const [show, setShow] = useState<'all' | 'open' | 'keep' | 'revoke'>('all')
  const [busy, setBusy] = useState(false)
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: reviewQuery(id).queryKey }), queryClient.invalidateQueries({ queryKey: reviewsQuery.queryKey })])
  const decide = useMutation({
    mutationFn: ({ item, decision }: { item: ReviewItem; decision: 'keep' | 'revoke' }) =>
      api<ReviewItem>(`/api/admin/compliance/reviews/${id}/items/${item.id}`, { method: 'POST', body: { decision } }),
    onSuccess: (item) => {
      if (item.outcome?.startsWith('Not done')) toast.warning(item.outcome)
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const close = useMutation({
    mutationFn: () => api(`/api/admin/compliance/reviews/${id}/close`, { method: 'POST', body: {} }),
    onSuccess: () => {
      toast.success('The review is closed and kept')
      void refresh()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  if (detail.error) return <QueryError error={detail.error} retry={() => detail.refetch()} />
  if (!detail.data) return <Skeleton className="h-64" />
  const { review, items } = detail.data
  const state = reviewState(review, new Date())
  const open = state !== 'closed'
  const undecided = items.filter((i) => !i.decision)
  const shown = items.filter((i) => (show === 'all' ? true : show === 'open' ? !i.decision : i.decision === show))

  const keepRest = async () => {
    if (!(await confirm({ title: `Keep the ${undecided.length} not decided yet?`, description: 'Each is kept as it is, under your name.', confirm: 'Keep them' }))) return
    setBusy(true)
    try {
      for (const item of undecided) await api(`/api/admin/compliance/reviews/${id}/items/${item.id}`, { method: 'POST', body: { decision: 'keep' } })
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
      void refresh()
    }
  }

  return (
    <>
      <Link to="/admin/compliance" className="mb-3 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" aria-hidden="true" /> Compliance
      </Link>
      <PageHeader
        title={review.title}
        description={
          <>
            Started by {review.createdBy} {ago(review.createdAt)}; due {new Date(review.dueAt).toLocaleDateString()}.{' '}
            {review.closedAt ? `Closed by ${review.closedBy} ${ago(review.closedAt)}.` : `${items.length - undecided.length} of ${items.length} decided.`}
          </>
        }
        actions={
          mayDecide &&
          open && (
            <>
              {undecided.length > 0 && (
                <Button variant="outline" onClick={keepRest} loading={busy}>
                  <Check /> Keep the rest
                </Button>
              )}
              <Button onClick={() => close.mutate()} loading={close.isPending} disabled={undecided.length > 0}>
                Close the review
              </Button>
            </>
          )
        }
      />
      {state === 'late' && (
        <Alert variant="warning" className="mb-4">
          It was due {new Date(review.dueAt).toLocaleDateString()}. Keep or revoke what is left, then close it.
        </Alert>
      )}
      <div className="mb-4">
        <Segmented
          label="Show"
          value={show}
          onChange={setShow}
          options={[
            { value: 'all', label: `All (${items.length})` },
            { value: 'open', label: `To decide (${undecided.length})` },
            { value: 'keep', label: 'Kept' },
            { value: 'revoke', label: 'Revoked' },
          ]}
        />
      </div>
      <div className="grid gap-8">
        {KINDS.map(({ kind, title, about }) => {
          const rows = shown.filter((i) => i.kind === kind)
          if (rows.length === 0) return null
          return (
            <section key={kind} aria-labelledby={`kind-${kind}`} className="grid gap-2">
              <h2 id={`kind-${kind}`} className="text-lg font-semibold">
                {title}
              </h2>
              <p className="text-sm text-muted-foreground">{about}</p>
              <ul className="divide-y rounded-lg border bg-card">
                {rows.map((item) => (
                  <li key={item.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 p-3 text-sm">
                    <div className="min-w-48 flex-1">
                      <div className="font-medium">{item.subject}</div>
                      <div className="text-muted-foreground">{item.what}</div>
                    </div>
                    <Decision item={item} />
                    {mayDecide && open && !(item.decision === 'revoke' && item.outcome && !item.outcome.startsWith('Not done')) && (
                      <div className="flex gap-2">
                        <Button size="sm" variant={item.decision === 'keep' ? 'secondary' : 'outline'} onClick={() => decide.mutate({ item, decision: 'keep' })} disabled={decide.isPending}>
                          <Check /> Keep
                        </Button>
                        <Button
                          size="sm"
                          variant="outline"
                          className="text-destructive-ink"
                          disabled={decide.isPending}
                          onClick={async () => {
                            if (await confirm({ title: `Revoke: ${item.subject}`, description: item.what, confirm: 'Revoke', destructive: true })) decide.mutate({ item, decision: 'revoke' })
                          }}
                        >
                          <CircleSlash /> Revoke
                        </Button>
                      </div>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )
        })}
        {items.length === 0 && <p className="text-muted-foreground">Nobody holds privileged access but the first admin, and no account went unused.</p>}
      </div>
    </>
  )
}
