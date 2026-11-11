import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

/**
 * The person's chats' changes as they happen (the server's /api/chat/watch: a message added here, on another device or by
 * Code Arena; a chat renamed, moved or deleted): the chats' list and the chat that changed are read again at once, instead
 * of at their next look. The browser opens the stream again by itself when it drops.
 */
export function useChatWatch(enabled = true) {
  const queryClient = useQueryClient()
  useEffect(() => {
    if (!enabled || typeof EventSource === 'undefined') return
    const source = new EventSource('/api/chat/watch')
    const due = new Set<string>()
    const drafts = new Set<string>()
    let timer: ReturnType<typeof setTimeout> | null = null
    const flush = () => {
      timer = null
      if (due.size > 0) void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      for (const id of due) void queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', id], refetchType: 'active' })
      for (const id of drafts) void queryClient.invalidateQueries({ queryKey: ['chat', 'draft', id], refetchType: 'active' })
      due.clear()
      drafts.clear()
    }
    source.onmessage = (m) => {
      try {
        const e = JSON.parse(m.data as string) as { type?: string; id?: string; what?: string }
        if (e.type !== 'chat' || !e.id) return
        // Code Arena's answer as it comes: that alone is read again, not the chat or the list.
        if (e.what === 'draft') drafts.add(e.id)
        else due.add(e.id)
        // Several saves of one change (the chat and its messages) read it once.
        timer ??= setTimeout(flush, 300)
      } catch {
        // Not a change: nothing to read again.
      }
    }
    return () => {
      source.close()
      if (timer) clearTimeout(timer)
    }
  }, [enabled, queryClient])
}
