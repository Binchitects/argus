/** The languages a first line names by itself where Monaco has no rule of its own: a #! line's program, PHP's opening tag. */
const own: [RegExp, string][] = [
  [/^#!.*\b(?:ba|z|k|da|a)?sh\b/, 'shell'],
  [/^#!.*\bpython[\d.]*\b/, 'python'],
  [/^#!.*\b(?:node|nodejs)\b/, 'javascript'],
  [/^#!.*\b(?:deno|bun|tsx|ts-node)\b/, 'typescript'],
  [/^#!.*\bruby\b/, 'ruby'],
  [/^#!.*\bperl\b/, 'perl'],
  [/^#!.*\bphp\b|^<\?php\b/, 'php'],
  [/^#!.*\bpwsh\b/, 'powershell'],
]

/**
 * A file's language by its first line, for a file whose name says none: the languages' own first-line rules (Monaco's
 * python, javascript and xml), then Code Arena's; null when none matches, or the language is not one Monaco has.
 */
export function languageByFirstLine(line: string, languages: { id: string; firstLine?: string }[]): string | null {
  for (const l of languages) {
    if (!l.firstLine) continue
    try {
      if (new RegExp(l.firstLine).test(line)) return l.id
    } catch {
      // A rule that is no pattern: not this one.
    }
  }
  for (const [pattern, id] of own) {
    if (pattern.test(line) && languages.some((l) => l.id === id)) return id
  }
  return null
}
