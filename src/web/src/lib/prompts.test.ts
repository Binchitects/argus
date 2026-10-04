import { describe, expect, it } from 'vitest'
import { fillPrompt, matchPrompts, slashItems, slashQuery, sourceLabel, variableLabel, variablesOf, type PromptItem } from './prompts'

const prompt = (over: Partial<PromptItem>): PromptItem => ({
  id: over.name ?? 'p', name: 'p', title: 'P', text: '', variables: [], sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

describe('prompts', () => {
  it('finds the blanks of a text in order, once each, as the server does', () => {
    expect(variablesOf('Review {{file}} for {{ focus }}; then {{file}} again, not {single} or {{ 9 }}.')).toEqual(['file', 'focus'])
  })

  it('fills each blank with its value, and leaves one without a value as written', () => {
    expect(fillPrompt('Review {{file}} for {{ focus }}; {{file}}! {{other}}', { file: 'a.ts ', focus: 'bugs' })).toBe('Review a.ts for bugs; a.ts! {{other}}')
  })

  it('labels a blank in words', () => {
    expect(variableLabel('file_path')).toBe('File path')
    expect(variableLabel('focus')).toBe('Focus')
  })

  it('matches by slash name first, then by title', () => {
    const list = [prompt({ name: 'standup', title: 'Write a review of the day' }), prompt({ name: 'code-review', title: 'Code' }), prompt({ name: 'review', title: 'Review' }), prompt({ name: 'other', title: 'Else' })]
    expect(matchPrompts(list, 'rev').map((p) => p.name)).toEqual(['review', 'code-review', 'standup'])
    expect(matchPrompts(list, '').map((p) => p.name)).toEqual(['standup', 'code-review', 'review', 'other'])
  })

  it('opens the menu only for a slash word at the start, with the chat’s commands first', () => {
    expect(slashQuery('/rev')).toBe('rev')
    expect(slashQuery('/')).toBe('')
    expect(slashQuery('/review the file')).toBeNull()
    expect(slashQuery('a /review')).toBeNull()
    const items = slashItems('', [prompt({ name: 'review', title: 'Review' })], true)
    expect(items.map((i) => i.name)).toEqual(['compact', 'review'])
    expect(slashItems('rev', [prompt({ name: 'review' })], true).map((i) => i.name)).toEqual(['review'])
    expect(slashItems('', [], false)).toEqual([])
  })

  it('says where a prompt comes from', () => {
    expect(sourceLabel(prompt({}))).toBe('Yours')
    expect(sourceLabel(prompt({ sharing: 'Groups', groups: [{ id: 'g', name: 'Team' }] }))).toBe('Yours · Team')
    expect(sourceLabel(prompt({ source: 'company', sharing: 'Company' }))).toBe('Company')
    expect(sourceLabel(prompt({ source: 'plugin', from: 'GitLab issues' }))).toBe('GitLab issues')
  })
})
