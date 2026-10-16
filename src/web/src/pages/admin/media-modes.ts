/** What a media model does, as the gateway names it. */
export type MediaMode = 'image_generation' | 'video_generation' | 'audio_transcription' | 'audio_speech'

export const mediaModeLabel: Record<MediaMode, string> = {
  image_generation: 'Pictures',
  video_generation: 'Video',
  audio_transcription: 'Speech to text',
  audio_speech: 'Text to speech',
}

/** What each of a picture or video model's files is, by its role. */
export const roleLabel: Record<string, string> = {
  diffusion: 'Diffusion model',
  model: 'Whole model (one file)',
  llm: 'Text encoder (LLM)',
  llm_vision: 'Text encoder vision',
  t5xxl: 'T5 text encoder',
  clip_l: 'CLIP-L',
  clip_g: 'CLIP-G',
  clip_vision: 'CLIP vision',
  vae: 'VAE (decoder)',
  taesd: 'Tiny decoder (TAESD)',
  high_noise: 'High-noise model (Wan A14B)',
}
