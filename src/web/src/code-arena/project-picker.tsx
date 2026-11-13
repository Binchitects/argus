import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, FolderKanban } from 'lucide-react'
import { useState } from 'react'
import { DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger } from '@/components/ui/dropdown-menu'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { chooseProject, projectsQuery, stateQuery } from './api'

/**
 * The project in Arena the session counts to, a submenu of the chat's menu (as /project in the terminal): the projects the
 * person may add to, the repository's marked, or none. The folder's next sessions start in the one chosen.
 */
export function ProjectSubmenu({ project }: { project: { id: string; name: string } | null | undefined }) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const list = useQuery({ ...projectsQuery, enabled: open })
  const choose = useMutation({
    mutationFn: chooseProject,
    onSuccess: async ({ said }) => {
      await queryClient.invalidateQueries({ queryKey: stateQuery.queryKey })
      toast.success(said)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const name = project?.name || (project ? 'A project' : 'None')
  return (
    <DropdownMenuSub open={open} onOpenChange={setOpen}>
      <DropdownMenuSubTrigger aria-label={`Project: ${project ? name : 'No project'}`}>
        <FolderKanban />
        <span className="flex-1">Project</span>
        <bdi className="max-w-28 truncate text-xs text-muted-foreground">{name}</bdi>
      </DropdownMenuSubTrigger>
      <DropdownMenuSubContent className="max-h-80 w-64 overflow-y-auto">
        <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">What this session uses counts to the project in Arena</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {list.isPending && <DropdownMenuItem disabled>Asking Arena…</DropdownMenuItem>}
        {list.data?.problem && <DropdownMenuItem disabled>{list.data.problem}</DropdownMenuItem>}
        {list.data?.projects?.length === 0 && <DropdownMenuItem disabled>No project you may add to</DropdownMenuItem>}
        {list.data?.projects?.map((p) => (
          <DropdownMenuItem key={p.id} role="menuitemcheckbox" aria-checked={p.id === project?.id} onSelect={() => p.id !== project?.id && choose.mutate(p.id)}>
            {p.id === project?.id ? <Check /> : <span className="size-4" aria-hidden="true" />}
            <bdi className="min-w-0 flex-1 truncate">{p.name}</bdi>
            {p.id === list.data?.suggested && <span className="text-xs text-muted-foreground">its repository&apos;s</span>}
          </DropdownMenuItem>
        ))}
        {project && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => choose.mutate('none')}>In no project</DropdownMenuItem>
          </>
        )}
      </DropdownMenuSubContent>
    </DropdownMenuSub>
  )
}
