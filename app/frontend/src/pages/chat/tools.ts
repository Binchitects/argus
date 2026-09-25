import { Calculator, Clock, Image as ImageIcon, Plug, SearchCode, type LucideIcon } from 'lucide-react'
import type { ChatTool } from './types'

/** The icon for a tool's icon hint (the API's ChatTool.icon). */
export const toolIcon: Record<string, LucideIcon> = { 'search-code': SearchCode, image: ImageIcon, calculator: Calculator, clock: Clock, plug: Plug }

/** The tools a chat has on: its own choice, or those on in new chats. */
export function toolsOn(tools: ChatTool[], chosen: string[] | undefined | null): string[] {
  return chosen ?? tools.filter((t) => t.onByDefault).map((t) => t.id)
}
