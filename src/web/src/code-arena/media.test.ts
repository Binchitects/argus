import { describe, expect, it } from 'vitest'
import { mediaNamed } from './media'

describe('the media a tool made in the folder', () => {
  it('finds each picture, sound and video named under .code-arena/media/, once, from .code-arena on', () => {
    const said = [
      'Saved the picture to /home/ada/shop/.code-arena/media/draw.v2-1.PNG.',
      'The speech is in `.code-arena/media/speak 1.mp3` and (.code-arena/media/clip.mp4).',
      'Again: .code-arena/media/clip.mp4, and a page .code-arena/media/notes.html, and .code-arena/media/clip.mp4x.',
    ].join('\n')
    expect(mediaNamed(said).map((m) => [m.id, m.kind, m.fileName])).toEqual([
      ['.code-arena/media/draw.v2-1.PNG', 'image', 'draw.v2-1.PNG'],
      ['.code-arena/media/clip.mp4', 'video', 'clip.mp4'],
    ])
  })

  it('asks for nothing outside the media folder', () => {
    expect(mediaNamed('.code-arena/media/../../secret.png and .code-arena/media/a/../../b.wav')).toEqual([])
    expect(mediaNamed('.code-arena/media/sub/ok.wav')[0]).toMatchObject({ id: '.code-arena/media/sub/ok.wav', kind: 'audio', contentType: 'audio/wav' })
  })
})
