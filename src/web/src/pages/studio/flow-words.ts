import {
  Bot, ClipboardList, Clock, FileText, GitBranch, GitMerge, Hand, Mail, MessageSquare, MessageSquareCode, MessagesSquare, Play, Repeat, Type, Webhook, Wrench, type LucideIcon,
} from 'lucide-react'
import { opLabels, type FlowStep, type FlowTrigger, type StepKind, type TriggerKind } from './studio-api'

export const stepIcons: Record<StepKind, LucideIcon> = {
  ask: MessageSquare,
  agent: Bot,
  tool: Wrench,
  condition: GitBranch,
  loop: Repeat,
  approval: Hand,
  text: Type,
  room: MessagesSquare,
  email: Mail,
  comment: MessageSquareCode,
  file: FileText,
}

export const triggerIcons: Record<TriggerKind, LucideIcon> = { manual: Play, schedule: Clock, webhook: Webhook, gitlab: GitMerge, room: MessagesSquare, form: ClipboardList }

export const groups: { label: string; kinds: StepKind[] }[] = [
  { label: 'Think', kinds: ['ask', 'agent', 'tool'] },
  { label: 'Decide', kinds: ['condition', 'loop', 'approval'] },
  { label: 'Give out', kinds: ['text', 'room', 'email', 'comment', 'file'] },
]

const cut = (s: string | null | undefined, n = 70) => {
  const t = (s ?? '').replace(/\s+/g, ' ').trim()
  return t.length > n ? `${t.slice(0, n - 1)}…` : t
}

/** What a step does, in a line. */
export function summary(s: FlowStep): string {
  switch (s.kind) {
    case 'ask':
    case 'agent':
      return cut(s.prompt) + (s.kind === 'agent' && s.tools?.length ? ` · ${s.tools.join(', ')}` : '')
    case 'tool':
      return s.tool ? `${s.tool}${s.function ? ` → ${s.function}` : ''}` : 'Choose a tool'
    case 'condition':
      return `${cut(s.left, 30) || '…'} ${opLabels[s.op ?? ''] ?? s.op} ${['empty', 'not_empty'].includes(s.op ?? '') ? '' : cut(s.right, 30)}`
    case 'loop':
      return `${cut(s.over, 40) || 'Choose what to go over'}${s.most ? ` · at most ${s.most}` : ''}`
    case 'approval':
      return cut(s.question)
    case 'email':
      return cut(s.subject || s.text)
    case 'file':
      return s.fileName ?? 'result.md'
    default:
      return cut(s.text)
  }
}

/** When the trigger runs it, in a line. */
export function when(t: FlowTrigger): string {
  switch (t.kind) {
    case 'schedule':
      return `${t.cron ?? ''} · ${t.timeZone ?? 'UTC'}`
    case 'gitlab':
      return (t.events ?? []).join(', ') || 'Choose events'
    case 'room':
      return t.keyword ? `A message starting with ${t.keyword}` : 'Choose a room and a word'
    case 'form':
      return `${t.fields?.length ?? 0} ${t.fields?.length === 1 ? 'field' : 'fields'}`
    case 'webhook':
      return 'Any JSON a system posts'
    default:
      return 'Run now, with words to start from'
  }
}
