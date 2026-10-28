import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Share2 } from 'lucide-react'
import { useId, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { reviewQuery, shareReview, type ReviewReach, type ReviewSharing } from './api'

const reachLabels: Record<ReviewReach, string> = { Private: 'Only the people named', Groups: 'Some groups', Company: 'Everyone' }

/** Share a review: with everyone, some groups, or people by name; each reads it only while they may read its project themselves. */
export function ShareReview({ id, share }: { id: string; share: ReviewSharing }) {
  const [open, setOpen] = useState(false)
  const shared = share.reach !== 'Private' || share.people.length > 0
  return (
    <>
      <Button variant="outline" onClick={() => setOpen(true)}>
        <Share2 /> {shared ? 'Shared' : 'Share'}
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-lg">{open && <ShareForm id={id} share={share} onClose={() => setOpen(false)} />}</DialogContent>
      </Dialog>
    </>
  )
}

function ShareForm({ id, share, onClose }: { id: string; share: ReviewSharing; onClose: () => void }) {
  const queryClient = useQueryClient()
  const formId = useId()
  const [reach, setReach] = useState<ReviewReach>(share.reach)
  const [groups, setGroups] = useState(share.groups.map((g) => g.id))
  const [people, setPeople] = useState(share.people.map((p) => p.userName).join(', '))
  const [error, setError] = useState<string | null>(null)
  const mine = useQuery({ queryKey: ['sharing', 'groups'], queryFn: ({ signal }) => api<{ id: string; name: string }[]>('/api/sharing/groups', { signal }), enabled: reach === 'Groups' })
  // The groups it is shared with already stay choosable, though the person left one.
  const choosable = [...(mine.data ?? []), ...share.groups.filter((g) => !mine.data?.some((m) => m.id === g.id))]
  const names = people.split(/[\s,;]+/).filter(Boolean)
  const save = useMutation({
    mutationFn: (body: { reach: ReviewReach; groups: string[]; people: string[] }) => shareReview(id, body),
    onSuccess: (_, body) => {
      toast.success(body.reach === 'Private' && body.people.length === 0 ? 'Not shared any more' : 'Shared', {
        description: body.reach === 'Private' && body.people.length === 0 ? undefined : 'Each of them reads it while they may read its project themselves.',
      })
      void queryClient.invalidateQueries({ queryKey: reviewQuery(id).queryKey })
      void queryClient.invalidateQueries({ queryKey: ['code', 'reviews'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const shared = share.reach !== 'Private' || share.people.length > 0
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate({ reach, groups: reach === 'Groups' ? groups : [], people: names })
      }}
    >
      <DialogHeader>
        <DialogTitle>Share the review</DialogTitle>
        <DialogDescription>
          They read its summary and findings, without changing them. Each of them sees it only while they may read its project themselves: GitLab’s
          rights stay the limit.
        </DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Segmented label="Who may read it" value={reach} onChange={setReach} options={(['Private', 'Groups', 'Company'] as const).map((r) => ({ value: r, label: reachLabels[r] }))} />
      {reach === 'Groups' && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Groups</legend>
          {choosable.map((g) => (
            <div key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${formId}-${g.id}`} checked={groups.includes(g.id)} onCheckedChange={(on) => setGroups(on ? [...groups, g.id] : groups.filter((x) => x !== g.id))} />
              <label htmlFor={`${formId}-${g.id}`}>{g.name}</label>
            </div>
          ))}
          {mine.data && choosable.length === 0 && <p className="text-xs text-muted-foreground">You are in no group.</p>}
        </fieldset>
      )}
      <Field label="People (user names or emails)" hint="They are told in their bell.">
        <Input value={people} onChange={(e) => setPeople(e.target.value)} placeholder="ann, ben@example.com" />
      </Field>
      <DialogFooter>
        {shared && (
          <Button type="button" variant="ghost" className="mr-auto text-destructive-ink" loading={save.isPending} onClick={() => save.mutate({ reach: 'Private', groups: [], people: [] })}>
            Stop sharing
          </Button>
        )}
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={(reach === 'Groups' && groups.length === 0) || (reach === 'Private' && names.length === 0)}>
          Share
        </Button>
      </DialogFooter>
    </form>
  )
}
