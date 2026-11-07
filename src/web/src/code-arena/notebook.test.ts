import { describe, expect, it } from 'vitest'
import { emptyNotebook, linesOf, newCell, notebookLanguage, parseNotebook, richest, serializeNotebook, stripAnsi, type Notebook } from './notebook'

/** A notebook as Jupyter writes one: keys sorted, one space of indentation, text as lists of lines. */
const jupyter = `{
 "cells": [
  {
   "cell_type": "markdown",
   "id": "a1",
   "metadata": {},
   "source": [
    "# Sales\\n",
    "By month."
   ]
  },
  {
   "cell_type": "code",
   "execution_count": 3,
   "id": "b2",
   "metadata": {
    "tags": [
     "load"
    ]
   },
   "outputs": [
    {
     "name": "stdout",
     "output_type": "stream",
     "text": [
      "loaded\\n",
      "12 rows\\n"
     ]
    },
    {
     "data": {
      "image/png": "iVBORw0KGgo=",
      "text/plain": [
       "<Figure size 640x480>"
      ]
     },
     "metadata": {},
     "output_type": "display_data"
    }
   ],
   "source": [
    "import pandas as pd\\n",
    "df = pd.read_csv('sales.csv')"
   ]
  }
 ],
 "metadata": {
  "kernelspec": {
   "display_name": "Python 3",
   "language": "python",
   "name": "python3"
  },
  "language_info": {
   "name": "python"
  }
 },
 "nbformat": 4,
 "nbformat_minor": 5
}
`

describe('notebooks', () => {
  it("reads Jupyter's file: each cell's source and outputs as one text, its count, metadata and id", () => {
    const nb = parseNotebook(jupyter) as Notebook
    expect(nb.cells.map((c) => [c.id, c.type, c.source, c.count])).toEqual([
      ['a1', 'markdown', '# Sales\nBy month.', null],
      ['b2', 'code', "import pandas as pd\ndf = pd.read_csv('sales.csv')", 3],
    ])
    expect(nb.cells[1]!.metadata).toEqual({ tags: ['load'] })
    expect(nb.cells[1]!.outputs[0]).toEqual({ name: 'stdout', output_type: 'stream', text: 'loaded\n12 rows\n' })
    expect(nb.cells[1]!.outputs[1]!.data).toEqual({ 'image/png': 'iVBORw0KGgo=', 'text/plain': '<Figure size 640x480>' })
    expect(notebookLanguage(nb)).toBe('python')
  })

  it('writes the file back as Jupyter wrote it, byte for byte', () => {
    expect(serializeNotebook(parseNotebook(jupyter) as Notebook)).toBe(jupyter)
  })

  it('keeps a notebook\'s own indentation and what it has besides, and gives an old notebook no ids', () => {
    const old = JSON.stringify({ nbformat: 4, nbformat_minor: 2, metadata: {}, extra: { kept: true }, cells: [{ cell_type: 'code', source: 'x = 1', metadata: {}, outputs: [], execution_count: null }] }, null, 2)
    const nb = parseNotebook(old) as Notebook
    expect(nb.indent).toBe(2)
    const written = serializeNotebook({ ...nb, cells: [...nb.cells, newCell('markdown', 'note')] })
    const back = JSON.parse(written)
    expect(back.extra).toEqual({ kept: true })
    expect(back.cells.map((c: { id?: string }) => c.id)).toEqual([undefined, undefined])
    expect(back.cells[1]).toEqual({ cell_type: 'markdown', metadata: {}, source: ['note'] })
    expect(written.startsWith('{\n  "cells"')).toBe(true)
  })

  it('opens an empty file as a new notebook, and says why one is not a notebook', () => {
    const fresh = parseNotebook('') as Notebook
    expect(fresh.cells).toHaveLength(1)
    expect(fresh.cells[0]!.type).toBe('code')
    expect(JSON.parse(serializeNotebook(emptyNotebook())).nbformat_minor).toBe(5)
    expect(parseNotebook('{ not json')).toHaveProperty('error')
    expect((parseNotebook('{"nbformat": 3, "worksheets": []}') as { error: string }).error).toContain('nbformat 4')
    expect(parseNotebook('{"nbformat": 4}')).toEqual({ error: 'The notebook has no list of cells.' })
  })

  it('splits text into lines as nbformat writes them', () => {
    expect(linesOf('')).toEqual([])
    expect(linesOf('a')).toEqual(['a'])
    expect(linesOf('a\nb')).toEqual(['a\n', 'b'])
    expect(linesOf('a\n')).toEqual(['a\n'])
    expect(linesOf('a\n\nb\n')).toEqual(['a\n', '\n', 'b\n'])
  })

  it('shows the richest form of an output, and a traceback without its colours', () => {
    expect(richest({ 'text/plain': 'df', 'text/html': '<table/>' })).toEqual({ mime: 'text/html', value: '<table/>' })
    expect(richest({ 'text/plain': '<Figure>', 'image/png': 'AAA' })?.mime).toBe('image/png')
    expect(richest({ 'application/vnd.custom': 1, 'text/plain': 'x' })?.mime).toBe('text/plain')
    expect(richest({})).toBeNull()
    expect(stripAnsi('\u001b[0;31mZeroDivisionError\u001b[0m: division by zero')).toBe('ZeroDivisionError: division by zero')
  })
})
