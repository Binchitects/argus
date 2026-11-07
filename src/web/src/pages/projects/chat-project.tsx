import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, FolderKanban, Globe2 } from 'lucide-react'
import { Link } from 'react-router'
import { DropdownMenuItem, DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger } from '@/components/ui/dropdown-menu'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { mayAdd, projectsQuery } from './api'

/**
 * A chat's menu's Project: the active projects the person may add to (the one it is in checked), or none. Its requests
 * go with it: the project's usage counts them from then on.
 */
export function ChatProjectMenu({ chatId, projectId }: { chatId: string; projectId: string | null }) {
  const queryClient = useQueryClient()
  const projects = useQuery(projectsQuery)
  const open = (projects.data ?? []).filter((p) => p.status === 'active' && mayAdd(p.role))
  const current = projects.data?.find((p) => p.id === projectId)
  const move = useMutation({
    mutationFn: (to: string | null) => api(`/api/chat/conversations/${chatId}/project`, { method: 'PUT', body: { projectId: to } }),
    onSuccess: async (_, to) => {
      await queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', chatId] })
      await queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      const name = projects.data?.find((p) => p.id === to)?.name
      toast.success(to ? `In ${name ?? 'the project'}` : 'In no project', { description: to ? 'What it uses counts to the project, what it used before too.' : undefined })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <DropdownMenuSub>
      <DropdownMenuSubTrigger>
        <FolderKanban /> Project{current ? `: ${current.name}` : ''}
      </DropdownMenuSubTrigger>
      <DropdownMenuSubContent className="max-h-80 w-64 overflow-y-auto">
        {projects.isPending && <DropdownMenuItem disabled>Loading…</DropdownMenuItem>}
        {open.map((p) => (
          <DropdownMenuItem key={p.id} onSelect={() => p.id !== projectId && move.mutate(p.id)} aria-checked={p.id === projectId} role="menuitemcheckbox">
            {p.id === projectId ? <Check /> : p.global ? <Globe2 className="text-muted-foreground" /> : <span className="size-4" aria-hidden="true" />}
            <bdi className="min-w-0 truncate">{p.name}</bdi>
          </DropdownMenuItem>
        ))}
        {projects.isSuccess && open.length === 0 && <DropdownMenuItem disabled>No project you may add to</DropdownMenuItem>}
        <DropdownMenuSeparator />
        {projectId && <DropdownMenuItem onSelect={() => move.mutate(null)}>Take it out of {current?.name ?? 'its project'}</DropdownMenuItem>}
        {projectId && (
          <DropdownMenuItem asChild>
            <Link to={`/projects/${projectId}`}>Open the project</Link>
          </DropdownMenuItem>
        )}
        <DropdownMenuItem asChild>
          <Link to="/projects">All projects</Link>
        </DropdownMenuItem>
      </DropdownMenuSubContent>
    </DropdownMenuSub>
  )
}
