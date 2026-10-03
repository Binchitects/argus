import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, FolderKanban, MessageSquarePlus, MessagesSquare, Pencil, Trash2, Upload, X } from 'lucide-react'
import { useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { projectQuery, uploadFile } from './api'

/**
 * A project: its name and what it is for, the instructions and files every answer in
 * its chats reads, its chats, and a new chat in it.
 */
export function ProjectView({ projectId, onOpenList, onNewChat }: { projectId: string; onOpenList: () => void; onNewChat: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const project = useQuery(projectQuery(projectId))
  const [naming, setNaming] = useState<{ name: string; description: string } | null>(null)
  const [instructions, setInstructions] = useState<string | null>(null)
  const [uploading, setUploading] = useState(0)
  const picker = useRef<HTMLInputElement>(null)
  const changed = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['projects'] }), queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })])
  const save = useMutation({
    mutationFn: (body: { name?: string; description?: string; instructions?: string }) => api(`/api/projects/${projectId}`, { method: 'PATCH', body }),
    onSuccess: async (_, body) => {
      await changed()
      if (body.instructions !== undefined) {
        setInstructions(null)
        toast.success('Instructions saved', { description: 'Every answer in this project reads them from now on.' })
      } else setNaming(null)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const removeFile = useMutation({
    mutationFn: (attachmentId: string) => api(`/api/projects/${projectId}/files/${attachmentId}`, { method: 'DELETE' }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (chats: 'keep' | 'delete') => api(`/api/projects/${projectId}${chats === 'delete' ? '?chats=delete' : ''}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await changed()
      toast.success('Project removed')
      void navigate('/chat')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const add = async (files: FileList) => {
    for (const file of Array.from(files)) {
      setUploading((n) => n + 1)
      try {
        const a = await uploadFile(file, () => {})
        await api(`/api/projects/${projectId}/files`, { body: { attachmentId: a.id } })
      } catch (e) {
        toast.error(`${file.name}: ${errorMessage(e)}`)
      } finally {
        setUploading((n) => n - 1)
      }
    }
    await changed()
  }

  if (project.error) {
    return (
      <div className="p-6">
        <QueryError error={project.error} retry={() => project.refetch()} />
      </div>
    )
  }
  const p = project.data
  return (
    <div className="min-h-0 overflow-y-auto">
      <div className="mx-auto grid max-w-3xl gap-4 px-4 py-6">
        <div className="flex items-start gap-2">
          <Button variant="ghost" size="icon-sm" className="lg:hidden" onClick={onOpenList} aria-label="Chats">
            <MessagesSquare />
          </Button>
          <FolderKanban className="mt-1 size-6 shrink-0 text-primary-ink" aria-hidden="true" />
          {!p ? (
            <Skeleton className="h-8 w-64" />
          ) : naming ? (
            <form
              className="grid min-w-0 flex-1 gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                save.mutate({ name: naming.name, description: naming.description })
              }}
            >
              <Input autoFocus required maxLength={100} value={naming.name} onChange={(e) => setNaming({ ...naming, name: e.target.value })} aria-label="Project name" />
              <Input maxLength={500} value={naming.description} onChange={(e) => setNaming({ ...naming, description: e.target.value })} placeholder="What it is for (optional)" aria-label="Description" />
              <div className="flex gap-2">
                <Button type="submit" size="sm" loading={save.isPending}>
                  Save
                </Button>
                <Button type="button" size="sm" variant="outline" onClick={() => setNaming(null)}>
                  Cancel
                </Button>
              </div>
            </form>
          ) : (
            <div className="grid min-w-0 flex-1 gap-0.5">
              <h1 dir="auto" className="text-xl font-semibold [overflow-wrap:anywhere]">
                {p.name}
              </h1>
              {p.description && (
                <p dir="auto" className="text-sm text-muted-foreground">
                  {p.description}
                </p>
              )}
            </div>
          )}
          {p && !naming && (
            <Button variant="ghost" size="icon-sm" onClick={() => setNaming({ name: p.name, description: p.description ?? '' })} aria-label="Rename the project">
              <Pencil />
            </Button>
          )}
        </div>

        <Button className="justify-self-start" onClick={onNewChat}>
          <MessageSquarePlus /> New chat in this project
        </Button>

        <Card aria-label="Instructions">
          <CardHeader>
            <CardTitle className="text-base">Instructions</CardTitle>
            <CardDescription>Every answer in this project reads them: who it is for, how to answer, what to keep in mind.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-2">
            {!p ? (
              <Skeleton className="h-24" />
            ) : (
              <>
                <Textarea
                  dir="auto"
                  rows={5}
                  maxLength={20_000}
                  value={instructions ?? p.instructions ?? ''}
                  onChange={(e) => setInstructions(e.target.value)}
                  placeholder="e.g. Answer as a senior codec engineer. Our code is C11, built with CMake."
                  aria-label="Project instructions"
                />
                {instructions !== null && instructions !== (p.instructions ?? '') && (
                  <div className="flex gap-2">
                    <Button size="sm" loading={save.isPending} onClick={() => save.mutate({ instructions })}>
                      Save
                    </Button>
                    <Button size="sm" variant="outline" onClick={() => setInstructions(null)}>
                      Cancel
                    </Button>
                  </div>
                )}
              </>
            )}
          </CardContent>
        </Card>

        <Card aria-label="Project files">
          <CardHeader className="flex flex-row flex-wrap items-start gap-3">
            <div className="min-w-0 flex-1">
              <CardTitle className="text-base">Files</CardTitle>
              <CardDescription>Every chat of the project has them: their text goes with each answer, and the tools can open them.</CardDescription>
            </div>
            <input ref={picker} type="file" multiple hidden onChange={(e) => e.target.files && void add(e.target.files).then(() => (e.target.value = ''))} aria-label="Add files to the project" />
            <Button variant="outline" size="sm" loading={uploading > 0} onClick={() => picker.current?.click()}>
              <Upload /> Add files
            </Button>
          </CardHeader>
          <CardContent>
            {!p ? (
              <Skeleton className="h-12" />
            ) : p.files.length === 0 ? (
              <p className="text-sm text-muted-foreground">No files yet.</p>
            ) : (
              <ul className="grid gap-1.5" aria-label="Files">
                {p.files.map((f) => (
                  <li key={f.id} className="flex min-w-0 items-center gap-2 rounded-lg border px-3 py-2 text-sm">
                    <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="min-w-0 flex-1 truncate font-medium">{f.fileName}</span>
                    <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{formatValue(f.size, 'bytes')}</span>
                    <Button variant="ghost" size="icon-sm" className="size-7" disabled={removeFile.isPending} onClick={() => removeFile.mutate(f.id)} aria-label={`Remove ${f.fileName} from the project`}>
                      <X />
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card aria-label="Project chats">
          <CardHeader>
            <CardTitle className="text-base">Chats</CardTitle>
          </CardHeader>
          <CardContent>
            {!p ? (
              <Skeleton className="h-12" />
            ) : p.chats.length === 0 ? (
              <p className="text-sm text-muted-foreground">No chats yet: start one above, or move one here from its menu.</p>
            ) : (
              <ul className="grid gap-0.5" aria-label="Chats in the project">
                {p.chats.map((c) => (
                  <li key={c.id}>
                    <Link to={`/chat/${c.id}`} className="flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                      <bdi className="min-w-0 flex-1 truncate">{c.title}</bdi>
                      <span className="shrink-0 text-xs text-muted-foreground">{new Date(c.updatedAt).toLocaleDateString()}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        {p && (
          <Button
            variant="ghost"
            className="justify-self-start text-destructive-ink"
            onClick={async () => {
              if (!(await confirm({ title: `Remove ${p.name}?`, description: 'Its instructions and file list go. Its chats stay, out of any project.', confirm: 'Remove project', destructive: true }))) return
              remove.mutate('keep')
            }}
          >
            <Trash2 /> Remove project
          </Button>
        )}
      </div>
    </div>
  )
}
