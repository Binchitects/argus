import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const ready = {
  id: 'x1', project: 'group/app', branch: 'main', baseSha: 'sha1abcdef', title: 'add() subtracts', ask: 'Fix issue #5', source: 'issue', status: 'ready', step: null, model: 'Main-Model',
  summary: 'add() adds now.', mergeRequestUrl: null, error: null, createdAt: '', updatedAt: '',
  diff: '--- a/calc.py\n+++ b/calc.py\n@@ -1,2 +1,2 @@\n def add(a, b):\n-    return a - b\n+    return a + b\n',
  log: [{ tool: 'read_file', about: 'calc.py 1–2', result: '1 def add' }, { tool: 'replace', about: 'calc.py', result: 'Changed.' }],
  tests: { command: 'python -m unittest', passed: true, output: 'OK\nExit code 0.' },
  files: [{ path: 'calc.py', deleted: false }],
}

afterEach(() => vi.restoreAllMocks())

describe('Fix it', () => {
  it('links one\'s own GitLab token, then starts a fix for an issue', async () => {
    let linked = false
    const calls = fakeApi(member, {
      'GET /api/fixes': () => ({ json: [] }),
      'GET /api/fixes/token': () => ({ json: { linked, account: linked ? 'kim' : null, gitlab: 'https://gitlab.test' } }),
      'PUT /api/fixes/token': () => {
        linked = true
        return { json: { linked: true, account: 'kim' } }
      },
      'POST /api/fixes': () => ({ status: 202, json: { ...ready, status: 'working' } }),
      'GET /api/fixes/x1': () => ({ json: { ...ready, status: 'working', step: 'Reading the code', diff: '', summary: null, tests: null } }),
    })
    const { router } = renderApp('/code/fixes')
    await userEvent.type(await screen.findByLabelText('Personal access token'), 'glpat-secret-1')
    await userEvent.click(screen.getByRole('button', { name: 'Link it' }))
    expect(await screen.findByText('kim')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ token: 'glpat-secret-1' })

    await userEvent.click(screen.getByRole('radio', { name: 'An issue' }))
    await userEvent.type(screen.getByLabelText('Project'), 'group/app')
    await userEvent.type(screen.getByLabelText('Issue'), '5')
    await userEvent.click(screen.getByRole('button', { name: /Fix it/ }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/fixes/x1'))
    expect(calls.find((c) => c.path === '/api/fixes' && c.method === 'POST')?.body).toEqual({ project: 'group/app', issue: 5 })
    expect(await screen.findByText('Reading the code…')).toBeInTheDocument()
  })

  it('shows the diff, the steps and the tests, and opens the merge request as the person', async () => {
    const calls = fakeApi(member, {
      'GET /api/fixes/x1': () => ({ json: ready }),
      'POST /api/fixes/x1/merge-request': () => ({ json: { url: 'https://gitlab.test/group/app/-/merge_requests/12', branch: 'fix/add-x1' } }),
    })
    const opened = vi.spyOn(window, 'open').mockReturnValue(null)
    renderApp('/code/fixes/x1')
    const diff = await screen.findByLabelText('The diff')
    expect(diff).toHaveTextContent('-    return a - b', { normalizeWhitespace: false })
    expect(diff).toHaveTextContent('+    return a + b', { normalizeWhitespace: false })
    expect(within(screen.getByRole('complementary', { name: 'What it did, step by step' })).getByText('Changed')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'The tests' })).toHaveTextContent('Passed')
    await userEvent.clear(screen.getByLabelText('Its title'))
    await userEvent.type(screen.getByLabelText('Its title'), 'Make add() add')
    await userEvent.click(screen.getByRole('button', { name: /Open the merge request as you/ }))
    await waitFor(() => expect(opened).toHaveBeenCalledWith('https://gitlab.test/group/app/-/merge_requests/12', '_blank', 'noopener'))
    expect(calls.find((c) => c.path === '/api/fixes/x1/merge-request')?.body).toEqual({ title: 'Make add() add' })
  })
})
