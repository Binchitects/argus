/** Questions the model asks the person (its ask_user tool), and the person's reply. */

export interface Question {
  question: string
  options: { label: string; description: string | null }[]
  multiple: boolean
}

/** The questions an ask_user call carries, read leniently (an option may be a bare string). */
export function questionsOf(raw: string): Question[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw || '{}')
  } catch {
    return []
  }
  const list = (parsed as { questions?: unknown })?.questions
  if (!Array.isArray(list)) return []
  return list.flatMap((q): Question[] => {
    const x = q as { question?: unknown; options?: unknown; multiple?: unknown }
    if (typeof x?.question !== 'string' || !Array.isArray(x.options)) return []
    const options = x.options.flatMap((o) => {
      if (typeof o === 'string') return o ? [{ label: o, description: null }] : []
      const y = o as { label?: unknown; description?: unknown }
      return typeof y?.label === 'string' && y.label ? [{ label: y.label, description: typeof y.description === 'string' ? y.description : null }] : []
    })
    return options.length ? [{ question: x.question, options, multiple: x.multiple === true }] : []
  })
}

/** The person's choices as their next message: one line per question, the question then what they chose. */
export function replyOf(questions: Question[], picked: string[][], other: string[]): string {
  return questions.map((q, i) => `${q.question} ${[...picked[i]!, ...(other[i]!.trim() ? [other[i]!.trim()] : [])].join(', ')}`).join('\n')
}
