import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Building2, Copy, GitBranch, Link2, MessagesSquare, Unlink, Users } from 'lucide-react'
import { useState } from 'react'
import { ChoiceCard } from '@/components/app/choice-card'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { shareQuery, shareUrl } from './api'
import { opened } from './format'
import { GroupChecklist } from './assistant-sharing'
import type { ChatShare } from './types'

interface ShareForm {
  reach: 'Company' | 'Groups'
  groups: string[]
  branch: boolean
}

/**
 * Sharing a chat: a read-only link for the company or chosen groups (never for anyone
 * signed out), the whole chat or the branch on screen, revoked in one click.
 */
export function ShareDialog({ chatId, leafId, open, onOpenChange }: { chatId: string; leafId: string | null; open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
  const share = useQuery({ ...shareQuery(chatId), enabled: open })
  const current = share.data ?? null
  const [form, setForm] = useState<ShareForm | null>(null)
  const saved: ShareForm = { reach: current?.reach === 'Groups' ? 'Groups' : 'Company', groups: current?.groups.map((g) => g.id) ?? [], branch: current?.branch ?? false }
  const value = form ?? saved
  const dirty = !current || JSON.stringify(value) !== JSON.stringify(saved)
  const set = (change: Partial<ShareForm>) => setForm({ ...value, ...change })
  const put = useMutation({
    mutationFn: () =>
      api<ChatShare>(`/api/chat/conversations/${chatId}/share`, {
        method: 'PUT',
        body: { reach: value.reach, groups: value.reach === 'Groups' ? value.groups : [], branch: value.branch, ...(value.branch && leafId ? { messageId: leafId } : {}) },
      }),
    onSuccess: async (s) => {
      queryClient.setQueryData(shareQuery(chatId).queryKey, s)
      setForm(null)
      await navigator.clipboard?.writeText(shareUrl(s.id)).catch(() => undefined)
      toast.success(current ? 'Link updated' : 'Link made and copied', { description: 'People it is for open it signed in, read-only.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const revoke = useMutation({
    mutationFn: () => api(`/api/chat/conversations/${chatId}/share`, { method: 'DELETE' }),
    onSuccess: () => {
      queryClient.setQueryData(shareQuery(chatId).queryKey, null)
      setForm(null)
      toast.success('Link revoked', { description: 'It stops working at once.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) setForm(null)
        onOpenChange(o)
      }}
    >
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Share this chat</DialogTitle>
          <DialogDescription>A read-only link: people see its messages and files, and can fork it into their own chats. It never works signed out.</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          <fieldset className="grid gap-2">
            <legend className="mb-2 text-sm font-medium">Who may open it</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              <ChoiceCard name="share-reach" checked={value.reach === 'Company'} onChange={() => set({ reach: 'Company' })} icon={Building2} title="Everyone in the company" hint="Everyone who can sign in." />
              <ChoiceCard name="share-reach" checked={value.reach === 'Groups'} onChange={() => set({ reach: 'Groups' })} icon={Users} title="Chosen groups" hint="Their members only." />
            </div>
            {value.reach === 'Groups' && <GroupChecklist label="Groups that may open it" chosen={value.groups} known={current?.groups} onChange={(groups) => set({ groups })} />}
          </fieldset>
          <fieldset className="grid gap-2">
            <legend className="mb-2 text-sm font-medium">What it shows</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              <ChoiceCard name="share-what" checked={!value.branch} onChange={() => set({ branch: false })} icon={MessagesSquare} title="The whole chat" hint="With its branches, as it grows." />
              <ChoiceCard name="share-what" checked={value.branch} onChange={() => set({ branch: true })} icon={GitBranch} title="The branch on screen" hint="Up to its last message, as it is now." />
            </div>
          </fieldset>
          {current && (
            <div className="grid gap-2 rounded-lg border bg-muted/30 p-3">
              <div className="flex items-center gap-2">
                <Input readOnly value={shareUrl(current.id)} aria-label="Link" className="h-8 font-mono text-xs" onFocus={(e) => e.target.select()} />
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() =>
                    void navigator.clipboard
                      ?.writeText(shareUrl(current.id))
                      .then(() => toast.success('Link copied'))
                      .catch(() => toast.error('The browser did not allow copying: select the link and copy it.'))
                  }
                >
                  <Copy /> Copy
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">{opened(current)}</p>
            </div>
          )}
        </div>
        <DialogFooter className="gap-2 sm:justify-between">
          {current ? (
            <Button variant="ghost" className="text-destructive-ink" loading={revoke.isPending} onClick={() => revoke.mutate()}>
              <Unlink /> Revoke link
            </Button>
          ) : (
            <span />
          )}
          <Button loading={put.isPending} disabled={!dirty || (value.reach === 'Groups' && value.groups.length === 0)} onClick={() => put.mutate()}>
            <Link2 /> {current ? 'Update link' : 'Make link'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
