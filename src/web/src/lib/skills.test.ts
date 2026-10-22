import { describe, expect, it } from 'vitest'
import { matchSkills, missingOf, mustFill, parameterLabel, slashItems, slashQuery, sourceLabel, variablesOf, type SkillItem } from './skills'

const skill = (over: Partial<SkillItem>): SkillItem => ({
  id: over.name ?? 's', name: 's', title: 'S', description: '', instructions: '', parameters: [], autoUse: true, sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

describe('skills', () => {
  it('finds the parameters of instructions in order, once each, as the server does', () => {
    expect(variablesOf('Review {{file}} for {{ focus }}; then {{file}} again, not {single} or {{ 9 }}.')).toEqual(['file', 'focus'])
  })

  it('labels a parameter in words', () => {
    expect(parameterLabel('file_path')).toBe('File path')
    expect(parameterLabel('focus')).toBe('Focus')
  })

  it('needs a required parameter with no default, and only that', () => {
    const s = skill({ parameters: [{ name: 'file', required: true }, { name: 'focus', required: true, default: 'bugs' }, { name: 'tone', required: false }] })
    expect(s.parameters.map(mustFill)).toEqual([true, false, false])
    expect(missingOf(s, { file: '  ' })).toEqual(['file'])
    expect(missingOf(s, { file: 'a.ts' })).toEqual([])
  })

  it('matches by slash name first, then by title, then by what it is for', () => {
    const list = [
      skill({ name: 'standup', title: 'Write a review of the day' }),
      skill({ name: 'code-review', title: 'Code' }),
      skill({ name: 'review', title: 'Review' }),
      skill({ name: 'notes', title: 'Notes', description: 'After a review meeting.' }),
      skill({ name: 'other', title: 'Else' }),
    ]
    expect(matchSkills(list, 'rev').map((s) => s.name)).toEqual(['review', 'code-review', 'standup', 'notes'])
    expect(matchSkills(list, '').map((s) => s.name)).toEqual(['standup', 'code-review', 'review', 'notes', 'other'])
  })

  it('opens the menu only for a slash word at the start, with the chat’s commands first', () => {
    expect(slashQuery('/rev')).toBe('rev')
    expect(slashQuery('/')).toBe('')
    expect(slashQuery('/review the file')).toBeNull()
    expect(slashQuery('a /review')).toBeNull()
    const items = slashItems('', [skill({ name: 'review', title: 'Review' })], true)
    expect(items.map((i) => i.name)).toEqual(['compact', 'review'])
    expect(slashItems('rev', [skill({ name: 'review' })], true).map((i) => i.name)).toEqual(['review'])
    expect(slashItems('', [], false)).toEqual([])
  })

  it('says where a skill comes from', () => {
    expect(sourceLabel(skill({}))).toBe('Yours')
    expect(sourceLabel(skill({ sharing: 'Groups', groups: [{ id: 'g', name: 'Team' }] }))).toBe('Yours · Team')
    expect(sourceLabel(skill({ source: 'company', sharing: 'Company' }))).toBe('Company')
    expect(sourceLabel(skill({ source: 'plugin', from: 'GitLab issues' }))).toBe('GitLab issues')
  })
})
