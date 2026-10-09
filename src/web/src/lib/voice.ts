import { api } from './api'

/** A voice a text to speech model at the gateway offers: "kokoro/af_heart", the language it reads, its accent, and a woman's or a man's when the speech server says. */
export interface OfferedVoice {
  id: string
  model: string
  name: string
  language: string
  accent: string
  gender: 'female' | 'male' | null
}

/** A person's own speech choices; null (a language left out): the company's. */
export interface VoiceChoices {
  /** auto, or the code of the language they speak. */
  language: string | null
  voices: Record<string, string>
  speed: number | null
  readAloud: boolean | null
}

/** A change to a person's choices: only the ones named change; null puts one back to the company's (`voices: null` all the voices, a language's null its voice). */
export interface VoiceChange {
  language?: string | null
  voices?: Record<string, string | null> | null
  speed?: number | null
  readAloud?: boolean | null
}

/** Your account → Voice: what the person chose, the company's defaults, and what the speech models offer. */
export interface VoiceSettings {
  chosen: VoiceChoices
  company: { language: string; voices: Record<string, string>; speed: number; readAloud: boolean }
  voices: OfferedVoice[]
  /** The languages speech to text knows. */
  languages: string[]
  /** A speech to text model is at the gateway. */
  hears: boolean
  /** The speech server said what it offers (else only the voices already chosen are listed). */
  known: boolean
}

export const voiceQuery = {
  queryKey: ['account', 'voice'] as const,
  queryFn: () => api<VoiceSettings>('/api/account/voice'),
  staleTime: 60_000,
}

/** Whether the answer to something said aloud (Talk, a voice message) is read aloud: the person's choice, else the company's; yes until known. */
export const readsAloud = (v: VoiceSettings | undefined) => v?.chosen.readAloud ?? v?.company.readAloud ?? true

let names: Intl.DisplayNames | null = null

/** A language's (or an accent's) name in English: "fa" → "Persian", "en-gb" → "British English"; the code itself when the browser has no name for it. */
export function languageName(code: string): string {
  try {
    names ??= new Intl.DisplayNames(['en'], { type: 'language' })
    return names.of(code) ?? code
  } catch {
    return code
  }
}

/** A voice's name as people say it: "af_heart" → "Heart", "gyro" → "Gyro". */
export function voiceName(v: OfferedVoice): string {
  const name = v.name.includes('_') ? v.name.slice(v.name.indexOf('_') + 1) : v.name
  return name.charAt(0).toUpperCase() + name.slice(1).replace(/_/g, ' ')
}

/** What tells a voice apart: a woman's or a man's, and its accent when the language has several. */
export function voiceDetail(v: OfferedVoice): string {
  const who = v.gender === 'female' ? 'woman' : v.gender === 'male' ? 'man' : null
  const accent = v.accent.includes('-') ? languageName(v.accent) : null
  return [who, accent].filter(Boolean).join(', ')
}

/** A voice as the lists name it: "Adam (man, American English)". */
export const voiceLabel = (v: OfferedVoice) => (voiceDetail(v) ? `${voiceName(v)} (${voiceDetail(v)})` : voiceName(v))

/** What Settings → Speech chooses the company's voices from: every voice offered, and the text to speech models at the gateway. */
export interface SpeechOffer {
  voices: OfferedVoice[]
  models: string[]
  /** The speech server said what it offers. */
  known: boolean
}

/**
 * The company's voices as the app reads Settings → Speech's value: "en:kokoro/af_heart, fa:piper-fa/gyro" → each
 * language with its voice, in order. A pair not in that form is left out, and a language's first pair is the one kept.
 */
export function voicePairs(value: string): [string, string][] {
  const pairs = new Map<string, string>()
  for (const pair of value.split(',')) {
    const at = pair.indexOf(':')
    const language = pair.slice(0, at).trim().toLowerCase()
    const id = pair.slice(at + 1).trim()
    if (at > 0 && /^[a-z]{2,3}$/.test(language) && /^[^/\s,:]+\/[^/\s,:]+$/.test(id) && !pairs.has(language)) pairs.set(language, id)
  }
  return [...pairs]
}

/** Each language with its voice, as Settings → Speech keeps them: "en:kokoro/af_heart,fa:piper-fa/gyro". */
export const voiceValue = (pairs: [string, string][]) => pairs.map(([language, id]) => `${language}:${id}`).join(',')
