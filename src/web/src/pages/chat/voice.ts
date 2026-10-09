/**
 * Talk's parts that need no page: hearing when someone speaks (voice activity),
 * cutting an answer into sentences as it is written, and reading them aloud in order.
 */

/** How Talk hears. Tests make it quicker. */
export const tuning = {
  /** How often the microphone's level is read, in ms. */
  frameMs: 50,
  /** Voice this long starts an utterance (shorter is a click or a cough). */
  speechMs: 150,
  /** Quiet this long after speech ends the utterance. */
  silenceMs: 800,
  /** Voice this long over an answer being read stops it: longer, as the answer's own sound may reach the microphone. */
  bargeInMs: 300,
  /** The quietest level that counts as voice (RMS of the samples, 0 to 1). */
  floor: 0.015,
  /** Voice is this many times louder than the room. */
  ratio: 3,
  /** A recording with no voice for this long starts again, so recordings stay small. */
  restartMs: 15_000,
}

/** The level of a frame of samples: their root mean square. */
export function level(samples: Float32Array): number {
  let sum = 0
  for (const s of samples) sum += s * s
  return samples.length ? Math.sqrt(sum / samples.length) : 0
}

/**
 * Voice activity from the microphone's level: louder than the room's noise (followed
 * slowly while nobody speaks) for long enough starts an utterance; quiet for long enough
 * ends it. `strict` while an answer is read aloud: twice as loud, and for longer.
 */
export class VoiceActivity {
  speaking = false
  private noise = 0.005
  private loudSince: number | null = null
  private quietSince: number | null = null
  private readonly t: typeof tuning

  constructor(t = tuning) {
    this.t = t
  }

  threshold(strict = false) {
    return Math.max(this.t.floor, this.noise * this.t.ratio) * (strict ? 2 : 1)
  }

  /** One reading; 'start' and 'end' when an utterance starts or ends. */
  push(value: number, now: number, strict = false): 'start' | 'end' | null {
    if (!this.speaking) {
      if (value > this.threshold(strict)) {
        this.loudSince ??= now
        if (now - this.loudSince >= (strict ? this.t.bargeInMs : this.t.speechMs)) {
          this.speaking = true
          this.quietSince = null
          return 'start'
        }
      } else {
        this.loudSince = null
        this.noise = Math.min(0.1, this.noise * 0.95 + value * 0.05)
      }
      return null
    }
    // A little quieter than the start still counts: words end softer than they begin.
    if (value > this.threshold() * 0.6) {
      this.quietSince = null
      return null
    }
    this.quietSince ??= now
    if (now - this.quietSince >= this.t.silenceMs) {
      this.speaking = false
      this.loudSince = null
      return 'end'
    }
    return null
  }
}

/** A sentence worth saying: it has a letter in it (not a list's "1." or a lone mark). */
const sayable = (s: string) => /\p{L}/u.test(s)

/** Where a sentence ends: its mark (and closing quotes or brackets) before a space, or a line's end. */
const sentenceEnd = /[.!?…؟。]+["'”’)\]]*(?=\s)|\n/g

/** Past this many characters with no sentence end, a sentence is cut at a comma or a space: a long one should not keep the voice waiting. */
const longest = 280

/**
 * Cuts an answer into sentences as it is written, for reading aloud. Code blocks are left
 * out (there is nothing to say in them); what has no letter in it joins the next sentence.
 */
export class Sentences {
  private buffer = ''
  private carry = ''
  private inCode = false

  /** More of the answer: the sentences it completes. */
  push(text: string): string[] {
    this.buffer += text
    return this.take(false)
  }

  /** The answer is over: what is left is its last sentence. */
  flush(): string[] {
    return this.take(true)
  }

  private take(end: boolean): string[] {
    const out: string[] = []
    const emit = (s: string) => {
      const text = `${this.carry} ${s}`.replace(/\s+/g, ' ').trim()
      if (!text) return
      if (sayable(text)) {
        out.push(text)
        this.carry = ''
      } else this.carry = text
    }
    for (;;) {
      if (this.inCode) {
        const close = this.buffer.indexOf('```')
        if (close < 0) {
          // Keep a fence that may be half written.
          this.buffer = end ? '' : this.buffer.slice(-2)
          break
        }
        this.buffer = this.buffer.slice(close + 3)
        this.inCode = false
        continue
      }
      const fence = this.buffer.indexOf('```')
      const prose = fence >= 0 ? this.buffer.slice(0, fence) : this.buffer
      sentenceEnd.lastIndex = 0
      let cut = 0
      for (let m = sentenceEnd.exec(prose); m; m = sentenceEnd.exec(prose)) {
        emit(prose.slice(cut, m.index + m[0].length))
        cut = m.index + m[0].length
      }
      if (fence >= 0) {
        emit(prose.slice(cut))
        this.buffer = this.buffer.slice(fence + 3)
        this.inCode = true
        continue
      }
      let rest = prose.slice(cut)
      if (!end && rest.length > longest) {
        const at = Math.max(rest.lastIndexOf(', ', longest), rest.lastIndexOf(' ', longest))
        if (at > 0) {
          emit(rest.slice(0, at + 1))
          rest = rest.slice(at + 1)
        }
      }
      if (end) {
        // A trailing backtick or two may be all there is of a fence: not words.
        emit(rest.replace(/`+$/, ''))
        rest = ''
        if (this.carry && !sayable(this.carry)) this.carry = ''
      }
      this.buffer = rest
      break
    }
    return out
  }
}

/** How a sentence becomes sound, and how sound is played. */
export interface Voice {
  /** The sentence as sound, or null when there is nothing to say in it; `context` is what came before it, whose language a short sentence takes. */
  fetch: (text: string, signal: AbortSignal, context?: string) => Promise<Blob | null>
  /** Plays it to its end; ends early when the signal is aborted. */
  play: (sound: Blob, signal: AbortSignal) => Promise<void>
}

/**
 * Sentences read aloud in order as they come. The next one's sound is fetched while one
 * plays (two at most, so a long answer does not flood the speech server); stop() silences
 * everything at once.
 */
export class SpeechQueue {
  /** A sentence is playing now. */
  playing = false
  /** Called when playing starts or stops, and when the queue runs dry. */
  onChange?: () => void
  private items: { text: string; context?: string; sound?: Promise<Blob | null> }[] = []
  private controller = new AbortController()
  private generation = 0
  private running = false
  private readonly voice: Voice

  constructor(voice: Voice) {
    this.voice = voice
  }

  /** Sentences waiting or playing. */
  get busy() {
    return this.running
  }

  /** A sentence to read, after what came before it in the answer (which tells the language of a short one). */
  say(text: string, context?: string) {
    this.items.push({ text, context })
    this.prefetch()
    if (!this.running) void this.run()
  }

  stop() {
    this.generation++
    this.controller.abort()
    this.controller = new AbortController()
    this.items = []
    const was = this.running || this.playing
    this.running = false
    this.playing = false
    if (was) this.onChange?.()
  }

  private prefetch() {
    const signal = this.controller.signal
    for (const item of this.items.slice(0, 2)) item.sound ??= this.voice.fetch(item.text, signal, item.context).catch(() => null)
  }

  private async run() {
    const generation = this.generation
    const signal = this.controller.signal
    this.running = true
    while (this.items.length > 0 && generation === this.generation) {
      this.prefetch()
      const sound = await this.items[0]!.sound
      if (generation !== this.generation) return
      this.items.shift()
      this.prefetch()
      if (!sound) continue
      this.playing = true
      this.onChange?.()
      await this.voice.play(sound, signal).catch(() => {})
      if (generation !== this.generation) return
      this.playing = false
      this.onChange?.()
    }
    if (generation !== this.generation) return
    this.running = false
    this.onChange?.()
  }
}
