import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import type { Message } from './types'

const preview = (m: Message) => {
  const text = m.content.trim().replace(/\s+/g, ' ') || m.attachments.map((a) => a.fileName).join(', ') || 'Question'
  return text.length > 120 ? `${text.slice(0, 119)}…` : text
}

/**
 * The questions of the branch on screen as marks at the thread's edge, like
 * ChatGPT's and DeepSeek's: hover one to read it, click to go to it. The one
 * being read is marked.
 */
export function QuestionRail({ questions, active, onJump, gutter = 0 }: { questions: Message[]; active: string | null; onJump: (id: string) => void; gutter?: number }) {
  if (questions.length < 2) return null
  return (
    <nav
      aria-label="Questions in this chat"
      className="pointer-events-none absolute inset-y-0 z-10 hidden items-center md:flex"
      // Beside the thread's scrollbar, not under it.
      style={{ right: gutter + 4 }}
    >
      <ol className="pointer-events-auto flex max-h-[70%] flex-col items-end overflow-y-auto py-2 [scrollbar-width:none]">
        {questions.map((q, i) => {
          const current = q.id === active
          return (
            <li key={q.id}>
              <Tooltip content={preview(q)} side="left">
                <button
                  type="button"
                  onClick={() => onJump(q.id)}
                  aria-label={`Question ${i + 1}: ${preview(q)}`}
                  aria-current={current ? 'location' : undefined}
                  className="group flex h-6 w-8 items-center justify-end pr-2 outline-none focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:ring-inset"
                >
                  <span className={cn('block h-[3px] rounded-full transition-[width,background-color] duration-300 ease-(--ease-out-expo) group-hover:w-5 group-hover:bg-foreground', current ? 'w-5 bg-primary' : 'w-3 bg-muted-foreground')} />
                </button>
              </Tooltip>
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
