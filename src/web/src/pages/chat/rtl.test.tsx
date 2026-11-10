import { readFileSync } from 'node:fs'
import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  codeIntel: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const question = 'این کد را توضیح بده\nwhy does it fail?'
const answer = 'این تابع **ورودی** را می‌خواند.\n\n- First, the `parse` call\n- سپس خطا\n\n> نکته: it retries\n\n```ts\nconst x = 1\n```'

const conversation: Conversation = {
  id: 'c1', title: 'کد', thinking: null, tools: [], useCodeIntel: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    { id: 'q1', parentId: null, role: 'user', content: question, attachments: [], createdAt: '' },
    { id: 'a1', parentId: 'q1', role: 'assistant', content: answer, attachments: [], createdAt: '', model: 'Main-Model' },
  ] as unknown as Conversation['messages'],
}

describe('right-to-left text in the chat', () => {
  it('lays out the question line by line, the answer block by block, and code left to right', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    })
    renderApp('/chat/c1')
    // The question: each of its lines by its own words.
    const asked = (await screen.findByText(/این کد را توضیح بده/)).closest('div')!
    expect(asked).toHaveAttribute('dir', 'auto')
    expect(asked).toHaveClass('bidi-lines')
    // The answer: a paragraph, the list by its first item and each item by its own words, the quote, and code.
    const said = (await screen.findByText('ورودی')).closest('.md')!
    expect(said.querySelector('p')).toHaveAttribute('dir', 'auto')
    expect(said.querySelector('ul')).toHaveAttribute('dir', 'ltr')
    expect([...said.querySelectorAll('li')].map((li) => li.getAttribute('dir'))).toEqual(['auto', 'auto'])
    expect(said.querySelector('blockquote')).toHaveAttribute('dir', 'rtl')
    expect(within(said as HTMLElement).getByRole('figure', { name: /^Code/ })).toHaveAttribute('dir', 'ltr')
    // The box: typed lines each by their own words, as the question shows them.
    expect(screen.getByRole('textbox', { name: 'Message' })).toHaveAttribute('dir', 'auto')
  })

  it('every box that guesses its direction does it line by line', () => {
    const css = readFileSync(`${process.cwd()}/src/styles/index.css`, 'utf8')
    expect(css).toMatch(/textarea\[dir='auto'\],\s*\.bidi-lines\s*\{\s*unicode-bidi: plaintext;/)
    // Code Arena's box is one of them.
    expect(readFileSync(`${process.cwd()}/src/code-arena/chat.tsx`, 'utf8')).toMatch(/<textarea\s+ref=\{area\}\s+dir="auto"/)
  })
})
