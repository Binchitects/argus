import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, use, useCallback, useState } from 'react'
import { canvasesQuery } from './canvas-api'
import type { ChatEvent } from './types'

/** Opens a canvas of the chat on screen in the panel beside it (a tool card's "Open in the canvas"). */
export const CanvasOpener = createContext<((id: string) => void) | null>(null)

export const useCanvasOpener = () => use(CanvasOpener)

/**
 * The chat's canvases and the panel that shows them: open or not, which canvas, how wide.
 * A canvas the model makes or changes opens by itself (`autoOpen`, on a wide screen); the
 * page calls `onEvent` with every event of an answer. `onShow`: the panel opened (the Files
 * panel, which shares its place, closes).
 */
export function useCanvasState(chatId: string | undefined, autoOpen: boolean, onShow: () => void) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  /** A canvas is for writing: the panel takes half the page until narrowed. */
  const [wide, setWide] = useState(true)
  const list = useQuery({ ...canvasesQuery(chatId ?? ''), enabled: !!chatId })
  const reveal = useCallback(
    (id: string | null) => {
      setSelected(id)
      setOpen(true)
      onShow()
    },
    [onShow],
  )
  const onEvent = (e: ChatEvent) => {
    const changed = e.type === 'tool_result' && !e.isError ? e.details?.canvas : undefined
    if (!changed || !chatId) return
    // Its text, its versions and the list all changed.
    void queryClient.invalidateQueries({ queryKey: ['chat', 'canvas', changed.id] })
    void queryClient.invalidateQueries({ queryKey: canvasesQuery(chatId).queryKey })
    if (autoOpen) reveal(changed.id)
  }
  return { open, setOpen, selected, setSelected, wide, setWide, count: list.data?.length ?? 0, reveal, onEvent }
}
