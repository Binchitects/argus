import { describe, expect, it } from 'vitest'
import { agentResults, argsSummary, runOutput, splitArgs } from './tool-args'

describe('tool arguments', () => {
  it('a program is code in its language; short values stay a list; the rest is JSON', () => {
    const a = splitArgs('run_python', JSON.stringify({ code: 'import os\nprint(os.getcwd())', timeout: 30, options: { verbose: true } }))
    expect(a.code).toEqual([{ key: 'code', code: 'import os\nprint(os.getcwd())', lang: 'python' }])
    expect(a.plain).toEqual([['timeout', '30']])
    expect(a.nested).toEqual({ options: { verbose: true } })
    expect(a.unparsed).toBeNull()
  })

  it('knows a command, a query and a file by what they are', () => {
    expect(splitArgs('run', '{"command":"ls -la"}').code[0]!.lang).toBe('bash')
    expect(splitArgs('query_db', '{"sql":"select 1"}').code[0]!.lang).toBe('sql')
    expect(splitArgs('write_file', JSON.stringify({ path: 'src/main.cpp', content: 'int main() {\n  return 0;\n}' })).code[0]).toMatchObject({ key: 'content', lang: 'cpp' })
    expect(splitArgs('run_code', JSON.stringify({ language: 'csharp', code: 'Console.WriteLine(1);' })).code[0]!.lang).toBe('csharp')
    // One short line of plain text is not code.
    expect(splitArgs('web_search', '{"query":"llama.cpp flags"}').code).toEqual([])
  })

  it('arguments that are not a JSON object are shown as they came', () => {
    expect(splitArgs('x', 'not json').unparsed).toBe('not json')
    expect(argsSummary('not json')).toEqual([['arguments', 'not json']])
  })

  it('the header shows the first line of code', () => {
    expect(argsSummary(JSON.stringify({ code: '\nimport pandas as pd\ndf = pd.read_csv("a.csv")' }))).toEqual([['code', 'import pandas as pd …']])
  })

  it('a run is its output, its errors and how it ended', () => {
    const r = runOutput({ exit_code: 1, stdout: 'a\n', stderr: 'Traceback…', timed_out: null, problem: null, seconds: 0.4, files_given_to_the_person: ['chart.png'] })
    expect(r).toEqual({ stdout: 'a\n', stderr: 'Traceback…', exitCode: 1, problem: null, seconds: 0.4, rest: { files_given_to_the_person: ['chart.png'] } })
    expect(runOutput({ rows: [] })).toBeNull()
  })
})

describe('what sub-agents brought back', () => {
  it('is read as parts only when every item is one', () => {
    expect(agentResults([{ title: 'Sum', result: '4', tool_calls: 1 }, { title: 'Colour', result: 'Red', tool_calls: 0, error: 'slow' }])).toEqual([
      { title: 'Sum', result: '4', toolCalls: 1, error: null },
      { title: 'Colour', result: 'Red', toolCalls: 0, error: 'slow' },
    ])
    expect(agentResults([{ title: 'Sum', result: '4' }, { name: 'x' }])).toBeNull()
    expect(agentResults([])).toBeNull()
  })
})
