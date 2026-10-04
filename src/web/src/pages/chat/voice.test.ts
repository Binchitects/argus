import { describe, expect, it, vi } from 'vitest'
import { level, Sentences, SpeechQueue, tuning, VoiceActivity, type Voice } from './voice'

describe('voice activity', () => {
  /** Readings every 50 ms: [level, how many]. */
  const run = (vad: VoiceActivity, parts: [number, number][], strict = false) => {
    const events: { at: number; e: string }[] = []
    let t = 0
    for (const [value, count] of parts)
      for (let i = 0; i < count; i++, t += 50) {
        const e = vad.push(value, t, strict)
        if (e) events.push({ at: t, e })
      }
    return events
  }

  it('speech louder than the room starts an utterance, and a pause ends it', () => {
    const events = run(new VoiceActivity(), [[0.002, 20], [0.2, 10], [0.002, 20]])
    expect(events.map((x) => x.e)).toEqual(['start', 'end'])
    // 150 ms of voice to start, 800 ms of quiet to end.
    expect(events[0]!.at).toBe(1000 + tuning.speechMs)
    expect(events[1]!.at - 1500).toBe(tuning.silenceMs)
  })

  it('a click is not speech, and a short pause between words does not end it', () => {
    expect(run(new VoiceActivity(), [[0.002, 10], [0.3, 2], [0.002, 10]])).toEqual([])
    const words = run(new VoiceActivity(), [[0.2, 5], [0.002, 6], [0.2, 5], [0.002, 20]])
    expect(words.map((x) => x.e)).toEqual(['start', 'end'])
  })

  it('over an answer being read it takes louder and longer voice to count', () => {
    // The answer's own sound, leaking into the microphone: not the person.
    expect(run(new VoiceActivity(), [[0.002, 10], [0.025, 20]], true)).toEqual([])
    const over = run(new VoiceActivity(), [[0.002, 10], [0.2, 10]], true)
    expect(over).toEqual([{ at: 500 + tuning.bargeInMs, e: 'start' }])
  })

  it('the level of a frame is its root mean square', () => {
    expect(level(new Float32Array([0.5, -0.5, 0.5, -0.5]))).toBeCloseTo(0.5)
    expect(level(new Float32Array(0))).toBe(0)
  })
})

describe('sentences', () => {
  it('an answer is cut into sentences as it is written, each when the next word starts', () => {
    const s = new Sentences()
    expect(s.push('Paris is the cap')).toEqual([])
    expect(s.push('ital of France. It lies')).toEqual(['Paris is the capital of France.'])
    expect(s.push(' on the Seine! Pi is 3.14 or so? Yes')).toEqual(['It lies on the Seine!', 'Pi is 3.14 or so?'])
    expect(s.flush()).toEqual(['Yes'])
  })

  it('Persian sentences end at their own question mark, and lines end sentences too', () => {
    const s = new Sentences()
    expect(s.push('پایتخت فرانسه کجاست؟ پاریس.\n## Next\n')).toEqual(['پایتخت فرانسه کجاست؟', 'پاریس.', '## Next'])
  })

  it('code blocks are not read, and a list number goes with its item', () => {
    const s = new Sentences()
    expect(s.push('Run this:\n```py')).toEqual(['Run this:'])
    expect(s.push('thon\nprint("hi. there")\n``')).toEqual([])
    expect(s.push('`\n1. Save it. 2. Run it.')).toEqual(['1. Save it.'])
    expect(s.flush()).toEqual(['2. Run it.'])
  })

  it('a long sentence with no end is cut at a comma so the voice does not wait', () => {
    const s = new Sentences()
    const long = `${'word '.repeat(40)}, ${'more '.repeat(30)}`
    const out = s.push(long)
    expect(out).toHaveLength(1)
    expect(out[0]!.length).toBeLessThanOrEqual(282)
  })
})

describe('speech queue', () => {
  /** A voice whose sentences arrive when the test says, and whose playing ends when the test says. */
  function voice() {
    const fetched: { text: string; resolve: (b: Blob | null) => void }[] = []
    const played: { text: string; end: () => void; signal: AbortSignal }[] = []
    const v: Voice = {
      fetch: (text) => new Promise((resolve) => fetched.push({ text, resolve })),
      play: (sound, signal) =>
        new Promise((resolve) => {
          void sound.text().then((text) => played.push({ text, end: () => resolve(), signal }))
          signal.addEventListener('abort', () => resolve())
        }),
    }
    return { v, fetched, played }
  }
  const tick = () => new Promise((r) => setTimeout(r, 0))

  it('sentences play in order, the next fetched while one plays, two at most', async () => {
    const { v, fetched, played } = voice()
    const q = new SpeechQueue(v)
    q.say('One.')
    q.say('Two.')
    q.say('Three.')
    expect(fetched.map((f) => f.text)).toEqual(['One.', 'Two.'])
    // The second is ready first: it still waits for the first.
    fetched[1]!.resolve(new Blob(['Two.']))
    await tick()
    expect(played).toHaveLength(0)
    fetched[0]!.resolve(new Blob(['One.']))
    await vi.waitFor(() => expect(played.map((p) => p.text)).toEqual(['One.']))
    expect(q.playing).toBe(true)
    expect(fetched.map((f) => f.text)).toEqual(['One.', 'Two.', 'Three.'])
    played[0]!.end()
    await vi.waitFor(() => expect(played.map((p) => p.text)).toEqual(['One.', 'Two.']))
    // Nothing to say in the third: the queue runs dry after the second.
    fetched[2]!.resolve(null)
    played[1]!.end()
    await vi.waitFor(() => expect(q.busy).toBe(false))
  })

  it('stop silences what plays and drops what waits', async () => {
    const { v, fetched, played } = voice()
    const q = new SpeechQueue(v)
    const changes = vi.fn()
    q.onChange = changes
    q.say('One.')
    q.say('Two.')
    fetched[0]!.resolve(new Blob(['One.']))
    await vi.waitFor(() => expect(played).toHaveLength(1))
    q.stop()
    expect(played[0]!.signal.aborted).toBe(true)
    expect(q.playing).toBe(false)
    expect(q.busy).toBe(false)
    fetched[1]!.resolve(new Blob(['Two.']))
    await tick()
    expect(played).toHaveLength(1)
    expect(changes).toHaveBeenCalled()
    // Speaking again after a stop works.
    q.say('Again.')
    fetched[2]!.resolve(new Blob(['Again.']))
    await vi.waitFor(() => expect(played.map((p) => p.text)).toEqual(['One.', 'Again.']))
  })
})
