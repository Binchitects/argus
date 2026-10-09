import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import type { Person } from './people-api'

/** One local account and the directory entry it would sign in with, or why it cannot move. */
export interface DirectoryMove {
  id: string
  userName: string
  email: string
  displayName: string
  admin: boolean
  dn: string | null
  newUserName: string | null
  newDisplayName: string | null
  newAdmin: boolean | null
  refusal: string | null
}

/** What moving each person to directory sign-in changes; who can move, moved on confirming. */
export function DirectoryMoveDialog({ people, onClose }: { people: Person[] | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const ids = (people ?? []).map((p) => p.id)
  const plan = useQuery({
    queryKey: ['admin', 'ldap-moves', ids],
    queryFn: ({ signal }) => api<{ people: DirectoryMove[] }>('/api/admin/ldap/moves/plan', { body: { ids }, signal }),
    enabled: people !== null,
    gcTime: 0,
  })
  const rows = plan.data?.people ?? []
  const ready = rows.filter((r) => r.refusal === null)
  const move = useMutation({
    mutationFn: () => api<{ moved: DirectoryMove[]; refused: DirectoryMove[] }>('/api/admin/ldap/moves', { body: { ids: ready.map((r) => r.id) } }),
    onSuccess: async ({ moved, refused }) => {
      if (refused.length === 0) toast.success(`${moved.length} moved to directory sign-in.`)
      else toast.error(`${moved.length} moved; ${refused[0]!.userName} did not: ${refused[0]!.refusal}`)
      await queryClient.invalidateQueries({ queryKey: ['admin'] })
      onClose()
    },
  })
  return (
    <Dialog open={people !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Move to directory sign-in</DialogTitle>
          <DialogDescription>
            Only the password check moves: they sign in with their directory password, and their password here stops working. Their account stays, with its
            email, chats, files, groups, API keys and spend; so do their sessions and two-step sign-in. From then on the directory gives their name and admin role.
          </DialogDescription>
        </DialogHeader>
        {plan.error ? (
          <Alert variant="destructive">{errorMessage(plan.error)}</Alert>
        ) : plan.isPending ? (
          <Skeleton className="h-24" />
        ) : (
          <ul aria-label="People to move" className="max-h-80 divide-y overflow-y-auto rounded-md border text-sm">
            {rows.map((r) => (
              <li key={r.id} className="space-y-1 px-3 py-2">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{r.displayName || r.userName}</span>
                  <span className="text-muted-foreground">
                    {r.userName} · {r.email}
                  </span>
                  <Badge variant={r.refusal === null ? 'secondary' : 'outline'} className="ml-auto">
                    {r.refusal === null ? 'Moves' : 'Stays'}
                  </Badge>
                </div>
                {r.refusal === null ? (
                  <p className="break-all text-xs text-muted-foreground">
                    As {r.dn}
                    {r.newUserName && `; username becomes ${r.newUserName}`}
                    {r.newDisplayName && `; name becomes ${r.newDisplayName}`}
                    {r.newAdmin !== null && (r.newAdmin ? '; becomes an admin' : '; stops being an admin (not in the admin group)')}
                  </p>
                ) : (
                  <p className="text-xs text-muted-foreground">Why not: {r.refusal}.</p>
                )}
              </li>
            ))}
          </ul>
        )}
        {move.error && <Alert variant="destructive">{errorMessage(move.error)}</Alert>}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={ready.length === 0} loading={move.isPending} onClick={() => move.mutate()}>
            Move {ready.length} {ready.length === 1 ? 'person' : 'people'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
