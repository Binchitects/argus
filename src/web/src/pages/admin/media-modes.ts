/** What a media model does, as the gateway names it. */
export type MediaMode = 'image_generation' | 'video_generation' | 'audio_transcription' | 'audio_speech'

export const mediaModeLabel: Record<MediaMode, string> = {
  image_generation: 'Pictures',
  video_generation: 'Video',
  audio_transcription: 'Speech to text',
  audio_speech: 'Text to speech',
}
