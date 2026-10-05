import { AudioLines, Brain, Calculator, Clapperboard, Clock, FilePen, FileText, Globe, Image as ImageIcon, Library, MessageCircleQuestion, Network, Plug, Scale, SearchCode, SquareTerminal, type LucideIcon } from 'lucide-react'
import type { ChatTool } from './types'

/** The icon for a tool's icon hint (the API's ChatTool.icon). */
export const toolIcon: Record<string, LucideIcon> = {
  'search-code': SearchCode,
  image: ImageIcon,
  video: Clapperboard,
  'audio-lines': AudioLines,
  calculator: Calculator,
  clock: Clock,
  plug: Plug,
  'file-text': FileText,
  'file-pen': FilePen,
  terminal: SquareTerminal,
  globe: Globe,
  'message-circle-question': MessageCircleQuestion,
  network: Network,
  brain: Brain,
  library: Library,
  scale: Scale,
}

/** The tools a chat has on: its own choice, or those on in new chats. */
export function toolsOn(tools: ChatTool[], chosen: string[] | undefined | null): string[] {
  return chosen ?? tools.filter((t) => t.onByDefault).map((t) => t.id)
}
