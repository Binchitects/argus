import type { LogLine } from './types'

/**
 * A line's level, from its labels (a level label, or the level Loki detected)
 * or its text: what colours its edge. The same names as the Logs page's filter.
 */
export function levelOf(line: LogLine): 'error' | 'warn' | 'info' | 'debug' | null {
  const raw = [line.labels.level, line.labels.detected_level].find((v) => v && v !== 'unknown') ?? ''
  const text = (raw || (/\b(?:level|lvl|severity)"?\s*[=:]\s*"?(\w+)/i.exec(line.line)?.[1] ?? /\b(ERROR|ERR|FATAL|CRITICAL|WARN(?:ING)?|INFO|DEBUG)\b/.exec(line.line)?.[1] ?? '')).toLowerCase()
  if (/^(e$|err|fatal|crit|panic|emerg|alert)/.test(text)) return 'error'
  if (/^(w$|warn)/.test(text)) return 'warn'
  if (/^(i$|info|notice)/.test(text)) return 'info'
  if (/^(d$|debug|trace)/.test(text)) return 'debug'
  return null
}
