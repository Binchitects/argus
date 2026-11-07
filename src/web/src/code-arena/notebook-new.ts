import { toast } from '@/components/ui/toaster'
import { ApiError } from './http'
import { writeFile } from './ide-api'
import { emptyNotebook, serializeNotebook } from './notebook'
import { inputBox } from './quick-pick'

/** Create: New Jupyter Notebook: a name in the folder, the file made with one empty Python cell, and opened. */
export async function newNotebook(open: (path: string) => Promise<void>) {
  const typed = await inputBox({
    title: 'New Jupyter Notebook',
    placeholder: 'Its path in the folder (notebooks/analysis.ipynb)',
    value: 'Untitled.ipynb',
    validate: (v) => (v.trim() && !v.includes('..') ? null : 'Give a path in the folder.'),
  })
  if (!typed) return
  const path = /\.ipynb$/i.test(typed.trim()) ? typed.trim() : `${typed.trim()}.ipynb`
  try {
    // "none": a file there already is refused, not written over.
    await writeFile(path, serializeNotebook(emptyNotebook()), 'none')
  } catch (e) {
    if (!(e instanceof ApiError && e.http === 409)) {
      toast.error(e instanceof ApiError ? e.message : `${path} could not be made.`)
      return
    }
    toast.info(`${path} is there already: it opens as it is.`)
  }
  await open(path)
}
