import { isRow, type Row } from './argus'
import { languageOf } from './highlight'

/** An argument that is code (a program, a command, a query): shown as a code block in its language. */
export interface CodeArg {
  key: string
  code: string
  lang: string | null
}

/** A tool call's arguments, sorted for showing: code as code blocks, short values as a list, the rest as JSON. */
export interface ToolArgs {
  code: CodeArg[]
  plain: [string, string][]
  nested: Row | null
  /** Arguments that were not a JSON object (the model wrote something else). */
  unparsed: string | null
}

/** Arguments that are a program even on one line, and ones that are when they are long. */
const programKeys = /^(code|script|source|program|command|cmd|shell|sql|patch|diff)$/i
const textKeys = /^(html|css|javascript|js|python|content|text)$/i

/** The language an argument is written in: named by the call, by the argument, by the tool, or by a file it goes to. */
export function codeLanguage(tool: string, key: string, args: Row): string | null {
  const named = typeof args.language === 'string' ? args.language : typeof args.lang === 'string' ? args.lang : null
  if (named && languageOf(named)) return named
  const k = key.toLowerCase()
  if (/^(command|cmd|shell)$/.test(k) || /shell|bash|terminal|exec_command/.test(tool)) return 'bash'
  if (/sql/.test(k) || /sql|database/.test(tool)) return 'sql'
  if (/^(patch|diff)$/.test(k)) return 'diff'
  if (/^(html|css|javascript|js|python)$/.test(k)) return k
  const path = [args.path, args.file, args.filename, args.file_name].find((v): v is string => typeof v === 'string' && v.includes('.'))
  if (path) return path.split('.').pop()!
  return /python/.test(tool) ? 'python' : null
}

/** Code: any text over one line, a program, or long text meant as a page or a file. Short values stay in the list. */
const isCode = (key: string, value: string) => value.includes('\n') || programKeys.test(key) || (textKeys.test(key) && value.length > 80)

export function splitArgs(tool: string, raw: string): ToolArgs {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw || '{}')
  } catch {
    return { code: [], plain: [], nested: null, unparsed: raw }
  }
  if (!isRow(parsed)) return { code: [], plain: [], nested: null, unparsed: raw }
  const out: ToolArgs = { code: [], plain: [], nested: null, unparsed: null }
  for (const [k, v] of Object.entries(parsed)) {
    if (typeof v === 'string' && isCode(k, v)) out.code.push({ key: k, code: v, lang: codeLanguage(tool, k, parsed) })
    else if (v === null || ['string', 'number', 'boolean'].includes(typeof v)) out.plain.push([k, v === null ? 'null' : String(v)])
    else out.nested = { ...out.nested, [k]: v }
  }
  return out
}

/** A program's run (Python...): what it printed, its errors and how it ended. */
export interface RunOutput {
  stdout: string
  stderr: string
  exitCode: number | null
  problem: string | null
  seconds: number | null
  /** The other fields, as they came. */
  rest: Row
}

export function runOutput(value: unknown): RunOutput | null {
  if (!isRow(value) || !('stdout' in value || 'stderr' in value) || !('exit_code' in value || 'exitCode' in value)) return null
  const { stdout, stderr, exit_code, exitCode, problem, timed_out, seconds, ...rest } = value
  const text = (v: unknown) => (typeof v === 'string' ? v : '')
  const code = exit_code ?? exitCode
  return {
    stdout: text(stdout),
    stderr: text(stderr),
    exitCode: typeof code === 'number' ? code : null,
    problem: [problem, timed_out].filter((p): p is string => typeof p === 'string' && p.length > 0).join(' ') || null,
    seconds: typeof seconds === 'number' ? seconds : null,
    rest: Object.fromEntries(Object.entries(rest).filter(([, v]) => v !== null && v !== undefined)),
  }
}

/** The header's one-line summary of the arguments: code shows its first line. */
export function argsSummary(raw: string): [string, string][] {
  const a = splitArgs('', raw)
  if (a.unparsed !== null) return a.unparsed ? [['arguments', a.unparsed]] : []
  const first = (s: string) => {
    const line = s.split('\n').find((l) => l.trim()) ?? ''
    return s.trim().includes('\n') ? `${line.trim()} …` : line
  }
  return [...a.code.map((c): [string, string] => [c.key, first(c.code)]), ...a.plain, ...Object.entries(a.nested ?? {}).map(([k, v]): [string, string] => [k, JSON.stringify(v)])]
}

/** What sub-agents brought back (the delegate tool): each part's title, result, tool calls and error. */
export interface AgentResult {
  title: string
  result: string
  toolCalls: number
  error: string | null
}

export function agentResults(value: unknown): AgentResult[] | null {
  if (!Array.isArray(value) || value.length === 0) return null
  const parts = value.filter((v): v is Row => isRow(v) && typeof v.title === 'string' && typeof v.result === 'string')
  if (parts.length !== value.length) return null
  return parts.map((p) => ({
    title: p.title as string,
    result: p.result as string,
    toolCalls: typeof p.tool_calls === 'number' ? p.tool_calls : 0,
    error: typeof p.error === 'string' ? p.error : null,
  }))
}

/** The parts a delegate call gave its sub-agents (titles and instructions), from its arguments. */
export function partsOf(raw: string): { title: string; instructions: string }[] {
  try {
    const tasks = (JSON.parse(raw || '{}') as { tasks?: unknown }).tasks
    return Array.isArray(tasks)
      ? tasks.map((t) => ({ title: String((t as { title?: unknown })?.title ?? ''), instructions: String((t as { instructions?: unknown })?.instructions ?? '') }))
      : []
  } catch {
    return []
  }
}

/** A delegate call in a line: how many parts, and their titles. */
export function partsSummary(raw: string): [string, string][] {
  const parts = partsOf(raw)
  return parts.length ? [[`${parts.length} parts`, parts.map((p) => p.title).join(', ')]] : []
}
