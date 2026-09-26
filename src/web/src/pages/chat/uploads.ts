import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { uploadFile } from './api'
import type { Attachment } from './types'

export interface Upload {
  key: string
  name: string
  size: number
  isImage: boolean
  progress: number
  /** A local picture of the file while and after it uploads. */
  preview?: string
  attachment?: Attachment
  error?: string
}

let next = 0

/** Files being attached to the next message: uploaded as soon as they are added. */
export function useUploads(maxBytes: number) {
  const [uploads, setUploads] = useState<Upload[]>([])
  const aborts = useRef(new Map<string, AbortController>())
  const set = (key: string, change: Partial<Upload>) => setUploads((us) => us.map((u) => (u.key === key ? { ...u, ...change } : u)))

  const add = useCallback(
    (files: Iterable<File>) => {
      for (const file of files) {
        const key = `u${++next}`
        const isImage = file.type.startsWith('image/') && file.type !== 'image/svg+xml'
        const upload: Upload = { key, name: file.name || 'pasted', size: file.size, isImage, progress: 0, preview: isImage ? URL.createObjectURL(file) : undefined }
        if (file.size > maxBytes) {
          setUploads((us) => [...us, { ...upload, error: `Larger than ${formatValue(maxBytes, 'bytes')}.` }])
          continue
        }
        setUploads((us) => [...us, upload])
        const abort = new AbortController()
        aborts.current.set(key, abort)
        uploadFile(file, (p) => set(key, { progress: p }), abort.signal)
          .then((attachment) => set(key, { attachment, progress: 1 }))
          .catch((e) => !abort.signal.aborted && set(key, { error: errorMessage(e, 'The upload failed.') }))
          .finally(() => aborts.current.delete(key))
      }
    },
    [maxBytes],
  )

  const remove = useCallback((key: string) => {
    aborts.current.get(key)?.abort()
    setUploads((us) => {
      const u = us.find((x) => x.key === key)
      if (u?.preview) URL.revokeObjectURL(u.preview)
      return us.filter((x) => x.key !== key)
    })
  }, [])

  const clear = useCallback(() => {
    setUploads((us) => {
      for (const u of us) if (u.preview) URL.revokeObjectURL(u.preview)
      return []
    })
  }, [])

  useEffect(() => () => aborts.current.forEach((a) => a.abort()), [])

  return {
    uploads,
    add,
    remove,
    clear,
    busy: uploads.some((u) => !u.attachment && !u.error),
    attachments: uploads.flatMap((u) => (u.attachment ? [u.attachment] : [])),
  }
}

export type Uploads = ReturnType<typeof useUploads>
