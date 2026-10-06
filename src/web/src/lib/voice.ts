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
