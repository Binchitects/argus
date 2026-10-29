import { describe, expect, it } from 'vitest'
import { languageByFirstLine } from './language-detect'

const languages = [{ id: 'python', firstLine: '^#!/.*\\bpython[0-9.-]*\\b' }, { id: 'shell' }, { id: 'javascript' }, { id: 'ruby' }, { id: 'php' }, { id: 'perl' }]

describe('the language a first line names', () => {
  it("reads #! lines and PHP's tag, the languages' own rules first", () => {
    expect(languageByFirstLine('#!/usr/bin/env python3', languages)).toBe('python')
    expect(languageByFirstLine('#!/bin/bash', languages)).toBe('shell')
    expect(languageByFirstLine('#!/usr/bin/env zsh', languages)).toBe('shell')
    expect(languageByFirstLine('#!/bin/sh -e', languages)).toBe('shell')
    expect(languageByFirstLine('#!/usr/bin/env node', languages)).toBe('javascript')
    expect(languageByFirstLine('#!/usr/bin/ruby -w', languages)).toBe('ruby')
    expect(languageByFirstLine('<?php echo 1;', languages)).toBe('php')
    expect(languageByFirstLine('#!/usr/bin/perl', languages)).toBe('perl')
  })

  it('says none for a line that names nothing, or a language Monaco does not have', () => {
    expect(languageByFirstLine('hello', languages)).toBeNull()
    expect(languageByFirstLine('#!/usr/bin/env pwsh', languages)).toBeNull()
    expect(languageByFirstLine('# bash is mentioned, but this is no #! line', languages)).toBeNull()
  })
})
