# Media studio

Pictures from a few words; a picture changed by saying how, all of it or only the
part painted over; variations of it; the picture four times as large, or cut out of
its background; a short clip that starts from it; speech in a voice from a sample,
and music. **Workspace → Media studio** (`/media`).

## Media studio

**Describe a picture**, choose its **Shape** (square, wide, photo, tall) and **How
many** (one, two or four), and press **Make it**. **Upload a picture** brings one of
yours in to work on.

Each piece of work runs in the background, in its turn on the GPU: the gallery
shows it where it will be, with what it is doing (waiting for the GPU, loading the
model, drawing). You may have three going at once. Work that takes more than a
minute says in the bell when it is done, so you can leave the page.

Open a picture to work on it:

- **Change**: say what changes ("give the fox a blue knitted hat"). It changes the
  whole picture as you say, keeping the rest of it. **Paint the part** to change
  only what you paint over: the picture model redraws it and the rest stays exactly
  as it was, the edge of the painted part blended softly. **Erase**, **Undo** and
  the brush's size help paint it.
- **Variations**: one, two or four pictures like it, **a little**, **some** or **a
  lot** different.
- **Enlarge**: four times as large, its details drawn in (Real-ESRGAN), up to 8,192
  pixels a side.
- **Move**: a clip of two to five seconds from the video model that starts from the
  picture; say what happens. It takes a few minutes.
- **Cut out**: the background taken away (BiRefNet), a PNG whose background is
  see-through, to put on slides, pages or other pictures.

**Made from** opens the picture it came from. **Download** saves the file; **Delete**
takes it out of the studio with its file (an upload stays in your files).

## Speech and music

With the **studio** module, the card at the top also makes:

- **Speech**: words spoken (up to 3,000 characters), in the voice model's own voice
  or in **a voice from a sample**: record 10 to 30 seconds, or give a sound file.
  A sample's voice is used only when you tick that the voice is yours or that its
  owner lets you use it; what it says carries the model maker's inaudible
  watermark (Chatterbox's Perth), so it can be told apart as made by a model. **How
  lively** sets how much feeling it speaks with.
- **Music**: from a description (style, instruments, mood, tempo) and lyrics, or
  none for instrumental music; 15 seconds to 2 minutes (ACE-Step). Mark the lyrics'
  parts with `[verse]`, `[chorus]` and `[bridge]`.

Each comes as an MP3 in the gallery, under **Sound**.

## What it needs

- The **imagegen** module and a picture model (Admin → Models); FLUX.2-klein is the
  default. Enlarge needs its upscaler, `image/upscale/RealESRGAN_x4plus.safetensors`
  in the model library: it is downloaded with the picture model's files
  (Comfy-Org/Real-ESRGAN_repackaged, 67 MB), and the picture server reads it from
  then on.
- For **Move**, the **videogen** module, a video model and the **Video** permission.
- For **Cut out**, **Speech** and **Music**, the **studio** module
  (`COMPOSE_PROFILES=studio`): one service on the GPU with BiRefNet lite (178 MB),
  Chatterbox's English model (3.2 GB) and ACE-Step v1 3.5B (8.3 GB), fetched into
  the model library's `studio` folder with the default models (Admin → Models).
  One of them works at a time; it is loaded when asked for and unloaded after three
  minutes unused, so the GPU goes back to the chat models. One that does not fit in
  the GPU's free memory says so. Speech and music need the **Speech** permission;
  speech is priced as text read aloud, music per minute (Settings → Prices).
- The **Pictures** permission opens the studio. Each picture counts toward the
  person's pictures a day (Safeguards) and their credit for pictures; a clip toward
  their credit for video. Their words go through the safeguards' rules first.

Pictures and clips are the person's files: they count toward their room (Storage),
and the storage page shows them as **In the media studio**. Their cost is booked in
the gateway's log as the chat's, at the model's price per picture (or per second of
video), so usage, credit and chargeback count them.
