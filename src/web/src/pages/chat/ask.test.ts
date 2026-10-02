import { describe, expect, it } from 'vitest'
import { questionsOf, replyOf } from './ask'

describe("the model's questions", () => {
  it('are read leniently: a bare string is an option, a broken question is left out', () => {
    const qs = questionsOf(JSON.stringify({
      questions: [
        { question: 'Which database?', options: [{ label: 'PostgreSQL', description: 'Already in the stack' }, 'SQLite'] },
        { question: 'Which parts?', options: ['Auth', 'Search', ''], multiple: true },
        { question: 'No options?' },
      ],
    }))
    expect(qs).toEqual([
      { question: 'Which database?', options: [{ label: 'PostgreSQL', description: 'Already in the stack' }, { label: 'SQLite', description: null }], multiple: false },
      { question: 'Which parts?', options: [{ label: 'Auth', description: null }, { label: 'Search', description: null }], multiple: true },
    ])
    expect(questionsOf('not json')).toEqual([])
  })

  it('come back as one line per question, with what was picked and written', () => {
    const qs = questionsOf(JSON.stringify({ questions: [{ question: 'Which database?', options: ['PostgreSQL', 'SQLite'] }, { question: 'Which parts?', options: ['Auth', 'Search'], multiple: true }] }))
    expect(replyOf(qs, [['PostgreSQL'], ['Auth', 'Search']], ['', ' and billing '])).toBe('Which database? PostgreSQL\nWhich parts? Auth, Search, and billing')
  })
})
