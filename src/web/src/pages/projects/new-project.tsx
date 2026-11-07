import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import { errorMessage } from '@/lib/api'
import { makeProject } from './api'

/**
 * A new project: its name, what it is, its dates, and whether it is everyone's (only offered to who may make global ones).
 * Arena's Projects page and Code Arena's Projects view both make one with it; `onMade` opens it.
 */
export function NewProjectDialog({ open, onOpenChange, mayGlobal, onMade }: { open: boolean; onOpenChange: (open: boolean) => void; mayGlobal: boolean; onMade: (id: string, name: string) => void }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ name: '', description: '', dueOn: '', global: false })
  const make = useMutation({
    mutationFn: () => makeProject({ name: form.name, description: form.description, dueOn: form.dueOn || undefined, global: form.global }),
    onSuccess: async ({ id }) => {
      const name = form.name.trim()
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      onOpenChange(false)
      setForm({ name: '', description: '', dueOn: '', global: false })
      onMade(id, name)
    },
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            if (form.name.trim()) make.mutate()
          }}
        >
          <DialogHeader>
            <DialogTitle>New project</DialogTitle>
            <DialogDescription>You are its owner. Add its people, repositories and chats on its page.</DialogDescription>
          </DialogHeader>
          <Field label="Name">
            <Input value={form.name} maxLength={100} onChange={(e) => setForm({ ...form, name: e.target.value })} autoFocus required />
          </Field>
          <Field label="What it is for" hint="Optional. It heads the project's reports.">
            <Textarea value={form.description} maxLength={2000} rows={3} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </Field>
          <Field label="Due" hint="Optional.">
            <Input type="date" value={form.dueOn} onChange={(e) => setForm({ ...form, dueOn: e.target.value })} className="w-44" />
          </Field>
          {mayGlobal && (
            <div className="flex items-start justify-between gap-4 rounded-lg border p-3">
              <div className="grid gap-0.5">
                <label htmlFor="project-global" className="text-sm font-medium">
                  Global project
                </label>
                <p className="text-xs text-muted-foreground">Everyone sees it and may add their chats and sessions to it.</p>
              </div>
              <Switch id="project-global" checked={form.global} onCheckedChange={(v) => setForm({ ...form, global: v })} />
            </div>
          )}
          {make.error && (
            <p className="text-sm text-destructive" role="alert">
              {errorMessage(make.error)}
            </p>
          )}
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={!form.name.trim() || make.isPending}>
              Make it
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
