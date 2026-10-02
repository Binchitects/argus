import { makeZip, uniqueNames } from '@/lib/zip'
import { downloadUrl } from './api'
import type { FileItem } from './files'

/** A file's path in the zip: a file Argus read under its repository, the rest by name. */
function pathOf(f: FileItem): string {
  const name = f.kind === 'repo' ? `${f.repo ?? 'repository'}/${f.path}` : f.name
  // No way out of the zip's folder, and no empty names.
  return name.split('/').filter((p) => p && p !== '.' && p !== '..').join('/') || 'file'
}

/**
 * Every file of the chat in one zip, as the Files panel lists them: attachments and
 * what tools made (their own bytes), code the model wrote, files Argus read.
 */
export async function zipFiles(files: FileItem[], signal?: AbortSignal): Promise<{ zip: Blob; missing: string[] }> {
  const encoder = new TextEncoder()
  const names = uniqueNames(files.map(pathOf))
  const missing: string[] = []
  const entries = await Promise.all(
    files.map(async (f, i) => {
      if (f.kind !== 'attachment') return { name: names[i]!, data: encoder.encode(f.code) }
      // One that cannot be had (removed meanwhile) is left out, and said; the rest still come.
      const res = await fetch(downloadUrl(f.attachment.id), { signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } }).catch(() => null)
      if (!res?.ok) {
        missing.push(f.name)
        return null
      }
      return { name: names[i]!, data: new Uint8Array(await res.arrayBuffer()) }
    }),
  )
  const kept = entries.filter((e) => e !== null)
  if (kept.length === 0) throw new Error('None of the files could be had. Check the connection and try again.')
  return { zip: await makeZip(kept), missing }
}

/** The zip's file name, from the chat's title. */
export function zipName(title: string | null): string {
  // eslint-disable-next-line no-control-regex -- control characters are what it takes out
  const base = (title ?? '').replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 80)
  return `${base || 'chat'} files.zip`
}
