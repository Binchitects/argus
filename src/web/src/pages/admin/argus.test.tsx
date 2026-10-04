import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const meta = { version: '1.0', model: 'nomic-embed-text', dim: '768', size_bytes: 2e8, license: 'MIT', compatible: true, incompatible_reason: null }
const idle = { state: 'idle', action: null, target: null, returncode: null, tail: [], finished: null }

describe('admin knowledge packs', () => {
  it('shows the packs Argus searches, and adds one from the library like a model: choose, see it, install', async () => {
    const details = { docs: 1234, chunks: 5000, symbols: 800, source_repo: 'https://github.com/MicrosoftDocs/sdk-api', source_branch: 'docs', attribution: 'Microsoft', license_url: 'https://creativecommons.org/licenses/by/4.0/' }
    const calls = fakeApi(admin, {
      'GET /api/admin/argus/packs': () => ({
        json: {
          packs: [{ name: 'react', ...meta, source: 'library', details }, { name: 'legacy', ...meta, source: 'installed', details }],
          library: [
            { file: 'react.arguspack', name: 'react', ...meta, loaded: true, details },
            { file: 'win/win32.arguspack', name: 'win32', ...meta, loaded: false, details },
            { file: 'old/cpp.arguspack', name: 'cpp', ...meta, model: 'other-model', compatible: false, incompatible_reason: 'built with other-model', loaded: false, details },
          ],
          library_dir: '/pack-library', job: idle, index_url: null,
        },
      }),
      'POST /api/admin/argus/packs/load': () => ({ json: { status: 'loaded' } }),
      'POST /api/admin/argus/packs/remove': () => ({ json: { status: 'removed' } }),
    })
    renderApp('/admin/packs')
    const react = (await screen.findByRole('heading', { name: /react/ })).closest('section')!
    expect(within(react).getByText('Searched')).toBeInTheDocument()
    expect(within(react).getByText('Library')).toBeInTheDocument()
    expect(within(react).getByText(/1,234 pages/)).toBeInTheDocument()
    const legacy = screen.getByRole('heading', { name: /legacy/ }).closest('section')!
    expect(within(legacy).getByText('From a URL')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Add a pack' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a pack' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Pack' }))
    // The installed one is not offered; one built with another embedding model is listed, and cannot be chosen.
    expect(screen.queryByRole('option', { name: /^react/ })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: /cpp .* cannot be searched/ })).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('option', { name: /win32/ }))
    const facts = within(dialog).getByRole('region', { name: 'About this pack' })
    expect(facts).toHaveTextContent('1,234 pages · 5,000 passages · 800 API symbols')
    expect(facts).toHaveTextContent('https://github.com/MicrosoftDocs/sdk-api (docs)')
    expect(within(facts).getByRole('link', { name: /MIT/ })).toHaveAttribute('href', 'https://creativecommons.org/licenses/by/4.0/')
    await userEvent.click(within(dialog).getByRole('button', { name: /Install/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/argus/packs/load')?.body).toMatchObject({ file: 'win/win32.arguspack' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    // One installed from a URL is a copy: removing it says its file is deleted.
    await userEvent.click(within(legacy).getByRole('button', { name: /Remove/ }))
    expect(await screen.findByText(/its file is deleted/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/argus/packs/remove')?.body).toMatchObject({ name: 'legacy' }))
  })
})

describe('admin explore', () => {
  const repos = [{ repo_id: 7, path_with_namespace: 'team/codec', branch: 'main', files: 10, symbols: 40, public_symbols: 12 }]
  const explore = { json: { repos, symbols: { rows: [], capped: false }, files: { rows: [], capped: false } } }

  it('finds where a name is used and opens the file at that line', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/argus/explore': () => explore,
      'GET /api/admin/argus/explore/references': () => ({
        json: { rows: [{ repo: 'team/codec', path: 'src/decoder.cpp', line: 2, context: '  DecodeFrame(buf);', is_definition: false }], capped: false },
      }),
      'GET /api/admin/argus/explore/file': () => ({ json: { path_with_namespace: 'team/codec', path: 'src/decoder.cpp', lang: 'C++', size: 40, content: 'int main() {\n  DecodeFrame(buf);\n}', truncated: false } }),
    })
    renderApp('/admin/explore?tab=references')
    await userEvent.type(await screen.findByLabelText('Name used in the code'), 'DecodeFrame')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await userEvent.click(await screen.findByRole('button', { name: /src\/decoder\.cpp:2/ }))
    expect(calls.find((c) => c.path.startsWith('/api/admin/argus/explore/references'))?.path).toContain('name=DecodeFrame')
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('DecodeFrame(buf);', { exact: false })).toBeInTheDocument()
    expect(calls.find((c) => c.path.startsWith('/api/admin/argus/explore/file'))?.path).toContain('repo_id=7')
  })

  it('searches the documentation by an API name and opens the page', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/argus/explore': () => explore,
      'GET /api/admin/argus/explore/docs': (_b, _i, url) => ({
        json: url.searchParams.get('q')
          ? { rows: [{ source: 'react', name: 'useEffect', kind: 'function', title: 'useEffect', doc_path: 'reference/useEffect.md', url: 'https://react.dev/reference/useEffect', signature: 'useEffect(setup, deps?)' }], capped: false, sources: ['react'] }
          : { rows: [], capped: false, sources: ['react'] },
      }),
      'GET /api/admin/argus/explore/doc': () => ({ json: { source: 'react', doc_path: 'reference/useEffect.md', title: 'useEffect', url: 'https://react.dev/reference/useEffect', text: '---\ntitle: useEffect\n---\n# useEffect\n\n`useEffect` is a React Hook that **lets you synchronize**.', truncated: false, license: 'CC-BY-4.0' } }),
    })
    renderApp('/admin/explore?tab=docs')
    await userEvent.type(await screen.findByLabelText('Search the documentation'), 'useEffect')
    await userEvent.click(screen.getByRole('combobox', { name: 'How' }))
    await userEvent.click(await screen.findByRole('option', { name: 'An API by its name' }))
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await userEvent.click(await screen.findByRole('button', { name: /useEffect\(setup, deps\?\)/ }))
    expect(calls.some((c) => c.path.includes('/explore/docs?') && c.path.includes('mode=name') && c.path.includes('q=useEffect'))).toBe(true)
    const dialog = await screen.findByRole('dialog')
    // Formatted: a heading and bold text, not the page's source (its front matter is left out).
    expect(await within(dialog).findByRole('heading', { name: 'useEffect', level: 1 })).toBeInTheDocument()
    expect(within(dialog).getByText('lets you synchronize').tagName).toBe('STRONG')
    expect(within(dialog).queryByText(/title: useEffect/)).not.toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: /The original page/ })).toHaveAttribute('href', 'https://react.dev/reference/useEffect')
  })
})

describe('admin indexing webhook', () => {
  it('turns the GitLab webhook on, shows its secret once with the steps for GitLab, and turns it off', async () => {
    const status = {
      job: { state: 'idle', branches: [], started: null, finished: null, returncode: null, tail: [], trigger: null },
      index: { repos: 1, stale: 0, errored: 0, files: 10, symbols: 100 },
      interval: 0,
      webhook: false,
      pending: [],
    }
    const view = { enabled: false, fromEnv: false, url: 'https://argus.llm.test/hook/gitlab', header: 'X-Gitlab-Token', deliveries: [] as object[] }
    const calls = fakeApi(admin, {
      'GET /api/admin/argus/status': () => ({ json: status }),
      'GET /api/admin/argus/webhook': () => ({ json: view }),
      'POST /api/admin/argus/webhook': () => {
        Object.assign(view, { enabled: true, deliveries: [{ at: Math.floor(Date.now() / 1000) - 60, event: 'merge', repo: 'team/codec', outcome: 'started' }] })
        return { json: { ...view, token: 'a1b2c3d4e5f6' } }
      },
      'DELETE /api/admin/argus/webhook': () => {
        Object.assign(view, { enabled: false })
        return { json: view }
      },
    })
    renderApp('/admin/indexing')
    const card = await screen.findByRole('region', { name: 'Push and merge webhook' })
    expect(within(card).getByText('Off')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('button', { name: /Turn on/ }))

    // The secret: hidden until asked, shown this once.
    const secret = await within(card).findByLabelText('Secret token')
    expect(secret).not.toHaveTextContent('a1b2c3d4e5f6')
    await userEvent.click(within(card).getByRole('button', { name: 'Show Secret token' }))
    expect(secret).toHaveTextContent('a1b2c3d4e5f6')
    expect(within(card).getByText(/Shown this once/)).toBeInTheDocument()
    expect(within(card).getByText('https://argus.llm.test/hook/gitlab')).toBeInTheDocument()
    expect(within(card).getByText(/Push events and Merge request events/)).toBeInTheDocument()
    expect(within(card).getByText('team/codec')).toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Turn off' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Turn off' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/admin/argus/webhook')).toBe(true))
    await waitFor(() => expect(within(card).getByText('Off')).toBeInTheDocument())
    expect(within(card).queryByLabelText('Secret token')).not.toBeInTheDocument()
  })
})
