import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { SkillItem, SkillLibrary } from '@/lib/skills'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const conversation: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

const skill = (over: Partial<SkillItem>): SkillItem => ({
  id: over.name ?? 's', name: 's', title: 'S', description: '', instructions: '', parameters: [], autoUse: true, sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

const library: SkillLibrary = {
  isAdmin: false,
  groups: [],
  skills: [
    skill({
      name: 'review', title: 'Review code', description: 'Review a file for one concern.', instructions: 'Review {{file}} for {{focus}}, in {{tone}} words. $ARGUMENTS',
      parameters: [{ name: 'file', description: 'The path', required: true }, { name: 'focus', required: true }, { name: 'tone', required: true, default: 'few' }],
    }),
    skill({ name: 'standup', title: 'Stand-up notes', instructions: 'Write my stand-up notes.', source: 'company', sharing: 'Company' }),
    skill({ name: 'triage', title: 'Triage a GitLab issue', instructions: 'Triage #{{issue}}.', parameters: [{ name: 'issue', required: true }], source: 'plugin', from: 'GitLab issues', canEdit: false }),
  ],
}

function backend() {
  let saved = conversation
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: saved }),
    'POST /api/chat/conversations/c1/messages': (body) => {
      // The server keeps the question with its skill filled in: the chat reads it back when the answer ends.
      const b = body as { content: string; skill?: { name: string; args: Record<string, string> } }
      const used = b.skill ? { ...b.skill, title: library.skills.find((x) => x.name === b.skill!.name)!.title, instructions: 'Filled in.' } : null
      saved = {
        ...conversation,
        currentLeafId: 'a1',
        messages: [
          { id: 'q1', parentId: null, role: 'user', content: b.content, skill: used, attachments: [], createdAt: '' },
          { id: 'a1', parentId: 'q1', role: 'assistant', content: 'Done.', attachments: [], createdAt: '', model: 'Main-Model' },
        ] as unknown as Conversation['messages'],
      }
      return {
        events: [
          { type: 'question', id: 'q1', parentId: null },
          { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
          { type: 'content', text: 'Done.' },
          { type: 'done', id: 'a1' },
        ],
      }
    },
    'GET /api/skills': () => ({ json: library }),
  })
}

describe('slash skills', () => {
  it('/review sits above the box with a field for each parameter, and goes with the words as a skill', async () => {
    const calls = backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/rev')
    const menu = await screen.findByRole('listbox', { name: 'Skills' })
    expect(within(menu).getAllByRole('option').map((o) => o.textContent)).toEqual([expect.stringContaining('/review')])
    await userEvent.keyboard('{Enter}')

    // The skill above the box (not its instructions in it), a field for each parameter, the first one ready to type in.
    const chosen = screen.getByRole('group', { name: 'Use /review' })
    expect(box).toHaveValue('')
    expect(box).toHaveAttribute('placeholder', 'Anything to add for /review (optional)')
    const file = within(chosen).getByRole('textbox', { name: /File/ })
    await waitFor(() => expect(file).toHaveFocus())
    expect(within(chosen).getByText('The path')).toBeInTheDocument()
    expect(within(chosen).getByRole('textbox', { name: 'Tone' })).toHaveAttribute('placeholder', 'few')
    expect(screen.queryByRole('listbox', { name: 'Skills' })).toBeNull()

    // Not sent while a required parameter is empty.
    await userEvent.type(file, 'src/parse.c')
    await userEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(await screen.findByText('Fill this in to send.')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/chat/conversations/c1/messages')).toBe(false)

    // Enter goes from field to field, then to the box; Enter there sends the skill with the words.
    await userEvent.type(within(chosen).getByRole('textbox', { name: /Focus/ }), 'buffer overflows{Enter}{Enter}')
    await waitFor(() => expect(box).toHaveFocus())
    await userEvent.type(box, 'mostly the header parser{Enter}')
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({
        content: 'mostly the header parser',
        attachments: [],
        root: true,
        skill: { name: 'review', args: { file: 'src/parse.c', focus: 'buffer overflows' } },
      }),
    )
    await waitFor(() => expect(box).toHaveValue(''))
    expect(screen.queryByRole('group', { name: 'Use /review' })).toBeNull()
    // The question shows the skill it used and its parameters, the words as typed.
    const used = await screen.findByRole('figure', { name: 'Used the skill /review' })
    expect(within(used).getByText('src/parse.c')).toBeInTheDocument()
    expect(screen.getByText('mostly the header parser')).toBeInTheDocument()
    // What the model read, on demand.
    await userEvent.click(within(used).getByRole('button', { name: 'Instructions' }))
    expect(within(used).getByText('Filled in.')).toBeInTheDocument()
  })

  it('the menu finds by title too, says where each comes from, and Escape closes it', async () => {
    backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/')
    const menu = await screen.findByRole('listbox', { name: 'Skills' })
    expect(within(menu).getAllByRole('option')).toHaveLength(3)
    expect(within(menu).getByRole('option', { name: /triage/ })).toHaveTextContent('GitLab issues')
    await userEvent.type(box, 'gitlab')
    expect(within(menu).getAllByRole('option')).toHaveLength(1)
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox', { name: 'Skills' })).toBeNull()
    expect(box).toHaveValue('/gitlab')
  })

  it('a skill without parameters goes alone, and Backspace in the empty box lets one go', async () => {
    const calls = backend()
    renderApp('/chat')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/tri')
    await userEvent.click(await screen.findByRole('option', { name: /triage/ }))
    expect(screen.getByRole('group', { name: 'Use /triage' })).toBeInTheDocument()
    await userEvent.click(box)
    await userEvent.keyboard('{Backspace}')
    expect(screen.queryByRole('group', { name: 'Use /triage' })).toBeNull()

    await userEvent.type(box, '/st')
    await userEvent.click(await screen.findByRole('option', { name: /standup/ }))
    expect(screen.getByRole('group', { name: 'Use /standup' })).toBeInTheDocument()
    await waitFor(() => expect(box).toHaveFocus())
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: '', skill: { name: 'standup', args: {} } }))
  })
})
