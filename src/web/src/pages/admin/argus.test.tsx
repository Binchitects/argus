import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const meta = { version: '1.0', model: 'nomic-embed-text', dim: '768', size_bytes: 2e8, license: 'MIT', compatible: true, incompatible_reason: null }
const idle = { state: 'idle', action: null, target: null, returncode: null, tail: [], finished: null }

describe('admin knowledge packs', () => {
  it('lists the pack library with what is loaded, and loads and unloads like the models', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/argus/packs': () => ({
        json: {
          packs: [{ name: 'react', ...meta, source: 'library' }, { name: 'legacy', ...meta, source: 'installed' }],
          library: [
            { file: 'react.arguspack', name: 'react', ...meta, loaded: true },
            { file: 'win/win32.arguspack', name: 'win32', ...meta, loaded: false },
            { file: 'old/cpp.arguspack', name: 'cpp', ...meta, model: 'other-model', compatible: false, incompatible_reason: 'built with other-model', loaded: false },
          ],
          library_dir: '/pack-library', job: idle, index_url: null,
        },
      }),
      'POST /api/admin/argus/packs/load': () => ({ json: { status: 'loaded' } }),
      'POST /api/admin/argus/packs/remove': () => ({ json: { status: 'removed' } }),
    })
    renderApp('/admin/packs')
    const library = await screen.findByRole('region', { name: 'Pack library' })
    const react = within(library).getByRole('heading', { name: /react/ }).closest('section')!
    const win32 = within(library).getByRole('heading', { name: /win32/ }).closest('section')!
    const cpp = within(library).getByRole('heading', { name: /cpp/ }).closest('section')!
    expect(within(react).getByText('Loaded')).toBeInTheDocument()
    expect(within(win32).getByText('Not loaded')).toBeInTheDocument()
    // Built with another embedding model: it says so, and cannot be loaded.
    expect(within(cpp).getByText('built with other-model')).toBeInTheDocument()
    expect(within(cpp).getByRole('button', { name: /Load/ })).toBeDisabled()

    await userEvent.click(within(win32).getByRole('button', { name: /Load/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/argus/packs/load')?.body).toMatchObject({ file: 'win/win32.arguspack' }))

    await userEvent.click(within(react).getByRole('button', { name: /Unload/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'Unload' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/argus/packs/remove')?.body).toMatchObject({ name: 'react' }))

    // One installed from a URL is a copy: removing it is said to delete it.
    const elsewhere = screen.getByRole('region', { name: 'Loaded from elsewhere' })
    expect(within(elsewhere).getByText('Installed from a URL')).toBeInTheDocument()
    await userEvent.click(within(elsewhere).getByRole('button', { name: /Remove/ }))
    expect(await screen.findByText(/its file is deleted/)).toBeInTheDocument()
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
      'GET /api/admin/argus/explore/doc': () => ({ json: { source: 'react', doc_path: 'reference/useEffect.md', title: 'useEffect', url: 'https://react.dev/reference/useEffect', text: 'useEffect is a React Hook that lets you synchronize.', truncated: false, license: 'CC-BY-4.0' } }),
    })
    renderApp('/admin/explore?tab=docs')
    await userEvent.type(await screen.findByLabelText('Search the documentation'), 'useEffect')
    await userEvent.click(screen.getByRole('combobox', { name: 'How' }))
    await userEvent.click(await screen.findByRole('option', { name: 'An API by its name' }))
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await userEvent.click(await screen.findByRole('button', { name: /useEffect\(setup, deps\?\)/ }))
    expect(calls.some((c) => c.path.includes('/explore/docs?') && c.path.includes('mode=name') && c.path.includes('q=useEffect'))).toBe(true)
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/lets you synchronize/)).toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: /The original page/ })).toHaveAttribute('href', 'https://react.dev/reference/useEffect')
  })
})
