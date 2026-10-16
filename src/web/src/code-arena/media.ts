import type { Attachment } from '@/pages/chat/types'

// The pictures, sounds and videos Arena's tools make for the agent: code-arena keeps each in the folder, under
// .code-arena/media/, and the tool's result names it there. The chat shows them as Arena's chat shows its media.

const extensions: Record<'image' | 'audio' | 'video', string[]> = {
  image: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'avif', 'bmp'],
  audio: ['mp3', 'wav', 'ogg', 'oga', 'opus', 'm4a', 'flac', 'aac'],
  video: ['mp4', 'webm', 'mov', 'm4v', 'ogv'],
}

/**
 * A path under .code-arena/media/ in a tool's result (relative to the folder, or whole: it is taken from .code-arena
 * on), up to the first extension of a picture, a sound or a video, and not a longer word.
 */
const named = new RegExp(`\\.code-arena/media/[^\\s"'\`<>()[\\]{}\\\\]+?\\.(?:${Object.values(extensions).flat().join('|')})(?![A-Za-z0-9])`, 'gi')

/** A file a tool made, as the chat's players take one: its id is its path in the folder. */
export interface MadeMedia extends Attachment {
  kind: 'image' | 'audio' | 'video'
}

/** The pictures, sounds and videos a tool's result names under .code-arena/media/, each once, in the order named. */
export function mediaNamed(text: string): MadeMedia[] {
  const out: MadeMedia[] = []
  for (const [path] of text.matchAll(named)) {
    // Nothing outside the media folder is asked for.
    if (path.split('/').includes('..') || out.some((m) => m.id === path)) continue
    const fileName = path.slice(path.lastIndexOf('/') + 1)
    const extension = fileName.slice(fileName.lastIndexOf('.') + 1).toLowerCase()
    const kind = (Object.keys(extensions) as MadeMedia['kind'][]).find((k) => extensions[k].includes(extension))!
    out.push({ id: path, fileName, size: 0, truncated: false, kind, contentType: `${kind}/${extension}` })
  }
  return out
}
