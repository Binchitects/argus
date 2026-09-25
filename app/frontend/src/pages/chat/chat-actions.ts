import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { archiveChat, forkChat } from './api'

/** Rename, fork, archive (or bring back) and delete a chat. */
export function useChatActions(chat: { id: string; title: string }, active: boolean) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['chat'] })
  const rename = useMutation({
    mutationFn: (title: string) => api(`/api/chat/conversations/${chat.id}`, { method: 'PATCH', body: { title } }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const archive = useMutation({
    mutationFn: (archived: boolean) => archiveChat(chat.id, archived),
    onSuccess: (_, archived) => {
      void refresh()
      if (archived)
        toast.success('Chat archived', {
          description: 'It is under Archived chats, and comes back when you write in it.',
          action: { label: 'Undo', onClick: () => archive.mutate(false) },
        })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const fork = useMutation({
    mutationFn: () => forkChat(chat.id),
    onSuccess: (made) => {
      void refresh()
      navigate(`/chat/${made.id}`)
      toast.success(`Forked into “${made.title}”`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/chat/conversations/${chat.id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void refresh()
      if (active) navigate('/chat')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const askDelete = async () => {
    const description = `“${chat.title}” and its files go for good. Files a fork also uses stay with the fork.`
    if (await confirm({ title: 'Delete this chat?', description, confirm: 'Delete', destructive: true })) remove.mutate()
  }
  return { rename, archive, fork, askDelete }
}
