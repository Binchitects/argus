import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Download, Gavel } from 'lucide-react'
import { useState } from 'react'
import { KeyValues } from '@/components/app/key-values'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'
import type { Person } from './people-api'

/** Legal hold (nothing of theirs is deleted while it lasts) and an export of their data for eDiscovery. */
export function LegalHoldCard({ p }: { p: Person }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const held = !!p.legalHoldSince
  const set = useMutation({
    mutationFn: (body: { hold: boolean; reason?: string }) => api<{ erasedChats: number }>(`/api/admin/people/${p.id}/legal-hold`, { method: 'PUT', body }),
    onSuccess: (r, v) => {
      setOpen(false)
      setReason('')
      toast.success(v.hold ? 'On legal hold' : 'Legal hold ended', {
        description: v.hold ? 'Nothing of theirs is deleted until the hold ends.' : r.erasedChats ? `${r.erasedChats} chats they deleted meanwhile are erased now.` : undefined,
      })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'person', p.id] })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'people'] })
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Gavel className="size-4" aria-hidden="true" /> Legal hold
        </CardTitle>
        <CardDescription>
          While on hold, nothing of theirs is deleted: retention passes them by, and a chat they delete is hidden from them, not erased, until the hold ends. They are not told.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {held ? (
          <KeyValues
            items={[
              ['On hold since', when(p.legalHoldSince)],
              ['Reason', p.legalHoldReason ?? ''],
            ]}
          />
        ) : (
          <p className="text-sm text-muted-foreground">Not on hold.</p>
        )}
        {error && !open && <Alert variant="destructive">{error}</Alert>}
      </CardContent>
      <CardFooter className="flex flex-wrap gap-2">
        {held ? (
          <Button
            variant="outline"
            loading={set.isPending}
            onClick={async () => {
              if (
                await confirm({
                  title: `End the legal hold on ${p.displayName}?`,
                  description: 'Retention applies again, and the chats they deleted while on hold are erased now.',
                  confirm: 'End the hold',
                  destructive: true,
                })
              )
                set.mutate({ hold: false })
            }}
          >
            End the hold
          </Button>
        ) : (
          <Button variant="outline" onClick={() => setOpen(true)}>
            <Gavel /> Place on legal hold
          </Button>
        )}
        <Button variant="outline" asChild>
          <a href={`/api/admin/people/${p.id}/export`} download>
            <Download /> Export their data
          </a>
        </Button>
      </CardFooter>
      <Dialog
        open={open}
        onOpenChange={(o) => {
          setOpen(o)
          setError(null)
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Place {p.displayName} on legal hold?</DialogTitle>
            <DialogDescription>Say why: the matter or the ticket. It is in the audit log, with you.</DialogDescription>
          </DialogHeader>
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              setError(null)
              if (!reason.trim()) setError('Say why the hold is placed.')
              else set.mutate({ hold: true, reason: reason.trim() })
            }}
          >
            {error && <Alert variant="destructive">{error}</Alert>}
            <Field label="Reason">
              <Textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />
            </Field>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" loading={set.isPending}>
                Place on hold
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
