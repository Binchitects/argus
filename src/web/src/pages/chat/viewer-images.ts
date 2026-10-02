import { formatValue } from '@/lib/format'
import { attachmentUrl } from './api'
import type { Attachment } from './types'

/** A picture to look at: an image, or a page of a document. */
export interface ViewerImage {
  key: string
  src: string
  name: string
  /** Under the name: its size, or which page. */
  detail?: string
  download?: { href: string; name: string }
}

export const asViewerImages = (images: Attachment[]): ViewerImage[] =>
  images.map((a) => ({ key: a.id, src: attachmentUrl(a.id), name: a.fileName, detail: formatValue(a.size, 'bytes'), download: { href: attachmentUrl(a.id), name: a.fileName } }))
