import {
  Bell, Bot, ClipboardList, Clock, Database, FileText, GitBranch, GitMerge, GitPullRequest, Globe, Hand, Hourglass, Library, Mail, MessageSquare, MessageSquareCode, MessagesSquare, OctagonX, Play,
  Repeat, SquareTerminal, Type, Webhook, Wrench, type LucideIcon,
} from 'lucide-react'
import { opLabels, type FlowStep, type FlowTrigger, type StepKind, type TriggerKind } from './studio-api'

export const stepIcons: Record<StepKind, LucideIcon> = {
  ask: MessageSquare,
  agent: Bot,
  tool: Wrench,
  code: SquareTerminal,
  data: Database,
  knowledge: Library,
  http: Globe,
  condition: GitBranch,
  loop: Repeat,
  approval: Hand,
  wait: Hourglass,
  stop: OctagonX,
  text: Type,
  room: MessagesSquare,
  email: Mail,
  notify: Bell,
  comment: MessageSquareCode,
  gitlab: GitPullRequest,
  file: FileText,
}

export const triggerIcons: Record<TriggerKind, LucideIcon> = { manual: Play, schedule: Clock, webhook: Webhook, gitlab: GitMerge, room: MessagesSquare, form: ClipboardList }

export const groups: { label: string; kinds: StepKind[] }[] = [
  { label: 'Think', kinds: ['ask', 'agent', 'tool', 'code'] },
  { label: 'Find', kinds: ['knowledge', 'data', 'http'] },
  { label: 'Decide', kinds: ['condition', 'loop', 'approval', 'wait', 'stop'] },
  { label: 'Give out', kinds: ['text', 'room', 'email', 'notify', 'comment', 'gitlab', 'file'] },
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
    case 'knowledge':
      return cut(s.prompt) + (s.kind === 'agent' && s.tools?.length ? ` · ${s.tools.join(', ')}` : '')
    case 'data':
      return cut(s.sql || s.prompt) || 'Choose a data source'
    case 'http':
      return `${s.method ?? 'GET'} ${cut(s.url, 60)}`
    case 'wait':
      return `${s.minutes ?? 1} ${(s.minutes ?? 1) === 1 ? 'minute' : 'minutes'}`
    case 'stop':
      return `${s.outcome === 'failed' ? 'Failed' : 'Done'}${s.text ? `: ${cut(s.text, 50)}` : ''}`
    case 'gitlab':
      return s.action === 'label' ? `Label ${s.target || 'it'}: ${cut(s.labels, 40)}` : s.action === 'issue' ? `Open an issue: ${cut(s.subject, 50)}` : `Comment on ${s.target || 'it'}: ${cut(s.text, 40)}`
    case 'notify':
      return cut(s.subject || s.text)
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
      return [(t.events ?? []).join(', ') || 'Choose events', t.branches && `on ${t.branches}`, t.labels && `labelled ${t.labels}`, t.keyword && `with "${t.keyword}"`].filter(Boolean).join(' · ')
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
