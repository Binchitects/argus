import { AudioLines, Box, Brain, Sparkles, ListChecks, Calculator, Clapperboard, Clock, Database, FilePen, FileText, Globe, Image as ImageIcon, Library, MessageCircleQuestion, Network, Plug, Presentation, Scale, SearchCode, SquareTerminal, Telescope, type LucideIcon } from 'lucide-react'
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
  box: Box,
  'list-checks': ListChecks,
  globe: Globe,
  database: Database,
  presentation: Presentation,
  'message-circle-question': MessageCircleQuestion,
  network: Network,
  brain: Brain,
  sparkles: Sparkles,
  library: Library,
  scale: Scale,
  telescope: Telescope,
}

/** The tools a chat has on: its own choice, or those on in new chats. */
export function toolsOn(tools: ChatTool[], chosen: string[] | undefined | null): string[] {
  return chosen ?? tools.filter((t) => t.onByDefault).map((t) => t.id)
}
